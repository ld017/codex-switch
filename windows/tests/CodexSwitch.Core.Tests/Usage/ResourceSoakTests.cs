using System.Diagnostics;
using System.Text.Json;
using CodexSwitch.Core.Auth;
using CodexSwitch.Core.Files;
using CodexSwitch.Core.Tests.Auth;
using CodexSwitch.Core.Usage;

namespace CodexSwitch.Core.Tests.Usage;

public sealed class ResourceSoakTests
{
    [Fact]
    public async Task Hundred_rounds_bound_query_concurrency_reuse_tokens_and_release_temporary_homes()
    {
        using var directory = new TestDirectory();
        var accounts = new AccountStore(directory.File("data"), new AccountImportServiceTests.PassthroughProtector(), new AtomicFileStore());
        for (var i = 0; i < 5; i++) await accounts.SaveAsync($"Synthetic {i}", TestAuth.OAuth("test-access", "test-refresh", $"synthetic-{i}"), default);
        var running = 0; var peak = 0; var quotaReads = 0; var tokenReads = 0;
        var fetcher = new UsageFetcher(accounts, directory.File("config.toml"), directory.File("tmp"), async (_, options, received, token) =>
        {
            var current = Interlocked.Increment(ref running);
            int observed;
            do { observed = Volatile.Read(ref peak); } while (current > observed && Interlocked.CompareExchange(ref peak, current, observed) != observed);
            try
            {
                await Task.Delay(1, token);
                Interlocked.Increment(ref quotaReads);
                var response = new RateLimitsResponse(new RateLimitsSnapshot(new RateLimitWindow(12, 300, null), null, null, "plus"), null);
                var time = DateTimeOffset.UtcNow;
                received?.Invoke(response, time);
                if (options.IncludeTokens)
                {
                    Interlocked.Increment(ref tokenReads);
                    response = response with { TokenUsage = TokenUsageState.Available(new TokenUsageSnapshot(new TokenUsageSummary(25_000_000),
                        [new TokenUsageDailyBucket(time.LocalDateTime.ToString("yyyy-MM-dd"), 1_000)], time)) };
                }
                return response with { QuotasFetchedAt = time };
            }
            finally { Interlocked.Decrement(ref running); }
        });
        var coordinator = new UsageRefreshCoordinator(accounts, fetcher, new UsageCache(directory.File("cache.json"), new AtomicFileStore()));
        var samples = new List<object>();
        var elapsed = Stopwatch.StartNew();
        for (var round = 1; round <= 100; round++)
        {
            var states = await coordinator.RefreshAllAsync(default, forceTokens: false);
            Assert.Equal(5, states.Count);
            Assert.All(states.Values, state => Assert.Equal(UsageStatus.Available, state.Status));
            Assert.Empty(Directory.GetDirectories(directory.File("tmp")));
            if (round % 10 == 0)
            {
                using var process = Process.GetCurrentProcess();
                samples.Add(new { round, privateBytes = process.PrivateMemorySize64, workingSetBytes = process.WorkingSet64, handles = process.HandleCount });
            }
        }
        Assert.InRange(peak, 1, 2);
        Assert.Equal(500, quotaReads);
        Assert.Equal(5, tokenReads);
        Assert.Equal(0, running);
        var output = Environment.GetEnvironmentVariable("CODEX_SWITCH_BENCHMARK_OUTPUT");
        if (!string.IsNullOrWhiteSpace(output))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new
            {
                scenario = "Synthetic Core refresh, 5 accounts, 100 automatic rounds; no real CLI or credentials",
                quotaReads, tokenReads, peakConcurrentQueries = peak, elapsedMilliseconds = elapsed.ElapsedMilliseconds, samples,
                limitation = "Memory samples belong to the .NET test host, not the installed WinForms app; no before/after claim."
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
