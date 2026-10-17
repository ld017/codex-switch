using CodexSwitch.Core.Auth;
using CodexSwitch.Core.Files;
using CodexSwitch.Core.Tests.Auth;
using CodexSwitch.Core.Usage;

namespace CodexSwitch.Core.Tests.Usage;

public sealed class UsageStreamingRefreshTests
{
    [Fact]
    public async Task Quotas_are_published_and_saved_before_optional_tokens_complete_and_observer_errors_are_isolated()
    {
        using var directory = new TestDirectory();
        var store = CreateStore(directory);
        var profile = await store.SaveAsync("Work", TestAuth.OAuth("a", "r", "work"), default);
        var cache = new UsageCache(directory.File("usage.json"), new AtomicFileStore());
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var quotaDate = DateTimeOffset.UtcNow.AddSeconds(-3);
        var fetcher = new UsageFetcher(store, directory.File("config.toml"), directory.File("tmp"),
            async (_, options, quotaReceived, token) =>
            {
                quotaReceived!(Response(12), quotaDate);
                received.TrySetResult();
                await release.Task.WaitAsync(token);
                return Response(12) with { QuotasFetchedAt = quotaDate, TokenUsage = Tokens(100) };
            });
        var coordinator = new UsageRefreshCoordinator(store, fetcher, cache);
        var refresh = coordinator.RefreshAllAsync(default, (_, _) => throw new InvalidOperationException("Broken observer"));
        try
        {
            await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(refresh.IsCompleted);
            var intermediate = (await cache.LoadAsync(default))[profile.Id];
            Assert.Equal(UsageStatus.Available, intermediate.Status);
            Assert.Equal(12, intermediate.Snapshot!.PrimaryUsedPercent);
            Assert.Equal(quotaDate, intermediate.Snapshot.FetchedAt);
        }
        finally { release.TrySetResult(); }

        var final = (await refresh)[profile.Id];
        Assert.Equal(quotaDate, final.Snapshot!.FetchedAt);
        Assert.Equal(100, final.Snapshot.TokenUsage!.Snapshot!.Summary!.LifetimeTokens);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failure_or_cancellation_after_early_quota_never_restores_an_older_quota(bool cancel)
    {
        using var directory = new TestDirectory();
        var store = CreateStore(directory);
        var profile = await store.SaveAsync("Work", TestAuth.OAuth("a", "r", "work"), default);
        var cache = new UsageCache(directory.File("usage.json"), new AtomicFileStore());
        await cache.SaveAsync(new Dictionary<Guid, UsageState>
        {
            [profile.Id] = UsageState.Available(UsageSnapshotMapper.Map(Response(80), DateTimeOffset.UtcNow.AddHours(-1)))
        }, default);
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var fetcher = new UsageFetcher(store, directory.File("config.toml"), directory.File("tmp"),
            async (_, _, quotaReceived, token) =>
            {
                quotaReceived!(Response(12), DateTimeOffset.UtcNow);
                received.TrySetResult();
                if (cancel) await Task.Delay(Timeout.InfiniteTimeSpan, token);
                throw new UsageException("Synthetic failure after quotas");
            });
        var coordinator = new UsageRefreshCoordinator(store, fetcher, cache);
        var refresh = coordinator.RefreshAllAsync(cancellation.Token);
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (cancel)
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh);
        }
        else await refresh;

        Assert.Equal(12, (await cache.LoadAsync(default))[profile.Id].Snapshot!.PrimaryUsedPercent);
    }

    [Fact]
    public async Task Automatic_refresh_skips_recent_token_attempt_preserves_its_error_and_manual_forces_tokens()
    {
        using var directory = new TestDirectory();
        var store = CreateStore(directory);
        var profile = await store.SaveAsync("Work", TestAuth.OAuth("a", "r", "work"), default);
        var cache = new UsageCache(directory.File("usage.json"), new AtomicFileStore());
        var oldTokenDate = DateTimeOffset.UtcNow.AddHours(-1);
        var attemptedAt = DateTimeOffset.UtcNow.AddSeconds(-20);
        var oldTokens = TokenUsageState.Failed("unsupported method", new TokenUsageSnapshot(new TokenUsageSummary(90), null, oldTokenDate));
        var previous = UsageSnapshotMapper.Map(Response(80), oldTokenDate) with { TokenUsage = oldTokens, TokenAttemptedAt = attemptedAt };
        await cache.SaveAsync(new Dictionary<Guid, UsageState> { [profile.Id] = UsageState.Available(previous) }, default);
        var tokenRequests = new List<bool>();
        var fetcher = new UsageFetcher(store, directory.File("config.toml"), directory.File("tmp"),
            (_, options, _, _) =>
            {
                tokenRequests.Add(options.IncludeTokens);
                return Task.FromResult(Response(12) with { TokenUsage = options.IncludeTokens ? Tokens(100) : null });
            });
        var coordinator = new UsageRefreshCoordinator(store, fetcher, cache);

        var automatic = (await coordinator.RefreshAllAsync(default, forceTokens: false))[profile.Id].Snapshot!;
        Assert.Equal(oldTokens, automatic.TokenUsage);
        Assert.Equal(attemptedAt, automatic.TokenAttemptedAt);
        var manual = (await coordinator.RefreshAllAsync(default))[profile.Id].Snapshot!;

        Assert.Equal(new[] { false, true }, tokenRequests);
        Assert.Equal(100, manual.TokenUsage!.Snapshot!.Summary!.LifetimeTokens);
        Assert.True(manual.TokenAttemptedAt > attemptedAt);
    }

    [Fact]
    public async Task Automatic_refresh_retries_after_cooldown_and_prioritizes_active_account()
    {
        using var directory = new TestDirectory();
        var store = CreateStore(directory);
        var first = await store.SaveAsync("First", TestAuth.OAuth("a", "r", "first"), default);
        var active = await store.SaveAsync("Active", TestAuth.OAuth("b", "s", "active"), default);
        await store.SetActiveProfileIdAsync(active.Id, default);
        var cache = new UsageCache(directory.File("usage.json"), new AtomicFileStore());
        var old = DateTimeOffset.UtcNow.AddMinutes(-6);
        await cache.SaveAsync(new Dictionary<Guid, UsageState>
        {
            [active.Id] = UsageState.Available(UsageSnapshotMapper.Map(Response(80), old) with { TokenAttemptedAt = old })
        }, default);
        var order = new List<Guid>();
        var tokenRequests = new List<bool>();
        var fetcher = new UsageFetcher(store, directory.File("config.toml"), directory.File("tmp"),
            (home, options, _, _) =>
            {
                order.Add(home.Contains(active.Id.ToString("N"), StringComparison.Ordinal) ? active.Id : first.Id);
                tokenRequests.Add(options.IncludeTokens);
                return Task.FromResult(Response(12) with { TokenUsage = Tokens(100) });
            });
        var coordinator = new UsageRefreshCoordinator(store, fetcher, cache, maximumConcurrency: 1);

        await coordinator.RefreshAllAsync(default, forceTokens: false);

        Assert.Equal(new[] { active.Id, first.Id }, order);
        Assert.All(tokenRequests, Assert.True);
    }

    [Fact]
    public async Task Failed_token_attempt_without_any_token_data_is_cooled_down_across_coordinator_instances()
    {
        using var directory = new TestDirectory();
        var store = CreateStore(directory);
        var profile = await store.SaveAsync("Work", TestAuth.OAuth("a", "r", "work"), default);
        var cache = new UsageCache(directory.File("usage.json"), new AtomicFileStore());
        var tokenRequests = new List<bool>();
        var fetcher = new UsageFetcher(store, directory.File("config.toml"), directory.File("tmp"),
            (_, options, _, _) =>
            {
                tokenRequests.Add(options.IncludeTokens);
                return Task.FromResult(Response(12) with
                {
                    TokenUsage = options.IncludeTokens ? TokenUsageState.Failed("unsupported method") : null
                });
            });

        await new UsageRefreshCoordinator(store, fetcher, cache).RefreshAllAsync(default, forceTokens: false);
        var afterFailure = (await cache.LoadAsync(default))[profile.Id].Snapshot!;
        Assert.NotNull(afterFailure.TokenAttemptedAt);
        Assert.Null(afterFailure.TokenUsage!.Snapshot);
        var next = (await new UsageRefreshCoordinator(store, fetcher, cache)
            .RefreshAllAsync(default, forceTokens: false))[profile.Id].Snapshot!;

        Assert.Equal(new[] { true, false }, tokenRequests);
        Assert.Equal(afterFailure.TokenAttemptedAt, next.TokenAttemptedAt);
        Assert.Equal(afterFailure.TokenUsage, next.TokenUsage);
    }

    private static AccountStore CreateStore(TestDirectory directory)
        => new(directory.File("data"), new AccountImportServiceTests.PassthroughProtector(), new AtomicFileStore());
    private static RateLimitsResponse Response(int used)
        => new(new RateLimitsSnapshot(new RateLimitWindow(used, 300, null), null, null, "pro"), null);
    private static TokenUsageState Tokens(long total)
        => TokenUsageState.Available(new TokenUsageSnapshot(new TokenUsageSummary(total), null, DateTimeOffset.UtcNow));
}
