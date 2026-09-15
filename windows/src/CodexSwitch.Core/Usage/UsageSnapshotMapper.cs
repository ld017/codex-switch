using System.Globalization;

namespace CodexSwitch.Core.Usage;

public static class UsageSnapshotMapper
{
    public static UsageSnapshot Map(RateLimitsResponse response, DateTimeOffset fetchedAt)
    {
        var limits = response.RateLimits;
        decimal? balance = null;
        if (limits.Credits?.Balance is { } raw
            && decimal.TryParse(raw.Replace(",", string.Empty, StringComparison.Ordinal), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed))
        {
            balance = parsed;
        }

        if (limits.Primary is null && limits.Secondary is null && balance is null)
            throw new UsageException("Codex CLI returned no usage data.");

        static DateTimeOffset? Time(long? value) => value is null ? null : DateTimeOffset.FromUnixTimeSeconds(value.Value);
        var creditExpiry = response.RateLimitResetCredits?.Credits
            .Where(x => string.Equals(x.Status, "available", StringComparison.OrdinalIgnoreCase))
            .Where(x => x.ExpiresAt.HasValue).Select(x => x.ExpiresAt!.Value).DefaultIfEmpty().Min();

        return new UsageSnapshot(
            limits.PlanType, balance,
            limits.Primary is null ? null : (int)Math.Round(limits.Primary.UsedPercent, MidpointRounding.AwayFromZero),
            Time(limits.Primary?.ResetsAt), limits.Primary?.WindowDurationMins,
            limits.Secondary is null ? null : (int)Math.Round(limits.Secondary.UsedPercent, MidpointRounding.AwayFromZero),
            Time(limits.Secondary?.ResetsAt), limits.Secondary?.WindowDurationMins,
            response.RateLimitResetCredits is null ? null : Math.Max(0, response.RateLimitResetCredits.AvailableCount),
            creditExpiry > 0 ? Time(creditExpiry) : null, fetchedAt);
    }
}
