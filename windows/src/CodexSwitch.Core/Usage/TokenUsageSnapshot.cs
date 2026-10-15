using CodexSwitch.Core.Diagnostics;

namespace CodexSwitch.Core.Usage;

// Server activity totals are independent of the rate-limit percentage windows.
// Nullable values mean the service did not report the value, never zero usage.
public sealed record TokenUsageSummary(long? LifetimeTokens = null, long? PeakDailyTokens = null,
    string? PeakDailyDate = null, int? ActiveDays = null);

// StartDate is the service's calendar-date string, not a UTC instant.
public sealed record TokenUsageDailyBucket(string StartDate, long Tokens);

public sealed record TokenUsageSnapshot(TokenUsageSummary? Summary,
    IReadOnlyList<TokenUsageDailyBucket>? DailyUsageBuckets, DateTimeOffset FetchedAt);

public enum TokenUsageStatus { Available, Error }

public sealed record TokenUsageState(TokenUsageStatus Status, TokenUsageSnapshot? Snapshot, string? Message)
{
    public static TokenUsageState Available(TokenUsageSnapshot snapshot) => new(TokenUsageStatus.Available, snapshot, null);
    public static TokenUsageState Failed(string message, TokenUsageSnapshot? previous = null)
        => new(TokenUsageStatus.Error, previous, SecretRedactor.Redact(message));
}
