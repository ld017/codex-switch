using System.Diagnostics;
using CodexSwitch.Core.Codex;

namespace CodexSwitch.App.Windows;

public sealed class WindowsCodexLifecycle : ICodexLifecycle
{
    private CapturedProcessSet _captured = new([]);

    public async Task<CodexShutdownResult> RequestCloseAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var processes = Process.GetProcessesByName("ChatGPT");
        _captured = CapturedProcessSet.SelectRoots(processes.Select(process => new CodexProcessCandidate(
            process.Id,
            process.MainWindowHandle != IntPtr.Zero,
            SafeStartTime(process))));
        foreach (var process in processes)
        {
            try
            {
                if (_captured.ProcessIds.Contains(process.Id))
                {
                    _ = process.CloseMainWindow();
                }
            }
            finally
            {
                process.Dispose();
            }
        }
        var exited = await WaitUntilCapturedExitAsync(timeout, cancellationToken);
        return new CodexShutdownResult(exited, _captured.ProcessIds);
    }

    public async Task<bool> ForceCloseCapturedAsync(CancellationToken cancellationToken)
    {
        var running = Process.GetProcessesByName("ChatGPT");
        var targets = _captured.RemainingFrom(running.Select(x => x.Id));
        foreach (var process in running)
        {
            try { if (targets.Contains(process.Id)) process.Kill(true); } catch (InvalidOperationException) { } finally { process.Dispose(); }
        }
        return await WaitUntilCapturedExitAsync(TimeSpan.FromSeconds(8), cancellationToken);
    }

    public async Task LaunchAsync(CancellationToken cancellationToken)
    {
        Process.Start(new ProcessStartInfo("explorer.exe", @"shell:AppsFolder\OpenAI.Codex_2p2nqsd0c76g0!App") { UseShellExecute = true });
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var found = Process.GetProcessesByName("ChatGPT");
            foreach (var process in found) process.Dispose();
            if (found.Length > 0) return;
            await Task.Delay(250, cancellationToken);
        }
        throw new InvalidOperationException("Codex did not start.");
    }

    private async Task<bool> WaitUntilCapturedExitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        do
        {
            var running = Process.GetProcessesByName("ChatGPT");
            var remaining = _captured.RemainingFrom(running.Select(x => x.Id));
            foreach (var process in running) process.Dispose();
            if (remaining.Count == 0) return true;
            await Task.Delay(250, cancellationToken);
        } while (DateTimeOffset.UtcNow < deadline);
        return false;
    }

    private static DateTimeOffset SafeStartTime(Process process)
    {
        try
        {
            return process.StartTime;
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return DateTimeOffset.MaxValue;
        }
    }
}
