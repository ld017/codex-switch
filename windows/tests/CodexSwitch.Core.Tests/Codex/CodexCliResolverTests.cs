using CodexSwitch.Core.Codex;

namespace CodexSwitch.Core.Tests.Codex;

public sealed class CodexCliResolverTests
{
    [Fact]
    public void Resolve_prefers_explicit_override()
    {
        using var directory = new TestDirectory();
        var explicitCli = directory.File("explicit.exe");
        File.WriteAllText(explicitCli, string.Empty);
        var pathDirectory = Directory.CreateDirectory(directory.File("path"));
        File.WriteAllText(Path.Combine(pathDirectory.FullName, "codex.exe"), string.Empty);
        var environment = new Dictionary<string, string?>
        {
            ["CODEX_CLI"] = explicitCli,
            ["PATH"] = pathDirectory.FullName,
        };

        Assert.Equal(explicitCli, CodexCliResolver.Resolve(environment));
    }

    [Fact]
    public void Resolve_uses_path_before_versioned_desktop_directory()
    {
        using var directory = new TestDirectory();
        var pathDirectory = Directory.CreateDirectory(directory.File("path"));
        var pathCli = Path.Combine(pathDirectory.FullName, "codex.exe");
        File.WriteAllText(pathCli, string.Empty);
        var localAppData = Directory.CreateDirectory(directory.File("local"));
        var bundled = Path.Combine(localAppData.FullName, "OpenAI", "Codex", "bin", "999", "codex.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(bundled)!);
        File.WriteAllText(bundled, string.Empty);
        var environment = new Dictionary<string, string?>
        {
            ["PATH"] = pathDirectory.FullName,
            ["LOCALAPPDATA"] = localAppData.FullName,
        };

        Assert.Equal(pathCli, CodexCliResolver.Resolve(environment));
    }

    [Fact]
    public void Resolve_chooses_most_recent_versioned_desktop_cli()
    {
        using var directory = new TestDirectory();
        var localAppData = Directory.CreateDirectory(directory.File("local"));
        var oldCli = CreateBundledCli(localAppData.FullName, "111", DateTime.UtcNow.AddDays(-1));
        var newCli = CreateBundledCli(localAppData.FullName, "222", DateTime.UtcNow);
        var environment = new Dictionary<string, string?>
        {
            ["PATH"] = string.Empty,
            ["LOCALAPPDATA"] = localAppData.FullName,
        };

        Assert.Equal(newCli, CodexCliResolver.Resolve(environment));
        Assert.NotEqual(oldCli, CodexCliResolver.Resolve(environment));
    }

    private static string CreateBundledCli(string localAppData, string version, DateTime timestamp)
    {
        var path = Path.Combine(localAppData, "OpenAI", "Codex", "bin", version, "codex.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Empty);
        File.SetLastWriteTimeUtc(path, timestamp);
        return path;
    }
}
