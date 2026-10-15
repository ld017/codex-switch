namespace CodexSwitch.App;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        UiExceptionGuard.Install();
        ApplicationConfiguration.Initialize();
        using var instance = new Mutex(true, @"Local\CodexSwitch", out var firstInstance);
        if (!firstInstance)
        {
            MessageBox.Show("Codex Switch 已在运行，请使用现有窗口。", "Codex Switch");
            return;
        }
        UiExceptionGuard.RecordSession("启动");
        Application.ApplicationExit += (_, _) => UiExceptionGuard.RecordSession("消息循环正常退出");
        AppDomain.CurrentDomain.ProcessExit += (_, _) => UiExceptionGuard.RecordSession("进程退出回调");
        try
        {
            var startMinimized = args.Any(arg => string.Equals(arg, "--startup", StringComparison.OrdinalIgnoreCase));
            Application.Run(new MainForm(CompositionRoot.Create(), startMinimized));
        }
        catch (Exception error)
        {
            UiExceptionGuard.Handle("Codex Switch 启动失败", error, showDialog: true);
        }
    }
}
