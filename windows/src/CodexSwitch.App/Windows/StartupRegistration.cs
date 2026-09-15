using Microsoft.Win32;

namespace CodexSwitch.App.Windows;

public sealed class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private readonly string _valueName;
    private readonly string _executablePath;
    public StartupRegistration(string valueName, string executablePath) { _valueName = valueName; _executablePath = executablePath; }
    public string? RegisteredCommand { get { using var key = Registry.CurrentUser.OpenSubKey(RunKey); return key?.GetValue(_valueName) as string; } }
    public bool IsEnabled => string.Equals(RegisteredCommand, Command, StringComparison.Ordinal);
    public void Enable() { using var key = Registry.CurrentUser.CreateSubKey(RunKey); key.SetValue(_valueName, Command, RegistryValueKind.String); }
    public void Disable() { using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true); key?.DeleteValue(_valueName, throwOnMissingValue: false); }
    private string Command => $"\"{_executablePath}\" --startup";
}
