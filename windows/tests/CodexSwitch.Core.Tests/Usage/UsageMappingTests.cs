using System.Text.Json;
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
        Assert.Equal(88, snapshot.PrimaryRemainingPercent);
        Assert.Equal(44, snapshot.SecondaryRemainingPercent);
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

    [Fact]
    public void Map_prefers_codex_bucket_over_legacy_and_other_model_buckets()
    {
        var response = Deserialize("""
            {
              "rateLimits": { "primary": { "usedPercent": 99 }, "planType": "old" },
              "rateLimitsByLimitId": {
                "codex_other": { "primary": { "usedPercent": 80 } },
                "codex": {
                  "limitId": "codex", "limitName": "Codex", "planType": "plus",
                  "primary": { "usedPercent": 25, "windowDurationMins": 300, "resetsAt": 2000000000 }
                }
              }
            }
            """);

        var snapshot = UsageSnapshotMapper.Map(response, DateTimeOffset.UnixEpoch);

        Assert.Equal(25, snapshot.PrimaryUsedPercent);
        Assert.Equal(75, snapshot.PrimaryRemainingPercent);
        Assert.Equal("plus", snapshot.PlanType);
        Assert.Equal("codex", snapshot.LimitId);
        Assert.Equal("Codex", snapshot.LimitName);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(2000000000), snapshot.PrimaryResetAt);
    }

    [Fact]
    public void Map_does_not_label_an_unrelated_model_bucket_as_general_codex_quota()
    {
        var response = Deserialize("""
            {
              "rateLimits": { "limitId": "codex_other", "primary": { "usedPercent": 99 } },
              "rateLimitsByLimitId": { "codex_other": { "primary": { "usedPercent": 20 } } }
            }
            """);

        Assert.Throws<UsageException>(() => UsageSnapshotMapper.Map(response, DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void Map_does_not_guess_an_unidentified_legacy_bucket_when_multi_bucket_data_is_ambiguous()
    {
        var response = Deserialize("""
            {
              "rateLimits": { "primary": { "usedPercent": 99 } },
              "rateLimitsByLimitId": { "codex_other": { "primary": { "usedPercent": 20 } } }
            }
            """);

        Assert.Throws<UsageException>(() => UsageSnapshotMapper.Map(response, DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void Map_supports_a_multi_bucket_response_without_a_legacy_view()
    {
        var response = Deserialize("""
            { "rateLimitsByLimitId": { "codex": { "primary": { "usedPercent": 10 } } } }
            """);

        var snapshot = UsageSnapshotMapper.Map(response, DateTimeOffset.UnixEpoch);

        Assert.Equal(90, snapshot.PrimaryRemainingPercent);
    }

    [Fact]
    public void Map_preserves_missing_percent_and_reset_credit_count_as_unknown()
    {
        var response = Deserialize("""
            { "rateLimits": { "primary": { "resetsAt": 2000000000 } }, "rateLimitResetCredits": { "credits": null } }
            """);

        var snapshot = UsageSnapshotMapper.Map(response, DateTimeOffset.UnixEpoch);

        Assert.Null(snapshot.PrimaryUsedPercent);
        Assert.Null(snapshot.PrimaryRemainingPercent);
        Assert.Null(snapshot.ResetCreditsAvailable);
        Assert.Null(snapshot.NextResetCreditExpiresAt);
    }

    [Fact]
    public void Map_accepts_authoritative_reset_credit_count_with_null_or_capped_details()
    {
        var countOnly = Deserialize("""
            { "rateLimitResetCredits": { "availableCount": 3, "credits": null } }
            """);
        var cappedDetails = Deserialize("""
            { "rateLimitResetCredits": { "availableCount": 3, "credits": [] } }
            """);

        Assert.Equal(3, UsageSnapshotMapper.Map(countOnly, DateTimeOffset.UnixEpoch).ResetCreditsAvailable);
        Assert.Equal(3, UsageSnapshotMapper.Map(cappedDetails, DateTimeOffset.UnixEpoch).ResetCreditsAvailable);
    }

    [Theory]
    [InlineData(-5, 0, 100)]
    [InlineData(101, 100, 0)]
    public void Map_bounds_percentages_before_deriving_remaining(double usage, int used, int remaining)
    {
        var response = new RateLimitsResponse(new RateLimitsSnapshot(new RateLimitWindow(usage, 300, null), null, null, null), null);

        var snapshot = UsageSnapshotMapper.Map(response, DateTimeOffset.UnixEpoch);

        Assert.Equal(used, snapshot.PrimaryUsedPercent);
        Assert.Equal(remaining, snapshot.PrimaryRemainingPercent);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Map_does_not_convert_invalid_percentages_to_available_quota(double usage)
    {
        var response = new RateLimitsResponse(new RateLimitsSnapshot(new RateLimitWindow(usage, 300, null), null, null, null), null);

        Assert.Null(UsageSnapshotMapper.Map(response, DateTimeOffset.UnixEpoch).PrimaryRemainingPercent);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(long.MaxValue)]
    public void Map_ignores_invalid_unix_times_without_discarding_known_percentages(long resetsAt)
    {
        var response = new RateLimitsResponse(new RateLimitsSnapshot(new RateLimitWindow(10, 0, resetsAt), null, null, null), null);

        var snapshot = UsageSnapshotMapper.Map(response, DateTimeOffset.UnixEpoch);

        Assert.Equal(90, snapshot.PrimaryRemainingPercent);
        Assert.Null(snapshot.PrimaryResetAt);
        Assert.Null(snapshot.PrimaryWindowDurationMinutes);
    }

    [Fact]
    public void Map_keeps_unlimited_credits_separate_from_unknown_numeric_balance()
    {
        var response = Deserialize("""
            { "rateLimits": { "credits": { "hasCredits": true, "unlimited": true, "balance": null } } }
            """);

        var snapshot = UsageSnapshotMapper.Map(response, DateTimeOffset.UnixEpoch);

        Assert.True(snapshot.CreditsUnlimited);
        Assert.Null(snapshot.CreditsRemaining);
        Assert.Null(snapshot.PrimaryRemainingPercent);
    }

    private static RateLimitsResponse Deserialize(string json) =>
        JsonSerializer.Deserialize<RateLimitsResponse>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
}
