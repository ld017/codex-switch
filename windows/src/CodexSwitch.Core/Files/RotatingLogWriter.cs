using System.Text;
using CodexSwitch.Core.Diagnostics;

namespace CodexSwitch.Core.Files;

public static class RotatingLogWriter
{
    public const int MaximumFileBytes = 2 * 1024 * 1024;
    public const int MaximumEntryBytes = 8 * 1024;
    public const int ArchiveCount = 3;
    internal static readonly object LogGate = new();

    public static bool TryAppend(string directory, string title, string detail, string fileName = "codex-switch.log")
    {
        if (!MaintenancePaths.IsLogFile(fileName)) return false;
        try
        {
            var path = Path.Combine(directory, fileName);
            if (!MaintenancePaths.IsSafe(directory, path)) return false;
            var value = $"{DateTimeOffset.Now:O} {SecretRedactor.Redact(title)}: {SecretRedactor.Redact(detail)}";
            // Redaction happens before byte truncation, so a cut JSON key cannot expose its value.
            var budget = MaximumEntryBytes - Encoding.UTF8.GetByteCount(Environment.NewLine);
            var bytes = new byte[MaximumEntryBytes];
            Encoding.UTF8.GetEncoder().Convert(value.AsSpan(), bytes.AsSpan(0, budget), flush: true,
                out _, out var bytesUsed, out _);
            bytesUsed += Encoding.UTF8.GetBytes(Environment.NewLine.AsSpan(), bytes.AsSpan(bytesUsed));
            lock (LogGate)
            {
                Directory.CreateDirectory(directory);
                if (!TryCapFile(directory, path)) return false;
                for (var i = 1; i <= ArchiveCount; i++)
                    if (!TryCapFile(directory, path + "." + i)) return false;
                if (File.Exists(path) && new FileInfo(path).Length + bytesUsed > MaximumFileBytes)
                {
                    File.Delete(path + "." + ArchiveCount);
                    for (var i = ArchiveCount - 1; i >= 1; i--)
                        if (File.Exists(path + "." + i)) File.Move(path + "." + i, path + "." + (i + 1));
                    File.Move(path, path + ".1");
                }
                using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
                stream.Write(bytes.AsSpan(0, bytesUsed));
            }
            return true;
        }
        catch { return false; }
    }

    public static bool TryCapKnownLogs(string directory)
        => TryCapKnownLogs(directory, out _);

    public static bool TryCapKnownLogs(string directory, out long freedBytes)
    {
        freedBytes = 0;
        try
        {
            if (!Directory.Exists(directory)) return true;
            lock (LogGate)
            {
                if (!MaintenancePaths.IsSafe(directory, directory)) return false;
                var succeeded = true;
                foreach (var name in new[] { "codex-switch.log", "codex-switch-crash.log" })
                {
                    succeeded &= TryCapFile(directory, Path.Combine(directory, name), out var cappedBytes);
                    freedBytes += cappedBytes;
                    for (var index = 1; index <= ArchiveCount; index++)
                    {
                        succeeded &= TryCapFile(directory, Path.Combine(directory, name + "." + index), out cappedBytes);
                        freedBytes += cappedBytes;
                    }
                }
                return succeeded;
            }
        }
        catch { return false; }
    }

    private static bool TryCapFile(string directory, string path)
        => TryCapFile(directory, path, out _);

    private static bool TryCapFile(string directory, string path, out long freedBytes)
    {
        freedBytes = 0;
        string? temporary = null;
        long removedBytes = 0;
        try
        {
            if (!MaintenancePaths.IsSafe(directory, path)) return false;
            if (!File.Exists(path) || new FileInfo(path).Length <= MaximumFileBytes) return true;
            temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
            if (!MaintenancePaths.IsSafe(directory, temporary)) return false;
            var modified = File.GetLastWriteTimeUtc(path);
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var length = input.Length;
                if (length <= MaximumFileBytes) return true;
                var buffer = new byte[MaximumFileBytes];
                input.Seek(length - MaximumFileBytes, SeekOrigin.Begin);
                input.ReadExactly(buffer);
                // The tail may begin inside UTF-8 or a log entry. Keep complete following lines only.
                var firstNewline = Array.IndexOf(buffer, (byte)'\n');
                var start = firstNewline < 0 ? buffer.Length : firstNewline + 1;
                removedBytes = length - (buffer.Length - start);
                using var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
                output.Write(buffer.AsSpan(start));
                output.Flush(flushToDisk: true);
            }
            File.SetLastWriteTimeUtc(temporary, modified);
            if (!MaintenancePaths.IsSafe(directory, path) || !MaintenancePaths.IsSafe(directory, temporary)) return false;
            File.Replace(temporary, path, destinationBackupFileName: null);
            freedBytes = removedBytes;
            return true;
        }
        catch { return false; }
        finally
        {
            try { if (temporary is not null && MaintenancePaths.IsSafe(directory, temporary)) File.Delete(temporary); }
            catch { /* A failed best-effort cleanup must not replace the append result. */ }
        }
    }
}
