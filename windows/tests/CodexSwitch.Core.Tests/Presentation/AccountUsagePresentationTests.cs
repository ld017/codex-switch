using CodexSwitch.Core.Presentation;
using CodexSwitch.Core.Usage;

namespace CodexSwitch.Core.Tests.Presentation;

public sealed class AccountUsagePresentationTests
{
    [Theory]
    [InlineData(20, UsageLevel.Healthy)]
    [InlineData(75, UsageLevel.Warning)]
    [InlineData(95, UsageLevel.Critical)]
    public void From_shows_remaining_quota_and_marks_low_remaining_quota_as_critical(int percent, UsageLevel expected)
    {
        var snapshot = Snapshot(primary: percent);

        var presentation = AccountUsagePresentation.From(snapshot, DateTimeOffset.UnixEpoch);

        Assert.Equal(expected, presentation.Primary.Level);
        Assert.Equal(100 - percent, presentation.Primary.Percent);
        Assert.Equal($"{100 - percent}%", presentation.Primary.PercentText);
    }

    [Fact]
    public void From_formats_reset_time_and_reset_cards()
    {
        var localOffset = TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 9, 17));
        var now = new DateTimeOffset(2026, 9, 17, 8, 0, 0, localOffset);
        var snapshot = Snapshot(primary: 20) with
        {
            PrimaryResetAt = now.AddHours(2).AddMinutes(44),
            ResetCreditsAvailable = 3,
            NextResetCreditExpiresAt = new DateTimeOffset(2026, 9, 21, 6, 46, 0, localOffset),
        };

        var presentation = AccountUsagePresentation.From(snapshot, now);

        Assert.Equal("2时44分", presentation.Primary.ResetText);
        Assert.Equal("重置卡 3 张", presentation.ResetCreditsText);
        Assert.Equal("最近到期 9月21日 06:46", presentation.ResetCreditExpiryText);
    }

    [Fact]
    public void From_uses_placeholders_when_snapshot_is_missing()
    {
        var presentation = AccountUsagePresentation.From(null, DateTimeOffset.UnixEpoch);

        Assert.Equal("—", presentation.Primary.PercentText);
        Assert.Equal("尚未刷新", presentation.StatusText);
    }

    [Fact]
    public void From_does_not_claim_a_past_reset_is_about_to_happen_or_reset_cached_usage()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = Snapshot(primary: 95) with { FetchedAt = now.AddDays(-1), PrimaryResetAt = now.AddHours(-1) };

        var presentation = AccountUsagePresentation.From(snapshot, now);

        Assert.Equal("待刷新", presentation.Primary.ResetText);
        Assert.Equal(5, presentation.Primary.Percent);
        Assert.Contains(snapshot.FetchedAt.LocalDateTime.ToString("M/d HH:mm"), presentation.StatusText);
    }

    [Fact]
    public void From_keeps_unknown_percent_distinct_from_zero_remaining()
    {
        var snapshot = Snapshot(primary: 20) with { PrimaryUsedPercent = null, SecondaryUsedPercent = 100 };

        var presentation = AccountUsagePresentation.From(snapshot, DateTimeOffset.UnixEpoch);

        Assert.Null(presentation.Primary.Percent);
        Assert.Equal("—", presentation.Primary.PercentText);
        Assert.Equal(UsageLevel.Unknown, presentation.Primary.Level);
        Assert.Equal("0%", presentation.Secondary.PercentText);
        Assert.Equal(UsageLevel.Critical, presentation.Secondary.Level);
    }

    [Theory]
    [InlineData(UsageStatus.Error, "刷新失败")]
    [InlineData(UsageStatus.Refreshing, "刷新中")]
    [InlineData(UsageStatus.AuthenticationRequired, "需重新登录")]
    public void FromState_marks_retained_values_as_cache_after_failed_or_pending_refresh(UsageStatus status, string message)
    {
        var snapshot = Snapshot(primary: 20);

        var presentation = AccountUsagePresentation.FromState(new UsageState(status, snapshot, "error"), DateTimeOffset.UnixEpoch);

        Assert.Equal(80, presentation.Primary.Percent);
        Assert.Contains(message, presentation.StatusText);
        Assert.Contains("缓存", presentation.StatusText);
    }

    [Fact]
    public void From_displays_unlimited_credit_state_without_fabricating_zero_balance()
    {
        var snapshot = Snapshot(primary: 20) with { CreditsRemaining = null, CreditsUnlimited = true };

        Assert.Equal("不限 cr", AccountUsagePresentation.From(snapshot, DateTimeOffset.UnixEpoch).CreditsText);
    }

    [Fact]
    public void From_displays_server_token_totals_in_hundred_millions_without_estimation_from_quota()
    {
        var now = DateTimeOffset.Now;
        var snapshot = Snapshot(primary: 20) with
        {
            TokenUsage = TokenUsageState.Available(new TokenUsageSnapshot(
                new TokenUsageSummary(123_456_789),
                [new TokenUsageDailyBucket(now.ToString("yyyy-MM-dd"), 200_000_000)], now)),
        };

        var presentation = AccountUsagePresentation.From(snapshot, now);

        Assert.Equal("1.23 亿", presentation.TokenUsage.LifetimeText);
        Assert.Equal("2.00 亿", presentation.TokenUsage.MonthText);
        Assert.Contains("更新于", presentation.TokenUsage.StatusText);
        Assert.Contains("本月", presentation.TokenUsage.DetailText);
        Assert.False(presentation.TokenUsage.HasError);
    }

    [Fact]
    public void From_uses_unknown_token_values_when_only_rate_limit_percentages_are_known()
    {
        var presentation = AccountUsagePresentation.From(Snapshot(primary: 20), DateTimeOffset.UnixEpoch);

        Assert.Equal(80, presentation.Primary.Percent);
        Assert.Equal("—", presentation.TokenUsage.LifetimeText);
        Assert.Equal("—", presentation.TokenUsage.MonthText);
        Assert.Contains("尚未获取", presentation.TokenUsage.StatusText);
    }

    [Fact]
    public void From_keeps_token_failure_independent_of_successful_quota_and_marks_retained_tokens_as_cache()
    {
        var now = DateTimeOffset.Now;
        var cachedAt = now.AddHours(-1);
        var snapshot = Snapshot(primary: 20) with
        {
            TokenUsage = TokenUsageState.Failed("Synthetic token request failed", new TokenUsageSnapshot(
                new TokenUsageSummary(1), [new TokenUsageDailyBucket(now.ToString("yyyy-MM-dd"), 0)], cachedAt)),
        };

        var presentation = AccountUsagePresentation.From(snapshot, now);

        Assert.Equal(80, presentation.Primary.Percent);
        Assert.Equal("<0.01 亿", presentation.TokenUsage.LifetimeText);
        Assert.Equal("0.00 亿", presentation.TokenUsage.MonthText);
        Assert.Contains("刷新失败", presentation.TokenUsage.StatusText);
        Assert.Contains("缓存", presentation.TokenUsage.StatusText);
        Assert.Contains(cachedAt.LocalDateTime.ToString("M/d HH:mm"), presentation.TokenUsage.StatusText);
        Assert.Contains("Synthetic token request failed", presentation.TokenUsage.DetailText);
        Assert.True(presentation.TokenUsage.HasError);
    }

    [Fact]
    public void From_shows_unknown_tokens_after_failure_without_previous_token_snapshot()
    {
        var snapshot = Snapshot(primary: 20) with { TokenUsage = TokenUsageState.Failed("Synthetic token error") };

        var presentation = AccountUsagePresentation.From(snapshot, DateTimeOffset.Now);

        Assert.Equal("—", presentation.TokenUsage.LifetimeText);
        Assert.Equal("—", presentation.TokenUsage.MonthText);
        Assert.Contains("刷新失败", presentation.TokenUsage.StatusText);
        Assert.DoesNotContain("缓存", presentation.TokenUsage.StatusText);
        Assert.True(presentation.TokenUsage.HasError);
    }

    [Theory]
    [InlineData(UsageStatus.Error)]
    [InlineData(UsageStatus.Refreshing)]
    [InlineData(UsageStatus.AuthenticationRequired)]
    public void FromState_marks_retained_token_values_as_cache_when_quota_refresh_does_not_complete(UsageStatus status)
    {
        var now = DateTimeOffset.Now;
        var snapshot = Snapshot(primary: 20) with
        {
            TokenUsage = TokenUsageState.Available(new TokenUsageSnapshot(new TokenUsageSummary(100_000_000), null, now.AddHours(-1))),
        };

        var presentation = AccountUsagePresentation.FromState(new UsageState(status, snapshot, "Synthetic quota error"), now);

        Assert.Equal("1.00 亿", presentation.TokenUsage.LifetimeText);
        Assert.Contains("缓存", presentation.TokenUsage.StatusText);
        Assert.Equal("—", presentation.TokenUsage.MonthText);
    }

    private static UsageSnapshot Snapshot(int primary) => new(
        "plus", 0, primary, null, 300, 66, null, 10_080, 0, null, DateTimeOffset.UnixEpoch);
}
