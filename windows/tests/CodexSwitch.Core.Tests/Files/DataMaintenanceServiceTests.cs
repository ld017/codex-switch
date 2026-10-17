using CodexSwitch.Core.Files;

namespace CodexSwitch.Core.Tests.Files;

public sealed class DataMaintenanceServiceTests
{
    [Fact]
    public async Task Maintenance_limits_completed_backups_and_expired_logs_but_keeps_unknown_recovery_data()
    {
        using var directory = new TestDirectory();
        var temp = directory.File("tmp"); var logs = directory.File("logs"); var backups = directory.File("auth-backups");
        Directory.CreateDirectory(logs); Directory.CreateDirectory(backups);
        for (var i = 0; i < 12; i++)
        {
            var path = Path.Combine(backups, $"auth-20260101-{i:000000}-{Guid.NewGuid():N}.json");
            File.WriteAllText(path, "not-read"); File.WriteAllText(path + ".cleanup-ready", "");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-i));
        }
        var unknown = Path.Combine(backups, $"auth-20200101-000000-{Guid.NewGuid():N}.json");
        File.WriteAllText(unknown, "unknown-not-read");
        var expired = Path.Combine(logs, "codex-switch.log.2");
        File.WriteAllText(expired, "old"); File.SetLastWriteTimeUtc(expired, DateTime.UtcNow.AddDays(-15));
        File.WriteAllText(Path.Combine(logs, "personal.log.2"), "keep");

        var result = await new DataMaintenanceService(temp, logs, backups).RunAsync(default);

        Assert.Equal(2, result.DeletedAuthBackups);
        Assert.Equal(1, result.DeletedLogFiles);
        Assert.True(File.Exists(unknown));
        Assert.Equal(11, Directory.GetFiles(backups, "*.json").Length);
        Assert.True(File.Exists(Path.Combine(logs, "personal.log.2")));
    }

    [Fact]
    public async Task Maintenance_always_retains_latest_completed_backup_even_after_thirty_days()
    {
        using var directory = new TestDirectory();
        var backups = directory.File("backups"); Directory.CreateDirectory(backups);
        for (var i = 0; i < 2; i++)
        {
            var path = Path.Combine(backups, $"auth-20200101-00000{i}-{Guid.NewGuid():N}.json");
            File.WriteAllText(path, "not-read"); File.WriteAllText(path + ".cleanup-ready", "");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-40 - i));
        }
        var result = await new DataMaintenanceService(directory.File("tmp"), directory.File("logs"), backups).RunAsync(default);
        Assert.Equal(1, result.DeletedAuthBackups);
        Assert.Single(Directory.GetFiles(backups, "*.json"));
    }

    [Fact]
    public async Task Maintenance_reports_recovery_and_unknown_credentials_without_deleting_them()
    {
        using var directory = new TestDirectory();
        var temp = directory.File("tmp"); Directory.CreateDirectory(temp);
        var recovery = Path.Combine(temp, $"usage-{Guid.NewGuid():N}-{Guid.NewGuid():N}");
        using (var lease = TemporaryHomeLease.Create(recovery)) lease.MarkRecoveryRequired();
        var unknown = Path.Combine(temp, $"login-{Guid.NewGuid():N}"); Directory.CreateDirectory(unknown);
        File.WriteAllText(Path.Combine(unknown, "auth.json"), "preserve");

        var result = await new DataMaintenanceService(temp, directory.File("logs"), directory.File("backups")).RunAsync(default);

        Assert.Equal(1, result.RecoveryDirectories);
        Assert.Equal(1, result.UnknownCredentialDirectories);
        Assert.Equal(0, result.DeletedTemporaryHomes);
    }

    [Fact]
    public async Task Maintenance_caps_old_logs_even_when_nothing_is_appended()
    {
        using var directory = new TestDirectory();
        var logs = directory.File("logs"); Directory.CreateDirectory(logs);
        var path = Path.Combine(logs, "codex-switch.log.1");
        File.WriteAllText(path, new string('a', RotatingLogWriter.MaximumFileBytes + 100) + "\nretained\n");
        await new DataMaintenanceService(directory.File("tmp"), logs, directory.File("backups")).RunAsync(default);
        Assert.Equal("retained\n", File.ReadAllText(path));
    }
}
