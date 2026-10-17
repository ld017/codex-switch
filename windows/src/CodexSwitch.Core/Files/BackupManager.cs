namespace CodexSwitch.Core.Files;

public sealed class BackupManager
{
    private readonly string _backupDirectory;
    private readonly int _retentionLimit;
    private readonly Func<DateTimeOffset> _utcNow;

    public BackupManager(
        string backupDirectory,
        int retentionLimit,
        Func<DateTimeOffset>? utcNow = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupDirectory);
        ArgumentOutOfRangeException.ThrowIfLessThan(retentionLimit, 1);
        _backupDirectory = backupDirectory;
        _retentionLimit = retentionLimit;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public string CreateBackup(string sourcePath)
    {
        if (!MaintenancePaths.IsSafe(_backupDirectory, _backupDirectory)) throw new IOException("Unsafe provider backup directory.");
        Directory.CreateDirectory(_backupDirectory);
        var stamp = _utcNow().UtcDateTime.ToString("yyyyMMdd-HHmmss-fffffff", System.Globalization.CultureInfo.InvariantCulture);
        var destination = Path.Combine(_backupDirectory, $"config-{stamp}.toml");
        File.Copy(sourcePath, destination, overwrite: false);
        try { Prune(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { /* The completed byte backup remains valid even when old-file cleanup is unavailable. */ }
        return destination;
    }

    private void Prune()
    {
        var stale = new DirectoryInfo(_backupDirectory)
            .EnumerateFiles("config-*.toml", SearchOption.TopDirectoryOnly)
            .Where(file => MaintenancePaths.IsProviderBackup(file.Name) && MaintenancePaths.IsSafe(_backupDirectory, file.FullName))
            .OrderByDescending(file => file.Name, StringComparer.Ordinal)
            .Skip(_retentionLimit);
        foreach (var file in stale)
        {
            try { file.Delete(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { /* Retry on the next successful backup. */ }
        }
    }
}
