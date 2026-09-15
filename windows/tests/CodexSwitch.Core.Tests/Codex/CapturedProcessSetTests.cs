using CodexSwitch.Core.Codex;

namespace CodexSwitch.Core.Tests.Codex;

public sealed class CapturedProcessSetTests
{
    [Fact]
    public void RemainingFrom_excludes_process_started_after_capture()
    {
        var captured = new CapturedProcessSet([11, 12]);

        var targets = captured.RemainingFrom([11, 12, 99]);

        Assert.Equal([11, 12], targets.Order());
        Assert.DoesNotContain(99, targets);
    }
}
