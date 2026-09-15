using CodexSwitch.Core.Presentation;

namespace CodexSwitch.Core.Tests.Presentation;

public sealed class OperationCoordinatorTests
{
    [Fact]
    public async Task TryRunAsync_rejects_concurrent_operation_and_clears_state_after_completion()
    {
        var coordinator = new OperationCoordinator();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = coordinator.TryRunAsync(async _ => await release.Task);

        Assert.True(coordinator.IsBusy);
        Assert.False(await coordinator.TryRunAsync(_ => Task.CompletedTask));
        release.SetResult();
        Assert.True(await first);
        Assert.False(coordinator.IsBusy);
        Assert.True(await coordinator.TryRunAsync(_ => Task.CompletedTask));
    }

    [Fact]
    public async Task TryRunAsync_clears_state_after_exception()
    {
        var coordinator = new OperationCoordinator();

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.TryRunAsync(_ => throw new InvalidOperationException("failed")));

        Assert.False(coordinator.IsBusy);
    }
}
