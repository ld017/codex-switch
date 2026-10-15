namespace CodexSwitch.App;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        using var instance = new Mutex(true, @"Local\CodexSwitch", out var firstInstance);
        if (!firstInstance)
        {
            MessageBox.Show("Codex Switch 已在运行，请使用现有窗口。", "Codex Switch");
            return;
        }
        try
        {
            var startMinimized = args.Any(arg => string.Equals(arg, "--startup", StringComparison.OrdinalIgnoreCase));
            Application.Run(new MainForm(CompositionRoot.Create(), startMinimized));
        }
        catch (Exception error)
        {
            MessageBox.Show(error.Message, "Codex Switch 启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
