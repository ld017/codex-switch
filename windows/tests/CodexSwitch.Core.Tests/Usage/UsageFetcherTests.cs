using CodexSwitch.Core.Auth;
using System.Text.Json;
using CodexSwitch.Core.Configuration;
using CodexSwitch.Core.Files;
using CodexSwitch.Core.Tests.Auth;
using CodexSwitch.Core.Usage;

namespace CodexSwitch.Core.Tests.Usage;

public sealed class UsageFetcherTests
{
    [Fact]
    public async Task FetchAsync_preserves_rotated_credentials_and_partial_quota_on_optional_token_cancellation()
    {
        using var directory = new TestDirectory();
        var store = new AccountStore(directory.File("data"), new AccountImportServiceTests.PassthroughProtector(), new AtomicFileStore());
        var profile = await store.SaveAsync("Work", TestAuth.OAuth("old-access", "old-refresh", "acct-1"), default);
        var rotated = TestAuth.OAuth("new-access", "new-refresh", "acct-1");
        var quotas = new RateLimitsResponse(new RateLimitsSnapshot(new RateLimitWindow(12, 300, null), null, null, "pro"), null);
        var quotaDate = DateTimeOffset.UtcNow;
        string? homeUsed = null;
        var fetcher = new UsageFetcher(store, directory.File("config.toml"), directory.File("tmp"), async (home, token) =>
        {
            homeUsed = home;
            await File.WriteAllBytesAsync(Path.Combine(home, "auth.json"), rotated, token);
            throw new PartialUsageCancellationException(quotas, quotaDate, new OperationCanceledException(token));
        });

        var error = await Assert.ThrowsAsync<PartialUsageCancellationException>(() => fetcher.FetchAsync(profile.Id, default));

        Assert.Same(quotas, error.Quotas);
        Assert.Equal(quotaDate, error.QuotasFetchedAt);
        Assert.Equal(rotated, await store.LoadAuthAsync(profile.Id, default));
        Assert.False(Directory.Exists(homeUsed));
    }

    [Fact]
    public async Task FetchAsync_carries_independent_token_activity_without_changing_its_server_read_timestamp()
    {
        using var directory = new TestDirectory();
        var store = new AccountStore(directory.File("data"), new AccountImportServiceTests.PassthroughProtector(), new AtomicFileStore());
        var profile = await store.SaveAsync("Work", TestAuth.OAuth("access", "refresh", "acct-1"), default);
        var tokenDate = DateTimeOffset.UtcNow.AddMinutes(-1);
        var activity = new TokenUsageSnapshot(new TokenUsageSummary(123_456_789),
            [new TokenUsageDailyBucket("2026-10-02", 25_000_000)], tokenDate);
        var fetcher = new UsageFetcher(store, directory.File("config.toml"), directory.File("tmp"),
            (_, _) => Task.FromResult(new RateLimitsResponse(new RateLimitsSnapshot(new RateLimitWindow(10, 300, null), null, null, "pro"), null)
                { TokenUsage = TokenUsageState.Available(activity) }));

        var result = await fetcher.FetchAsync(profile.Id, default);

        Assert.Equal(10, result.PrimaryUsedPercent);
        Assert.Equal(activity, result.TokenUsage!.Snapshot);
        Assert.True(result.FetchedAt > result.TokenUsage.Snapshot!.FetchedAt);
    }

    [Fact]
    public async Task FetchAsync_uses_isolated_home_forced_to_openai_and_cleans_it()
    {
        using var directory = new TestDirectory();
        var store = new AccountStore(directory.File("data"), new AccountImportServiceTests.PassthroughProtector(), new AtomicFileStore());
        var profile = await store.SaveAsync("Work", TestAuth.OAuth("access", "refresh", "acct-1"), default);
        var configPath = directory.File("config.toml");
        await File.WriteAllTextAsync(configPath, "model = \"gpt-5\"\nmodel_provider = \"sub2api\"\n");
        string? observedHome = null;
        var fetcher = new UsageFetcher(store, configPath, directory.File("tmp"), async (home, token) =>
        {
            observedHome = home;
            Assert.Equal("openai", ProviderConfigEditor.Read(await File.ReadAllTextAsync(Path.Combine(home, "config.toml"), token)).ToConfigValue());
            Assert.True(File.Exists(Path.Combine(home, "auth.json")));
            return new RateLimitsResponse(new RateLimitsSnapshot(new RateLimitWindow(10, 300, null), null, null, "pro"), null);
        });

        var result = await fetcher.FetchAsync(profile.Id, default);

        Assert.Equal(10, result.PrimaryUsedPercent);
        Assert.NotNull(observedHome);
        Assert.False(Directory.Exists(observedHome));
    }

    [Fact]
    public async Task FetchAsync_returns_usage_when_temporary_cleanup_cannot_finish()
    {
        using var directory = new TestDirectory();
        var store = new AccountStore(directory.File("data"), new AccountImportServiceTests.PassthroughProtector(), new AtomicFileStore());
        var profile = await store.SaveAsync("Work", TestAuth.OAuth("access", "refresh", "acct-1"), default);
        var configPath = directory.File("config.toml");
        await File.WriteAllTextAsync(configPath, "model = \"gpt-5\"\n");
        var cleaner = new RefusingCleaner();
        var fetcher = new UsageFetcher(store, configPath, directory.File("tmp"),
            (_, _) => Task.FromResult(new RateLimitsResponse(new RateLimitsSnapshot(new RateLimitWindow(17, 300, null), null, null, "pro"), null)), cleaner);

        var result = await fetcher.FetchAsync(profile.Id, default);

        Assert.Equal(17, result.PrimaryUsedPercent);
        Assert.Equal(1, cleaner.DeleteAttempts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FetchAsync_preserves_rotated_credentials_when_request_fails_or_is_cancelled(bool cancelled)
    {
        using var directory = new TestDirectory();
        var store = new AccountStore(directory.File("data"), new AccountImportServiceTests.PassthroughProtector(), new AtomicFileStore());
        var profile = await store.SaveAsync("Work", TestAuth.OAuth("old-access", "old-refresh", "acct-1"), default);
        var rotated = TestAuth.OAuth("new-access", "new-refresh", "acct-1");
        using var cancellation = new CancellationTokenSource();
        string? observedHome = null;
        var fetcher = new UsageFetcher(store, directory.File("config.toml"), directory.File("tmp"), async (home, token) =>
        {
            observedHome = home;
            await File.WriteAllBytesAsync(Path.Combine(home, "auth.json"), rotated, token);
            if (cancelled) { cancellation.Cancel(); token.ThrowIfCancellationRequested(); }
            throw new UsageException("Quota service unavailable");
        });

        if (cancelled) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fetcher.FetchAsync(profile.Id, cancellation.Token));
        else await Assert.ThrowsAsync<UsageException>(() => fetcher.FetchAsync(profile.Id, cancellation.Token));

        Assert.Equal(rotated, await store.LoadAuthAsync(profile.Id, default));
        Assert.False(Directory.Exists(observedHome));
    }

    [Fact]
    public async Task FetchAsync_does_not_overwrite_a_concurrent_login_with_its_own_rotation()
    {
        using var directory = new TestDirectory();
        var store = new AccountStore(directory.File("data"), new AccountImportServiceTests.PassthroughProtector(), new AtomicFileStore());
        var profile = await store.SaveAsync("Work", TestAuth.OAuth("old", "old-refresh", "acct-1"), default);
        var newLogin = TestAuth.OAuth("new-login", "new-login-refresh", "acct-1");
        var fetcher = new UsageFetcher(store, directory.File("config.toml"), directory.File("tmp"), async (home, token) =>
        {
            await store.ReplaceAuthAsync(profile.Id, newLogin, token);
            await File.WriteAllBytesAsync(Path.Combine(home, "auth.json"), TestAuth.OAuth("rotated", "rotated-refresh", "acct-1"), token);
            return new RateLimitsResponse(new RateLimitsSnapshot(new RateLimitWindow(9, 300, null), null, null, "pro"), null);
        });

        await fetcher.FetchAsync(profile.Id, default);

        Assert.Equal(newLogin, await store.LoadAuthAsync(profile.Id, default));
    }

    [Fact]
    public async Task FetchAsync_uses_latest_live_access_token_without_exposing_its_refresh_token_to_cli()
    {
        using var directory = new TestDirectory();
        var store = new AccountStore(directory.File("data"), new AccountImportServiceTests.PassthroughProtector(), new AtomicFileStore());
        var stored = TestAuth.OAuth("old-access", "old-refresh", "acct-1");
        var profile = await store.SaveAsync("Current", stored, default);
        var liveAuth = TestAuth.OAuth("latest-access", "live-refresh-must-never-reach-cli", "acct-1");
        var livePath = directory.File("live-auth.json");
        await File.WriteAllBytesAsync(livePath, liveAuth);
        var fetcher = new UsageFetcher(store, directory.File("config.toml"), directory.File("tmp"), async (home, token) =>
        {
            Assert.False(File.Exists(Path.Combine(home, "auth.json")));
            var manifest = await File.ReadAllTextAsync(Path.Combine(home, "usage-external-auth.json"), token);
            Assert.DoesNotContain("refresh", manifest);
            using var document = JsonDocument.Parse(manifest);
            Assert.Equal("latest-access", document.RootElement.GetProperty("accessToken").GetString());
            Assert.Equal("acct-1", document.RootElement.GetProperty("chatgptAccountId").GetString());
            return new RateLimitsResponse(new RateLimitsSnapshot(new RateLimitWindow(8, 300, null), null, null, "pro"), null);
        }, liveAuthPath: livePath);

        var result = await fetcher.FetchAsync(profile.Id, default);

        Assert.Equal(8, result.PrimaryUsedPercent);
        Assert.Equal(liveAuth, await File.ReadAllBytesAsync(livePath));
        Assert.Equal(stored, await store.LoadAuthAsync(profile.Id, default));
    }

    [Fact]
    public async Task FetchAsync_rejects_a_different_account_in_rotated_auth()
    {
        using var directory = new TestDirectory();
        var store = new AccountStore(directory.File("data"), new AccountImportServiceTests.PassthroughProtector(), new AtomicFileStore());
        var original = TestAuth.OAuth("original", "refresh", "acct-1");
        var profile = await store.SaveAsync("Work", original, default);
        var fetcher = new UsageFetcher(store, directory.File("config.toml"), directory.File("tmp"), async (home, token) =>
        {
            await File.WriteAllBytesAsync(Path.Combine(home, "auth.json"), TestAuth.OAuth("other", "other-refresh", "acct-2"), token);
            return new RateLimitsResponse(new RateLimitsSnapshot(null, null, null, "pro"), null);
        });

        await Assert.ThrowsAsync<UsageException>(() => fetcher.FetchAsync(profile.Id, default));

        Assert.Equal(original, await store.LoadAuthAsync(profile.Id, default));
    }

    [Fact]
    public async Task FetchAsync_keeps_recovery_credentials_when_store_write_fails_and_stale_cleanup_runs()
    {
        using var directory = new TestDirectory();
        var files = new RejectingAccountWritesStore();
        var store = new AccountStore(directory.File("data"), new AccountImportServiceTests.PassthroughProtector(), files);
        var profile = await store.SaveAsync("Work", TestAuth.OAuth("old", "old-refresh", "acct-1"), default);
        files.RejectAccountWrites = true;
        var rotated = TestAuth.OAuth("new", "new-refresh", "acct-1");
        string? observedHome = null;
        var fetcher = new UsageFetcher(store, directory.File("config.toml"), directory.File("tmp"), async (home, token) =>
        {
            observedHome = home;
            await File.WriteAllBytesAsync(Path.Combine(home, "auth.json"), rotated, token);
            return new RateLimitsResponse(new RateLimitsSnapshot(null, null, null, "pro"), null);
        });

        await Assert.ThrowsAsync<UsageException>(() => fetcher.FetchAsync(profile.Id, default));
        await new TemporaryDirectoryCleaner().CleanStaleAsync(directory.File("tmp"), TimeSpan.Zero, default);

        Assert.NotNull(observedHome);
        Assert.True(File.Exists(Path.Combine(observedHome, "auth-recovery-required")));
        Assert.Equal(rotated, await File.ReadAllBytesAsync(Path.Combine(observedHome, "auth.json")));
    }

    [Fact]
    public async Task FetchAsync_refuses_managed_refresh_when_active_live_credentials_are_missing()
    {
        using var directory = new TestDirectory();
        var store = new AccountStore(directory.File("data"), new AccountImportServiceTests.PassthroughProtector(), new AtomicFileStore());
        var profile = await store.SaveAsync("Current", TestAuth.OAuth("old", "refresh", "acct-1"), default);
        var fetcher = new UsageFetcher(store, directory.File("config.toml"), directory.File("tmp"),
            (_, _) => throw new InvalidOperationException("CLI must never start"), liveAuthPath: directory.File("missing-live-auth.json"));

        var error = await Assert.ThrowsAsync<UsageException>(() => fetcher.FetchAsync(profile.Id, default));

        Assert.Contains("Authentication required", error.Message);
    }

    private sealed class RejectingAccountWritesStore : IAtomicFileStore
    {
        private readonly AtomicFileStore _inner = new();
        public bool RejectAccountWrites { get; set; }
        public bool Exists(string path) => _inner.Exists(path);
        public byte[] ReadAllBytes(string path) => _inner.ReadAllBytes(path);
        public void Delete(string path) => _inner.Delete(path);
        public void WriteAtomically(string path, byte[] contents)
        {
            if (RejectAccountWrites && Path.GetExtension(path) == ".bin") throw new IOException("Simulated account store failure");
            _inner.WriteAtomically(path, contents);
        }
    }

    private sealed class RefusingCleaner : ITemporaryDirectoryCleaner
    {
        public int DeleteAttempts { get; private set; }
        public Task CleanStaleAsync(string root, TimeSpan minimumAge, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<bool> TryDeleteAsync(string path, CancellationToken cancellationToken)
        {
            DeleteAttempts++;
            return Task.FromResult(false);
        }
    }
}
