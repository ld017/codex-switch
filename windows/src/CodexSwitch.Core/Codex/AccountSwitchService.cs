using CodexSwitch.Core.Auth;
using CodexSwitch.Core.Configuration;
using CodexSwitch.Core.Files;

namespace CodexSwitch.Core.Codex;

public enum AccountSwitchStage
{
    Prerequisites,
    Backup,
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
    private readonly SemaphoreSlim _gate = new(1, 1);

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

    // Run this before asking the desktop to close, so invalid targets leave the live app alone.
    public async Task ValidateAsync(Guid profileId, CancellationToken cancellationToken)
    {
        var provider = ProviderConfigEditor.Read(await File.ReadAllTextAsync(_configPath, cancellationToken));
        if (provider != Provider.OpenAI)
        {
            throw new AccountSwitchException(
                AccountSwitchStage.Prerequisites,
                "只有当前 Provider 为 OpenAI 时才能切换账号。");
        }

        _ = await LoadValidatedTargetAsync(profileId, cancellationToken);
    }

    private async Task<byte[]> LoadValidatedTargetAsync(Guid profileId, CancellationToken cancellationToken)
    {
        var profiles = await _accounts.ListAsync(cancellationToken);
        var targetProfile = profiles.FirstOrDefault(profile => profile.Id == profileId)
            ?? throw new AccountSwitchException(AccountSwitchStage.Prerequisites, "没有找到目标账号。");
        var targetBytes = await _accounts.LoadAuthAsync(profileId, cancellationToken);
        var target = AuthBlob.Parse(targetBytes);
        if (!string.Equals(target.GetIdentity().Fingerprint(), targetProfile.IdentityFingerprint, StringComparison.Ordinal))
        {
            throw new AccountSwitchException(AccountSwitchStage.Validation, "目标账号身份校验失败。");
        }
        return targetBytes;
    }

    public async Task<AccountSwitchResult> SwitchAsync(Guid profileId, CancellationToken cancellationToken,
        bool preserveTargetCredentials = false)
    {
        await _gate.WaitAsync(cancellationToken);
        try { return await SwitchCoreAsync(profileId, cancellationToken, preserveTargetCredentials); }
        finally { _gate.Release(); }
    }

    private async Task<AccountSwitchResult> SwitchCoreAsync(Guid profileId, CancellationToken cancellationToken,
        bool preserveTargetCredentials)
    {
        await ValidateAsync(profileId, cancellationToken);
        var previousActive = await _accounts.GetActiveProfileIdAsync(cancellationToken);
        var previousBytes = _files.Exists(_liveAuthPath) ? _files.ReadAllBytes(_liveAuthPath) : null;
        string? backupPath = null;
        if (previousBytes is not null)
        {
            try
            {
                Directory.CreateDirectory(_backupDirectory);
                backupPath = Path.Combine(_backupDirectory, $"auth-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json");
                _files.WriteAtomically(backupPath, previousBytes);
                if (!_files.ReadAllBytes(backupPath).SequenceEqual(previousBytes))
                    throw new InvalidDataException("The auth backup differs from the current credentials.");
                await PreserveCurrentCredentialsAsync(previousBytes,
                    preserveTargetCredentials ? profileId : null, cancellationToken);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error)
            {
                throw new AccountSwitchException(AccountSwitchStage.Backup,
                    "无法备份并保存当前账号的最新凭据，已取消切换。", error);
            }
        }

        // Reload after preserving live credentials. Switching to the current account must not
        // replace a refresh token already rotated by the desktop with an older stored token.
        var targetBytes = await LoadValidatedTargetAsync(profileId, cancellationToken);
        var target = AuthBlob.Parse(targetBytes);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureLiveAuthUnchanged(previousBytes);
        var stage = AccountSwitchStage.Write;
        try
        {
            _files.WriteAtomically(_liveAuthPath, targetBytes);
            var writtenBytes = _files.ReadAllBytes(_liveAuthPath);
            var readBack = AuthBlob.Parse(writtenBytes);
            if (!target.IdentityMatches(readBack) || !writtenBytes.SequenceEqual(targetBytes))
            {
                throw new AccountSwitchException(AccountSwitchStage.Validation, "写入后的账号身份校验失败。");
            }

            stage = AccountSwitchStage.Metadata;
            await _accounts.SetActiveProfileIdAsync(profileId, cancellationToken);
            if (await _accounts.GetActiveProfileIdAsync(cancellationToken) != profileId)
                throw new InvalidDataException("The new active account was not saved.");
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

                if (previousBytes is null ? _files.Exists(_liveAuthPath)
                    : !_files.ReadAllBytes(_liveAuthPath).SequenceEqual(previousBytes))
                    throw new InvalidDataException("Restored credentials differ from the original bytes.");

                // Cancellation must not interrupt recovery after credentials have been replaced.
                await _accounts.SetActiveProfileIdAsync(previousActive, CancellationToken.None);
                if (await _accounts.GetActiveProfileIdAsync(CancellationToken.None) != previousActive)
                    throw new InvalidDataException("The original active account was not restored.");
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

            throw new AccountSwitchException(stage, "无法安全切换 OpenAI 账号。", error);
        }

        if (backupPath is not null)
        {
            try { _files.WriteAtomically(backupPath + ".cleanup-ready", []); }
            catch { /* An unmarked backup stays protected; cleanup failure does not undo a verified switch. */ }
        }
        return new AccountSwitchResult(previousActive, profileId, backupPath);
    }

    private void EnsureLiveAuthUnchanged(byte[]? expected)
    {
        if (expected is null ? _files.Exists(_liveAuthPath)
            : !_files.Exists(_liveAuthPath) || !_files.ReadAllBytes(_liveAuthPath).SequenceEqual(expected))
            throw new AccountSwitchException(AccountSwitchStage.Prerequisites,
                "当前登录凭据仍在变化，请等待 Codex 完全退出后再切换。");
    }

    private async Task PreserveCurrentCredentialsAsync(byte[] liveBytes, Guid? skipProfileId, CancellationToken cancellationToken)
    {
        AuthBlob live;
        try { live = AuthBlob.Parse(liveBytes); }
        catch (AuthBlobException) { return; } // Keep a byte-exact backup even when the old file is damaged.

        foreach (var profile in await _accounts.ListAsync(cancellationToken))
        {
            if (profile.Id == skipProfileId) continue; // A fresh explicit login supersedes the old live token.
            var storedBytes = await _accounts.LoadAuthAsync(profile.Id, cancellationToken);
            var stored = AuthBlob.Parse(storedBytes);
            if (!stored.IdentityMatches(live)) continue;
            if (!await _accounts.TryReplaceAuthAsync(profile.Id, storedBytes, liveBytes, cancellationToken))
                throw new InvalidDataException("Account credentials changed while preserving the live token.");
            if (!(await _accounts.LoadAuthAsync(profile.Id, cancellationToken)).SequenceEqual(liveBytes))
                throw new InvalidDataException("The latest live credentials were not saved.");
        }
    }
}
