using CodexSwitch.Core.Files;
using CodexSwitch.Core.Usage;

namespace CodexSwitch.Core.Tests.Usage;

public sealed class UsageCacheTests
{
    [Fact]
    public async Task Save_and_load_round_trip_without_authentication_fields()
    {
        using var directory = new TestDirectory();
        var path = directory.File("cache.json");
        var cache = new UsageCache(path, new AtomicFileStore());
        var profileId = Guid.NewGuid();
        var snapshot = new UsageSnapshot("pro", 5m, 10, DateTimeOffset.UnixEpoch, 300, 20, null, 10_080, 1, null, DateTimeOffset.UnixEpoch);

        await cache.SaveAsync(new Dictionary<Guid, UsageState> { [profileId] = UsageState.Available(snapshot) }, default);
        var loaded = await cache.LoadAsync(default);

        Assert.Equal(snapshot, loaded[profileId].Snapshot);
        var diskText = await File.ReadAllTextAsync(path);
        Assert.DoesNotContain("access_token", diskText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("refresh_token", diskText, StringComparison.OrdinalIgnoreCase);
    }
}
