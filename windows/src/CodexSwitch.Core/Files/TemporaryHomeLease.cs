namespace CodexSwitch.Core.Files;

/// <summary>Protects an isolated CODEX_HOME until its credentials have been committed or retained for recovery.</summary>
public sealed class TemporaryHomeLease : IDisposable
{
    public const string LeaseFileName = ".active-lease";
    public const string PendingFileName = "pending-credentials";
    public const string CleanupReadyFileName = "cleanup-ready";
    public const string RecoveryFileName = "auth-recovery-required";
    private readonly string _home;
    private FileStream? _lease;

    private TemporaryHomeLease(string home, FileStream lease) { _home = home; _lease = lease; }

    public static TemporaryHomeLease Create(string home)
    {
        var fullHome = Path.GetFullPath(home);
        if (!MaintenancePaths.IsSafe(fullHome, fullHome)) throw new IOException("Unsafe isolated home path.");
        Directory.CreateDirectory(fullHome);
        foreach (var name in new[] { LeaseFileName, PendingFileName, CleanupReadyFileName, RecoveryFileName })
            if (!MaintenancePaths.IsSafe(fullHome, Path.Combine(fullHome, name))) throw new IOException("Unsafe isolated home marker.");
        var lease = new FileStream(Path.Combine(fullHome, LeaseFileName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            File.WriteAllText(Path.Combine(fullHome, PendingFileName), string.Empty);
            File.Delete(Path.Combine(fullHome, CleanupReadyFileName));
            return new TemporaryHomeLease(fullHome, lease);
        }
        catch { lease.Dispose(); throw; }
    }

    public void MarkCleanupReady()
    {
        ObjectDisposedException.ThrowIf(_lease is null, this);
        if (HasRecoveryRequired(_home)) return;
        try
        {
            File.WriteAllText(Path.Combine(_home, CleanupReadyFileName), string.Empty);
            File.Delete(Path.Combine(_home, PendingFileName));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // The business commit already succeeded. Leave the pending state for inspection.
        }
    }

    public void MarkRecoveryRequired()
    {
        ObjectDisposedException.ThrowIf(_lease is null, this);
        // A failed marker write leaves pending-credentials in place, which is also protected.
        TryMarker(() => File.WriteAllText(Path.Combine(_home, PendingFileName), string.Empty));
        TryMarker(() => File.WriteAllText(Path.Combine(_home, RecoveryFileName), string.Empty));
        TryMarker(() => File.Delete(Path.Combine(_home, CleanupReadyFileName)));
    }

    public static bool HasRecoveryRequired(string home) => Path.Exists(Path.Combine(home, RecoveryFileName));
    public static bool IsCleanupReady(string home) => File.Exists(Path.Combine(home, CleanupReadyFileName))
        && !Path.Exists(Path.Combine(home, PendingFileName)) && !HasRecoveryRequired(home);

    public static bool IsActive(string home)
    {
        var path = Path.Combine(home, LeaseFileName);
        if (!File.Exists(path)) return false;
        try
        {
            using var lease = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return true; }
    }

    public void Dispose() => Interlocked.Exchange(ref _lease, null)?.Dispose();

    private static void TryMarker(Action action)
    {
        try { action(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
    }
}
