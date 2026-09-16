using CodexSwitch.Core.Auth;
using CodexSwitch.Core.Diagnostics;

namespace CodexSwitch.Core.Usage;

public sealed class UsageRefreshCoordinator
{
    private readonly AccountStore _accounts;
    private readonly UsageFetcher _fetcher;
    private readonly UsageCache _cache;

    public UsageRefreshCoordinator(AccountStore accounts, UsageFetcher fetcher, UsageCache cache)
    {
        _accounts = accounts; _fetcher = fetcher; _cache = cache;
    }

    public async Task<IReadOnlyDictionary<Guid, UsageState>> RefreshAllAsync(CancellationToken cancellationToken)
    {
        var existing = new Dictionary<Guid, UsageState>(await _cache.LoadAsync(cancellationToken));
        foreach (var profile in await _accounts.ListAsync(cancellationToken))
        {
            try { existing[profile.Id] = UsageState.Available(await _fetcher.FetchAsync(profile.Id, cancellationToken)); }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                var message = SecretRedactor.Redact(error.Message);
                var previous = existing.GetValueOrDefault(profile.Id)?.Snapshot;
                existing[profile.Id] = UsageErrorClassifier.IsAuthenticationRequired(message)
                    ? UsageState.LoginRequired(message, previous)
                    : UsageState.Failed(message, previous);
            }
        }
        await _cache.SaveAsync(existing, cancellationToken);
        return existing;
    }
}
