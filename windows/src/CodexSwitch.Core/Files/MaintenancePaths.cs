using System.Text.RegularExpressions;

namespace CodexSwitch.Core.Files;

internal static partial class MaintenancePaths
{
    internal static bool IsGeneratedHome(string name) => GeneratedHomeRegex().IsMatch(name);
    internal static bool IsAuthBackup(string name) => AuthBackupRegex().IsMatch(name);
    internal static bool IsProviderBackup(string name) => ProviderBackupRegex().IsMatch(name);
    internal static bool IsLogFile(string name) => name is "codex-switch.log" or "codex-switch-crash.log";
    internal static bool IsLogArchive(string name) => LogArchiveRegex().IsMatch(name);

    internal static bool IsSafe(string root, string candidate)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullPath = Path.GetFullPath(candidate);
        if (!string.Equals(fullRoot, fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
            && !fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;
        // Check every existing ancestor, including the configured root itself.
        for (string? current = fullPath; current is not null; current = Path.GetDirectoryName(current))
        {
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false; }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        return true;
    }

    internal static bool IsSafeTree(string root, string path, out long bytes)
    {
        bytes = 0;
        var pending = new Stack<string>();
        pending.Push(path);
        while (pending.TryPop(out var directory))
        {
            if (!IsSafe(root, directory)) return false;
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                if (!IsSafe(root, entry)) return false;
                if (Directory.Exists(entry)) pending.Push(entry);
                else bytes += new FileInfo(entry).Length;
            }
        }
        return true;
    }

    [GeneratedRegex("^(?:usage-[0-9a-f]{32}-[0-9a-f]{32}|login-[0-9a-f]{32})$", RegexOptions.CultureInvariant)]
    private static partial Regex GeneratedHomeRegex();
    [GeneratedRegex("^auth-[0-9]{8}-[0-9]{6}-[0-9a-f]{32}\\.json$", RegexOptions.CultureInvariant)]
    private static partial Regex AuthBackupRegex();
    [GeneratedRegex("^config-[0-9]{8}-[0-9]{6}-[0-9]{7}\\.toml$", RegexOptions.CultureInvariant)]
    private static partial Regex ProviderBackupRegex();
    [GeneratedRegex("^codex-switch(?:-crash)?\\.log\\.[1-3]$", RegexOptions.CultureInvariant)]
    private static partial Regex LogArchiveRegex();
}
