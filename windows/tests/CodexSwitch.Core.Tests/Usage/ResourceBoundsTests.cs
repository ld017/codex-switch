using System.Globalization;
using CodexSwitch.Core.Files;
using CodexSwitch.Core.Usage;

namespace CodexSwitch.Core.Tests.Usage;

public sealed class ResourceBoundsTests
{
    [Fact]
    public void Daily_retention_keeps_latest_366_unique_valid_dates_without_recomputing_summary()
    {
        var today = new DateOnly(2026, 10, 18);
        var days = Enumerable.Range(0, 700).Select(i => new TokenUsageDailyBucket(today.AddDays(-i).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), i)).ToList();
        days.Add(new(today.ToString("yyyy-MM-dd"), 999));
        days.Add(new("invalid", 123));
        days.Add(new("2026-10-19", 123));
        days.Add(new("2026-10-18", -1));
        var summary = new TokenUsageSummary(9_000_000_000, 1_000, "2024-01-01", 700);
        var snapshot = new TokenUsageSnapshot(summary, days, new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero));

        var result = TokenUsageRetention.Normalize(snapshot);

        Assert.Same(summary, result.Summary);
        Assert.Equal(snapshot.FetchedAt, result.FetchedAt);
        Assert.Equal(366, result.DailyUsageBuckets!.Count);
        Assert.Equal("2026-10-18", result.DailyUsageBuckets[0].StartDate);
        Assert.Equal(999, result.DailyUsageBuckets[0].Tokens);
        Assert.Equal(today.AddDays(-365).ToString("yyyy-MM-dd"), result.DailyUsageBuckets[^1].StartDate);
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("oversized")]
    public async Task Invalid_or_oversized_cache_is_rebuildable_and_preserves_original_file(string mode)
    {
        using var directory = new TestDirectory();
        var path = directory.File("cache.json");
        if (mode == "malformed") await File.WriteAllTextAsync(path, "{broken");
        else { using var stream = File.Create(path); stream.SetLength(8 * 1024 * 1024 + 1); }
        var length = new FileInfo(path).Length;

        var result = await new UsageCache(path, new AtomicFileStore()).LoadAsync(default);

        Assert.Empty(result);
        Assert.Equal(length, new FileInfo(path).Length);
    }

    [Fact]
    public void Bounded_file_read_rejects_oversized_input_before_reading_content()
    {
        using var directory = new TestDirectory();
        var path = directory.File("large.json");
        using (var stream = File.Create(path)) stream.SetLength(8 * 1024 * 1024 + 1);
        Assert.Throws<InvalidDataException>(() => new AtomicFileStore().ReadBounded(path, 8 * 1024 * 1024));
    }
}
