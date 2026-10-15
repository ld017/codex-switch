using System.Collections.Concurrent;
using CodexSwitch.Core.Auth;
using CodexSwitch.Core.Files;
using CodexSwitch.Core.Tests.Auth;
using CodexSwitch.Core.Usage;

namespace CodexSwitch.Core.Tests.Usage;

public sealed class UsageRefreshCoordinatorTests
{
    [Fact]
    public async Task RefreshAllAsync_preserves_received_quota_when_account_deadline_expires_during_optional_tokens()
    {
        using var directory = new TestDirectory();
        var store = CreateStore(directory);
        var profile = await store.SaveAsync("Work", TestAuth.OAuth("access", "refresh", "acct-1"), default);
        var cache = new UsageCache(directory.File("usage.json"), new AtomicFileStore());
        var quotaReceived = false;
        var fetcher = new UsageFetcher(store, directory.File("config.toml"), directory.File("tmp"), async (_, token) =>
        {
            var quotas = Response(12);
            quotaReceived = true;
            // The quota has arrived, then the optional token endpoint stalls past the account deadline.
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException error)
            {
                throw new PartialUsageCancellationException(quotas, DateTimeOffset.UtcNow, error);
            }
            return quotas;
        });
        var coordinator = new UsageRefreshCoordinator(store, fetcher, cache, accountTimeout: TimeSpan.FromMilliseconds(150));

        var result = (await coordinator.RefreshAllAsync(default))[profile.Id];

        Assert.True(quotaReceived);
        Assert.Equal(UsageStatus.Available, result.Status);
        Assert.Equal(12, result.Snapshot!.PrimaryUsedPercent);
        Assert.Equal(TokenUsageStatus.Error, result.Snapshot.TokenUsage!.Status);
    }

    [Fact]
    public async Task RefreshAllAsync_user_cancellation_during_optional_tokens_remains_cancelled()
    {
        using var directory = new TestDirectory();
        var store = CreateStore(directory);
        var profile = await store.SaveAsync("Work", TestAuth.OAuth("access", "refresh", "acct-1"), default);
        var cache = new UsageCache(directory.File("usage.json"), new AtomicFileStore());
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fetcher = new UsageFetcher(store, directory.File("config.toml"), directory.File("tmp"), async (_, token) =>
        {
            var quotas = Response(12);
            received.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException error)
            {
                throw new PartialUsageCancellationException(quotas, DateTimeOffset.UtcNow, error);
            }
            return quotas;
        });
        var coordinator = new UsageRefreshCoordinator(store, fetcher, cache);
        using var cancellation = new CancellationTokenSource();
        var refresh = coordinator.RefreshAllAsync(cancellation.Token);
        try { await received.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { cancellation.Cancel(); }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh);
        Assert.False((await cache.LoadAsync(default)).ContainsKey(profile.Id));
    }

    [Fact]
    public async Task RefreshAllAsync_deadline_during_tokens_keeps_original_token_cache_date()
    {
        using var directory = new TestDirectory();
        var store = CreateStore(directory);
        var profile = await store.SaveAsync("Work", TestAuth.OAuth("access", "refresh", "acct-1"), default);
        var cache = new UsageCache(directory.File("usage.json"), new AtomicFileStore());
        var originalTokenDate = DateTimeOffset.UtcNow.AddHours(-3);
        var tokens = new TokenUsageSnapshot(new TokenUsageSummary(120_000_000), null, originalTokenDate);
        var previous = UsageSnapshotMapper.Map(Response(33), originalTokenDate) with { TokenUsage = TokenUsageState.Available(tokens) };
        await cache.SaveAsync(new Dictionary<Guid, UsageState> { [profile.Id] = UsageState.Available(previous) }, default);
        var quotaDate = DateTimeOffset.UtcNow;
        var fetcher = new UsageFetcher(store, directory.File("config.toml"), directory.File("tmp"), async (_, token) =>
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException error)
            {
                throw new PartialUsageCancellationException(Response(12), quotaDate, error);
            }
            return Response(12);
        });
        var coordinator = new UsageRefreshCoordinator(store, fetcher, cache, accountTimeout: TimeSpan.FromMilliseconds(150));

        var result = (await coordinator.RefreshAllAsync(default))[profile.Id];

        Assert.Equal(UsageStatus.Available, result.Status);
        Assert.Equal(12, result.Snapshot!.PrimaryUsedPercent);
        Assert.Equal(quotaDate, result.Snapshot.FetchedAt);
        Assert.Equal(TokenUsageStatus.Error, result.Snapshot.TokenUsage!.Status);
        Assert.Equal(originalTokenDate, result.Snapshot.TokenUsage.Snapshot!.FetchedAt);
        Assert.Equal(120_000_000, result.Snapshot.TokenUsage.Snapshot.Summary!.LifetimeTokens);
    }

    [Fact]
    public async Task RefreshAllAsync_token_failure_keeps_original_token_timestamp_while_quota_becomes_current()
    {
        using var directory = new TestDirectory();
        var store = CreateStore(directory);
        var profile = await store.SaveAsync("Work", TestAuth.OAuth("access", "refresh", "acct-1"), default);
        var cache = new UsageCache(directory.File("usage.json"), new AtomicFileStore());
        var tokenDate = DateTimeOffset.UtcNow.AddHours(-3);
        var token = new TokenUsageSnapshot(new TokenUsageSummary(100_000_000), null, tokenDate);
        var previous = UsageSnapshotMapper.Map(Response(31), tokenDate) with { TokenUsage = TokenUsageState.Available(token) };
        await cache.SaveAsync(new Dictionary<Guid, UsageState> { [profile.Id] = UsageState.Available(previous) }, default);
        var fetcher = new UsageFetcher(store, directory.File("config.toml"), directory.File("tmp"),
            (_, _) => Task.FromResult(Response(12) with { TokenUsage = TokenUsageState.Failed("unsupported method") }));
        var coordinator = new UsageRefreshCoordinator(store, fetcher, cache);

        var result = (await coordinator.RefreshAllAsync(default))[profile.Id];

        Assert.Equal(UsageStatus.Available, result.Status);
        Assert.Equal(12, result.Snapshot!.PrimaryUsedPercent);
        Assert.True(result.Snapshot.FetchedAt > tokenDate);
        Assert.Equal(TokenUsageStatus.Error, result.Snapshot.TokenUsage!.Status);
        Assert.Equal(tokenDate, result.Snapshot.TokenUsage.Snapshot!.FetchedAt);
        Assert.Equal(100_000_000, result.Snapshot.TokenUsage.Snapshot.Summary!.LifetimeTokens);
        Assert.Contains("unsupported", result.Snapshot.TokenUsage.Message);
    }

    [Fact]
    public async Task RefreshAllAsync_bounds_parallelism_coalesces_reentry_and_reports_each_account()
    {
        using var directory = new TestDirectory();
        var store = CreateStore(directory);
        for (var index = 0; index < 5; index++)
            await store.SaveAsync($"Account {index}", TestAuth.OAuth("access", "refresh", $"acct-{index}"), default);
        var cache = new UsageCache(directory.File("usage.json"), new AtomicFileStore());
        var twoStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = 0;
        var maximumRunning = 0;
        var calls = 0;
        var fetcher = new UsageFetcher(store, directory.File("config.toml"), directory.File("tmp"), async (_, token) =>
        {
            Interlocked.Increment(ref calls);
            var count = Interlocked.Increment(ref running);
            lock (release) maximumRunning = Math.Max(maximumRunning, count);
            if (count == 2) twoStarted.TrySetResult();
            try { await release.Task.WaitAsync(token); }
            finally { Interlocked.Decrement(ref running); }
            return Response(20);
        });
        var coordinator = new UsageRefreshCoordinator(store, fetcher, cache, maximumConcurrency: 2);
        var updates = new ConcurrentQueue<UsageState>();

        var first = coordinator.RefreshAllAsync(default, (_, state) => updates.Enqueue(state));
        try
        {
            await twoStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var repeated = coordinator.RefreshAllAsync(default);
            Assert.Same(first, repeated);
            Assert.Equal(2, Volatile.Read(ref calls));
        }
        finally { release.TrySetResult(); }
        var result = await first;

        Assert.Equal(2, maximumRunning);
        Assert.Equal(5, calls);
        Assert.Equal(5, result.Count);
        Assert.Equal(5, updates.Count(state => state.Status == UsageStatus.Available));
        Assert.Equal(5, (await coordinator.LoadCachedAsync(default)).Count);
    }

    [Fact]
    public async Task RefreshAllAsync_times_out_one_account_without_losing_other_results_or_previous_snapshot()
    {
        using var directory = new TestDirectory();
        var store = CreateStore(directory);
        var slow = await store.SaveAsync("Slow", TestAuth.OAuth("a1", "r1", "slow"), default);
        var fast = await store.SaveAsync("Fast", TestAuth.OAuth("a2", "r2", "fast"), default);
        var cache = new UsageCache(directory.File("usage.json"), new AtomicFileStore());
        var previous = UsageSnapshotMapper.Map(Response(31), DateTimeOffset.UtcNow.AddMinutes(-10));
        await cache.SaveAsync(new Dictionary<Guid, UsageState> { [slow.Id] = UsageState.Available(previous) }, default);
        var fetcher = new UsageFetcher(store, directory.File("config.toml"), directory.File("tmp"), async (home, token) =>
        {
            if (home.Contains(slow.Id.ToString("N"), StringComparison.Ordinal)) await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Response(12);
        });
        var coordinator = new UsageRefreshCoordinator(store, fetcher, cache, accountTimeout: TimeSpan.FromMilliseconds(150));

        var result = await coordinator.RefreshAllAsync(default);

        Assert.Equal(UsageStatus.Error, result[slow.Id].Status);
        Assert.Contains("超时", result[slow.Id].Message);
        Assert.Equal(previous, result[slow.Id].Snapshot);
        Assert.Equal(UsageStatus.Available, result[fast.Id].Status);
    }

    [Fact]
    public async Task RefreshAllAsync_caller_cancellation_stays_cancelled_and_preserves_completed_accounts()
    {
        using var directory = new TestDirectory();
        var store = CreateStore(directory);
        var fast = await store.SaveAsync("Fast", TestAuth.OAuth("a1", "r1", "fast"), default);
        var slow = await store.SaveAsync("Slow", TestAuth.OAuth("a2", "r2", "slow"), default);
        var cache = new UsageCache(directory.File("usage.json"), new AtomicFileStore());
        var fastFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fetcher = new UsageFetcher(store, directory.File("config.toml"), directory.File("tmp"), async (home, token) =>
        {
            if (home.Contains(slow.Id.ToString("N"), StringComparison.Ordinal)) await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Response(14);
        });
        var coordinator = new UsageRefreshCoordinator(store, fetcher, cache);
        using var cancellation = new CancellationTokenSource();
        var refresh = coordinator.RefreshAllAsync(cancellation.Token, (id, state) =>
        {
            if (id == fast.Id && state.Status == UsageStatus.Available) fastFinished.TrySetResult();
        });
        try { await fastFinished.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { cancellation.Cancel(); }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh);
        var result = await cache.LoadAsync(default);
        Assert.Equal(UsageStatus.Available, result[fast.Id].Status);
        Assert.False(result.ContainsKey(slow.Id));
        Assert.Empty(Directory.GetDirectories(directory.File("tmp")));
    }

    [Fact]
    public async Task RefreshAllAsync_keeps_authentication_errors_distinct_and_redacted()
    {
        using var directory = new TestDirectory();
        var store = CreateStore(directory);
        var profile = await store.SaveAsync("Work", TestAuth.OAuth("a1", "r1", "acct-1"), default);
        var fetcher = new UsageFetcher(store, directory.File("config.toml"), directory.File("tmp"),
            (_, _) => throw new UsageException("401 Unauthorized {\"access_token\":\"test-secret\"}"));
        var coordinator = new UsageRefreshCoordinator(store, fetcher, new UsageCache(directory.File("usage.json"), new AtomicFileStore()));

        var result = await coordinator.RefreshAllAsync(default);

        Assert.Equal(UsageStatus.AuthenticationRequired, result[profile.Id].Status);
        Assert.DoesNotContain("test-secret", result[profile.Id].Message);
    }

    private static AccountStore CreateStore(TestDirectory directory)
        => new(directory.File("data"), new AccountImportServiceTests.PassthroughProtector(), new AtomicFileStore());

    private static RateLimitsResponse Response(int used)
        => new(new RateLimitsSnapshot(new RateLimitWindow(used, 300, null), null, null, "pro"), null);
}
