namespace CodexSwitch.Core.Codex;

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(ProcessSpec specification, CancellationToken cancellationToken);
}
