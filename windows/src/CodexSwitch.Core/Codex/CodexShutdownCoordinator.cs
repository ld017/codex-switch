namespace CodexSwitch.Core.Codex;

public static class CodexShutdownCoordinator
{
    public static async Task CloseAfterConfirmationAsync(ICodexLifecycle lifecycle, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lifecycle);
        cancellationToken.ThrowIfCancellationRequested();

        var result = await lifecycle.RequestCloseAsync(TimeSpan.FromSeconds(3), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (result.Exited) return;

        var exited = await lifecycle.ForceCloseCapturedAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!exited)
            throw new InvalidOperationException("无法自动关闭全部 Codex 进程，本次未修改账号凭据。请重试切换。");
    }
}
