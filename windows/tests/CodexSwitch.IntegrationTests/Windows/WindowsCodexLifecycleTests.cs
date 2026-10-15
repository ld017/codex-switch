using System.ComponentModel;
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
        var lifecycle = new WindowsCodexLifecycle(() => snapshot, () => { },
            requestClose: _ => throw new ArgumentException("process already exited"));

        var result = await lifecycle.RequestCloseAsync(TimeSpan.Zero, default);

        Assert.False(result.Exited);
        Assert.Contains(999999, result.CapturedProcessIds);
    }

    [Fact]
    public async Task ForceCloseCapturedAsync_requires_a_successful_shutdown_capture()
    {
        var terminated = false;
        var lifecycle = new WindowsCodexLifecycle(() => [], () => { },
            terminate: _ => terminated = true);

        Assert.False(await lifecycle.ForceCloseCapturedAsync(default));
        Assert.False(terminated);
    }

    [Fact]
    public async Task ForceCloseCapturedAsync_terminates_verified_tree_and_preserves_switcher_subtree_and_unrelated_cli()
    {
        var desktop = Desktop(10);
        var running = new List<CodexProcessCandidate>
        {
            desktop,
            new(11, false, Started.AddSeconds(1), 10),
            new(12, false, Started.AddSeconds(2), 11),
            new(20, false, Started.AddSeconds(3), 10), // Switcher launched from Codex.
            new(21, false, Started.AddSeconds(4), 20),
            new(22, false, Started.AddSeconds(5), 21),
            new(99, false, Started, ExecutablePath: @"C:\Tools\codex.exe"),
        };
        var closed = new List<int>();
        var terminated = new List<int>();
        var lifecycle = new WindowsCodexLifecycle(() => running.ToArray(), () => { },
            requestClose: candidate => { closed.Add(candidate.Id); return true; },
            terminate: candidate =>
            {
                terminated.Add(candidate.Id);
                running.RemoveAll(current => current == candidate);
                return true;
            },
            protectedProcessId: 20);

        var requested = await lifecycle.RequestCloseAsync(TimeSpan.Zero, default);
        var exited = await lifecycle.ForceCloseCapturedAsync(default);

        Assert.False(requested.Exited);
        Assert.Equal([10, 11, 12], requested.CapturedProcessIds.Order());
        Assert.Equal([10], closed);
        Assert.True(exited);
        Assert.Equal([10, 11, 12], terminated);
        Assert.Equal([20, 21, 22, 99], running.Select(candidate => candidate.Id));
    }

    [Fact]
    public async Task ForceCloseCapturedAsync_observes_children_created_between_individual_terminations()
    {
        var running = new List<CodexProcessCandidate> { Desktop(10), new(11, false, Started.AddSeconds(1), 10) };
        var terminated = new List<int>();
        var lifecycle = new WindowsCodexLifecycle(() => running.ToArray(), () => { },
            requestClose: _ => true,
            terminate: candidate =>
            {
                terminated.Add(candidate.Id);
                running.Remove(candidate);
                if (candidate.Id == 10) running.Add(new(12, false, Started.AddSeconds(2), 11));
                return true;
            });
        await lifecycle.RequestCloseAsync(TimeSpan.Zero, default);

        Assert.True(await lifecycle.ForceCloseCapturedAsync(default));
        Assert.Equal([10, 11, 12], terminated);
    }

    [Fact]
    public async Task ForceCloseCapturedAsync_does_not_hide_backend_reusing_an_exited_protected_child_pid()
    {
        var running = new List<CodexProcessCandidate>
        {
            Desktop(10),
            new(20, false, Started.AddSeconds(1), 10),
            new(21, false, Started.AddSeconds(2), 20),
        };
        var terminated = new List<int>();
        var lifecycle = new WindowsCodexLifecycle(() => running.ToArray(), () => { },
            requestClose: _ => true,
            terminate: candidate => { terminated.Add(candidate.Id); running.Remove(candidate); return true; },
            protectedProcessId: 20);
        await lifecycle.RequestCloseAsync(TimeSpan.Zero, default);
        running.RemoveAll(candidate => candidate.Id == 21);
        running.Add(new(21, false, Started.AddSeconds(3), 10));

        Assert.True(await lifecycle.ForceCloseCapturedAsync(default));
        Assert.Equal([10, 21], terminated);
        Assert.Equal([20], running.Select(candidate => candidate.Id));
    }

    [Fact]
    public async Task ForceCloseCapturedAsync_accepts_children_exiting_together_with_the_parent()
    {
        var running = new List<CodexProcessCandidate> { Desktop(10), new(11, false, Started.AddSeconds(1), 10) };
        var terminated = new List<int>();
        var lifecycle = new WindowsCodexLifecycle(() => running.ToArray(), () => { },
            requestClose: _ => true,
            terminate: candidate => { terminated.Add(candidate.Id); running.Clear(); return true; });
        await lifecycle.RequestCloseAsync(TimeSpan.Zero, default);

        Assert.True(await lifecycle.ForceCloseCapturedAsync(default));
        Assert.Equal([10], terminated);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ForceCloseCapturedAsync_accepts_process_exiting_during_a_failed_termination_action(bool throws)
    {
        var running = new List<CodexProcessCandidate> { Desktop(10) };
        var lifecycle = new WindowsCodexLifecycle(() => running.ToArray(), () => { },
            requestClose: _ => true,
            terminate: _ =>
            {
                running.Clear();
                if (throws) throw new ArgumentException("process already exited");
                return false;
            });
        await lifecycle.RequestCloseAsync(TimeSpan.Zero, default);

        Assert.True(await lifecycle.ForceCloseCapturedAsync(default));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ForceCloseCapturedAsync_returns_false_when_a_live_instance_cannot_be_terminated(bool throws)
    {
        var lifecycle = new WindowsCodexLifecycle(() => [Desktop(10)], () => { },
            requestClose: _ => true,
            terminate: _ => throws ? throw new Win32Exception(5, "access denied") : false);
        await lifecycle.RequestCloseAsync(TimeSpan.Zero, default);

        Assert.False(await lifecycle.ForceCloseCapturedAsync(default));
    }

    [Fact]
    public async Task ForceCloseCapturedAsync_does_not_claim_exit_until_the_terminated_process_disappears()
    {
        var terminated = 0;
        var lifecycle = new WindowsCodexLifecycle(() => [Desktop(10)], () => { },
            requestClose: _ => true,
            terminate: _ => { terminated++; return true; },
            forceCloseTimeout: TimeSpan.Zero);
        await lifecycle.RequestCloseAsync(TimeSpan.Zero, default);

        Assert.False(await lifecycle.ForceCloseCapturedAsync(default));
        Assert.Equal(1, terminated);
    }

    [Fact]
    public async Task ForceCloseCapturedAsync_preserves_a_reused_pid_and_its_children()
    {
        var running = new List<CodexProcessCandidate> { Desktop(10) };
        var terminated = false;
        var lifecycle = new WindowsCodexLifecycle(() => running.ToArray(), () => { },
            requestClose: _ => true,
            terminate: _ => terminated = true);
        await lifecycle.RequestCloseAsync(TimeSpan.Zero, default);
        running.Clear();
        running.Add(new(10, false, Started.AddMinutes(1)));
        running.Add(new(11, false, Started.AddMinutes(2), 10));

        Assert.True(await lifecycle.ForceCloseCapturedAsync(default));
        Assert.False(terminated);
    }

    [Fact]
    public async Task ForceCloseCapturedAsync_does_not_terminate_a_replacement_desktop_instance()
    {
        var running = new List<CodexProcessCandidate> { Desktop(10) };
        var terminated = false;
        var lifecycle = new WindowsCodexLifecycle(() => running.ToArray(), () => { },
            requestClose: _ => true,
            terminate: _ => terminated = true);
        await lifecycle.RequestCloseAsync(TimeSpan.Zero, default);
        running[0] = Desktop(10) with { StartTime = Started.AddMinutes(1) };

        Assert.False(await lifecycle.ForceCloseCapturedAsync(default));
        Assert.False(terminated);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ForceCloseCapturedAsync_does_not_terminate_instances_with_unknown_creation_time(bool unknownAtCapture)
    {
        var running = new List<CodexProcessCandidate> { Desktop(10) with { StartTime = unknownAtCapture ? DateTimeOffset.MaxValue : Started } };
        var terminated = false;
        var lifecycle = new WindowsCodexLifecycle(() => running.ToArray(), () => { },
            requestClose: _ => true,
            terminate: _ => terminated = true);
        await lifecycle.RequestCloseAsync(TimeSpan.Zero, default);
        running[0] = Desktop(10) with { StartTime = unknownAtCapture ? Started : DateTimeOffset.MaxValue };

        Assert.False(await lifecycle.ForceCloseCapturedAsync(default));
        Assert.False(terminated);
    }

    [Fact]
    public async Task ForceCloseCapturedAsync_leaves_a_new_orphan_with_ambiguous_parent_identity_running()
    {
        var running = new List<CodexProcessCandidate> { Desktop(10) };
        var terminated = false;
        var lifecycle = new WindowsCodexLifecycle(() => running.ToArray(), () => { },
            requestClose: _ => true,
            terminate: _ => terminated = true);
        await lifecycle.RequestCloseAsync(TimeSpan.Zero, default);
        running.Clear();
        running.Add(new(11, false, Started.AddSeconds(1), 10));

        Assert.False(await lifecycle.ForceCloseCapturedAsync(default));
        Assert.False(terminated);
    }

    [Fact]
    public async Task ForceCloseCapturedAsync_does_not_authorize_known_child_from_an_unknown_parent_instance()
    {
        var running = new List<CodexProcessCandidate>
        {
            Desktop(10) with { StartTime = DateTimeOffset.MaxValue },
            new(11, false, Started.AddSeconds(1), 10),
        };
        var terminated = false;
        var lifecycle = new WindowsCodexLifecycle(() => running.ToArray(), () => { },
            requestClose: _ => true,
            terminate: _ => terminated = true);
        await lifecycle.RequestCloseAsync(TimeSpan.Zero, default);
        running.RemoveAll(candidate => candidate.Id == 10);

        Assert.False(await lifecycle.ForceCloseCapturedAsync(default));
        Assert.False(terminated);
    }

    [Fact]
    public async Task ForceCloseCapturedAsync_returns_false_when_identity_snapshot_cannot_be_read()
    {
        var fail = false;
        var terminated = false;
        var lifecycle = new WindowsCodexLifecycle(
            () => fail ? throw new UnauthorizedAccessException("identity unavailable") : [Desktop(10)], () => { },
            requestClose: _ => true,
            terminate: _ => terminated = true);
        await lifecycle.RequestCloseAsync(TimeSpan.Zero, default);
        fail = true;

        Assert.False(await lifecycle.ForceCloseCapturedAsync(default));
        Assert.False(terminated);
    }

    [Fact]
    public async Task RequestCloseAsync_failed_recapture_cannot_reuse_a_previous_shutdown_capture()
    {
        var fail = false;
        var terminated = false;
        var lifecycle = new WindowsCodexLifecycle(
            () => fail ? throw new InvalidOperationException("snapshot race") : [Desktop(10)], () => { },
            requestClose: _ => true,
            terminate: _ => terminated = true);
        await lifecycle.RequestCloseAsync(TimeSpan.Zero, default);
        fail = true;
        Assert.False((await lifecycle.RequestCloseAsync(TimeSpan.Zero, default)).Exited);
        fail = false;

        Assert.False(await lifecycle.ForceCloseCapturedAsync(default));
        Assert.False(terminated);
    }

    [Fact]
    public async Task ForceCloseCapturedAsync_honors_cancellation_before_any_termination()
    {
        var terminated = false;
        var lifecycle = new WindowsCodexLifecycle(() => [Desktop(10)], () => { },
            requestClose: _ => true,
            terminate: _ => terminated = true);
        await lifecycle.RequestCloseAsync(TimeSpan.Zero, default);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lifecycle.ForceCloseCapturedAsync(cancellation.Token));
        Assert.False(terminated);
    }

    private static readonly DateTimeOffset Started = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static CodexProcessCandidate Desktop(int id) => new(
        id, true, Started,
        PackageFamilyName: "OpenAI.Codex_2p2nqsd0c76g0",
        ExecutablePath: @"C:\WindowsApps\OpenAI.Codex_version\app\ChatGPT.exe");
}
