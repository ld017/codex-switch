using CodexSwitch.Core.Files;
using CodexSwitch.Core.Usage;

namespace CodexSwitch.Core.Tests.Files;

public sealed class TemporaryHomeLeaseTests
{
    [Fact]
    public async Task Active_and_recovery_homes_are_not_reclaimed_but_finished_home_is()
    {
        using var directory = new TestDirectory();
        var home = directory.File($"usage-{Guid.NewGuid():N}-{Guid.NewGuid():N}");
        using (var lease = TemporaryHomeLease.Create(home))
        {
            File.WriteAllText(Path.Combine(home, "auth.json"), "sensitive-content");
            Assert.True(TemporaryHomeLease.IsActive(home));
            Assert.True(File.Exists(Path.Combine(home, "pending-credentials")));
            lease.MarkCleanupReady();
            Directory.SetLastWriteTimeUtc(home, DateTime.UtcNow.AddHours(-2));
            await new TemporaryDirectoryCleaner().CleanStaleAsync(directory.Path, TimeSpan.FromHours(1), default);
            Assert.True(Directory.Exists(home));
        }
        Assert.False(TemporaryHomeLease.IsActive(home));
        Directory.SetLastWriteTimeUtc(home, DateTime.UtcNow.AddHours(-2));
        await new TemporaryDirectoryCleaner().CleanStaleAsync(directory.Path, TimeSpan.FromHours(1), default);
        Assert.False(Directory.Exists(home));
    }

    [Fact]
    public async Task Unknown_pending_and_recovery_credentials_are_preserved()
    {
        using var directory = new TestDirectory();
        foreach (var kind in new[] { "unknown", "pending", "recovery" })
        {
            var home = directory.File($"login-{Guid.NewGuid():N}");
            Directory.CreateDirectory(home);
            File.WriteAllText(Path.Combine(home, "auth.json"), "unread-credentials");
            if (kind != "unknown")
            {
                using var lease = TemporaryHomeLease.Create(home);
                if (kind == "recovery") lease.MarkRecoveryRequired();
            }
            Directory.SetLastWriteTimeUtc(home, DateTime.UtcNow.AddHours(-2));
        }
        await new TemporaryDirectoryCleaner().CleanStaleAsync(directory.Path, TimeSpan.FromHours(1), default);
        Assert.Equal(3, Directory.GetDirectories(directory.Path).Length);
    }

    [Fact]
    public async Task Only_strict_generated_empty_homes_are_reclaimed()
    {
        using var directory = new TestDirectory();
        var generated = Directory.CreateDirectory(directory.File($"login-{Guid.NewGuid():N}"));
        var unknown = Directory.CreateDirectory(directory.File("usage-personal-notes"));
        generated.LastWriteTimeUtc = unknown.LastWriteTimeUtc = DateTime.UtcNow.AddHours(-2);
        await new TemporaryDirectoryCleaner().CleanStaleAsync(directory.Path, TimeSpan.FromHours(1), default);
        Assert.False(Directory.Exists(generated.FullName));
        Assert.True(Directory.Exists(unknown.FullName));
    }

    [Fact]
    public void Recovery_marker_cannot_be_overridden_by_cleanup_ready()
    {
        using var directory = new TestDirectory();
        var home = directory.File($"login-{Guid.NewGuid():N}");
        using var lease = TemporaryHomeLease.Create(home);
        lease.MarkRecoveryRequired();
        lease.MarkCleanupReady();
        Assert.False(TemporaryHomeLease.IsCleanupReady(home));
    }

    [Fact]
    public void Marker_write_failures_leave_pending_and_do_not_throw()
    {
        using var directory = new TestDirectory();
        var home = directory.File($"login-{Guid.NewGuid():N}");
        using var lease = TemporaryHomeLease.Create(home);
        Directory.CreateDirectory(Path.Combine(home, "cleanup-ready"));
        lease.MarkCleanupReady();
        Assert.True(File.Exists(Path.Combine(home, "pending-credentials")));
        Directory.CreateDirectory(Path.Combine(home, "auth-recovery-required"));
        lease.MarkRecoveryRequired();
        Assert.True(File.Exists(Path.Combine(home, "pending-credentials")));
    }

    [Fact]
    public async Task Cleaner_preserves_interrupted_atomic_credential_write_without_auth_json()
    {
        using var directory = new TestDirectory();
        var home = Directory.CreateDirectory(directory.File($"login-{Guid.NewGuid():N}"));
        File.WriteAllText(Path.Combine(home.FullName, $".auth.json.{Guid.NewGuid():N}.tmp"), "do-not-read");
        home.LastWriteTimeUtc = DateTime.UtcNow.AddHours(-2);
        await new TemporaryDirectoryCleaner().CleanStaleAsync(directory.Path, TimeSpan.FromHours(1), default);
        Assert.True(Directory.Exists(home.FullName));
    }

    [Fact]
    public void Credential_scan_allows_only_verified_relative_files_and_ignores_own_markers()
    {
        using var directory = new TestDirectory();
        var home = directory.File($"login-{Guid.NewGuid():N}");
        using var lease = TemporaryHomeLease.Create(home);
        Assert.False(TemporaryDirectoryCleaner.HasCredentialFiles(home));
        File.WriteAllText(Path.Combine(home, "auth.json"), "no-read");
        Assert.True(TemporaryDirectoryCleaner.HasCredentialFiles(home));
        Assert.False(TemporaryDirectoryCleaner.HasCredentialFiles(home, ["auth.json"]));
        var nested = Path.Combine(home, "nested"); Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, "auth.json"), "no-read");
        Assert.True(TemporaryDirectoryCleaner.HasCredentialFiles(home, ["auth.json"]));
        Assert.False(TemporaryDirectoryCleaner.HasCredentialFiles(home, ["auth.json", "nested/auth.json"]));
        File.WriteAllText(Path.Combine(home, $".auth.json.{Guid.NewGuid():N}.tmp"), "no-read");
        Assert.True(TemporaryDirectoryCleaner.HasCredentialFiles(home, ["auth.json", "nested/auth.json"]));
    }

    [Fact]
    public async Task Cleaner_skips_reparse_points_and_their_targets()
    {
        using var directory = new TestDirectory();
        var root = directory.File("tmp"); Directory.CreateDirectory(root);
        var outside = directory.File("outside"); Directory.CreateDirectory(outside);
        var sentinel = Path.Combine(outside, "sentinel.txt"); File.WriteAllText(sentinel, "keep");
        var link = Path.Combine(root, $"login-{Guid.NewGuid():N}");
        try { Directory.CreateSymbolicLink(link, outside); }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException or PlatformNotSupportedException) { return; }
        try
        {
            await new TemporaryDirectoryCleaner().CleanStaleAsync(root, TimeSpan.FromHours(1), default);
            Assert.True(TemporaryDirectoryCleaner.HasCredentialFiles(link));
            Assert.True(File.Exists(sentinel));
            Assert.True(Directory.Exists(link));
        }
        finally { Directory.Delete(link); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Cleaner_skips_a_linked_root_or_any_link_inside_a_finished_home(bool linkedRoot)
    {
        using var directory = new TestDirectory();
        var root = directory.File("tmp"); Directory.CreateDirectory(root);
        var outside = directory.File("outside"); Directory.CreateDirectory(outside);
        var targetHome = Path.Combine(outside, $"login-{Guid.NewGuid():N}");
        using (var lease = TemporaryHomeLease.Create(targetHome)) lease.MarkCleanupReady();
        File.WriteAllText(Path.Combine(targetHome, "sentinel.txt"), "keep");
        Directory.SetLastWriteTimeUtc(targetHome, DateTime.UtcNow.AddHours(-2));
        string link;
        if (linkedRoot) link = directory.File("linked-root");
        else
        {
            var home = Path.Combine(root, $"login-{Guid.NewGuid():N}");
            using (var lease = TemporaryHomeLease.Create(home)) lease.MarkCleanupReady();
            link = Path.Combine(home, "linked-child");
        }
        try { Directory.CreateSymbolicLink(link, outside); }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException or PlatformNotSupportedException) { return; }
        try
        {
            if (!linkedRoot) Directory.SetLastWriteTimeUtc(Path.GetDirectoryName(link)!, DateTime.UtcNow.AddHours(-2));
            await new TemporaryDirectoryCleaner().CleanStaleAsync(linkedRoot ? link : root, TimeSpan.FromHours(1), default);
            Assert.True(TemporaryDirectoryCleaner.HasCredentialFiles(linkedRoot ? link : Path.GetDirectoryName(link)!));
            Assert.True(File.Exists(Path.Combine(targetHome, "sentinel.txt")));
            Assert.True(Directory.Exists(link));
        }
        finally { Directory.Delete(link); }
    }
}
