namespace CodexSwitch.Core.Usage;

public sealed record UsageFetchOptions(bool IncludeTokens = true);

public delegate Task<RateLimitsResponse> StreamingRateLimitsFetcher(
    string isolatedCodexHome,
    UsageFetchOptions options,
    Action<RateLimitsResponse, DateTimeOffset>? quotaReceived,
    CancellationToken cancellationToken);
