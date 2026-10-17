using System.Text;
using CodexSwitch.Core.Files;

namespace CodexSwitch.Core.Tests.Files;

public sealed class RotatingLogWriterTests
{
    [Fact]
    public void Writer_redacts_before_bounding_entries_and_retains_three_archives()
    {
        using var directory = new TestDirectory();
        var path = directory.File("codex-switch.log");
        for (var i = 0; i < 5; i++)
        {
            File.WriteAllBytes(path, new byte[2 * 1024 * 1024]);
            Assert.True(RotatingLogWriter.TryAppend(directory.Path, "event", "{\"access_token\":\"do-not-log\"} " + new string('界', 10000)));
        }
        Assert.True(new FileInfo(path).Length <= 8192);
        Assert.Equal(4, Directory.GetFiles(directory.Path, "codex-switch.log*").Length);
        Assert.DoesNotContain("do-not-log", File.ReadAllText(path));
        Assert.False(File.Exists(path + ".4"));
    }

    [Fact]
    public void Writer_rejects_unknown_file_names_and_path_traversal()
    {
        using var directory = new TestDirectory();
        Assert.False(RotatingLogWriter.TryAppend(directory.Path, "title", "detail", "../outside.log"));
        Assert.False(RotatingLogWriter.TryAppend(directory.Path, "title", "detail", "personal.txt"));
        Assert.Empty(Directory.GetFiles(directory.Path));
    }

    [Fact]
    public void Writer_does_not_expose_credentials_in_short_entries_or_titles()
    {
        using var directory = new TestDirectory();
        Assert.True(RotatingLogWriter.TryAppend(directory.Path, "Authorization: Bearer title-secret", "{\"refresh_token\":\"refresh-secret\"}"));
        var value = File.ReadAllText(directory.File("codex-switch.log"));
        Assert.Contains("<redacted>", value);
        Assert.DoesNotContain("title-secret", value);
        Assert.DoesNotContain("refresh-secret", value);
    }

    [Fact]
    public void Capping_legacy_logs_keeps_complete_tail_lines_and_bounds_all_known_archives()
    {
        using var directory = new TestDirectory();
        var prefix = new string('界', 800000);
        foreach (var name in new[] { "codex-switch.log", "codex-switch-crash.log.3" })
            File.WriteAllText(directory.File(name), prefix + "\nkept line one\nkept line two\n", new UTF8Encoding(false));

        Assert.True(RotatingLogWriter.TryCapKnownLogs(directory.Path));

        foreach (var file in Directory.GetFiles(directory.Path))
        {
            Assert.True(new FileInfo(file).Length <= RotatingLogWriter.MaximumFileBytes);
            Assert.Equal("kept line one\nkept line two\n", File.ReadAllText(file));
        }
    }

    [Fact]
    public void Capping_locked_legacy_file_preserves_original_and_reports_failure()
    {
        using var directory = new TestDirectory();
        var path = directory.File("codex-switch.log");
        File.WriteAllBytes(path, new byte[RotatingLogWriter.MaximumFileBytes + 5]);
        using var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.False(RotatingLogWriter.TryCapKnownLogs(directory.Path));
        Assert.Equal(RotatingLogWriter.MaximumFileBytes + 5, locked.Length);
    }

    [Fact]
    public void Multibyte_entry_is_truncated_once_to_valid_utf8_with_a_complete_newline()
    {
        using var directory = new TestDirectory();
        Assert.True(RotatingLogWriter.TryAppend(directory.Path, new string('界', 4000), new string('界', 4000)));
        var bytes = File.ReadAllBytes(directory.File("codex-switch.log"));
        Assert.True(bytes.Length <= RotatingLogWriter.MaximumEntryBytes);
        Assert.EndsWith(Environment.NewLine, new UTF8Encoding(false, true).GetString(bytes));
    }
}
