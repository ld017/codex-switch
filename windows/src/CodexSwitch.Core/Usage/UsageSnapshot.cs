using System.Text.Json.Serialization;

namespace CodexSwitch.Core.Usage;

public sealed record RateLimitWindow(double UsedPercent, int? WindowDurationMins, long? ResetsAt);
public sealed record CreditsSnapshot(bool HasCredits, bool Unlimited, string? Balance);
public sealed record RateLimitsSnapshot(RateLimitWindow? Primary, RateLimitWindow? Secondary, CreditsSnapshot? Credits, string? PlanType);
public sealed record ResetCredit(string Status, long? ExpiresAt);
public sealed record ResetCreditsSnapshot(int AvailableCount, IReadOnlyList<ResetCredit> Credits);
public sealed record RateLimitsResponse(RateLimitsSnapshot RateLimits, ResetCreditsSnapshot? RateLimitResetCredits);

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
    DateTimeOffset FetchedAt);

public enum UsageStatus { Available, Refreshing, AuthenticationRequired, Error }

public sealed record UsageState(UsageStatus Status, UsageSnapshot? Snapshot, string? Message)
{
    public static UsageState Available(UsageSnapshot snapshot) => new(UsageStatus.Available, snapshot, null);
    public static UsageState Refreshing(UsageSnapshot? previous = null) => new(UsageStatus.Refreshing, previous, null);
    public static UsageState Failed(string message, UsageSnapshot? previous = null) => new(UsageStatus.Error, previous, message);
}

public sealed class UsageException : Exception
{
    public UsageException(string message) : base(message) { }
}
