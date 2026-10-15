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

    public UsageRefreshCoordinator(AccountStore accounts, UsageFetcher fetcher, UsageCache cache,
        int maximumConcurrency = 3, TimeSpan? accountTimeout = null)
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
        Action<Guid, UsageState>? progress = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_refreshGate)
        {
            // Repeated refresh clicks share the same work, including cancellation cleanup.
            // Run process startup and synchronous cache I/O away from the UI thread.
            if (_inFlight is null || _inFlight.IsCompleted)
                _inFlight = Task.Run(() => RefreshCoreAsync(cancellationToken, progress), CancellationToken.None);
            return _inFlight;
        }
    }

    private async Task<IReadOnlyDictionary<Guid, UsageState>> RefreshCoreAsync(CancellationToken cancellationToken,
        Action<Guid, UsageState>? progress)
    {
        var profiles = await _accounts.ListAsync(cancellationToken).ConfigureAwait(false);
        var profileIds = profiles.Select(profile => profile.Id).ToHashSet();
        var cached = await _cache.LoadAsync(cancellationToken).ConfigureAwait(false);
        var states = new ConcurrentDictionary<Guid, UsageState>(cached.Where(pair => profileIds.Contains(pair.Key)));
        using var cacheGate = new SemaphoreSlim(1, 1);
        try
        {
            await Parallel.ForEachAsync(profiles,
                new ParallelOptions { MaxDegreeOfParallelism = _maximumConcurrency, CancellationToken = cancellationToken },
                async (profile, token) =>
                {
                    var previous = states.GetValueOrDefault(profile.Id)?.Snapshot;
                    progress?.Invoke(profile.Id, UsageState.Refreshing(previous));
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    timeout.CancelAfter(_accountTimeout);
                    UsageState state;
                    try
                    {
                        var snapshot = await _fetcher.FetchAsync(profile.Id, timeout.Token).ConfigureAwait(false);
                        state = UsageState.Available(PreserveTokenCache(snapshot, previous));
                    }
                    catch (PartialUsageCancellationException partial) when (!cancellationToken.IsCancellationRequested)
                    {
                        // This account's deadline expired during the optional read. The quota
                        // already succeeded, so report it and retain token data independently.
                        // User cancellation remains cancellation, even after a quota response.
                        cancellationToken.ThrowIfCancellationRequested();
                        try
                        {
                            var snapshot = UsageSnapshotMapper.Map(partial.Quotas, partial.QuotasFetchedAt) with
                            {
                                TokenUsage = TokenUsageState.Failed("Token 用量刷新超时，已保留额度。"),
                            };
                            state = UsageState.Available(PreserveTokenCache(snapshot, previous));
                        }
                        catch (Exception error) when (error is not OperationCanceledException)
                        {
                            state = Failure(error, previous);
                        }
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        state = UsageState.Failed($"刷新额度超时（{_accountTimeout.TotalSeconds:0.#} 秒），请重试。", previous);
                    }
                    catch (Exception error) when (error is not OperationCanceledException)
                    {
                        state = Failure(error, previous);
                    }

                    states[profile.Id] = state;
                    progress?.Invoke(profile.Id, state);
                    await cacheGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                    try { await _cache.SaveAsync(new Dictionary<Guid, UsageState>(states), CancellationToken.None).ConfigureAwait(false); }
                    finally { cacheGate.Release(); }
                }).ConfigureAwait(false);
        }
        finally
        {
            // Cancellation must keep completed accounts instead of restoring the previous all-account cache.
            await _cache.SaveAsync(new Dictionary<Guid, UsageState>(states), CancellationToken.None).ConfigureAwait(false);
        }
        return new Dictionary<Guid, UsageState>(states);
    }

    private static UsageSnapshot PreserveTokenCache(UsageSnapshot snapshot, UsageSnapshot? previous)
    {
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
