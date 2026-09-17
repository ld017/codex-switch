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

    public async Task<AccountProfile> ReauthenticateAsync(
        Guid profileId,
        byte[] authJson,
        CancellationToken cancellationToken)
    {
        var profile = (await _store.ListAsync(cancellationToken))
            .FirstOrDefault(candidate => candidate.Id == profileId)
            ?? throw new KeyNotFoundException($"Unknown account profile: {profileId}");
        var current = AuthBlob.Parse(await _store.LoadAuthAsync(profileId, cancellationToken));
        var replacement = AuthBlob.Parse(authJson);
        if (!current.IdentityMatches(replacement))
        {
            throw new AuthBlobException("重新登录的账号与所选账号不一致，原凭据未被修改。");
        }

        await _store.ReplaceAuthAsync(profileId, authJson, cancellationToken);
        return profile;
    }
}
