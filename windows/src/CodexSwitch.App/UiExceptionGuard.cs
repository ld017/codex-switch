using CodexSwitch.Core.Diagnostics;
using CodexSwitch.Core.Files;

namespace CodexSwitch.App;

internal static class UiExceptionGuard
{
    private static int _handling;
    private static int _installed;

    public static void Install()
    {
        if (Interlocked.Exchange(ref _installed, 1) != 0) return;
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, args) => Handle("UI 未处理异常", args.Exception, showDialog: true);
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception error) Handle("进程未处理异常", error, showDialog: false);
            else Append("进程未处理异常", new InvalidOperationException(Convert.ToString(args.ExceptionObject)));
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Handle("后台任务未处理异常", args.Exception, showDialog: false);
            args.SetObserved();
        };
    }

    public static void Handle(string title, Exception error, bool showDialog)
    {
        Append(title, error);
        if (Interlocked.Exchange(ref _handling, 1) != 0) return;
        try
        {
            if (showDialog)
            {
                try { MessageBox.Show(SecretRedactor.Redact(error.Message), title, MessageBoxButtons.OK, MessageBoxIcon.Error); }
                catch { }
            }
        }
        finally { Volatile.Write(ref _handling, 0); }
    }

    private static void Append(string title, Exception error)
    {
        var dataHome = Environment.GetEnvironmentVariable("CODEX_SWITCH_DATA_HOME");
        var directory = Path.Combine(string.IsNullOrWhiteSpace(dataHome)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexSwitch") : dataHome, "logs");
        _ = TryAppend(directory, title, error);
    }

    public static bool TryAppend(string directory, string title, Exception error)
        => TryAppendText(directory, title, error.ToString(), "codex-switch-crash.log");

    public static bool TryAppendText(string directory, string title, string detail, string fileName = "codex-switch.log")
    {
        return RotatingLogWriter.TryAppend(directory, title, detail, fileName);
    }

    public static void RecordSession(string eventName)
    {
        var dataHome = Environment.GetEnvironmentVariable("CODEX_SWITCH_DATA_HOME");
        var directory = Path.Combine(string.IsNullOrWhiteSpace(dataHome)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexSwitch") : dataHome, "logs");
        _ = TryAppendText(directory, "进程会话", $"PID={Environment.ProcessId} {eventName}");
    }

    // Event handlers start this Task from a synchronous callback. Both exceptions
    // before the first await and faults after it are observed here.
    public static async Task RunAsync(Func<Task> action, Action<Exception> reportError, CancellationToken lifetime = default)
    {
        try { await action(); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            try { reportError(error); }
            catch (Exception reportingError) { Handle("报告界面异常失败", reportingError, showDialog: false); }
        }
    }
}
