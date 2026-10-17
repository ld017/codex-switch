using CodexSwitch.Core.Usage;

namespace CodexSwitch.Core.Files;

public sealed record DataMaintenanceResult(int DeletedTemporaryHomes, int DeletedLogFiles, int DeletedAuthBackups,
    int RecoveryDirectories, int UnknownCredentialDirectories, int Failures, long FreedBytes);

/// <summary>Reclaims only recognized, completed data; reads metadata rather than credential or log contents.</summary>
public sealed class DataMaintenanceService(string tempRoot, string logDir, string authBackupDir)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<DataMaintenanceResult> RunAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var temporary = await new TemporaryDirectoryCleaner().CleanStaleWithResultAsync(tempRoot, TimeSpan.FromHours(1), cancellationToken).ConfigureAwait(false);
            var logFiles = 0; var backups = 0; var failures = temporary.Failures; var bytes = temporary.FreedBytes;
            try
            {
                if (!RotatingLogWriter.TryCapKnownLogs(logDir, out var cappedBytes)) failures++;
                bytes += cappedBytes;
                if (Directory.Exists(logDir) && MaintenancePaths.IsSafe(logDir, logDir))
                {
                    lock (RotatingLogWriter.LogGate)
                    {
                        foreach (var path in Directory.EnumerateFiles(logDir))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (!MaintenancePaths.IsLogArchive(Path.GetFileName(path))) continue;
                            try
                            {
                                if (!MaintenancePaths.IsSafe(logDir, path) || File.GetLastWriteTimeUtc(path) > DateTime.UtcNow.AddDays(-14)) continue;
                                var size = new FileInfo(path).Length;
                                File.Delete(path); logFiles++; bytes += size;
                            }
                            catch (Exception error) when (IsFileError(error)) { failures++; }
                        }
                    }
                }
            }
            catch (Exception error) when (IsFileError(error)) { failures++; }
            try
            {
                if (Directory.Exists(authBackupDir) && MaintenancePaths.IsSafe(authBackupDir, authBackupDir))
                {
                    var eligible = new List<FileInfo>();
                    foreach (var path in Directory.EnumerateFiles(authBackupDir, "auth-*.json"))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        try
                        {
                            if (MaintenancePaths.IsAuthBackup(Path.GetFileName(path)) && MaintenancePaths.IsSafe(authBackupDir, path)
                                && MaintenancePaths.IsSafe(authBackupDir, path + ".cleanup-ready") && File.Exists(path + ".cleanup-ready")
                                && !File.Exists(path + ".auth-recovery-required")) eligible.Add(new FileInfo(path));
                        }
                        catch (Exception error) when (IsFileError(error)) { failures++; }
                    }
                    var ordered = eligible.OrderByDescending(file => file.LastWriteTimeUtc).ThenByDescending(file => file.Name, StringComparer.Ordinal).ToArray();
                    for (var index = 1; index < ordered.Length; index++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var file = ordered[index];
                        if (index < 10 && file.LastWriteTimeUtc > DateTime.UtcNow.AddDays(-30)) continue;
                        try
                        {
                            if (!MaintenancePaths.IsSafe(authBackupDir, file.FullName) || !File.Exists(file.FullName + ".cleanup-ready")
                                || File.Exists(file.FullName + ".auth-recovery-required")) continue;
                            var size = file.Length;
                            file.Delete(); backups++; bytes += size;
                            File.Delete(file.FullName + ".cleanup-ready");
                        }
                        catch (Exception error) when (IsFileError(error)) { failures++; }
                    }
                }
            }
            catch (Exception error) when (IsFileError(error)) { failures++; }
            return new(temporary.DeletedDirectories, logFiles, backups, temporary.RecoveryDirectories,
                temporary.UnknownCredentialDirectories, failures, bytes);
        }
        finally { _gate.Release(); }
    }

    private static bool IsFileError(Exception error) => error is IOException or UnauthorizedAccessException or System.Security.SecurityException;
}
