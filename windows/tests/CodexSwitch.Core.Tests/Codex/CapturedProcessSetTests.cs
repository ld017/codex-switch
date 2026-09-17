using CodexSwitch.Core.Codex;

namespace CodexSwitch.Core.Tests.Codex;

public sealed class CapturedProcessSetTests
{
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
