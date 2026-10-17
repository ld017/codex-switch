using System.Collections.Concurrent;
using CodexSwitch.Core.Auth;
using CodexSwitch.Core.Diagnostics;

namespace CodexSwitch.Core.Usage;

public sealed class UsageRefreshCoordinator
{
    private readonly AccountStore _accounts;
    private readonly UsageFetcher _fetcher;
    private readonly UsageCache _cache;
    private readonly int _maximumConcurrency;
    private readonly TimeSpan _accountTimeout;
    private readonly object _refreshGate = new();
    private Task<IReadOnlyDictionary<Guid, UsageState>>? _inFlight;
    private static readonly TimeSpan TokenRefreshInterval = TimeSpan.FromMinutes(5);

    public UsageRefreshCoordinator(AccountStore accounts, UsageFetcher fetcher, UsageCache cache,
        int maximumConcurrency = 2, TimeSpan? accountTimeout = null)
    {
        if (maximumConcurrency < 1) throw new ArgumentOutOfRangeException(nameof(maximumConcurrency));
        _accounts = accounts;
        _fetcher = fetcher;
        _cache = cache;
        _maximumConcurrency = maximumConcurrency;
        _accountTimeout = accountTimeout ?? TimeSpan.FromSeconds(20);
        if (_accountTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(accountTimeout));
    }

    public Task<IReadOnlyDictionary<Guid, UsageState>> LoadCachedAsync(CancellationToken cancellationToken)
        => _cache.LoadAsync(cancellationToken);

    public Task<IReadOnlyDictionary<Guid, UsageState>> RefreshAllAsync(CancellationToken cancellationToken,
        Action<Guid, UsageState>? progress = null, bool forceTokens = true)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_refreshGate)
        {
            // Repeated refresh clicks share the same work, including cancellation cleanup.
            // Run process startup and synchronous cache I/O away from the UI thread.
            if (_inFlight is null || _inFlight.IsCompleted)
                _inFlight = Task.Run(() => RefreshCoreAsync(cancellationToken, progress, forceTokens), CancellationToken.None);
            return _inFlight;
        }
    }

    private async Task<IReadOnlyDictionary<Guid, UsageState>> RefreshCoreAsync(CancellationToken cancellationToken,
        Action<Guid, UsageState>? progress, bool forceTokens)
    {
        var profiles = await _accounts.ListAsync(cancellationToken).ConfigureAwait(false);
        var activeId = await _accounts.GetActiveProfileIdAsync(cancellationToken).ConfigureAwait(false);
        var profileIds = profiles.Select(profile => profile.Id).ToHashSet();
        var cached = await _cache.LoadAsync(cancellationToken).ConfigureAwait(false);
        var states = new ConcurrentDictionary<Guid, UsageState>(cached.Where(pair => profileIds.Contains(pair.Key)));
        var cacheGate = new object();

        void Notify(Guid id, UsageState state)
        {
            // A UI or diagnostics observer must not prevent fetching, caching, or credential cleanup.
            try { progress?.Invoke(id, state); }
            catch { }
        }

        void Publish(Guid id, UsageState state)
        {
            lock (cacheGate)
            {
                states[id] = state;
                // UsageCache uses atomic synchronous I/O. Complete the checkpoint before
                // the fetcher proceeds to the optional endpoint or acknowledges cancellation.
                _cache.SaveAsync(new Dictionary<Guid, UsageState>(states), CancellationToken.None).GetAwaiter().GetResult();
            }
            Notify(id, state);
        }

        try
        {
            await Parallel.ForEachAsync(profiles.OrderByDescending(profile => profile.Id == activeId),
                new ParallelOptions { MaxDegreeOfParallelism = _maximumConcurrency, CancellationToken = cancellationToken },
                async (profile, token) =>
                {
                    var previous = states.GetValueOrDefault(profile.Id)?.Snapshot;
                    Notify(profile.Id, UsageState.Refreshing(previous));
                    var includeTokens = forceTokens || TokensDue(previous, DateTimeOffset.UtcNow);
                    UsageSnapshot? receivedQuota = null;
                    DateTimeOffset? tokenAttemptedAt = null;

                    UsageSnapshot Merge(UsageSnapshot snapshot)
                    {
                        if (includeTokens) tokenAttemptedAt ??= DateTimeOffset.UtcNow;
                        return PreserveTokenCache(snapshot, previous, includeTokens) with
                        {
                            TokenAttemptedAt = tokenAttemptedAt ?? previous?.TokenAttemptedAt,
                        };
                    }

                    UsageSnapshot WithTokenFailure(UsageSnapshot snapshot, string message)
                        => includeTokens
                            ? snapshot with { TokenUsage = TokenUsageState.Failed(message, snapshot.TokenUsage?.Snapshot) }
                            : snapshot;

                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    timeout.CancelAfter(_accountTimeout);
                    UsageState state;
                    try
                    {
                        var snapshot = await _fetcher.FetchAsync(profile.Id, timeout.Token, new UsageFetchOptions(includeTokens), quota =>
                        {
                            receivedQuota = Merge(quota);
                            Publish(profile.Id, UsageState.Available(receivedQuota));
                        }).ConfigureAwait(false);
                        state = UsageState.Available(Merge(snapshot));
                    }
                    catch (PartialUsageCancellationException partial)
                    {
                        // This account's deadline expired during the optional read. The quota
                        // already succeeded, so report it and retain token data independently.
                        // User cancellation remains cancellation, but must retain a received quota.
                        try
                        {
                            receivedQuota ??= Merge(UsageSnapshotMapper.Map(partial.Quotas, partial.QuotasFetchedAt));
                            state = UsageState.Available(WithTokenFailure(receivedQuota,
                                cancellationToken.IsCancellationRequested ? "Token 用量刷新已取消，已保留额度。" : "Token 用量刷新超时，已保留额度。"));
                        }
                        catch (Exception error) when (error is not OperationCanceledException)
                        {
                            state = Failure(error, receivedQuota ?? previous);
                        }
                        if (cancellationToken.IsCancellationRequested)
                        {
                            Publish(profile.Id, state);
                            cancellationToken.ThrowIfCancellationRequested();
                        }
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        state = receivedQuota is null
                            ? UsageState.Failed($"刷新额度超时（{_accountTimeout.TotalSeconds:0.#} 秒），请重试。", previous)
                            : UsageState.Available(WithTokenFailure(receivedQuota, "Token 用量刷新超时，已保留额度。"));
                    }
                    catch (OperationCanceledException)
                    {
                        if (receivedQuota is not null)
                            Publish(profile.Id, UsageState.Available(WithTokenFailure(receivedQuota, "Token 用量刷新已取消，已保留额度。")));
                        throw;
                    }
                    catch (Exception error) when (error is not OperationCanceledException)
                    {
                        state = Failure(error, receivedQuota ?? previous);
                    }

                    Publish(profile.Id, state);
                }).ConfigureAwait(false);
        }
        finally
        {
            // Cancellation must keep completed accounts instead of restoring the previous all-account cache.
            await _cache.SaveAsync(new Dictionary<Guid, UsageState>(states), CancellationToken.None).ConfigureAwait(false);
        }
        return new Dictionary<Guid, UsageState>(states);
    }

    private static bool TokensDue(UsageSnapshot? previous, DateTimeOffset now)
    {
        var attempted = previous?.TokenAttemptedAt ?? previous?.TokenUsage?.Snapshot?.FetchedAt;
        return attempted is null || attempted > now || now - attempted >= TokenRefreshInterval;
    }

    private static UsageSnapshot PreserveTokenCache(UsageSnapshot snapshot, UsageSnapshot? previous, bool includeTokens)
    {
        if (!includeTokens || snapshot.TokenUsage is null)
            return snapshot with { TokenUsage = previous?.TokenUsage };
        if (snapshot.TokenUsage is { Status: TokenUsageStatus.Error, Snapshot: null } failedTokens
            && previous?.TokenUsage?.Snapshot is { } previousTokens)
        {
            // Quota and token timestamps describe separate reads. Never mark old activity
            // as current merely because the required quota endpoint still works.
            return snapshot with { TokenUsage = failedTokens with { Snapshot = previousTokens } };
        }
        return snapshot;
    }

    private static UsageState Failure(Exception error, UsageSnapshot? previous)
    {
        var message = SecretRedactor.Redact(error.Message);
        return UsageErrorClassifier.IsAuthenticationRequired(message)
            ? UsageState.LoginRequired(message, previous)
            : UsageState.Failed(message, previous);
    }
}
