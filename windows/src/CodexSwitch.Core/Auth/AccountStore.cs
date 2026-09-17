using System.Text.Json;
using CodexSwitch.Core.Files;

namespace CodexSwitch.Core.Auth;

public sealed class AccountStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _root;
    private readonly string _accountsDirectory;
    private readonly string _configurationPath;
    private readonly ICredentialProtector _protector;
    private readonly IAtomicFileStore _files;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public AccountStore(string root, ICredentialProtector protector, IAtomicFileStore files)
    {
        _root = root;
        _accountsDirectory = Path.Combine(root, "accounts");
        _configurationPath = Path.Combine(root, "config.json");
        _protector = protector;
        _files = files;
    }

    public async Task<IReadOnlyList<AccountProfile>> ListAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return LoadConfiguration().Profiles.ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AccountProfile> SaveAsync(string label, byte[] authJson, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        var auth = AuthBlob.Parse(authJson);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(_root);
            Directory.CreateDirectory(_accountsDirectory);
            var configuration = LoadConfiguration();
            var profile = new AccountProfile(Guid.NewGuid(), label.Trim(), auth.GetIdentity().Fingerprint());
            var accountPath = GetEncryptedPath(profile.Id);
            _files.WriteAtomically(accountPath, _protector.Protect(authJson));
            try
            {
                var profiles = configuration.Profiles.Append(profile).ToArray();
                var updated = configuration with
                {
                    ActiveProfileId = configuration.ActiveProfileId ?? profile.Id,
                    Profiles = profiles,
                };
                SaveConfiguration(updated);
            }
            catch
            {
                _files.Delete(accountPath);
                throw;
            }

            return profile;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<byte[]> LoadAuthAsync(Guid profileId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var profile = FindProfile(LoadConfiguration(), profileId);
            var plaintext = _protector.Unprotect(_files.ReadAllBytes(GetEncryptedPath(profile.Id)));
            _ = AuthBlob.Parse(plaintext);
            return plaintext;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RenameAsync(Guid profileId, string label, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var configuration = LoadConfiguration();
            _ = FindProfile(configuration, profileId);
            var profiles = configuration.Profiles
                .Select(profile => profile.Id == profileId ? profile with { Label = label.Trim() } : profile)
                .ToArray();
            SaveConfiguration(configuration with { Profiles = profiles });
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RemoveAsync(Guid profileId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var configuration = LoadConfiguration();
            _ = FindProfile(configuration, profileId);
            if (configuration.ActiveProfileId == profileId && configuration.Profiles.Count > 1)
            {
                throw new InvalidOperationException("请先切换到其他账号，再删除当前账号。");
            }
            var profiles = configuration.Profiles.Where(profile => profile.Id != profileId).ToArray();
            var active = configuration.ActiveProfileId == profileId ? null : configuration.ActiveProfileId;
            SaveConfiguration(configuration with { ActiveProfileId = active, Profiles = profiles });
            _files.Delete(GetEncryptedPath(profileId));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<Guid?> GetActiveProfileIdAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return LoadConfiguration().ActiveProfileId;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetActiveProfileIdAsync(Guid? profileId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var configuration = LoadConfiguration();
            if (profileId is Guid id)
            {
                _ = FindProfile(configuration, id);
            }

            SaveConfiguration(configuration with { ActiveProfileId = profileId });
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<Guid?> ReconcileActiveProfileAsync(byte[] liveAuthJson, CancellationToken cancellationToken)
    {
        var liveAuth = AuthBlob.Parse(liveAuthJson);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var configuration = LoadConfiguration();
            Guid? matchingProfileId = null;
            foreach (var profile in configuration.Profiles)
            {
                var storedBytes = _protector.Unprotect(_files.ReadAllBytes(GetEncryptedPath(profile.Id)));
                if (AuthBlob.Parse(storedBytes).IdentityMatches(liveAuth))
                {
                    matchingProfileId = profile.Id;
                    break;
                }
            }

            if (configuration.ActiveProfileId != matchingProfileId)
            {
                SaveConfiguration(configuration with { ActiveProfileId = matchingProfileId });
            }

            return matchingProfileId;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ReplaceAuthAsync(Guid profileId, byte[] authJson, CancellationToken cancellationToken)
    {
        var auth = AuthBlob.Parse(authJson);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var configuration = LoadConfiguration();
            var profile = FindProfile(configuration, profileId);
            if (!string.Equals(profile.IdentityFingerprint, auth.GetIdentity().Fingerprint(), StringComparison.Ordinal))
            {
                var current = AuthBlob.Parse(_protector.Unprotect(_files.ReadAllBytes(GetEncryptedPath(profileId))));
                if (!current.IdentityMatches(auth))
                {
                    throw new AuthBlobException("Replacement credentials belong to a different account.");
                }
            }

            _files.WriteAtomically(GetEncryptedPath(profileId), _protector.Protect(authJson));
        }
        finally
        {
            _gate.Release();
        }
    }

    private AccountConfiguration LoadConfiguration()
    {
        if (!_files.Exists(_configurationPath))
        {
            return AccountConfiguration.Empty;
        }

        return JsonSerializer.Deserialize<AccountConfiguration>(_files.ReadAllBytes(_configurationPath), JsonOptions)
            ?? throw new InvalidDataException("Account configuration is empty.");
    }

    private void SaveConfiguration(AccountConfiguration configuration)
        => _files.WriteAtomically(_configurationPath, JsonSerializer.SerializeToUtf8Bytes(configuration, JsonOptions));

    private string GetEncryptedPath(Guid profileId) => Path.Combine(_accountsDirectory, $"{profileId:N}.bin");

    private static AccountProfile FindProfile(AccountConfiguration configuration, Guid profileId)
        => configuration.Profiles.FirstOrDefault(profile => profile.Id == profileId)
           ?? throw new KeyNotFoundException($"Unknown account profile: {profileId}");
}
