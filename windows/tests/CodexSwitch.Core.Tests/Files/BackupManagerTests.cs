using CodexSwitch.Core.Files;

namespace CodexSwitch.Core.Tests.Files;

public sealed class BackupManagerTests
{
    [Fact]
    public void CreateBackup_retains_ten_newest_backups()
    {
        using var directory = new TestDirectory();
        var source = directory.File("config.toml");
        File.WriteAllText(source, "source");
        var backupDirectory = directory.File("backups");
        var clock = new IncrementingClock(new DateTimeOffset(2026, 9, 15, 8, 0, 0, TimeSpan.Zero));
        var manager = new BackupManager(backupDirectory, retentionLimit: 10, clock.GetUtcNow);

        for (var index = 0; index < 12; index++)
        {
            manager.CreateBackup(source);
        }

        var backups = Directory.GetFiles(backupDirectory, "config-*.toml");
        Assert.Equal(10, backups.Length);
        Assert.DoesNotContain(backups, path => System.IO.Path.GetFileName(path).Contains("080000", StringComparison.Ordinal));
        Assert.DoesNotContain(backups, path => System.IO.Path.GetFileName(path).Contains("080001", StringComparison.Ordinal));
    }

    private sealed class IncrementingClock(DateTimeOffset now)
    {
        private DateTimeOffset _now = now;

        public DateTimeOffset GetUtcNow()
        {
            var value = _now;
            _now = _now.AddSeconds(1);
            return value;
        }
    }
}
