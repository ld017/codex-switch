namespace CodexSwitch.Core.Usage;

// A cancellation can arrive after the required quota request has succeeded.
// Keep those quotas available for the coordinator to recover on its own account
// deadline, while preserving ordinary OperationCanceledException semantics for
// cancellation explicitly requested by the user.
public sealed class PartialUsageCancellationException : OperationCanceledException
{
    public RateLimitsResponse Quotas { get; }
    public DateTimeOffset QuotasFetchedAt { get; }

    public PartialUsageCancellationException(RateLimitsResponse quotas, DateTimeOffset quotasFetchedAt,
        OperationCanceledException cancellation)
        : base("Token usage refresh was cancelled after quotas were received.", cancellation, cancellation.CancellationToken)
    {
        Quotas = quotas;
        QuotasFetchedAt = quotasFetchedAt;
    }
}
