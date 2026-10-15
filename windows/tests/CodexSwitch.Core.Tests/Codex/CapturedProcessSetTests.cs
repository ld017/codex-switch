using CodexSwitch.Core.Codex;

namespace CodexSwitch.Core.Tests.Codex;

public sealed class CapturedProcessSetTests
{
    [Fact]
    public void SelectDesktopProcesses_requires_codex_package_and_desktop_entry_path()
    {
        var now = DateTimeOffset.UtcNow;
        var candidates = new[]
        {
            new CodexProcessCandidate(1, true, now, PackageFamilyName: "OpenAI.Codex_2p2nqsd0c76g0", ExecutablePath: @"C:\WindowsApps\OpenAI.Codex_version\app\ChatGPT.exe"),
            new CodexProcessCandidate(2, true, now, PackageFamilyName: "Other.ChatGPT_family", ExecutablePath: @"C:\Other\app\ChatGPT.exe"),
            new CodexProcessCandidate(3, false, now, ExecutablePath: @"C:\OpenAI\Codex\bin\version\codex.exe"),
            new CodexProcessCandidate(4, false, now, PackageFamilyName: "OpenAI.Codex_2p2nqsd0c76g0", ExecutablePath: @"C:\WindowsApps\OpenAI.Codex_version\app\resources\codex.exe"),
        };

        Assert.Equal([1], CapturedProcessSet.SelectDesktopProcesses(candidates).Select(candidate => candidate.Id));
    }

    [Fact]
    public void CaptureTree_waits_for_auth_backend_after_desktop_window_exits()
    {
        var now = DateTimeOffset.UtcNow;
        var root = new CodexProcessCandidate(10, true, now);
        var backend = new CodexProcessCandidate(11, false, now.AddSeconds(1), 10);
        var unrelated = new CodexProcessCandidate(99, false, now.AddSeconds(1), 5);
        var captured = CapturedProcessSet.CaptureTree([root, backend, unrelated], [10]);

        Assert.Equal([11], captured.RemainingInstancesFrom([backend, unrelated]));
    }

    [Fact]
    public void IncludeDescendants_tracks_new_backend_children_without_capturing_unrelated_processes()
    {
        var now = DateTimeOffset.UtcNow;
        var root = new CodexProcessCandidate(10, true, now);
        var captured = CapturedProcessSet.CaptureTree([root], [10]);
        var backend = new CodexProcessCandidate(11, false, now.AddSeconds(1), 10);
        var child = new CodexProcessCandidate(12, false, now.AddSeconds(2), 11);
        captured.IncludeDescendants([root, backend, child, new(99, false, now, 5)]);

        Assert.Equal([10, 11, 12], captured.ProcessIds.Order());
    }

    [Fact]
    public void Process_id_reuse_does_not_capture_replacement_or_its_children()
    {
        var now = DateTimeOffset.UtcNow;
        var captured = CapturedProcessSet.CaptureTree([new(10, true, now)], [10]);
        var replacement = new CodexProcessCandidate(10, false, now.AddSeconds(10));
        var child = new CodexProcessCandidate(11, false, now.AddSeconds(11), 10);
        captured.IncludeDescendants([replacement, child]);

        Assert.Empty(captured.RemainingInstancesFrom([replacement, child]));
        Assert.DoesNotContain(11, captured.ProcessIds);
    }

    [Fact]
    public void IncludeDescendants_tracks_child_created_just_before_parent_exit()
    {
        var now = DateTimeOffset.UtcNow;
        var captured = CapturedProcessSet.CaptureTree([new(10, true, now)], [10]);
        var orphanedBackend = new CodexProcessCandidate(11, false, now.AddSeconds(1), 10);

        captured.IncludeDescendants([orphanedBackend]);

        Assert.Equal([11], captured.RemainingInstancesFrom([orphanedBackend]));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Unknown_creation_time_is_not_treated_as_evidence_of_exit(bool unknownAtCapture)
    {
        var known = DateTimeOffset.UtcNow;
        var captured = CapturedProcessSet.CaptureTree([new(10, true, unknownAtCapture ? DateTimeOffset.MaxValue : known)], [10]);
        var current = new CodexProcessCandidate(10, true, unknownAtCapture ? known : DateTimeOffset.MaxValue);

        Assert.Equal([10], captured.RemainingInstancesFrom([current]));
    }

    [Fact]
    public void Unknown_parent_creation_time_still_tracks_auth_backend()
    {
        var captured = CapturedProcessSet.CaptureTree([new(10, true, DateTimeOffset.MaxValue)], [10]);
        var backend = new CodexProcessCandidate(11, false, DateTimeOffset.UtcNow, 10);

        captured.IncludeDescendants([backend]);

        Assert.Equal([11], captured.RemainingInstancesFrom([backend]));
    }

    [Fact]
    public void SelectRoots_prefers_process_with_main_window_over_electron_children()
    {
        var candidates = new[]
        {
            new CodexProcessCandidate(10, true, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)),
            new CodexProcessCandidate(11, false, new DateTimeOffset(2026, 1, 1, 0, 0, 1, TimeSpan.Zero)),
            new CodexProcessCandidate(12, false, new DateTimeOffset(2026, 1, 1, 0, 0, 2, TimeSpan.Zero)),
        };

        var captured = CapturedProcessSet.SelectRoots(candidates);

        Assert.Equal([10], captured.ProcessIds);
    }

    [Fact]
    public void SelectRoots_falls_back_to_oldest_process_when_no_window_is_available()
    {
        var candidates = new[]
        {
            new CodexProcessCandidate(20, false, new DateTimeOffset(2026, 1, 1, 0, 0, 2, TimeSpan.Zero)),
            new CodexProcessCandidate(10, false, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)),
        };

        var captured = CapturedProcessSet.SelectRoots(candidates);

        Assert.Equal([10], captured.ProcessIds);
    }

    [Fact]
    public void RemainingFrom_excludes_process_started_after_capture()
    {
        var captured = new CapturedProcessSet([11, 12]);

        var targets = captured.RemainingFrom([11, 12, 99]);

        Assert.Equal([11, 12], targets.Order());
        Assert.DoesNotContain(99, targets);
    }
}
