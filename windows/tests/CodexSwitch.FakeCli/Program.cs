using System.Text.Json;

namespace CodexSwitch.FakeCli;

public sealed class Marker;

public static class Program
{
    public static async Task Main()
    {
        var mode = Environment.GetEnvironmentVariable("FAKE_CODEX_MODE") ?? "success";
        while (await Console.In.ReadLineAsync() is { } line)
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!root.TryGetProperty("id", out var id)) continue;
            if (!root.TryGetProperty("method", out var methodElement)) continue;
            var method = methodElement.GetString();
            if (method == "initialize")
            {
                if (mode == "stderr-flood")
                {
                    for (var index = 0; index < 128; index++) await Console.Error.WriteLineAsync(new string('x', 8192));
                    await Console.Error.FlushAsync();
                }
                if (mode.StartsWith("external", StringComparison.Ordinal)
                    && !root.GetProperty("params").GetProperty("capabilities").GetProperty("experimentalApi").GetBoolean())
                    throw new InvalidOperationException("Experimental capability required");
                Console.WriteLine(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = id.GetInt32(), result = new { } }));
            }
            else if (method == "account/login/start")
            {
                var parameters = root.GetProperty("params");
                if (parameters.GetProperty("type").GetString() != "chatgptAuthTokens"
                    || parameters.GetProperty("accessToken").GetString() != "external-test-access"
                    || parameters.GetProperty("chatgptAccountId").GetString() != "external-test-account")
                    throw new InvalidOperationException("Unexpected external credentials");
                Console.WriteLine(JsonSerializer.Serialize(new { id = id.GetInt32(), result = new { type = "chatgptAuthTokens" } }));
            }
            else if (mode == "timeout")
            {
                await Task.Delay(Timeout.InfiniteTimeSpan);
            }
            else if (mode == "exit")
            {
                await Console.Error.WriteLineAsync("Quota endpoint failed: {\"access_token\":\"stderr-secret\"}");
                await Console.Error.FlushAsync();
                return;
            }
            else if (mode == "external-refresh")
            {
                // Intentionally use the same numeric ID as the client's request: server IDs are independent.
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    id = id.GetInt32(), method = "account/chatgptAuthTokens/refresh",
                    @params = new { reason = "unauthorized", previousAccountId = "external-test-account" },
                }));
            }
            else if (mode == "error")
            {
                Console.WriteLine(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = id.GetInt32(), error = new { message = "{\"access_token\":\"secret-access\"}" } }));
            }
            else if (mode is "oversized-line" or "oversized-unterminated")
            {
                Console.Write("synthetic-payload-secret");
                for (var index = 0; index < 257; index++) Console.Write(new string('x', 4096));
                if (mode == "oversized-line") Console.WriteLine();
                await Console.Out.FlushAsync();
                if (mode == "oversized-unterminated") await Task.Delay(Timeout.InfiniteTimeSpan);
            }
            else if (method == "account/usage/read")
            {
                if (mode == "token-timeout") await Task.Delay(Timeout.InfiniteTimeSpan);
                if (mode == "token-unsupported")
                    Console.WriteLine(JsonSerializer.Serialize(new { id = id.GetInt32(), error = new { message = "unsupported method account/usage/read" } }));
                else if (mode == "token-error-secret")
                    Console.WriteLine(JsonSerializer.Serialize(new { id = id.GetInt32(), error = new { message = "{\"access_token\":\"token-error-secret-value\"}" } }));
                else if (mode == "token-malformed")
                    Console.WriteLine(JsonSerializer.Serialize(new { id = id.GetInt32(), result = new { summary = new { lifetimeTokens = "invalid" } } }));
                else if (mode == "token-empty")
                    Console.WriteLine(JsonSerializer.Serialize(new { id = id.GetInt32(), result = new { summary = (object?)null, dailyUsageBuckets = (object?)null } }));
                else if (mode == "token-partial")
                    Console.WriteLine(JsonSerializer.Serialize(new
                    {
                        id = id.GetInt32(), result = new
                        {
                            summary = new { lifetimeTokens = -1L, peakDailyTokens = -2L },
                            dailyUsageBuckets = new object[] { new { startDate = "2026-10-02", tokens = 0L },
                                new { startDate = "2026-10-03", tokens = -100L }, new { startDate = "2026-10-04" } },
                        },
                    }));
                else
                    Console.WriteLine(JsonSerializer.Serialize(new
                    {
                        id = id.GetInt32(), result = new
                        {
                            summary = new { lifetimeTokens = 145_250_530L, peakDailyTokens = 9_000_000L },
                            dailyUsageBuckets = new[] { new { startDate = "2026-10-02", tokens = 6_907_839L } },
                        },
                    }));
            }
            else
            {
                if (mode == "noise")
                {
                    Console.WriteLine("[]");
                    Console.WriteLine("{\"id\":\"unrelated-string-id\",\"result\":{}}");
                    Console.WriteLine("{\"method\":\"account/rateLimits/updated\",\"params\":{}}");
                }
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0",
                    id = id.GetInt32(),
                    result = new
                    {
                        rateLimits = new { primary = new { usedPercent = 25.0, windowDurationMins = 300, resetsAt = 2000000000L }, secondary = (object?)null, credits = (object?)null, planType = "pro" },
                        rateLimitResetCredits = new { availableCount = 2, credits = Array.Empty<object>() },
                    }
                }));
            }
            await Console.Out.FlushAsync();
        }
    }
}
