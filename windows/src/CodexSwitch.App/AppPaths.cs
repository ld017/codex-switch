namespace CodexSwitch.App;

public sealed class AppPaths
{
    public AppPaths(IReadOnlyDictionary<string, string?> environment)
    {
        var user = environment.GetValueOrDefault("USERPROFILE") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        CodexHome = environment.GetValueOrDefault("CODEX_HOME") ?? Path.Combine(user, ".codex");
        DataHome = environment.GetValueOrDefault("CODEX_SWITCH_DATA_HOME") ?? Path.Combine(environment.GetValueOrDefault("LOCALAPPDATA") ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexSwitch");
    }

    public string CodexHome { get; }
    public string DataHome { get; }
    public string ConfigPath => Path.Combine(CodexHome, "config.toml");
    public string AuthPath => Path.Combine(CodexHome, "auth.json");
    public string TemporaryRoot => Path.Combine(DataHome, "tmp");
    public string UsageCachePath => Path.Combine(DataHome, "cache.json");
    public string LogDirectory => Path.Combine(DataHome, "logs");
    public string ProviderBackupDirectory => Path.Combine(CodexHome, "provider-switcher", "backups");
    public string AccountBackupDirectory => Path.Combine(CodexHome, "provider-switcher", "auth-backups");
}
