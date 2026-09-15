using CodexSwitch.App.Windows;

namespace CodexSwitch.IntegrationTests.Windows;

public sealed class StartupRegistrationTests : IDisposable
{
    private readonly string _valueName = "CodexSwitch-Test-" + Guid.NewGuid().ToString("N");

    [Fact]
    public void Enable_and_disable_round_trip_quoted_executable_path()
    {
        var registration = new StartupRegistration(_valueName, @"C:\Program Files\Codex Switch\CodexSwitch.exe");

        registration.Enable();
        Assert.True(registration.IsEnabled);
        Assert.Equal("\"C:\\Program Files\\Codex Switch\\CodexSwitch.exe\" --startup", registration.RegisteredCommand);

        registration.Disable();
        Assert.False(registration.IsEnabled);
    }

    public void Dispose() => new StartupRegistration(_valueName, "ignored.exe").Disable();
}
