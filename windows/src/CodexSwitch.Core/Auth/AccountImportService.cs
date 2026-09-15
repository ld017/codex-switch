namespace CodexSwitch.Core.Auth;

public sealed class DuplicateAccountException : Exception
{
    public DuplicateAccountException(AccountProfile existing)
        : base($"Account '{existing.Label}' is already managed.")
    {
        Existing = existing;
    }

    public AccountProfile Existing { get; }
}

public sealed class AccountImportService
{
    private readonly AccountStore _store;

    public AccountImportService(AccountStore store)
    {
        _store = store;
    }

    public async Task<AccountProfile> ImportAsync(
        string label,
        byte[] authJson,
        bool overwriteDuplicate,
        CancellationToken cancellationToken)
    {
        var auth = AuthBlob.Parse(authJson);
        AccountProfile? existing = null;
        foreach (var profile in await _store.ListAsync(cancellationToken))
        {
            var stored = AuthBlob.Parse(await _store.LoadAuthAsync(profile.Id, cancellationToken));
            if (stored.IdentityMatches(auth))
            {
                existing = profile;
                break;
            }
        }
        if (existing is null)
        {
            return await _store.SaveAsync(label, authJson, cancellationToken);
        }

        if (!overwriteDuplicate)
        {
            throw new DuplicateAccountException(existing);
        }

        await _store.ReplaceAuthAsync(existing.Id, authJson, cancellationToken);
        await _store.RenameAsync(existing.Id, label, cancellationToken);
        return existing with { Label = label.Trim() };
    }
}
