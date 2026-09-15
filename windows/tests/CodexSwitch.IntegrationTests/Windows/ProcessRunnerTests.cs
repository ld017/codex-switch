using CodexSwitch.App.Windows;
using CodexSwitch.Core.Codex;

namespace CodexSwitch.IntegrationTests.Windows;

public sealed class ProcessRunnerTests
{
    [Fact]
    public async Task RunAsync_visible_process_inherits_custom_environment()
    {
        var runner = new ProcessRunner();
        var specification = new ProcessSpec(
            "powershell.exe",
            ["-NoProfile", "-NonInteractive", "-Command", "if ($env:CODEX_SWITCH_TEST_VALUE -ne 'works') { exit 9 }"],
            new Dictionary<string, string?> { ["CODEX_SWITCH_TEST_VALUE"] = "works" },
            TimeSpan.FromSeconds(10),
            Visible: true);

        var result = await runner.RunAsync(specification, default);

        Assert.Equal(0, result.ExitCode);
    }
}
