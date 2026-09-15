using System.Diagnostics;
using System.Text;
using CodexSwitch.Core.Codex;

namespace CodexSwitch.App.Windows;

public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(ProcessSpec specification, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(specification.FileName) { UseShellExecute = specification.Visible };
        foreach (var argument in specification.Arguments) start.ArgumentList.Add(argument);
        if (!specification.Visible)
        {
            start.RedirectStandardOutput = true; start.RedirectStandardError = true; start.CreateNoWindow = true;
            start.StandardOutputEncoding = new UTF8Encoding(false); start.StandardErrorEncoding = new UTF8Encoding(false);
        }
        if (specification.Environment is not null)
            foreach (var pair in specification.Environment) start.Environment[pair.Key] = pair.Value;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start process.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (specification.Timeout is { } limit) timeout.CancelAfter(limit);
        var stdout = specification.Visible ? Task.FromResult(string.Empty) : process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = specification.Visible ? Task.FromResult(string.Empty) : process.StandardError.ReadToEndAsync(timeout.Token);
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { if (!process.HasExited) process.Kill(true); throw; }
        return new ProcessResult(process.ExitCode, await stdout, await stderr);
    }
}
