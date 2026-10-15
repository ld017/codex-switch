using CodexSwitch.Core.Usage;
using CodexSwitch.FakeCli;

namespace CodexSwitch.IntegrationTests.Usage;

public sealed class CodexRpcClientTests
{
    [Fact]
    public async Task FetchUsageAsync_reads_server_token_totals_and_daily_buckets_after_quotas()
    {
        await using var client = CreateClient("success");
        await client.InitializeAsync("Test", "1.0", default);

        var result = await client.FetchUsageAsync(default);

        Assert.Equal(25, result.RateLimits!.Primary!.UsedPercent);
        Assert.Equal(TokenUsageStatus.Available, result.TokenUsage!.Status);
        Assert.Equal(145_250_530, result.TokenUsage.Snapshot!.Summary!.LifetimeTokens);
        Assert.Equal(9_000_000, result.TokenUsage.Snapshot.Summary.PeakDailyTokens);
        Assert.Equal(6_907_839, Assert.Single(result.TokenUsage.Snapshot.DailyUsageBuckets!).Tokens);
    }

    [Theory]
    [InlineData("token-unsupported")]
    [InlineData("token-malformed")]
    [InlineData("token-empty")]
    public async Task FetchUsageAsync_keeps_valid_quotas_when_token_endpoint_is_unavailable(string mode)
    {
        await using var client = CreateClient(mode);
        await client.InitializeAsync("Test", "1.0", default);

        var result = await client.FetchUsageAsync(default);

        Assert.Equal(25, result.RateLimits!.Primary!.UsedPercent);
        Assert.Equal(TokenUsageStatus.Error, result.TokenUsage!.Status);
        Assert.Null(result.TokenUsage.Snapshot);
    }

    [Fact]
    public async Task FetchUsageAsync_token_timeout_is_independent_and_does_not_discard_valid_quotas()
    {
        await using var client = CreateClient("token-timeout", TimeSpan.FromMilliseconds(400));
        await client.InitializeAsync("Test", "1.0", default);

        var result = await client.FetchUsageAsync(default);

        Assert.Equal(25, result.RateLimits!.Primary!.UsedPercent);
        Assert.Equal(TokenUsageStatus.Error, result.TokenUsage!.Status);
        Assert.Contains("account/usage/read", result.TokenUsage.Message);
    }

    [Fact]
    public async Task FetchUsageAsync_caller_cancellation_during_token_request_is_not_reported_as_success()
    {
        await using var client = CreateClient("token-timeout");
        await client.InitializeAsync("Test", "1.0", default);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var error = await Assert.ThrowsAsync<PartialUsageCancellationException>(() => client.FetchUsageAsync(cancellation.Token));
        Assert.Equal(25, error.Quotas.RateLimits!.Primary!.UsedPercent);
        Assert.True(error.QuotasFetchedAt <= DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task FetchUsageAsync_redacts_independent_token_errors()
    {
        await using var client = CreateClient("token-error-secret");
        await client.InitializeAsync("Test", "1.0", default);

        var result = await client.FetchUsageAsync(default);

        Assert.Equal(25, result.RateLimits!.Primary!.UsedPercent);
        Assert.DoesNotContain("token-error-secret-value", result.TokenUsage!.Message);
        Assert.Contains("<redacted>", result.TokenUsage.Message);
    }

    [Fact]
    public async Task FetchUsageAsync_missing_and_negative_totals_remain_unknown_but_valid_zero_is_preserved()
    {
        await using var client = CreateClient("token-partial");
        await client.InitializeAsync("Test", "1.0", default);

        var result = await client.FetchUsageAsync(default);

        Assert.Equal(TokenUsageStatus.Available, result.TokenUsage!.Status);
        Assert.Null(result.TokenUsage.Snapshot!.Summary!.LifetimeTokens);
        Assert.Null(result.TokenUsage.Snapshot.Summary.PeakDailyTokens);
        Assert.Equal(0, Assert.Single(result.TokenUsage.Snapshot.DailyUsageBuckets!).Tokens);
    }

    [Fact]
    public async Task Initialize_drains_stderr_while_waiting_for_stdout()
    {
        await using var client = CreateClient("stderr-flood");

        await client.InitializeAsync("Test", "1.0", default);
        var response = await client.FetchRateLimitsAsync(default);

        Assert.Equal("pro", response.RateLimits!.PlanType);
    }

    [Fact]
    public async Task FetchRateLimitsAsync_preserves_caller_cancellation_instead_of_reporting_timeout()
    {
        await using var client = CreateClient("timeout");
        await client.InitializeAsync("Test", "1.0", default);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.FetchRateLimitsAsync(cancellation.Token));
    }

    [Fact]
    public async Task FetchRateLimitsAsync_has_a_bounded_request_timeout()
    {
        await using var client = CreateClient("timeout", TimeSpan.FromMilliseconds(500));
        await client.InitializeAsync("Test", "1.0", default);

        var error = await Assert.ThrowsAsync<CodexRpcException>(() => client.FetchRateLimitsAsync(default));

        Assert.Contains("timed out on account/rateLimits/read", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FetchRateLimitsAsync_reports_cli_exit_and_redacts_stderr()
    {
        await using var client = CreateClient("exit");
        await client.InitializeAsync("Test", "1.0", default);

        var error = await Assert.ThrowsAsync<CodexRpcException>(() => client.FetchRateLimitsAsync(default));

        Assert.Contains("closed stdout", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("stderr-secret", error.Message);
    }

    [Fact]
    public async Task FetchRateLimitsAsync_ignores_notifications_and_unrelated_message_ids()
    {
        await using var client = CreateClient("noise");
        await client.InitializeAsync("Test", "1.0", default);

        var responses = await Task.WhenAll(client.FetchRateLimitsAsync(default), client.FetchRateLimitsAsync(default));

        Assert.All(responses, response => Assert.Equal("pro", response.RateLimits!.PlanType));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UseExternalAuthFromHomeAsync_uses_access_only_auth_and_refuses_server_refresh(bool refreshRequested)
    {
        var home = Path.Combine(Path.GetTempPath(), "codex-switch-rpc-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(home, "usage-external-auth.json"),
                "{\"accessToken\":\"external-test-access\",\"chatgptAccountId\":\"external-test-account\"}");
            await using var client = CreateClient(refreshRequested ? "external-refresh" : "external");
            await client.InitializeAsync("Test", "1.0", default);
            await client.UseExternalAuthFromHomeAsync(home, default);
            if (refreshRequested)
            {
                var error = await Assert.ThrowsAsync<CodexRpcException>(() => client.FetchRateLimitsAsync(default));
                Assert.Contains("Authentication required", error.Message, StringComparison.Ordinal);
            }
            else
            {
                var response = await client.FetchRateLimitsAsync(default);
                Assert.Equal("pro", response.RateLimits!.PlanType);
            }
            Assert.False(File.Exists(Path.Combine(home, "auth.json")));
        }
        finally { Directory.Delete(home, true); }
    }

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

        Assert.Equal("pro", response.RateLimits!.PlanType);
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

    private static CodexRpcClient CreateClient(string mode, TimeSpan? timeout = null)
        => new("dotnet", new Dictionary<string, string?> { ["FAKE_CODEX_MODE"] = mode },
            [typeof(Marker).Assembly.Location], timeout ?? TimeSpan.FromSeconds(5));
}
