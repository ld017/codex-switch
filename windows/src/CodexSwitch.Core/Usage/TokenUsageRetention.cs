using System.Globalization;

namespace CodexSwitch.Core.Usage;

public static class TokenUsageRetention
{
    public const int MaximumDailyBuckets = 366;

    public static TokenUsageSnapshot Normalize(TokenUsageSnapshot snapshot)
    {
        if (snapshot.DailyUsageBuckets is null) return snapshot;
        var today = DateOnly.FromDateTime(snapshot.FetchedAt.LocalDateTime);
        var days = new SortedDictionary<DateOnly, long>();
        foreach (var day in snapshot.DailyUsageBuckets)
        {
            if (day is null || day.Tokens < 0 || day.StartDate?.Length != 10
                || !DateOnly.TryParseExact(day.StartDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                || date > today) continue;
            days[date] = Math.Max(days.GetValueOrDefault(date), day.Tokens);
            if (days.Count > MaximumDailyBuckets) days.Remove(days.First().Key);
        }
        var retained = days.Reverse()
            .Select(day => new TokenUsageDailyBucket(day.Key.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), day.Value)).ToArray();
        return snapshot.DailyUsageBuckets.SequenceEqual(retained) ? snapshot : snapshot with { DailyUsageBuckets = retained };
    }

    public static UsageState Normalize(UsageState state)
        => state.Snapshot?.TokenUsage?.Snapshot is { } tokens
            ? state with { Snapshot = state.Snapshot with { TokenUsage = state.Snapshot.TokenUsage with { Snapshot = Normalize(tokens) } } }
            : state;
}
