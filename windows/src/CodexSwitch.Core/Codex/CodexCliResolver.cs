namespace CodexSwitch.Core.Codex;

public static class CodexCliResolver
{
    public static string Resolve(IReadOnlyDictionary<string, string?> environment)
    {
        if (environment.TryGetValue("CODEX_CLI", out var configured)
            && !string.IsNullOrWhiteSpace(configured)
            && File.Exists(configured))
        {
            return Path.GetFullPath(configured);
        }

        if (environment.TryGetValue("PATH", out var pathValue))
        {
            foreach (var directory in (pathValue ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var candidate = Path.Combine(directory, "codex.exe");
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
        }

        if (environment.TryGetValue("LOCALAPPDATA", out var localAppData) && !string.IsNullOrWhiteSpace(localAppData))
        {
            var binDirectory = Path.Combine(localAppData, "OpenAI", "Codex", "bin");
            if (Directory.Exists(binDirectory))
            {
                var candidate = new DirectoryInfo(binDirectory)
                    .EnumerateDirectories("*", SearchOption.TopDirectoryOnly)
                    .Where(directory => !directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    .Select(directory => new FileInfo(Path.Combine(directory.FullName, "codex.exe")))
                    .Where(file => file.Exists)
                    .OrderByDescending(file => file.LastWriteTimeUtc)
                    .ThenByDescending(file => file.DirectoryName, StringComparer.Ordinal)
                    .FirstOrDefault();
                if (candidate is not null)
                {
                    return candidate.FullName;
                }
            }
        }

        throw new FileNotFoundException("Codex CLI not found. Install Codex or set CODEX_CLI.");
    }
}
