using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using CodexSwitch.Core.Codex;

namespace CodexSwitch.App.Windows;

public sealed class WindowsCodexLifecycle : ICodexLifecycle
{
    private static readonly ProcessStartInfo ActivationInfo = new(
        "explorer.exe",
        @"shell:AppsFolder\OpenAI.Codex_2p2nqsd0c76g0!App") { UseShellExecute = true };

    private readonly Func<IReadOnlyList<CodexProcessCandidate>> _snapshotReader;
    private readonly Action _activateCodex;
    private CapturedProcessSet _captured = new([]);

    // The delegates keep lifecycle tests synthetic. Production uses the real process snapshot
    // and shell activation, while tests can exercise process races without touching Codex.
    public WindowsCodexLifecycle(
        Func<IReadOnlyList<CodexProcessCandidate>>? snapshotReader = null,
        Action? activateCodex = null)
    {
        _snapshotReader = snapshotReader ?? ReadSnapshot;
        _activateCodex = activateCodex ?? (() =>
        {
            using var activation = Process.Start(ActivationInfo);
        });
    }

    public async Task<CodexShutdownResult> RequestCloseAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryReadSnapshot(out var snapshot, out var snapshotFailure))
            return new CodexShutdownResult(false, [], snapshotFailure);

        var desktop = CapturedProcessSet.SelectDesktopProcesses(snapshot);
        // Capture the whole desktop tree, including its auth-writing CLI backend, before closing
        // only top-level windows. A renderer exiting does not mean auth.json is safe to replace.
        _captured = CapturedProcessSet.CaptureTree(snapshot, desktop.Select(process => process.Id));
        var roots = CapturedProcessSet.SelectRoots(desktop.Where(process => process.HasMainWindow));
        string? closeFailure = null;
        foreach (var id in roots.ProcessIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var process = Process.GetProcessById(id);
                var original = desktop.FirstOrDefault(candidate => candidate.Id == id);
                if (original is null || !IsSameProcessInstance(process, original.StartTime)) continue;
                _ = process.CloseMainWindow();
            }
            catch (Exception error) when (IsProcessRace(error))
            {
                // The process may have exited between enumeration and CloseMainWindow. It is
                // safer to report a non-destructive failure than to touch a reused PID.
                closeFailure ??= DescribeFailure(error);
            }
        }

        var wait = await WaitUntilCapturedExitAsync(timeout, cancellationToken);
        return new CodexShutdownResult(wait.Exited, _captured.ProcessIds,
            closeFailure ?? wait.FailureReason);
    }

    // Kept for older callers. Switching credentials must never forcibly kill a user's tasks.
    public async Task<bool> ForceCloseCapturedAsync(CancellationToken cancellationToken)
        => (await WaitUntilCapturedExitAsync(TimeSpan.Zero, cancellationToken)).Exited;

    public async Task LaunchAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? activationFailure = null;
        try { _activateCodex(); }
        catch (Exception error) when (IsProcessRace(error))
        {
            // Explorer/Codex can disappear during activation. Continue observing in case the
            // desktop is already starting; only report a failure after the normal deadline.
            activationFailure = DescribeFailure(error);
        }

        var elapsed = Stopwatch.StartNew();
        string? lastFailure = activationFailure;
        while (elapsed.Elapsed < TimeSpan.FromSeconds(30))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryReadSnapshot(out var snapshot, out var snapshotFailure))
            {
                if (CapturedProcessSet.SelectDesktopProcesses(snapshot).Any(process => process.HasMainWindow)) return;
                lastFailure = null;
            }
            else
            {
                // A process can disappear while its package identity is being inspected. Keep
                // polling; the caller only sees a normal launch failure after the deadline.
                lastFailure = snapshotFailure;
            }

            await Task.Delay(250, cancellationToken);
        }
        throw new InvalidOperationException(lastFailure
            ?? "未能确认 Codex 桌面窗口已打开，请从开始菜单手动打开 Codex。");
    }

    private async Task<WaitResult> WaitUntilCapturedExitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var elapsed = Stopwatch.StartNew();
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryReadSnapshot(out var snapshot, out var snapshotFailure))
                return new WaitResult(false, snapshotFailure);

            _captured.IncludeDescendants(snapshot);
            if (_captured.RemainingInstancesFrom(snapshot).Count == 0
                && CapturedProcessSet.SelectDesktopProcesses(snapshot).Count == 0) return new WaitResult(true);
            if (elapsed.Elapsed >= timeout) break;
            await Task.Delay(250, cancellationToken);
        } while (true);
        return new WaitResult(false, "Codex 仍有进程运行或退出状态无法确认，请先手动退出后再重试。");
    }

    private bool TryReadSnapshot(
        out IReadOnlyList<CodexProcessCandidate> snapshot,
        out string? failureReason)
    {
        try
        {
            snapshot = _snapshotReader() ?? [];
            failureReason = null;
            return true;
        }
        catch (Exception error) when (IsProcessRace(error))
        {
            snapshot = [];
            failureReason = DescribeFailure(error);
            return false;
        }
    }

    private static IReadOnlyList<CodexProcessCandidate> ReadSnapshot()
    {
        var entries = ReadProcessEntries();
        var result = new List<CodexProcessCandidate>();
        foreach (var (id, entry) in entries)
        {
            Process process;
            try { process = Process.GetProcessById(id); }
            catch (Exception error) when (IsProcessRace(error)) { continue; }

            using (process)
            {
                var possibleDesktop = string.Equals(entry.Name, "ChatGPT.exe", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(entry.Name, "Codex.exe", StringComparison.OrdinalIgnoreCase);
                try
                {
                    // Toolhelp supplies names without opening protected system processes.
                    string? path = null;
                    string? family = null;
                    var hasWindow = false;
                    if (possibleDesktop)
                    {
                        family = GetFamilyName(process);
                        if (family == "OpenAI.Codex_2p2nqsd0c76g0")
                        {
                            path = process.MainModule?.FileName;
                            if (path is null)
                                throw new SnapshotRaceException("无法确认 Codex 桌面程序路径。");
                            hasWindow = process.MainWindowHandle != IntPtr.Zero;
                        }
                    }

                    result.Add(new CodexProcessCandidate(process.Id, hasWindow, SafeStartTime(process),
                        entry.ParentId, family, path));
                }
                catch (Exception error) when (IsProcessRace(error))
                {
                    if (possibleDesktop)
                        throw new SnapshotRaceException("无法稳定核对 Codex 进程状态。", error);
                }
            }
        }
        return result;
    }

    private static bool IsSameProcessInstance(Process process, DateTimeOffset expectedStartTime)
    {
        if (expectedStartTime == DateTimeOffset.MaxValue) return false;
        var currentStartTime = SafeStartTime(process);
        return currentStartTime != DateTimeOffset.MaxValue && currentStartTime == expectedStartTime;
    }

    private static bool IsProcessRace(Exception error)
        => error is SnapshotRaceException
            or ArgumentException
            or InvalidOperationException
            or UnauthorizedAccessException
            or Win32Exception
            or NotSupportedException;

    private static string DescribeFailure(Exception error)
        => error is SnapshotRaceException snapshotError && snapshotError.Message.Length > 0
            ? snapshotError.Message
            : "暂时无法稳定读取 Codex 进程状态，请稍后重试。";

    private static DateTimeOffset SafeStartTime(Process process)
    {
        try { return new DateTimeOffset(process.StartTime.ToUniversalTime()); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or Win32Exception or UnauthorizedAccessException or NotSupportedException)
        {
            // Unknown instances stay captured; inability to inspect a child is not proof of exit.
            return DateTimeOffset.MaxValue;
        }
    }

    private static string? GetFamilyName(Process process)
    {
        uint length = 0;
        var error = GetPackageFamilyName(process.Handle, ref length, null);
        if (error == 15700) return null; // APPMODEL_ERROR_NO_PACKAGE
        if (error != 122) throw new Win32Exception(error, "无法核对 Codex 进程的应用包身份。");
        var name = new StringBuilder((int)length);
        error = GetPackageFamilyName(process.Handle, ref length, name);
        if (error != 0) throw new Win32Exception(error, "无法核对 Codex 进程的应用包身份。");
        return name.ToString();
    }

    private static Dictionary<int, (int ParentId, string Name)> ReadProcessEntries()
    {
        var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var result = new Dictionary<int, (int ParentId, string Name)>();
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
            if (!Process32First(snapshot, ref entry))
            {
                var firstError = Marshal.GetLastWin32Error();
                if (firstError == 18) return result; // ERROR_NO_MORE_FILES
                throw new Win32Exception(firstError);
            }
            do { result[(int)entry.ProcessId] = ((int)entry.ParentProcessId, entry.ExecutableName); }
            while (Process32Next(snapshot, ref entry));
            var error = Marshal.GetLastWin32Error();
            if (error != 18) throw new Win32Exception(error);
            return result;
        }
        finally { _ = CloseHandle(snapshot); }
    }

    private sealed record WaitResult(bool Exited, string? FailureReason = null);

    private sealed class SnapshotRaceException : Exception
    {
        public SnapshotRaceException(string message, Exception? innerException = null)
            : base(message, innerException) { }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public UIntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int Priority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExecutableName;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetPackageFamilyName(IntPtr process, ref uint length, StringBuilder? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "Process32FirstW")]
    private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "Process32NextW")]
    private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
