using CodexSwitch.Core.Auth;
using CodexSwitch.Core.Configuration;
using CodexSwitch.Core.Files;

namespace CodexSwitch.Core.Codex;

public enum AccountSwitchStage
{
    Prerequisites,
    Write,
    Validation,
    Metadata,
    Recovery,
}

public sealed class AccountSwitchException : Exception
{
    public AccountSwitchException(AccountSwitchStage stage, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Stage = stage;
    }

    public AccountSwitchStage Stage { get; }
}

public sealed record AccountSwitchResult(Guid? PreviousProfileId, Guid CurrentProfileId, string? LiveAuthBackupPath);

public sealed class AccountSwitchService
{
    private readonly string _configPath;
    private readonly string _liveAuthPath;
    private readonly string _backupDirectory;
    private readonly AccountStore _accounts;
    private readonly IAtomicFileStore _files;

    public AccountSwitchService(
        string configPath,
        string liveAuthPath,
        string backupDirectory,
        AccountStore accounts,
        IAtomicFileStore files)
    {
        _configPath = configPath;
        _liveAuthPath = liveAuthPath;
        _backupDirectory = backupDirectory;
        _accounts = accounts;
        _files = files;
    }

    public async Task<AccountSwitchResult> SwitchAsync(Guid profileId, CancellationToken cancellationToken)
    {
        var provider = ProviderConfigEditor.Read(await File.ReadAllTextAsync(_configPath, cancellationToken));
        if (provider != Provider.OpenAI)
        {
            throw new AccountSwitchException(
                AccountSwitchStage.Prerequisites,
                "只有当前 Provider 为 OpenAI 时才能切换账号。");
        }

        var profiles = await _accounts.ListAsync(cancellationToken);
        var targetProfile = profiles.FirstOrDefault(profile => profile.Id == profileId)
            ?? throw new AccountSwitchException(AccountSwitchStage.Prerequisites, "没有找到目标账号。");
        var targetBytes = await _accounts.LoadAuthAsync(profileId, cancellationToken);
        var target = AuthBlob.Parse(targetBytes);
        if (!string.Equals(target.GetIdentity().Fingerprint(), targetProfile.IdentityFingerprint, StringComparison.Ordinal))
        {
            throw new AccountSwitchException(AccountSwitchStage.Validation, "目标账号身份校验失败。");
        }

        var previousActive = await _accounts.GetActiveProfileIdAsync(cancellationToken);
        var previousBytes = _files.Exists(_liveAuthPath) ? _files.ReadAllBytes(_liveAuthPath) : null;
        string? backupPath = null;
        if (previousBytes is not null)
        {
            Directory.CreateDirectory(_backupDirectory);
            backupPath = Path.Combine(_backupDirectory, $"auth-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json");
            _files.WriteAtomically(backupPath, previousBytes);
        }

        try
        {
            _files.WriteAtomically(_liveAuthPath, targetBytes);
            var readBack = AuthBlob.Parse(_files.ReadAllBytes(_liveAuthPath));
            if (!target.IdentityMatches(readBack))
            {
                throw new AccountSwitchException(AccountSwitchStage.Validation, "写入后的账号身份校验失败。");
            }

            await _accounts.SetActiveProfileIdAsync(profileId, cancellationToken);
        }
        catch (Exception error)
        {
            try
            {
                if (previousBytes is null)
                {
                    _files.Delete(_liveAuthPath);
                }
                else
                {
                    _files.WriteAtomically(_liveAuthPath, previousBytes);
                }

                await _accounts.SetActiveProfileIdAsync(previousActive, cancellationToken);
            }
            catch (Exception recoveryError)
            {
                throw new AccountSwitchException(
                    AccountSwitchStage.Recovery,
                    "账号切换失败，并且无法验证原登录状态已恢复。",
                    new AggregateException(error, recoveryError));
            }

            if (error is AccountSwitchException switchError)
            {
                throw switchError;
            }

            throw new AccountSwitchException(AccountSwitchStage.Write, "无法安全切换 OpenAI 账号。", error);
        }

        return new AccountSwitchResult(previousActive, profileId, backupPath);
    }
}
