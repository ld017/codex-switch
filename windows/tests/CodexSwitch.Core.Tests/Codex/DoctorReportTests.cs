using CodexSwitch.Core.Codex;

namespace CodexSwitch.Core.Tests.Codex;

public sealed class DoctorReportTests
{
    [Theory]
    [InlineData("{\"checks\":{\"config.load\":{\"status\":\"ok\"}}}", true)]
    [InlineData("{\"checks\":{\"config.load\":{\"status\":\"error\"}}}", false)]
    [InlineData("{\"checks\":{}}", false)]
    [InlineData("not-json", false)]
    public void IsConfigValid_requires_ok_config_load_status(string output, bool expected)
    {
        Assert.Equal(expected, DoctorReport.IsConfigValid(output));
    }
}
