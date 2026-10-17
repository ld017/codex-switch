using CodexSwitch.App.Windows;
using CodexSwitch.Core.Codex;
using CodexSwitch.FakeCli;

namespace CodexSwitch.IntegrationTests.Windows;

public sealed class ProcessRunnerTests
{
    [Fact]
    public async Task RunAsync_visible_process_inherits_custom_environment()
    {
        var runner = new ProcessRunner();
        var specification = new ProcessSpec(
            "dotnet",
            [typeof(Marker).Assembly.Location],
            new Dictionary<string, string?>
            {
                ["FAKE_CODEX_MODE"] = "environment-probe",
                ["CODEX_SWITCH_TEST_VALUE"] = "works",
            },
            TimeSpan.FromSeconds(10),
            Visible: true);

        var result = await runner.RunAsync(specification, default);

        Assert.Equal(0, result.ExitCode);
    }
}
