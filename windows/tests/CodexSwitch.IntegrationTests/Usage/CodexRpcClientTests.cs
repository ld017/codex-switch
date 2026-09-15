using CodexSwitch.Core.Usage;
using CodexSwitch.FakeCli;

namespace CodexSwitch.IntegrationTests.Usage;

public sealed class CodexRpcClientTests
{
    [Fact]
    public async Task Initialize_and_fetch_rate_limits_over_newline_delimited_json_rpc()
    {
        var environment = new Dictionary<string, string?>
        {
            ["FAKE_CODEX_MODE"] = "success",
        };
        await using var client = new CodexRpcClient(
            "dotnet",
            environment,
            [typeof(Marker).Assembly.Location],
            TimeSpan.FromSeconds(5));

        await client.InitializeAsync("CodexSwitchWindows", "1.0.0", default);
        var response = await client.FetchRateLimitsAsync(default);

        Assert.Equal("pro", response.RateLimits.PlanType);
        Assert.Equal(2, response.RateLimitResetCredits!.AvailableCount);
    }

    [Fact]
    public async Task FetchRateLimitsAsync_redacts_rpc_error_message()
    {
        var environment = new Dictionary<string, string?>
        {
            ["FAKE_CODEX_MODE"] = "error",
        };
        await using var client = new CodexRpcClient(
            "dotnet",
            environment,
            [typeof(Marker).Assembly.Location],
            TimeSpan.FromSeconds(5));
        await client.InitializeAsync("CodexSwitchWindows", "1.0.0", default);

        var error = await Assert.ThrowsAsync<CodexRpcException>(() => client.FetchRateLimitsAsync(default));

        Assert.DoesNotContain("secret-access", error.Message);
        Assert.Contains("<redacted>", error.Message, StringComparison.Ordinal);
    }
}
