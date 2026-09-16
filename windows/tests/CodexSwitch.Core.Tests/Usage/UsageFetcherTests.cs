using CodexSwitch.Core.Auth;
using CodexSwitch.Core.Configuration;
using CodexSwitch.Core.Files;
using CodexSwitch.Core.Tests.Auth;
using CodexSwitch.Core.Usage;

namespace CodexSwitch.Core.Tests.Usage;

public sealed class UsageFetcherTests
{
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
