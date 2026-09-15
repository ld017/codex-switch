using CodexSwitch.Core.Usage;

namespace CodexSwitch.Core.Tests.Usage;

public sealed class UsageMappingTests
{
    [Fact]
    public void Map_uses_earliest_available_reset_credit_expiration()
    {
        var response = new RateLimitsResponse(
            new RateLimitsSnapshot(
                new RateLimitWindow(12.4, 300, 100),
                new RateLimitWindow(55.6, 10_080, 200),
                new CreditsSnapshot(true, false, "1,234.5"),
                "pro"),
            new ResetCreditsSnapshot(2, [
                new ResetCredit("expired", 100),
                new ResetCredit("available", 300),
                new ResetCredit("available", 200),
            ]));

        var snapshot = UsageSnapshotMapper.Map(response, DateTimeOffset.UnixEpoch);

        Assert.Equal(12, snapshot.PrimaryUsedPercent);
        Assert.Equal(56, snapshot.SecondaryUsedPercent);
        Assert.Equal(1234.5m, snapshot.CreditsRemaining);
        Assert.Equal(2, snapshot.ResetCreditsAvailable);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(200), snapshot.NextResetCreditExpiresAt);
    }

    [Fact]
    public void Map_rejects_response_without_usage_or_credit_data()
    {
        var response = new RateLimitsResponse(new RateLimitsSnapshot(null, null, null, null), null);

        Assert.Throws<UsageException>(() => UsageSnapshotMapper.Map(response, DateTimeOffset.UtcNow));
    }
}
