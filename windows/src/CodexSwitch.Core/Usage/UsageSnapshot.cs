using System.Text.Json.Serialization;

namespace CodexSwitch.Core.Usage;

public sealed record RateLimitWindow(double? UsedPercent, int? WindowDurationMins, long? ResetsAt);
public sealed record CreditsSnapshot(bool HasCredits, bool Unlimited, string? Balance);
public sealed record RateLimitsSnapshot(RateLimitWindow? Primary, RateLimitWindow? Secondary, CreditsSnapshot? Credits, string? PlanType,
    string? LimitId = null, string? LimitName = null);
public sealed record ResetCredit(string Status, long? ExpiresAt);
public sealed record ResetCreditsSnapshot(int? AvailableCount, IReadOnlyList<ResetCredit>? Credits);
public sealed record RateLimitsResponse(RateLimitsSnapshot? RateLimits, ResetCreditsSnapshot? RateLimitResetCredits,
    IReadOnlyDictionary<string, RateLimitsSnapshot>? RateLimitsByLimitId = null)
{
    // Populated only by the combined client read; this is not part of the quota RPC payload.
    [JsonIgnore]
    public TokenUsageState? TokenUsage { get; init; }
}

public sealed record UsageSnapshot(
    string? PlanType,
    decimal? CreditsRemaining,
    int? PrimaryUsedPercent,
    DateTimeOffset? PrimaryResetAt,
    int? PrimaryWindowDurationMinutes,
    int? SecondaryUsedPercent,
    DateTimeOffset? SecondaryResetAt,
    int? SecondaryWindowDurationMinutes,
    int? ResetCreditsAvailable,
    DateTimeOffset? NextResetCreditExpiresAt,
    DateTimeOffset FetchedAt,
    string? LimitId = null,
    string? LimitName = null,
    bool? CreditsUnlimited = null,
    TokenUsageState? TokenUsage = null)
{
    // Persist usage, as returned by app-server; derive remaining quota only for display.
    // This also keeps older usage-cache files compatible with the corrected UI.
    [JsonIgnore]
    public int? PrimaryRemainingPercent => Remaining(PrimaryUsedPercent);
    [JsonIgnore]
    public int? SecondaryRemainingPercent => Remaining(SecondaryUsedPercent);

    private static int? Remaining(int? used) => used is { } value ? 100 - Math.Clamp(value, 0, 100) : null;
}

public enum UsageStatus { Available, Refreshing, AuthenticationRequired, Error }

public sealed record UsageState(UsageStatus Status, UsageSnapshot? Snapshot, string? Message)
{
    public static UsageState Available(UsageSnapshot snapshot) => new(UsageStatus.Available, snapshot, null);
    public static UsageState Refreshing(UsageSnapshot? previous = null) => new(UsageStatus.Refreshing, previous, null);
    public static UsageState LoginRequired(string message, UsageSnapshot? previous = null) => new(UsageStatus.AuthenticationRequired, previous, message);
    public static UsageState Failed(string message, UsageSnapshot? previous = null) => new(UsageStatus.Error, previous, message);
}

public sealed class UsageException : Exception
{
    public UsageException(string message) : base(message) { }
}
