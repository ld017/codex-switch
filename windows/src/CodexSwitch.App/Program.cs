using CodexSwitch.App.Tray;

namespace CodexSwitch.App;

internal static class Program
{
    /// <summary>
    ///  The main entry point for the application.
    /// </summary>
    [STAThread]
    static void Main()
    {
        // To customize application configuration such as set high DPI settings or default font,
        // see https://aka.ms/applicationconfiguration.
        ApplicationConfiguration.Initialize();
        try { Application.Run(new TrayApplicationContext(CompositionRoot.Create())); }
        catch (Exception error) { MessageBox.Show(error.Message, "Codex Switch 启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }
}
