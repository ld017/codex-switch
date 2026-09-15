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
            var method = root.GetProperty("method").GetString();
            if (method == "initialize")
            {
                Console.WriteLine(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = id.GetInt32(), result = new { } }));
            }
            else if (mode == "error")
            {
                Console.WriteLine(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = id.GetInt32(), error = new { message = "{\"access_token\":\"secret-access\"}" } }));
            }
            else
            {
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
