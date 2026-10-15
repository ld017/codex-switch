using System.Globalization;
using CodexSwitch.Core.Auth;
using CodexSwitch.Core.Usage;

namespace CodexSwitch.Core.Presentation;

public sealed record TokenMonthUsage(long Tokens, DateOnly StartDate, DateOnly ThroughDate);

public sealed record TokenUsageAggregate(
    long? TotalLifetimeTokens,
    long? TotalMonthTokens,
    int IncludedAccountCount,
    int MonthIncludedAccountCount,
    int UniqueAccountCount,
    int UnknownIdentityCount,
    int CachedCount,
    DateOnly MonthStartDate,
    DateOnly? MonthThroughDate,
    bool LifetimeOverflow,
    bool MonthOverflow)
{
    public int CoverageAccountCount => UniqueAccountCount + UnknownIdentityCount;
    public int ExcludedCount => CoverageAccountCount - IncludedAccountCount;
    public int MonthExcludedCount => CoverageAccountCount - MonthIncludedAccountCount;
    public bool HasOverflow => LifetimeOverflow || MonthOverflow;

    public static TokenUsageAggregate Build(IReadOnlyList<AccountProfile> accounts,
        IReadOnlyDictionary<Guid, UsageState> usage, DateTimeOffset now,
        IReadOnlyDictionary<Guid, AuthIdentity> identities, TimeZoneInfo? timeZone = null)
    {
        var groups = new List<List<Entry>>();
        var unknown = 0;
        var entries = new List<Entry>();
        foreach (var account in accounts.DistinctBy(account => account.Id))
        {
            if (!identities.TryGetValue(account.Id, out var identity) || StableIdentity(identity) is not { } stable)
            {
                unknown++;
                continue;
            }

            usage.TryGetValue(account.Id, out var state);
            entries.Add(new Entry(stable, state));
        }

        // Evaluate complete identities first. A profile with only a workspace account ID
        // cannot resolve two known users in that workspace and is excluded if ambiguous.
        foreach (var entry in entries.OrderByDescending(entry => ClaimCount(entry.Identity)))
        {
            var matches = groups.Where(group => group.All(existing => existing.Identity.Matches(entry.Identity))).ToArray();
            if (matches.Length == 1) matches[0].Add(entry);
            else if (matches.Length == 0) groups.Add([entry]);
            else unknown++;
        }

        long lifetime = 0, monthly = 0;
        var lifetimeCount = 0;
        var monthCount = 0;
        var cachedCount = 0;
        var lifetimeOverflow = false;
        var monthOverflow = false;
        DateOnly? monthThrough = null;
        foreach (var group in groups)
        {
            // A duplicate profile never contributes a second record. Keep the latest
            // activity as a unit so old lifetime and new monthly data are not combined.
            var newest = group.Where(entry => entry.State?.Snapshot?.TokenUsage?.Snapshot is not null)
                .OrderByDescending(entry => entry.State!.Snapshot!.TokenUsage!.Snapshot!.FetchedAt)
                .ThenBy(entry => IsCached(entry.State!))
                .FirstOrDefault();
            if (newest?.State?.Snapshot?.TokenUsage?.Snapshot is not { } activity) continue;

            var included = false;
            if (activity.Summary?.LifetimeTokens is >= 0 and { } value)
            {
                lifetimeCount++;
                included = true;
                try { lifetime = checked(lifetime + value); }
                catch (OverflowException) { lifetimeOverflow = true; }
            }

            var month = MonthUsageCore(activity, now, timeZone, out var accountMonthOverflow);
            monthOverflow |= accountMonthOverflow;
            if (month is not null)
            {
                monthCount++;
                included = true;
                if (monthThrough is null || month.ThroughDate > monthThrough) monthThrough = month.ThroughDate;
                try { monthly = checked(monthly + month.Tokens); }
                catch (OverflowException) { monthOverflow = true; }
            }

            if (included && IsCached(newest.State)) cachedCount++;
        }

        var today = LocalToday(now, timeZone);
        return new TokenUsageAggregate(
            lifetimeCount > 0 && !lifetimeOverflow ? lifetime : null,
            monthCount > 0 && !monthOverflow ? monthly : null,
            lifetimeCount, monthCount, groups.Count, unknown, cachedCount,
            new DateOnly(today.Year, today.Month, 1), monthThrough, lifetimeOverflow, monthOverflow);
    }

    public static TokenMonthUsage? MonthUsage(TokenUsageSnapshot? snapshot, DateTimeOffset now,
        TimeZoneInfo? timeZone = null) => MonthUsageCore(snapshot, now, timeZone, out _);

    public static string FormatHundredMillions(long? value)
    {
        if (value is null or < 0) return "—";
        if (value is > 0 and < 500_000) return "<0.01 亿";
        return decimal.Round(value.Value / 100_000_000m, 2, MidpointRounding.AwayFromZero)
            .ToString("0.00", CultureInfo.InvariantCulture) + " 亿";
    }

    private static TokenMonthUsage? MonthUsageCore(TokenUsageSnapshot? snapshot, DateTimeOffset now,
        TimeZoneInfo? timeZone, out bool overflow)
    {
        overflow = false;
        var today = LocalToday(now, timeZone);
        var byDate = new Dictionary<DateOnly, long>();
        foreach (var day in snapshot?.DailyUsageBuckets ?? [])
        {
            // Service bucket dates are calendar labels, not UTC timestamps.
            if (day.Tokens < 0 || day.StartDate?.Length != 10
                || !DateOnly.TryParseExact(day.StartDate, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var date)
                || date.Year != today.Year || date.Month != today.Month || date > today) continue;
            byDate[date] = Math.Max(byDate.GetValueOrDefault(date), day.Tokens);
        }

        if (byDate.Count == 0) return null;
        long total = 0;
        try
        {
            foreach (var value in byDate.Values) total = checked(total + value);
        }
        catch (OverflowException)
        {
            overflow = true;
            return null;
        }

        return new TokenMonthUsage(total, new DateOnly(today.Year, today.Month, 1), byDate.Keys.Max());
    }

    private static DateOnly LocalToday(DateTimeOffset now, TimeZoneInfo? timeZone)
        => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, timeZone ?? TimeZoneInfo.Local).DateTime);

    private static bool IsCached(UsageState state)
        => state.Status != UsageStatus.Available || state.Snapshot?.TokenUsage?.Status != TokenUsageStatus.Available;

    private static AuthIdentity? StableIdentity(AuthIdentity identity)
    {
        static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        if (identity.Kind == AuthKind.ApiKey)
        {
            var keyHash = NonEmpty(identity.ApiKeyHash);
            return keyHash is null ? null : new AuthIdentity(AuthKind.ApiKey, ApiKeyHash: keyHash);
        }

        var stable = new AuthIdentity(AuthKind.OAuth, NonEmpty(identity.AccountId),
            NonEmpty(identity.Subject), NonEmpty(identity.UserId));
        return ClaimCount(stable) == 0 ? null : stable;
    }

    private static int ClaimCount(AuthIdentity identity)
        => (identity.AccountId is null ? 0 : 1) + (identity.Subject is null ? 0 : 1)
           + (identity.UserId is null ? 0 : 1) + (identity.ApiKeyHash is null ? 0 : 1);

    private sealed record Entry(AuthIdentity Identity, UsageState? State);
}
