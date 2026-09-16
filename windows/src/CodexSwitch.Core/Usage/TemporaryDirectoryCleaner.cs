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
        foreach (var delay in _delays)
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken);
            }

            try
            {
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
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        var cutoff = DateTime.UtcNow - minimumAge;
        foreach (var directory in new DirectoryInfo(root).EnumerateDirectories("usage-*", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (directory.LastWriteTimeUtc <= cutoff)
            {
                _ = await TryDeleteAsync(directory.FullName, cancellationToken);
            }
        }
    }
}
