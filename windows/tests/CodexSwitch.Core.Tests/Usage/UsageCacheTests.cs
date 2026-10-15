using CodexSwitch.Core.Files;
using CodexSwitch.Core.Usage;

namespace CodexSwitch.Core.Tests.Usage;

public sealed class UsageCacheTests
{
    [Fact]
    public async Task Token_usage_and_independent_cache_timestamp_round_trip()
    {
        using var directory = new TestDirectory();
        var cache = new UsageCache(directory.File("cache.json"), new AtomicFileStore());
        var id = Guid.NewGuid();
        var tokenDate = DateTimeOffset.Parse("2026-10-13T09:00:00+00:00");
        var token = new TokenUsageSnapshot(new TokenUsageSummary(9_000_000_000L),
            [new TokenUsageDailyBucket("2026-10-13", 7_000_000)], tokenDate);
        var quota = new UsageSnapshot("plus", null, 10, null, 300, 20, null, 10080, null, null, DateTimeOffset.UtcNow,
            TokenUsage: TokenUsageState.Failed("unsupported endpoint", token));

        await cache.SaveAsync(new Dictionary<Guid, UsageState> { [id] = UsageState.Available(quota) }, default);
        var result = (await cache.LoadAsync(default))[id];

        Assert.Equal(UsageStatus.Available, result.Status);
        Assert.Equal(TokenUsageStatus.Error, result.Snapshot!.TokenUsage!.Status);
        Assert.Equal(tokenDate, result.Snapshot.TokenUsage.Snapshot!.FetchedAt);
        Assert.Equal(9_000_000_000L, result.Snapshot.TokenUsage.Snapshot.Summary!.LifetimeTokens);
        Assert.Equal(7_000_000, Assert.Single(result.Snapshot.TokenUsage.Snapshot.DailyUsageBuckets!).Tokens);
    }

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
        Assert.Equal(90, loaded[profileId].Snapshot!.PrimaryRemainingPercent);
        var diskText = await File.ReadAllTextAsync(path);
        Assert.Contains("primaryUsedPercent", diskText);
        Assert.DoesNotContain("primaryRemainingPercent", diskText);
        Assert.DoesNotContain("access_token", diskText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("refresh_token", diskText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Load_old_cache_keeps_used_percent_and_does_not_turn_a_failed_refresh_into_current_data()
    {
        using var directory = new TestDirectory();
        var path = directory.File("old-cache.json");
        await File.WriteAllTextAsync(path, """
            {
              "11111111-1111-1111-1111-111111111111": {
                "status": 3, "message": "refresh failed",
                "snapshot": {
                  "planType": "plus", "creditsRemaining": null,
                  "primaryUsedPercent": 99, "primaryResetAt": null, "primaryWindowDurationMinutes": 300,
                  "secondaryUsedPercent": 44, "secondaryResetAt": null, "secondaryWindowDurationMinutes": 10080,
                  "resetCreditsAvailable": 2, "nextResetCreditExpiresAt": null, "fetchedAt": "2026-10-14T10:00:00+00:00"
                }
              }
            }
            """);

        var loaded = await new UsageCache(path, new AtomicFileStore()).LoadAsync(default);
        var state = loaded[Guid.Parse("11111111-1111-1111-1111-111111111111")];

        Assert.Equal(UsageStatus.Error, state.Status);
        Assert.Equal(99, state.Snapshot!.PrimaryUsedPercent);
        Assert.Equal(1, state.Snapshot.PrimaryRemainingPercent);
        Assert.Equal(56, state.Snapshot.SecondaryRemainingPercent);
        Assert.Equal(DateTimeOffset.Parse("2026-10-14T10:00:00+00:00"), state.Snapshot.FetchedAt);
        Assert.Null(state.Snapshot.LimitId);
        Assert.Null(state.Snapshot.CreditsUnlimited);
        Assert.Null(state.Snapshot.TokenUsage);
    }
}
