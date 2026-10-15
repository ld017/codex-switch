using CodexSwitch.Core.Codex;

namespace CodexSwitch.Core.Tests.Codex;

public sealed class CodexShutdownCoordinatorTests
{
    [Fact]
    public async Task Normal_exit_uses_a_short_grace_period_without_forcing()
    {
        var lifecycle = new Lifecycle { NormalExit = true };

        await CodexShutdownCoordinator.CloseAfterConfirmationAsync(lifecycle, default);

        Assert.Equal(TimeSpan.FromSeconds(3), lifecycle.RequestedTimeout);
        Assert.Equal(["close"], lifecycle.Calls);
    }

    [Fact]
    public async Task Active_work_does_not_require_another_confirmation_before_force_close()
    {
        var lifecycle = new Lifecycle { NormalExit = false, ForceExit = true };

        await CodexShutdownCoordinator.CloseAfterConfirmationAsync(lifecycle, default);

        Assert.Equal(["close", "force"], lifecycle.Calls);
        Assert.Equal(TimeSpan.FromSeconds(3), lifecycle.RequestedTimeout);
    }

    [Fact]
    public async Task Failed_force_close_reports_that_credentials_were_not_changed()
    {
        var lifecycle = new Lifecycle { NormalExit = false, ForceExit = false };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CodexShutdownCoordinator.CloseAfterConfirmationAsync(lifecycle, default));

        Assert.Contains("本次未修改账号凭据", error.Message);
        Assert.DoesNotContain("手动退出", error.Message);
        Assert.Equal(["close", "force"], lifecycle.Calls);
    }

    [Fact]
    public async Task Normal_close_exception_does_not_start_force_close()
    {
        var failure = new IOException("Synthetic snapshot failure");
        var lifecycle = new Lifecycle { CloseError = failure };

        var error = await Assert.ThrowsAsync<IOException>(() =>
            CodexShutdownCoordinator.CloseAfterConfirmationAsync(lifecycle, default));

        Assert.Same(failure, error);
        Assert.Equal(["close"], lifecycle.Calls);
    }

    [Fact]
    public async Task Force_close_exception_is_observed_by_the_caller()
    {
        var failure = new IOException("Synthetic force failure");
        var lifecycle = new Lifecycle { NormalExit = false, ForceError = failure };

        var error = await Assert.ThrowsAsync<IOException>(() =>
            CodexShutdownCoordinator.CloseAfterConfirmationAsync(lifecycle, default));

        Assert.Same(failure, error);
        Assert.Equal(["close", "force"], lifecycle.Calls);
    }

    [Fact]
    public async Task Already_cancelled_request_does_not_close_any_process()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var lifecycle = new Lifecycle();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CodexShutdownCoordinator.CloseAfterConfirmationAsync(lifecycle, cancellation.Token));

        Assert.Empty(lifecycle.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Cancellation_after_normal_close_never_continues_to_force(bool normalExit)
    {
        using var cancellation = new CancellationTokenSource();
        var lifecycle = new Lifecycle { NormalExit = normalExit, AfterClose = cancellation.Cancel };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CodexShutdownCoordinator.CloseAfterConfirmationAsync(lifecycle, cancellation.Token));

        Assert.Equal(cancellation.Token, lifecycle.ObservedCloseToken);
        Assert.Equal(["close"], lifecycle.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Cancellation_after_force_is_not_reported_as_success_or_failure(bool forceExit)
    {
        using var cancellation = new CancellationTokenSource();
        var lifecycle = new Lifecycle { NormalExit = false, ForceExit = forceExit, AfterForce = cancellation.Cancel };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CodexShutdownCoordinator.CloseAfterConfirmationAsync(lifecycle, cancellation.Token));

        Assert.Equal(cancellation.Token, lifecycle.ObservedForceToken);
        Assert.Equal(["close", "force"], lifecycle.Calls);
    }

    private sealed class Lifecycle : ICodexLifecycle
    {
        public bool NormalExit { get; init; }
        public bool ForceExit { get; init; }
        public Exception? CloseError { get; init; }
        public Exception? ForceError { get; init; }
        public Action? AfterClose { get; init; }
        public Action? AfterForce { get; init; }
        public List<string> Calls { get; } = [];
        public TimeSpan RequestedTimeout { get; private set; }
        public CancellationToken ObservedCloseToken { get; private set; }
        public CancellationToken ObservedForceToken { get; private set; }

        public Task<CodexShutdownResult> RequestCloseAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            Calls.Add("close");
            RequestedTimeout = timeout;
            ObservedCloseToken = cancellationToken;
            if (CloseError is not null) return Task.FromException<CodexShutdownResult>(CloseError);
            AfterClose?.Invoke();
            return Task.FromResult(new CodexShutdownResult(NormalExit, [42], "Synthetic active task"));
        }

        public Task<bool> ForceCloseCapturedAsync(CancellationToken cancellationToken)
        {
            Calls.Add("force");
            ObservedForceToken = cancellationToken;
            if (ForceError is not null) return Task.FromException<bool>(ForceError);
            AfterForce?.Invoke();
            return Task.FromResult(ForceExit);
        }

        public Task LaunchAsync(CancellationToken cancellationToken)
            => throw new InvalidOperationException("Shutdown coordination must not launch Codex");
    }
}
