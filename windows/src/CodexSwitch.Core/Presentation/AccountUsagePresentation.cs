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

public sealed record TokenUsagePresentation(
    string LifetimeText,
    string MonthText,
    string StatusText,
    string DetailText,
    bool HasError)
{
    public static TokenUsagePresentation Empty { get; } = new("—", "—", "Token 尚未获取", "Token 尚未获取", false);

    public static TokenUsagePresentation From(TokenUsageState? state, DateTimeOffset now)
    {
        if (state is null) return Empty;
        var snapshot = state.Snapshot;
        var month = TokenUsageAggregate.MonthUsage(snapshot, now);
        var lifetimeText = TokenUsageAggregate.FormatHundredMillions(snapshot?.Summary?.LifetimeTokens);
        var monthText = TokenUsageAggregate.FormatHundredMillions(month?.Tokens);
        var fetchedAt = snapshot?.FetchedAt.LocalDateTime.ToString("M/d HH:mm");
        var hasError = state.Status == TokenUsageStatus.Error;
        var status = hasError
            ? "Token 刷新失败" + (fetchedAt is null ? string.Empty : $" · 缓存 {fetchedAt}")
            : fetchedAt is null ? "Token 尚未获取" : $"Token 更新于 {fetchedAt}";
        var monthDetail = month is null
            ? "本月 Token：服务未提供本月有效日数据。"
            : $"本月 Token：{month.StartDate:M月d日}至{month.ThroughDate:M月d日}的日数据合计。";
        static string Exact(long? count) => count is >= 0 ? count.Value.ToString("N0", CultureInfo.InvariantCulture) : "未获取";
        var detail = $"累计 Token：{Exact(snapshot?.Summary?.LifetimeTokens)}，使用服务端累计值。\n本月 Token：{Exact(month?.Tokens)}。\n{monthDetail}\n{status}";
        if (!string.IsNullOrWhiteSpace(state.Message)) detail += $"\n{state.Message}";
        return new TokenUsagePresentation(lifetimeText, monthText, status, detail, hasError);
    }

    public TokenUsagePresentation AsCached(DateTimeOffset fetchedAt, UsageStatus status)
    {
        var reason = status == UsageStatus.Refreshing ? "Token 刷新中" : "Token 未更新";
        var statusText = $"{reason} · 缓存 {fetchedAt.LocalDateTime:M/d HH:mm}";
        return this with
        {
            StatusText = statusText,
            DetailText = $"{DetailText}\n{statusText}",
        };
    }
}

public sealed record AccountUsagePresentation(
    string PlanText,
    string CreditsText,
    UsageBarPresentation Primary,
    UsageBarPresentation Secondary,
    string ResetCreditsText,
    string ResetCreditExpiryText,
    string StatusText)
{
    public TokenUsagePresentation TokenUsage { get; init; } = TokenUsagePresentation.Empty;

    public static AccountUsagePresentation From(UsageSnapshot? snapshot, DateTimeOffset now)
    {
        if (snapshot is null)
        {
            var empty = Bar(null, null, now);
            return new AccountUsagePresentation("—", "— cr", empty, empty, "重置卡 —", string.Empty, "尚未刷新");
        }

        var credits = snapshot.CreditsUnlimited == true ? "不限 cr" : snapshot.CreditsRemaining is { } balance
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
            Bar(snapshot.PrimaryRemainingPercent, snapshot.PrimaryResetAt, now),
            Bar(snapshot.SecondaryRemainingPercent, snapshot.SecondaryResetAt, now),
            resetCards,
            expiryText,
            $"更新于 {snapshot.FetchedAt.LocalDateTime:M/d HH:mm}")
        {
            TokenUsage = TokenUsagePresentation.From(snapshot.TokenUsage, now),
        };
    }

    public static AccountUsagePresentation FromState(UsageState? state, DateTimeOffset now)
    {
        var presentation = From(state?.Snapshot, now);
        if (state is { Status: not UsageStatus.Available, Snapshot.TokenUsage.Snapshot: { } tokenSnapshot })
        {
            presentation = presentation with
            {
                TokenUsage = presentation.TokenUsage.AsCached(tokenSnapshot.FetchedAt, state.Status),
            };
        }
        var cachedAt = state?.Snapshot is { } snapshot
            ? $" · 缓存 {snapshot.FetchedAt.LocalDateTime:M/d HH:mm}" : string.Empty;
        return state?.Status switch
        {
            UsageStatus.Refreshing => presentation with { StatusText = "刷新中…" + cachedAt },
            UsageStatus.AuthenticationRequired => presentation with { StatusText = "需重新登录" + cachedAt },
            UsageStatus.Error => presentation with { StatusText = "刷新失败" + cachedAt },
            _ => presentation,
        };
    }

    private static UsageBarPresentation Bar(int? percent, DateTimeOffset? resetAt, DateTimeOffset now)
    {
        var level = percent switch
        {
            null => UsageLevel.Unknown,
            <= 10 => UsageLevel.Critical,
            <= 30 => UsageLevel.Warning,
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
            return "待刷新";
        }

        if (remaining.TotalDays >= 1)
        {
            return $"{(int)remaining.TotalDays}天{remaining.Hours}时";
        }

        return $"{remaining.Hours}时{remaining.Minutes}分";
    }
}
