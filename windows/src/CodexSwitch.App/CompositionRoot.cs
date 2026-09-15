using System.Reflection;
using CodexSwitch.App.Windows;
using CodexSwitch.Core.Auth;
using CodexSwitch.Core.Codex;
using CodexSwitch.Core.Files;
using CodexSwitch.Core.Usage;

namespace CodexSwitch.App;

public sealed record AppServices(AppPaths Paths, AccountStore Accounts, LoginService Login, AccountSwitchService AccountSwitch,
    ProviderSwitchService ProviderSwitch, UsageRefreshCoordinator Usage, ICodexLifecycle Lifecycle, Sub2ApiProbe Sub2Api, StartupRegistration Startup);

public static class CompositionRoot
{
    public static AppServices Create()
    {
        var environment = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(entry => (string)entry.Key, entry => entry.Value?.ToString(), StringComparer.OrdinalIgnoreCase);
        var paths = new AppPaths(environment);
        AppDataSecurity.EnsurePrivateDirectory(paths.DataHome);
        Directory.CreateDirectory(paths.LogDirectory);
        var files = new AtomicFileStore();
        var accounts = new AccountStore(paths.DataHome, new DpapiCredentialProtector(), files);
        var cli = CodexCliResolver.Resolve(environment);
        var runner = new ProcessRunner();
        var login = new LoginService(cli, paths.TemporaryRoot, runner, new AccountImportService(accounts));
        var accountSwitch = new AccountSwitchService(paths.ConfigPath, paths.AuthPath, paths.AccountBackupDirectory, accounts, files);
        var providerSwitch = new ProviderSwitchService(paths.ConfigPath, cli, files, new BackupManager(paths.ProviderBackupDirectory, 10), runner);
        var fetcher = new UsageFetcher(accounts, paths.ConfigPath, paths.TemporaryRoot, async (home, token) =>
        {
            await using var rpc = new CodexRpcClient(cli, new Dictionary<string, string?> { ["CODEX_HOME"] = home });
            await rpc.InitializeAsync("CodexSwitchWindows", Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0", token);
            return await rpc.FetchRateLimitsAsync(token);
        });
        var usage = new UsageRefreshCoordinator(accounts, fetcher, new UsageCache(paths.UsageCachePath, files));
        return new AppServices(paths, accounts, login, accountSwitch, providerSwitch, usage, new WindowsCodexLifecycle(), new Sub2ApiProbe(), new StartupRegistration("CodexSwitch", Environment.ProcessPath!));
    }
}
