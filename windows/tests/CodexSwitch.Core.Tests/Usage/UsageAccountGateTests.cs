using CodexSwitch.Core.Auth;
using CodexSwitch.Core.Files;
using CodexSwitch.Core.Tests.Auth;
using CodexSwitch.Core.Usage;

namespace CodexSwitch.Core.Tests.Usage;

public sealed class UsageAccountGateTests
{
    [Fact]
    public async Task Different_accounts_with_the_same_former_stripe_can_run_concurrently()
    {
        using var directory = new TestDirectory();
        var store = new AccountStore(directory.File("data"), new AccountImportServiceTests.PassthroughProtector(), new AtomicFileStore());
        var profiles = new List<AccountProfile>();
        for (var index = 0; index < 33; index++) profiles.Add(await store.SaveAsync($"Synthetic {index}", TestAuth.OAuth("a", "r", $"synthetic-{index}"), default));
        var pair = profiles.GroupBy(profile => (uint)profile.Id.GetHashCode() % 32).First(group => group.Count() > 1).Take(2).ToArray();
        var started = 0;
        var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fetcher = new UsageFetcher(store, directory.File("config.toml"), directory.File("tmp"), async (_, token) =>
        {
            if (Interlocked.Increment(ref started) == 2) bothStarted.TrySetResult();
            await release.Task.WaitAsync(token);
            return new RateLimitsResponse(new RateLimitsSnapshot(new RateLimitWindow(12, 300, null), null, null, "plus"), null);
        });
        var tasks = pair.Select(profile => fetcher.FetchAsync(profile.Id, default)).ToArray();
        try { await bothStarted.Task.WaitAsync(TimeSpan.FromSeconds(2)); }
        finally { release.TrySetResult(); await Task.WhenAll(tasks); }
        Assert.Equal(2, started);
        Assert.Empty((System.Collections.IDictionary)typeof(UsageFetcher).GetField("_accountGates", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(fetcher)!);
    }

    [Fact]
    public async Task Cancelled_waiter_cannot_retire_another_callers_account_gate()
    {
        using var directory = new TestDirectory();
        var store = new AccountStore(directory.File("data"), new AccountImportServiceTests.PassthroughProtector(), new AtomicFileStore());
        var profile = await store.SaveAsync("Synthetic", TestAuth.OAuth("a", "r", "synthetic"), default);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = 0; var calls = 0;
        var fetcher = new UsageFetcher(store, directory.File("config.toml"), directory.File("tmp"), async (_, token) =>
        {
            Assert.Equal(1, Interlocked.Increment(ref running));
            Interlocked.Increment(ref calls);
            entered.TrySetResult();
            try { await release.Task.WaitAsync(token); }
            finally { Interlocked.Decrement(ref running); }
            return new RateLimitsResponse(new RateLimitsSnapshot(new RateLimitWindow(12, 300, null), null, null, "plus"), null);
        });
        var first = fetcher.FetchAsync(profile.Id, default);
        Task<UsageSnapshot>? third = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var cancellation = new CancellationTokenSource();
            var cancelled = fetcher.FetchAsync(profile.Id, cancellation.Token);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
            third = fetcher.FetchAsync(profile.Id, default);
            Assert.Equal(1, calls);
        }
        finally { release.TrySetResult(); await first; if (third is not null) await third; }
        Assert.Equal(2, calls);
        Assert.Empty((System.Collections.IDictionary)typeof(UsageFetcher).GetField("_accountGates", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(fetcher)!);
    }
}
