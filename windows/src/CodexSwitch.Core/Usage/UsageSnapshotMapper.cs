using System.Globalization;

namespace CodexSwitch.Core.Usage;

public static class UsageSnapshotMapper
{
    public static UsageSnapshot Map(RateLimitsResponse response, DateTimeOffset fetchedAt)
    {
        var limits = SelectCodexLimits(response);
        decimal? balance = null;
        if (limits?.Credits?.Balance is { } raw
            && decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
            && parsed >= 0)
        {
            balance = parsed;
        }

        var resetCount = response.RateLimitResetCredits?.AvailableCount is >= 0
            ? response.RateLimitResetCredits.AvailableCount : null;
        if (limits?.Primary is null && limits?.Secondary is null && balance is null
            && limits?.Credits?.Unlimited != true && resetCount is null)
            throw new UsageException("Codex CLI returned no usage data.");

        var creditExpiry = response.RateLimitResetCredits?.Credits?
            .Where(x => string.Equals(x.Status, "available", StringComparison.OrdinalIgnoreCase))
            .Select(x => Time(x.ExpiresAt))
            .Where(x => x > fetchedAt).Min();

        return new UsageSnapshot(
            limits?.PlanType, balance,
            Percent(limits?.Primary?.UsedPercent),
            Time(limits?.Primary?.ResetsAt), Duration(limits?.Primary?.WindowDurationMins),
            Percent(limits?.Secondary?.UsedPercent),
            Time(limits?.Secondary?.ResetsAt), Duration(limits?.Secondary?.WindowDurationMins),
            resetCount, creditExpiry, fetchedAt,
            limits?.LimitId, limits?.LimitName, limits?.Credits?.Unlimited, response.TokenUsage);
    }

    private static RateLimitsSnapshot? SelectCodexLimits(RateLimitsResponse response)
    {
        // The multi-bucket response is authoritative. Never choose the first bucket:
        // it may represent a different model with a separate quota.
        var codex = response.RateLimitsByLimitId?
            .FirstOrDefault(x => string.Equals(x.Key, "codex", StringComparison.OrdinalIgnoreCase)).Value;
        if (codex is not null) return codex with { LimitId = "codex" };

        var legacy = response.RateLimits;
        return (response.RateLimitsByLimitId is not { Count: > 0 } && string.IsNullOrWhiteSpace(legacy?.LimitId))
            || string.Equals(legacy?.LimitId, "codex", StringComparison.OrdinalIgnoreCase)
            ? legacy : null;
    }

    private static int? Percent(double? value) => value is { } number && double.IsFinite(number)
        ? (int)Math.Round(Math.Clamp(number, 0, 100), MidpointRounding.AwayFromZero) : null;

    private static int? Duration(int? minutes) => minutes is > 0 ? minutes : null;

    private static DateTimeOffset? Time(long? value) => value is > 0 and <= 253402300799
        ? DateTimeOffset.FromUnixTimeSeconds(value.Value) : null;
}
