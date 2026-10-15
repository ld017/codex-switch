namespace CodexSwitch.Core.Codex;

// Desktop updates replace the versioned CLI directory while Switch can stay open for days.
public sealed class ResolvingCodexProcessRunner(IProcessRunner inner, Func<string> resolveCli) : IProcessRunner
{
    public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken)
        => inner.RunAsync(spec with { FileName = resolveCli() }, cancellationToken);
}
