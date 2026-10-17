using CodexSwitch.Core.Auth;
using CodexSwitch.Core.Diagnostics;
using CodexSwitch.Core.Files;
using CodexSwitch.Core.Usage;

namespace CodexSwitch.Core.Codex;

public enum LoginMode
{
    Browser,
    DeviceCode,
}

public sealed class LoginException : Exception
{
    public LoginException(string message)
        : base(message)
    {
    }
}

public sealed class LoginService
{
    private readonly string _codexCliPath;
    private readonly string _temporaryRoot;
    private readonly IProcessRunner _processRunner;
    private readonly AccountImportService _importer;
    private readonly ITemporaryDirectoryCleaner _cleaner;

    public LoginService(
        string codexCliPath,
        string temporaryRoot,
        IProcessRunner processRunner,
        AccountImportService importer,
        ITemporaryDirectoryCleaner? cleaner = null)
    {
        _codexCliPath = codexCliPath;
        _temporaryRoot = Path.GetFullPath(temporaryRoot);
        _processRunner = processRunner;
        _importer = importer;
        _cleaner = cleaner ?? new TemporaryDirectoryCleaner();
    }

    public async Task<AccountProfile> LoginAsync(
        string label,
        bool overwriteDuplicate,
        CancellationToken cancellationToken)
        => await LoginAsync(label, LoginMode.Browser, _ => overwriteDuplicate, cancellationToken);

    public async Task<AccountProfile> LoginAsync(
        string label,
        LoginMode mode,
        bool overwriteDuplicate,
        CancellationToken cancellationToken)
        => await LoginAsync(label, mode, _ => overwriteDuplicate, cancellationToken);

    public async Task<AccountProfile> LoginAsync(
        string label,
        Func<AccountProfile, bool> confirmDuplicate,
        CancellationToken cancellationToken)
        => await LoginAsync(label, LoginMode.Browser, confirmDuplicate, cancellationToken);

    public async Task<AccountProfile> LoginAsync(
        string label,
        LoginMode mode,
        Func<AccountProfile, bool> confirmDuplicate,
        CancellationToken cancellationToken)
    {
        return await AuthenticateAsync(mode, async authJson =>
        {
            try { return await _importer.ImportAsync(label, authJson, false, cancellationToken); }
            catch (DuplicateAccountException duplicate) when (confirmDuplicate(duplicate.Existing))
            { return await _importer.ImportAsync(label, authJson, true, cancellationToken); }
        }, cancellationToken);
    }

    public async Task<AccountProfile> ReauthenticateAsync(
        AccountProfile profile,
        LoginMode mode,
        CancellationToken cancellationToken)
    {
        return await AuthenticateAsync(mode, authJson => _importer.ReauthenticateAsync(profile.Id, authJson, cancellationToken), cancellationToken);
    }

    private async Task<AccountProfile> AuthenticateAsync(LoginMode mode, Func<byte[], Task<AccountProfile>> import,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_temporaryRoot);
        var codexHome = Path.Combine(_temporaryRoot, $"login-{Guid.NewGuid():N}");
        Directory.CreateDirectory(codexHome);
        using var lease = TemporaryHomeLease.Create(codexHome);
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(codexHome, "config.toml"),
                "cli_auth_credentials_store = \"file\"\n",
                cancellationToken);
            var environment = new Dictionary<string, string?>
            {
                ["CODEX_HOME"] = codexHome,
            };
            IReadOnlyList<string> arguments = mode == LoginMode.DeviceCode
                ? ["login", "--device-auth"]
                : ["login"];
            var result = await _processRunner.RunAsync(
                new ProcessSpec(
                    _codexCliPath,
                    arguments,
                    environment,
                    Timeout: TimeSpan.FromMinutes(10),
                    Visible: mode == LoginMode.DeviceCode,
                    WorkingDirectory: codexHome),
                cancellationToken);
            if (result.ExitCode != 0)
            {
                if (result.ExitCode == unchecked((int)0xC000013A))
                {
                    throw new LoginException("Codex 登录已取消或登录窗口被关闭。");
                }

                var detail = SecretRedactor.Redact(
                    string.IsNullOrWhiteSpace(result.StandardError)
                        ? result.StandardOutput
                        : result.StandardError);
                var suffix = string.IsNullOrWhiteSpace(detail) ? string.Empty : $"：{detail}";
                throw new LoginException($"Codex 登录失败（退出码 {result.ExitCode}）{suffix}");
            }

            var authPath = Path.Combine(codexHome, "auth.json");
            if (!File.Exists(authPath))
            {
                throw new LoginException("Codex login did not create auth.json.");
            }

            var authJson = await File.ReadAllBytesAsync(authPath, cancellationToken);
            try
            {
                _ = AuthBlob.Parse(authJson);
                var profile = await import(authJson);
                if (TemporaryDirectoryCleaner.HasCredentialFiles(codexHome, ["auth.json"])) lease.MarkRecoveryRequired();
                else lease.MarkCleanupReady();
                return profile;
            }
            finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(authJson); }
        }
        catch
        {
            if (TemporaryDirectoryCleaner.HasCredentialFiles(codexHome)) lease.MarkRecoveryRequired();
            else lease.MarkCleanupReady();
            throw;
        }
        finally
        {
            lease.Dispose();
            try { _ = await _cleaner.TryDeleteAsync(codexHome, CancellationToken.None); }
            catch { /* Cleanup cannot reverse the import result or hide the original login error. */ }
        }
    }
}
