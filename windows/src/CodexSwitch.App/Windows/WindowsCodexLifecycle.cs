using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using CodexSwitch.Core.Codex;

namespace CodexSwitch.App.Windows;

public sealed class WindowsCodexLifecycle : ICodexLifecycle
{
    private CapturedProcessSet _captured = new([]);

    public async Task<CodexShutdownResult> RequestCloseAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = ReadSnapshot();
        var desktop = CapturedProcessSet.SelectDesktopProcesses(snapshot);
        // Capture the whole desktop tree, including its auth-writing CLI backend, before closing
        // only top-level windows. A renderer exiting does not mean auth.json is safe to replace.
        _captured = CapturedProcessSet.CaptureTree(snapshot, desktop.Select(process => process.Id));
        var roots = CapturedProcessSet.SelectRoots(desktop.Where(process => process.HasMainWindow));
        foreach (var id in roots.ProcessIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var process = Process.GetProcessById(id);
                var original = desktop.Single(candidate => candidate.Id == id);
                if (process.StartTime.ToUniversalTime() != original.StartTime.UtcDateTime) continue;
                _ = process.CloseMainWindow();
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
        }

        var exited = await WaitUntilCapturedExitAsync(timeout, cancellationToken);
        return new CodexShutdownResult(exited, _captured.ProcessIds);
    }

    // Kept for older callers. Switching credentials must never forcibly kill a user's tasks.
    public Task<bool> ForceCloseCapturedAsync(CancellationToken cancellationToken)
        => WaitUntilCapturedExitAsync(TimeSpan.Zero, cancellationToken);

    public async Task LaunchAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var activation = Process.Start(new ProcessStartInfo("explorer.exe", @"shell:AppsFolder\OpenAI.Codex_2p2nqsd0c76g0!App") { UseShellExecute = true });
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromSeconds(30))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (CapturedProcessSet.SelectDesktopProcesses(ReadSnapshot()).Any(process => process.HasMainWindow)) return;
            await Task.Delay(250, cancellationToken);
        }
        throw new InvalidOperationException("未能确认 Codex 桌面窗口已打开，请从开始菜单手动打开 Codex。");
    }

    private async Task<bool> WaitUntilCapturedExitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var elapsed = Stopwatch.StartNew();
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = ReadSnapshot();
            _captured.IncludeDescendants(snapshot);
            if (_captured.RemainingInstancesFrom(snapshot).Count == 0
                && CapturedProcessSet.SelectDesktopProcesses(snapshot).Count == 0) return true;
            if (elapsed.Elapsed >= timeout) break;
            await Task.Delay(250, cancellationToken);
        } while (true);
        return false;
    }

    private static IReadOnlyList<CodexProcessCandidate> ReadSnapshot()
    {
        var entries = ReadProcessEntries();
        var result = new List<CodexProcessCandidate>();
        foreach (var (id, entry) in entries)
        {
            Process process;
            try { process = Process.GetProcessById(id); }
            catch (ArgumentException) { continue; }
            using (process)
            {
                try
                {
                    // Toolhelp supplies names without opening protected system processes.
                    var possibleDesktop = entry.Name.Equals("ChatGPT.exe", StringComparison.OrdinalIgnoreCase)
                        || entry.Name.Equals("Codex.exe", StringComparison.OrdinalIgnoreCase);
                    string? path = null;
                    string? family = null;
                    var hasWindow = false;
                    if (possibleDesktop)
                    {
                        family = GetFamilyName(process);
                        if (family == "OpenAI.Codex_2p2nqsd0c76g0")
                        {
                            path = process.MainModule?.FileName;
                            if (path is null) throw new InvalidOperationException("无法确认 Codex 桌面程序路径，已停止切换。");
                            hasWindow = process.MainWindowHandle != IntPtr.Zero;
                        }
                    }

                    result.Add(new CodexProcessCandidate(process.Id, hasWindow, SafeStartTime(process),
                        entry.ParentId, family, path));
                }
                catch (InvalidOperationException) when (HasExited(process)) { }
                catch (Win32Exception) when (HasExited(process)) { }
            }
        }
        return result;
    }

    private static bool HasExited(Process process)
    {
        try { return process.HasExited; }
        catch (InvalidOperationException) { return true; }
        catch (Win32Exception) { return false; }
    }

    private static DateTimeOffset SafeStartTime(Process process)
    {
        try { return new DateTimeOffset(process.StartTime.ToUniversalTime()); }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception)
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
            if (!Process32First(snapshot, ref entry)) throw new Win32Exception(Marshal.GetLastWin32Error());
            do { result[(int)entry.ProcessId] = ((int)entry.ParentProcessId, entry.ExecutableName); }
            while (Process32Next(snapshot, ref entry));
            var error = Marshal.GetLastWin32Error();
            if (error != 18) throw new Win32Exception(error);
            return result;
        }
        finally { _ = CloseHandle(snapshot); }
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
