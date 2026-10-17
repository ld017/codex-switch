using CodexSwitch.Core.Files;

namespace CodexSwitch.Core.Usage;

public interface ITemporaryDirectoryCleaner
{
    Task<bool> TryDeleteAsync(string path, CancellationToken cancellationToken);

    Task CleanStaleAsync(string root, TimeSpan minimumAge, CancellationToken cancellationToken);
}

public sealed class TemporaryDirectoryCleaner : ITemporaryDirectoryCleaner
{
    private static readonly IReadOnlyList<TimeSpan> DefaultDelays =
        [TimeSpan.Zero, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1)];

    private readonly Action<string> _delete;
    private readonly IReadOnlyList<TimeSpan> _delays;

    public TemporaryDirectoryCleaner(Action<string>? delete = null, IReadOnlyList<TimeSpan>? delays = null)
    {
        _delete = delete ?? (path => Directory.Delete(path, recursive: true));
        _delays = delays ?? DefaultDelays;
    }

    public async Task<bool> TryDeleteAsync(string path, CancellationToken cancellationToken)
    {
        path = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!MaintenancePaths.IsGeneratedHome(Path.GetFileName(path))) return false;
        var root = Path.GetDirectoryName(path)!;
        foreach (var delay in _delays)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken);
            }

            try
            {
                if (!MaintenancePaths.IsSafe(root, path)) return false;
                if (!Directory.Exists(path)) return true;
                if (!MaintenancePaths.IsSafeTree(root, path, out _) || TemporaryHomeLease.IsActive(path) || !MayDeleteCredentials(path)) return false;
                _delete(path);
                return true;
            }
            catch (DirectoryNotFoundException)
            {
                return true;
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return !Directory.Exists(path);
    }

    public async Task CleanStaleAsync(string root, TimeSpan minimumAge, CancellationToken cancellationToken)
        => _ = await CleanStaleWithResultAsync(root, minimumAge, cancellationToken);

    public async Task<TemporaryCleanupResult> CleanStaleWithResultAsync(string root, TimeSpan minimumAge, CancellationToken cancellationToken)
    {
        var deleted = 0; var failures = 0; var recovery = 0; var unknown = 0; long bytes = 0;
        if (!Directory.Exists(root)) return new(0, 0, 0, 0, 0);
        try
        {
            if (!MaintenancePaths.IsSafe(root, root)) return new(0, 1, 0, 0, 0);
            // A caller cannot accidentally shorten the crash-recovery grace period.
            var cutoff = DateTime.UtcNow - (minimumAge < TimeSpan.FromHours(1) ? TimeSpan.FromHours(1) : minimumAge);
            foreach (var path in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!MaintenancePaths.IsGeneratedHome(Path.GetFileName(path))) continue;
                try
                {
                    if (!MaintenancePaths.IsSafeTree(root, path, out var size)) continue;
                    if (TemporaryHomeLease.HasRecoveryRequired(path)) { recovery++; continue; }
                    if (TemporaryHomeLease.IsActive(path)) continue;
                    if (!MayDeleteCredentials(path)) { unknown++; continue; }
                    if (Directory.GetLastWriteTimeUtc(path) > cutoff) continue;
                    if (await TryDeleteAsync(path, cancellationToken)) { deleted++; bytes += size; }
                    else failures++;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException) { failures++; }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException) { failures++; }
        return new(deleted, failures, recovery, unknown, bytes);
    }

    private static bool MayDeleteCredentials(string home)
        => !TemporaryHomeLease.HasRecoveryRequired(home)
           && !Path.Exists(Path.Combine(home, TemporaryHomeLease.PendingFileName))
           && (TemporaryHomeLease.IsCleanupReady(home)
               || !HasCredentialFiles(home));

    public static bool HasCredentialFiles(string home, IReadOnlyCollection<string>? allowedRelativePaths = null)
    {
        // Only explicitly verified relative files may be exempted. A matching basename in a
        // nested directory is not the same file, and interrupted atomic writes remain protected.
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in allowedRelativePaths ?? [])
        {
            var relative = value.Replace('\\', '/');
            if (!Path.IsPathRooted(value) && !relative.Split('/').Any(segment => segment is "" or "." or "..")) allowed.Add(relative);
        }
        try
        {
            if (!MaintenancePaths.IsSafe(home, home)) return true;
            if (!Directory.Exists(home)) return false;
            var pending = new Stack<string>(); pending.Push(home);
            while (pending.TryPop(out var directory))
                foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    if (!MaintenancePaths.IsSafe(home, entry)) return true;
                    var relative = Path.GetRelativePath(home, entry).Replace('\\', '/');
                    var name = Path.GetFileName(entry);
                    if (Directory.Exists(entry)) { pending.Push(entry); continue; }
                    if (relative is TemporaryHomeLease.LeaseFileName or TemporaryHomeLease.PendingFileName
                        or TemporaryHomeLease.CleanupReadyFileName or TemporaryHomeLease.RecoveryFileName) continue;
                    if (!allowed.Contains(relative) && (name.Contains("auth", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("credential", StringComparison.OrdinalIgnoreCase) || name.Contains("token", StringComparison.OrdinalIgnoreCase))) return true;
                }
            return false;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return true; }
    }
}

public sealed record TemporaryCleanupResult(int DeletedDirectories, int Failures, int RecoveryDirectories,
    int UnknownCredentialDirectories, long FreedBytes);
