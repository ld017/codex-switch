using CodexSwitch.Core.Usage;

namespace CodexSwitch.Core.Tests.Usage;

public sealed class TemporaryDirectoryCleanerTests
{
    [Fact]
    public async Task TryDeleteAsync_retries_transient_io_failure()
    {
        var attempts = 0;
        var cleaner = new TemporaryDirectoryCleaner(
            delete: _ =>
            {
                attempts++;
                if (attempts < 3) throw new IOException("locked");
            },
            delays: [TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero]);

        var deleted = await cleaner.TryDeleteAsync("safe-test-path", default);

        Assert.True(deleted);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task CleanStaleAsync_skips_recent_directory()
    {
        using var directory = new TestDirectory();
        var recent = Directory.CreateDirectory(directory.File("usage-recent"));
        var old = Directory.CreateDirectory(directory.File("usage-old"));
        old.LastWriteTimeUtc = DateTime.UtcNow.AddHours(-1);
        var cleaner = new TemporaryDirectoryCleaner(delays: [TimeSpan.Zero]);

        await cleaner.CleanStaleAsync(directory.Path, TimeSpan.FromMinutes(5), default);

        Assert.True(recent.Exists);
        Assert.False(Directory.Exists(old.FullName));
    }
}
