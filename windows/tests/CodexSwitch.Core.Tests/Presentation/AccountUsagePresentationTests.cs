using CodexSwitch.Core.Presentation;
using CodexSwitch.Core.Usage;

namespace CodexSwitch.Core.Tests.Presentation;

public sealed class AccountUsagePresentationTests
{
    [Theory]
    [InlineData(20, UsageLevel.Healthy)]
    [InlineData(75, UsageLevel.Warning)]
    [InlineData(95, UsageLevel.Critical)]
    public void From_maps_usage_percentage_to_visual_level(int percent, UsageLevel expected)
    {
        var snapshot = Snapshot(primary: percent);

        var presentation = AccountUsagePresentation.From(snapshot, DateTimeOffset.UnixEpoch);

        Assert.Equal(expected, presentation.Primary.Level);
        Assert.Equal(percent, presentation.Primary.Percent);
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

    private static UsageSnapshot Snapshot(int primary) => new(
        "plus", 0, primary, null, 300, 66, null, 10_080, 0, null, DateTimeOffset.UnixEpoch);
}
