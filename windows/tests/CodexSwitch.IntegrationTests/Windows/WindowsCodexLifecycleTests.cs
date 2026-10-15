using CodexSwitch.App.Windows;
using CodexSwitch.Core.Codex;

namespace CodexSwitch.IntegrationTests.Windows;

public sealed class WindowsCodexLifecycleTests
{
    [Fact]
    public async Task RequestCloseAsync_returns_structured_failure_when_snapshot_races()
    {
        var lifecycle = new WindowsCodexLifecycle(
            () => throw new InvalidOperationException("process exited during snapshot"),
            () => { });

        var result = await lifecycle.RequestCloseAsync(TimeSpan.Zero, default);

        Assert.False(result.Exited);
        Assert.Empty(result.CapturedProcessIds);
        Assert.NotNull(result.FailureReason);
    }

    [Fact]
    public async Task LaunchAsync_retries_after_transient_snapshot_race_without_starting_real_codex()
    {
        var attempts = 0;
        var lifecycle = new WindowsCodexLifecycle(
            () =>
            {
                if (Interlocked.Increment(ref attempts) == 1)
                    throw new InvalidOperationException("process exited during snapshot");

                return [new CodexProcessCandidate(
                    42,
                    HasMainWindow: true,
                    DateTimeOffset.UtcNow,
                    PackageFamilyName: "OpenAI.Codex_2p2nqsd0c76g0",
                    ExecutablePath: @"C:\WindowsApps\OpenAI.Codex_version\app\ChatGPT.exe")];
            },
            () => { });

        await lifecycle.LaunchAsync(default);

        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task RequestCloseAsync_does_not_force_kill_or_throw_when_pid_disappears()
    {
        var snapshot = new[]
        {
            new CodexProcessCandidate(
                999999,
                HasMainWindow: true,
                DateTimeOffset.UtcNow,
                PackageFamilyName: "OpenAI.Codex_2p2nqsd0c76g0",
                ExecutablePath: @"C:\WindowsApps\OpenAI.Codex_version\app\ChatGPT.exe")
        };
        var lifecycle = new WindowsCodexLifecycle(() => snapshot, () => { });

        var result = await lifecycle.RequestCloseAsync(TimeSpan.Zero, default);

        Assert.False(result.Exited);
        Assert.Contains(999999, result.CapturedProcessIds);
    }
}
