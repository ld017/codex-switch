namespace CodexSwitch.Core.Codex;

public sealed record CodexShutdownResult(bool Exited, IReadOnlyCollection<int> CapturedProcessIds);

public interface ICodexLifecycle
{
    Task<CodexShutdownResult> RequestCloseAsync(TimeSpan timeout, CancellationToken cancellationToken);
    Task<bool> ForceCloseCapturedAsync(CancellationToken cancellationToken);
    Task LaunchAsync(CancellationToken cancellationToken);
}
