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
    private readonly Func<CodexProcessCandidate, bool> _requestClose;
    private readonly Func<CodexProcessCandidate, bool> _terminate;
    private readonly CapturedProcessSet _protected;
    private readonly TimeSpan _forceCloseTimeout;
    private CapturedProcessSet _captured = new([]);
    private CapturedProcessSet _terminable = new([]);
    private bool _hasShutdownCapture;

    // The delegates keep lifecycle tests synthetic. Production uses the real process snapshot
    // and shell activation, while tests can exercise process races without touching Codex.
    public WindowsCodexLifecycle(
        Func<IReadOnlyList<CodexProcessCandidate>>? snapshotReader = null,
        Action? activateCodex = null,
        Func<CodexProcessCandidate, bool>? requestClose = null,
        Func<CodexProcessCandidate, bool>? terminate = null,
        int? protectedProcessId = null,
        TimeSpan? forceCloseTimeout = null)
    {
        _snapshotReader = snapshotReader ?? ReadSnapshot;
        _requestClose = requestClose ?? CloseVerifiedWindow;
        _terminate = terminate ?? TerminateVerifiedInstance;
        _protected = new CapturedProcessSet([protectedProcessId ?? Environment.ProcessId]);
        _forceCloseTimeout = forceCloseTimeout ?? TimeSpan.FromSeconds(5);
        _activateCodex = activateCodex ?? (() =>
        {
            using var activation = Process.Start(ActivationInfo);
        });
    }

    public async Task<CodexShutdownResult> RequestCloseAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _hasShutdownCapture = false;
        if (!TryReadSnapshot(out var snapshot, out var snapshotFailure))
            return new CodexShutdownResult(false, [], snapshotFailure);

        var desktop = CapturedProcessSet.SelectDesktopProcesses(snapshot);
        // Capture the whole desktop tree, including its auth-writing CLI backend, before closing
        // only top-level windows. A renderer exiting does not mean auth.json is safe to replace.
        _captured = CapturedProcessSet.CaptureTree(snapshot, desktop.Select(process => process.Id));
        _terminable = new CapturedProcessSet(desktop.Select(process => process.Id));
        _terminable.IncludeDescendants(snapshot, requirePresentParent: true);
        _hasShutdownCapture = true;
        var roots = CapturedProcessSet.SelectRoots(desktop.Where(process => process.HasMainWindow));
        string? closeFailure = null;
        foreach (var id in roots.ProcessIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var original = desktop.FirstOrDefault(candidate => candidate.Id == id);
                if (original is null || !_requestClose(original))
                    closeFailure ??= "无法确认 Codex 窗口对应的进程实例。";
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

    // The caller obtains the user's explicit confirmation before requesting forced shutdown.
    // Kill each verified instance separately: a tree kill could also kill this switcher when
    // it was originally launched from a Codex terminal.
    public async Task<bool> ForceCloseCapturedAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_hasShutdownCapture) return false;

        var elapsed = Stopwatch.StartNew();
        var terminationRequested = new HashSet<int>();
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryReadSnapshot(out var snapshot, out _)) return false;
            _captured.IncludeDescendants(snapshot);
            _terminable.IncludeDescendants(snapshot, requirePresentParent: true);
            var remaining = _captured.RemainingInstancesFrom(snapshot).ToHashSet();
            var desktop = CapturedProcessSet.SelectDesktopProcesses(snapshot);
            if (remaining.Count == 0 && desktop.Count == 0) return true;

            // A new desktop instance or an unverified creation time is not permission to
            // terminate an unrelated process. An orphan first seen after its parent exited
            // also has ambiguous ancestry, so leave credentials unchanged in that case.
            if (desktop.Any(candidate => !_captured.MatchesKnownInstance(candidate))) return false;
            var targets = snapshot.Where(candidate => remaining.Contains(candidate.Id)).ToArray();
            if (targets.Any(candidate => !_terminable.MatchesKnownInstance(candidate))) return false;
            var candidate = targets.OrderBy(candidate => candidate.StartTime)
                .FirstOrDefault(candidate => !terminationRequested.Contains(candidate.Id));
            if (candidate is not null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var requested = false;
                try
                {
                    requested = _terminate(candidate);
                }
                catch (Exception error) when (IsProcessRace(error)) { }
                if (!requested)
                {
                    // A parent can exit together with its children between snapshots. A
                    // failed action is harmless only after the original instance is gone.
                    if (!TryReadSnapshot(out var afterAction, out _)) return false;
                    if (afterAction.Any(current => current.Id == candidate.Id
                        && (current.StartTime == DateTimeOffset.MaxValue || current.StartTime == candidate.StartTime))) return false;
                }
                terminationRequested.Add(candidate.Id);
            }

            // Always take a fresh snapshot after termination, including with a zero deadline.
            if (elapsed.Elapsed >= _forceCloseTimeout)
                return (await WaitUntilCapturedExitAsync(TimeSpan.Zero, cancellationToken)).Exited;
            // Refresh between individual kills so newly spawned children are observed while
            // their verified parent is still present. Poll only when all known kills were sent.
            if (candidate is null) await Task.Delay(100, cancellationToken);
        } while (true);
    }

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
            _terminable.IncludeDescendants(snapshot, requirePresentParent: true);
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
            _protected.IncludeDescendants(snapshot);
            var protectedInstances = _protected.RemainingInstancesFrom(snapshot).ToHashSet();
            if (snapshot.Any(candidate => protectedInstances.Contains(candidate.Id) && !_protected.MatchesKnownInstance(candidate)))
                throw new SnapshotRaceException("无法确认切号器自身进程的身份，已停止切换。");
            snapshot = snapshot.Where(candidate => !protectedInstances.Contains(candidate.Id)).ToArray();
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
            var possibleDesktop = string.Equals(entry.Name, "ChatGPT.exe", StringComparison.OrdinalIgnoreCase)
                || string.Equals(entry.Name, "Codex.exe", StringComparison.OrdinalIgnoreCase);
            Process process;
            try { process = Process.GetProcessById(id); }
            catch (Exception error) when (IsProcessRace(error))
            {
                if (error is ArgumentException) continue; // The enumerated process already exited.
                if (possibleDesktop)
                    throw new SnapshotRaceException("无法稳定核对 Codex 进程状态。", error);
                // Retain inaccessible children as unknown instances rather than treating
                // inability to inspect an auth-writing backend as evidence that it exited.
                result.Add(new CodexProcessCandidate(id, false, DateTimeOffset.MaxValue, entry.ParentId));
                continue;
            }

            using (process)
            {
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

    private static bool CloseVerifiedWindow(CodexProcessCandidate candidate)
    {
        using var process = Process.GetProcessById(candidate.Id);
        if (!IsSameProcessInstance(process, candidate.StartTime) || !HasExpectedDesktopIdentity(process, candidate)) return false;
        _ = process.CloseMainWindow();
        return true;
    }

    private static bool TerminateVerifiedInstance(CodexProcessCandidate candidate)
    {
        using var process = Process.GetProcessById(candidate.Id);
        if (!IsSameProcessInstance(process, candidate.StartTime) || !HasExpectedDesktopIdentity(process, candidate)) return false;
        process.Kill(entireProcessTree: false);
        return true;
    }

    private static bool HasExpectedDesktopIdentity(Process process, CodexProcessCandidate candidate)
    {
        // Descendants are authorized by their captured ancestry and creation time. Desktop
        // entry points additionally retain the package and executable identity checks.
        if (CapturedProcessSet.SelectDesktopProcesses([candidate]).Count == 0) return true;
        return string.Equals(GetFamilyName(process), candidate.PackageFamilyName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(process.MainModule?.FileName, candidate.ExecutablePath, StringComparison.OrdinalIgnoreCase);
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
