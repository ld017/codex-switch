using System.Globalization;
using CodexSwitch.Core.Usage;

namespace CodexSwitch.Core.Presentation;

public enum UsageLevel
{
    Unknown,
    Healthy,
    Warning,
    Critical,
}

public sealed record UsageBarPresentation(
    int? Percent,
    string PercentText,
    string ResetText,
    UsageLevel Level);

public sealed record AccountUsagePresentation(
    string PlanText,
    string CreditsText,
    UsageBarPresentation Primary,
    UsageBarPresentation Secondary,
    string ResetCreditsText,
    string ResetCreditExpiryText,
    string StatusText)
{
    public static AccountUsagePresentation From(UsageSnapshot? snapshot, DateTimeOffset now)
    {
        if (snapshot is null)
        {
            var empty = Bar(null, null, now);
            return new AccountUsagePresentation("—", "— cr", empty, empty, "重置卡 —", string.Empty, "尚未刷新");
        }

        var credits = snapshot.CreditsRemaining is { } balance
            ? $"{balance:0.##} cr"
            : "— cr";
        var resetCards = snapshot.ResetCreditsAvailable is { } count
            ? $"重置卡 {count} 张"
            : "重置卡 —";
        var expiryText = snapshot.NextResetCreditExpiresAt is { } expires
            ? $"最近到期 {expires.LocalDateTime:M月d日 HH:mm}"
            : string.Empty;

        return new AccountUsagePresentation(
            string.IsNullOrWhiteSpace(snapshot.PlanType) ? "—" : snapshot.PlanType,
            credits,
            Bar(snapshot.PrimaryUsedPercent, snapshot.PrimaryResetAt, now),
            Bar(snapshot.SecondaryUsedPercent, snapshot.SecondaryResetAt, now),
            resetCards,
            expiryText,
            $"更新于 {snapshot.FetchedAt.LocalDateTime:HH:mm}");
    }

    private static UsageBarPresentation Bar(int? percent, DateTimeOffset? resetAt, DateTimeOffset now)
    {
        var level = percent switch
        {
            null => UsageLevel.Unknown,
            >= 90 => UsageLevel.Critical,
            >= 70 => UsageLevel.Warning,
            _ => UsageLevel.Healthy,
        };
        return new UsageBarPresentation(
            percent,
            percent is null ? "—" : $"{percent}%",
            FormatDuration(resetAt, now),
            level);
    }

    private static string FormatDuration(DateTimeOffset? resetAt, DateTimeOffset now)
    {
        if (resetAt is null)
        {
            return "—";
        }

        var remaining = resetAt.Value - now;
        if (remaining <= TimeSpan.Zero)
        {
            return "即将重置";
        }

        if (remaining.TotalDays >= 1)
        {
            return $"{(int)remaining.TotalDays}天{remaining.Hours}时";
        }

        return $"{remaining.Hours}时{remaining.Minutes}分";
    }
}
