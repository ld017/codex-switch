using CodexSwitch.Core.Auth;
using CodexSwitch.Core.Files;
using CodexSwitch.Core.Tests.Auth;
using CodexSwitch.Core.Usage;

namespace CodexSwitch.Core.Tests.Usage;

public sealed class UsageRecoveryBoundaryTests
{
    [Fact]
    public async Task Interrupted_atomic_rotation_is_preserved_even_when_final_auth_is_unchanged()
    {
        using var directory = new TestDirectory();
        var store = new AccountStore(directory.File("data"), new AccountImportServiceTests.PassthroughProtector(), new AtomicFileStore());
        var old = TestAuth.OAuth("old-access", "old-refresh", "synthetic");
        var profile = await store.SaveAsync("Synthetic", old, default);
        string? homeUsed = null;
        var fetcher = new UsageFetcher(store, directory.File("config.toml"), directory.File("tmp"), async (home, token) =>
        {
            homeUsed = home;
            await File.WriteAllBytesAsync(Path.Combine(home, ".auth.json.interrupted.tmp"), TestAuth.OAuth("new-access", "new-refresh", "synthetic"), token);
            return new RateLimitsResponse(new RateLimitsSnapshot(new RateLimitWindow(12, 300, null), null, null, "plus"), null);
        });

        await Assert.ThrowsAsync<UsageException>(() => fetcher.FetchAsync(profile.Id, default));

        Assert.Equal(old, await store.LoadAuthAsync(profile.Id, default));
        Assert.True(Directory.Exists(homeUsed));
        Assert.True(TemporaryHomeLease.HasRecoveryRequired(homeUsed!));
        Assert.True(File.Exists(Path.Combine(homeUsed!, ".auth.json.interrupted.tmp")));
    }
}
