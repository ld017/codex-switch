using CodexSwitch.Core.Auth;

namespace CodexSwitch.Core.Codex;

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

    public LoginService(
        string codexCliPath,
        string temporaryRoot,
        IProcessRunner processRunner,
        AccountImportService importer)
    {
        _codexCliPath = codexCliPath;
        _temporaryRoot = Path.GetFullPath(temporaryRoot);
        _processRunner = processRunner;
        _importer = importer;
    }

    public async Task<AccountProfile> LoginAsync(
        string label,
        bool overwriteDuplicate,
        CancellationToken cancellationToken)
        => await LoginAsync(label, _ => overwriteDuplicate, cancellationToken);

    public async Task<AccountProfile> LoginAsync(
        string label,
        Func<AccountProfile, bool> confirmDuplicate,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_temporaryRoot);
        var codexHome = Path.Combine(_temporaryRoot, $"login-{Guid.NewGuid():N}");
        Directory.CreateDirectory(codexHome);
        try
        {
            var environment = new Dictionary<string, string?>
            {
                ["CODEX_HOME"] = codexHome,
            };
            var result = await _processRunner.RunAsync(
                new ProcessSpec(
                    _codexCliPath,
                    ["login"],
                    environment,
                    Timeout: TimeSpan.FromMinutes(10),
                    Visible: true),
                cancellationToken);
            if (result.ExitCode != 0)
            {
                throw new LoginException($"Codex login exited with status {result.ExitCode}.");
            }

            var authPath = Path.Combine(codexHome, "auth.json");
            if (!File.Exists(authPath))
            {
                throw new LoginException("Codex login did not create auth.json.");
            }

            var authJson = await File.ReadAllBytesAsync(authPath, cancellationToken);
            _ = AuthBlob.Parse(authJson);
            try
            {
                return await _importer.ImportAsync(label, authJson, false, cancellationToken);
            }
            catch (DuplicateAccountException duplicate) when (confirmDuplicate(duplicate.Existing))
            {
                return await _importer.ImportAsync(label, authJson, true, cancellationToken);
            }
        }
        finally
        {
            var fullHome = Path.GetFullPath(codexHome);
            var rootPrefix = _temporaryRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (fullHome.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) && Directory.Exists(fullHome))
            {
                Directory.Delete(fullHome, recursive: true);
            }
        }
    }
}
