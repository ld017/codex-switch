using CodexSwitch.Core.Auth;
using CodexSwitch.Core.Presentation;
using CodexSwitch.Core.Usage;

namespace CodexSwitch.Core.Tests.Presentation;

public sealed class TokenUsageAggregateTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Build_counts_each_saved_identity_once_using_the_newest_token_activity()
    {
        var first = Profile("original", "old-fingerprint");
        var duplicate = Profile("same account after login", "new-fingerprint");
        var second = Profile("other account");
        AccountProfile[] accounts = [first, duplicate, second];
        var identities = Identities((first, OAuth("account-1")),
            (duplicate, OAuth("account-1", "user-1")), (second, OAuth("account-2")));
        var usage = States((first, Activity(100_000_000, Now.AddHours(-1), ("2026-10-01", 1_000_000))),
            (duplicate, Activity(200_000_000, Now, ("2026-10-01", 2_000_000))),
            (second, Activity(300_000_000, Now, ("2026-10-15", 4_000_000))));

        var result = TokenUsageAggregate.Build(accounts, usage, Now, identities, TimeZoneInfo.Utc);

        Assert.Equal(500_000_000, result.TotalLifetimeTokens);
        Assert.Equal(6_000_000, result.TotalMonthTokens);
        Assert.Equal(2, result.UniqueAccountCount);
        Assert.Equal(2, result.IncludedAccountCount);
        Assert.Equal(2, result.MonthIncludedAccountCount);
        Assert.Equal(2, result.CoverageAccountCount);
        Assert.Equal(0, result.ExcludedCount);
        Assert.Equal(new DateOnly(2026, 10, 1), result.MonthStartDate);
        Assert.Equal(new DateOnly(2026, 10, 15), result.MonthThroughDate);
    }

    [Fact]
    public void Build_excludes_unknown_and_email_only_identities_even_if_fingerprints_match()
    {
        var unknown = Profile("unknown", "same-fingerprint");
        var emailOnly = Profile("email only", "same-fingerprint");
        var known = Profile("known");
        AccountProfile[] accounts = [unknown, emailOnly, known];
        var identities = Identities((emailOnly, new AuthIdentity(AuthKind.OAuth, Email: "same@example.test")),
            (known, OAuth("account-1")));
        var usage = States((unknown, Activity(1_000, Now)), (emailOnly, Activity(2_000, Now)),
            (known, Activity(3_000, Now)));

        var result = TokenUsageAggregate.Build(accounts, usage, Now, identities, TimeZoneInfo.Utc);

        Assert.Equal(3_000, result.TotalLifetimeTokens);
        Assert.Equal(1, result.UniqueAccountCount);
        Assert.Equal(2, result.UnknownIdentityCount);
        Assert.Equal(3, result.CoverageAccountCount);
        Assert.Equal(2, result.ExcludedCount);
        Assert.Null(result.TotalMonthTokens);
        Assert.Equal(3, result.MonthExcludedCount);
    }

    [Fact]
    public void Build_does_not_merge_users_with_conflicting_stable_claims_or_use_email_to_split_the_same_user()
    {
        var first = Profile("one");
        var second = Profile("two");
        var duplicate = Profile("one with changed email");
        AccountProfile[] accounts = [first, second, duplicate];
        var identities = Identities(
            (first, new AuthIdentity(AuthKind.OAuth, "shared-workspace", "user-1", Email: "old@example.test")),
            (second, new AuthIdentity(AuthKind.OAuth, "shared-workspace", "user-2", Email: "old@example.test")),
            (duplicate, new AuthIdentity(AuthKind.OAuth, "shared-workspace", "user-1", Email: "new@example.test")));

        var result = TokenUsageAggregate.Build(accounts,
            States((first, Activity(10, Now)), (second, Activity(20, Now)), (duplicate, Activity(15, Now.AddMinutes(1)))),
            Now, identities, TimeZoneInfo.Utc);

        Assert.Equal(35, result.TotalLifetimeTokens);
        Assert.Equal(2, result.UniqueAccountCount);
    }

    [Fact]
    public void Build_reports_partial_coverage_and_cached_accounts_without_fabricating_missing_zeroes()
    {
        var cached = Profile("cached");
        var noTokens = Profile("quota only");
        var monthOnly = Profile("month only");
        AccountProfile[] accounts = [cached, noTokens, monthOnly];
        var identities = Identities((cached, OAuth("a")), (noTokens, OAuth("b")), (monthOnly, OAuth("c")));
        var usage = States((cached, Activity(50, Now.AddDays(-1), ("2026-10-14", 10))),
            (monthOnly, Activity(null, Now, ("2026-10-15", 20))));
        usage[cached.Id] = UsageState.Failed("quota unavailable", usage[cached.Id].Snapshot);

        var result = TokenUsageAggregate.Build(accounts, usage, Now, identities, TimeZoneInfo.Utc);

        Assert.Equal(50, result.TotalLifetimeTokens);
        Assert.Equal(30, result.TotalMonthTokens);
        Assert.Equal(1, result.IncludedAccountCount);
        Assert.Equal(2, result.MonthIncludedAccountCount);
        Assert.Equal(2, result.ExcludedCount);
        Assert.Equal(1, result.MonthExcludedCount);
        Assert.Equal(1, result.CachedCount);
    }

    [Fact]
    public void Build_marks_token_errors_with_retained_snapshots_as_cache_while_quota_succeeds()
    {
        var account = Profile("cached token only");
        var token = Activity(900, Now.AddDays(-1), ("2026-10-14", 7));
        var state = Snapshot(TokenUsageState.Failed("activity unavailable", token));

        var result = TokenUsageAggregate.Build([account], new Dictionary<Guid, UsageState> { [account.Id] = state },
            Now, Identities((account, OAuth("a"))), TimeZoneInfo.Utc);

        Assert.Equal(900, result.TotalLifetimeTokens);
        Assert.Equal(7, result.TotalMonthTokens);
        Assert.Equal(1, result.CachedCount);
    }

    [Fact]
    public void Build_deduplicates_api_key_identities_by_hash_and_rejects_empty_hashes()
    {
        var first = Profile("api one");
        var duplicate = Profile("api duplicate");
        var unknown = Profile("api empty");

        var result = TokenUsageAggregate.Build([first, duplicate, unknown],
            States((first, Activity(50, Now)), (duplicate, Activity(70, Now.AddMinutes(1))),
                (unknown, Activity(900, Now))), Now,
            Identities((first, new AuthIdentity(AuthKind.ApiKey, ApiKeyHash: "hash-a")),
                (duplicate, new AuthIdentity(AuthKind.ApiKey, ApiKeyHash: "hash-a")),
                (unknown, new AuthIdentity(AuthKind.ApiKey, ApiKeyHash: "  "))), TimeZoneInfo.Utc);

        Assert.Equal(70, result.TotalLifetimeTokens);
        Assert.Equal(1, result.UniqueAccountCount);
        Assert.Equal(1, result.UnknownIdentityCount);
    }

    [Fact]
    public void Build_excludes_sparse_identities_that_cannot_distinguish_two_users_in_the_same_workspace()
    {
        var sparse = Profile("workspace only");
        var first = Profile("user one");
        var second = Profile("user two");

        var result = TokenUsageAggregate.Build([sparse, first, second],
            States((sparse, Activity(1_000, Now)), (first, Activity(10, Now)), (second, Activity(20, Now))),
            Now, Identities((sparse, OAuth("workspace")), (first, OAuth("workspace", "user-1")),
                (second, OAuth("workspace", "user-2"))), TimeZoneInfo.Utc);

        Assert.Equal(30, result.TotalLifetimeTokens);
        Assert.Equal(2, result.UniqueAccountCount);
        Assert.Equal(1, result.UnknownIdentityCount);
        Assert.Equal(1, result.ExcludedCount);
    }

    [Fact]
    public void Build_preserves_unknown_totals_when_no_valid_activity_is_available()
    {
        var account = Profile("negative corrupt snapshot");
        var result = TokenUsageAggregate.Build([account], States((account, Activity(-1, Now))),
            Now, Identities((account, OAuth("account"))), TimeZoneInfo.Utc);

        Assert.Null(result.TotalLifetimeTokens);
        Assert.Null(result.TotalMonthTokens);
        Assert.Equal(0, result.IncludedAccountCount);
        Assert.Equal(1, result.ExcludedCount);
        Assert.Equal(0, result.CachedCount);
        Assert.False(result.HasOverflow);
    }

    [Fact]
    public void Build_uses_the_latest_activity_instead_of_combining_old_lifetime_and_new_month_records()
    {
        var first = Profile("old");
        var latest = Profile("new");
        var result = TokenUsageAggregate.Build([first, latest],
            States((first, Activity(100, Now.AddHours(-1), ("2026-10-14", 50))),
                (latest, Activity(null, Now, ("2026-10-15", 10)))), Now,
            Identities((first, OAuth("a")), (latest, OAuth("a"))), TimeZoneInfo.Utc);

        Assert.Null(result.TotalLifetimeTokens);
        Assert.Equal(10, result.TotalMonthTokens);
        Assert.Equal(0, result.IncludedAccountCount);
        Assert.Equal(1, result.ExcludedCount);
    }

    [Fact]
    public void MonthUsage_uses_local_calendar_dates_and_counts_duplicate_dates_only_once()
    {
        var localZone = TimeZoneInfo.CreateCustomTimeZone("test UTC+8", TimeSpan.FromHours(8), "test UTC+8", "test UTC+8");
        var firstOfMonthUtc = new DateTimeOffset(2026, 9, 30, 16, 5, 0, TimeSpan.Zero);
        var activity = Activity(null, Now,
            ("2026-10-01", 10), ("2026-10-01", 15), ("2026-10-02", 99), ("2026-09-30", 100),
            ("2026-10-00", 100), ("2026-10-32", 100), ("2026-10-1", 100), ("2026-10-01T00:00:00Z", 100),
            ("2026-10-01", -100));

        var month = TokenUsageAggregate.MonthUsage(activity, firstOfMonthUtc, localZone);

        Assert.NotNull(month);
        Assert.Equal(15, month.Tokens);
        Assert.Equal(new DateOnly(2026, 10, 1), month.StartDate);
        Assert.Equal(new DateOnly(2026, 10, 1), month.ThroughDate);
    }

    [Fact]
    public void MonthUsage_keeps_missing_month_records_distinct_from_explicit_zero_usage()
    {
        Assert.Null(TokenUsageAggregate.MonthUsage(Activity(20, Now, ("2026-09-30", 20)), Now, TimeZoneInfo.Utc));
        var zero = TokenUsageAggregate.MonthUsage(Activity(20, Now, ("2026-10-01", 0)), Now, TimeZoneInfo.Utc);
        Assert.Equal(0, zero?.Tokens);
    }

    [Fact]
    public void MonthUsage_rejects_overflow_instead_of_wrapping_to_a_negative_value()
    {
        var activity = Activity(null, Now, ("2026-10-01", long.MaxValue), ("2026-10-02", 1));

        Assert.Null(TokenUsageAggregate.MonthUsage(activity, Now, TimeZoneInfo.Utc));
    }

    [Fact]
    public void Build_rejects_overflow_in_aggregated_lifetime_and_month_values()
    {
        var first = Profile("max");
        var second = Profile("one more");
        var result = TokenUsageAggregate.Build([first, second],
            States((first, Activity(long.MaxValue, Now, ("2026-10-01", long.MaxValue))),
                (second, Activity(1, Now, ("2026-10-01", 1)))), Now,
            Identities((first, OAuth("a")), (second, OAuth("b"))), TimeZoneInfo.Utc);

        Assert.Null(result.TotalLifetimeTokens);
        Assert.Null(result.TotalMonthTokens);
        Assert.True(result.HasOverflow);
    }

    [Theory]
    [InlineData(null, "—")]
    [InlineData(-1L, "—")]
    [InlineData(0L, "0.00 亿")]
    [InlineData(1L, "<0.01 亿")]
    [InlineData(499_999L, "<0.01 亿")]
    [InlineData(500_000L, "0.01 亿")]
    [InlineData(1_000_000L, "0.01 亿")]
    [InlineData(123_456_789L, "1.23 亿")]
    public void FormatHundredMillions_uses_consistent_units_and_preserves_small_nonzero_values(long? value, string expected)
        => Assert.Equal(expected, TokenUsageAggregate.FormatHundredMillions(value));

    private static AccountProfile Profile(string label, string fingerprint = "unused") => new(Guid.NewGuid(), label, fingerprint);
    private static AuthIdentity OAuth(string accountId, string? subject = null) => new(AuthKind.OAuth, accountId, subject);
    private static Dictionary<Guid, AuthIdentity> Identities(params (AccountProfile Account, AuthIdentity Identity)[] values)
        => values.ToDictionary(value => value.Account.Id, value => value.Identity);
    private static Dictionary<Guid, UsageState> States(params (AccountProfile Account, TokenUsageSnapshot Token)[] values)
        => values.ToDictionary(value => value.Account.Id, value => Snapshot(TokenUsageState.Available(value.Token)));
    private static UsageState Snapshot(TokenUsageState tokens) => UsageState.Available(new UsageSnapshot(
        "plus", null, 10, null, 300, 20, null, 10_080, null, null, Now, TokenUsage: tokens));
    private static TokenUsageSnapshot Activity(long? lifetime, DateTimeOffset fetchedAt,
        params (string Date, long Tokens)[] days) => new(new TokenUsageSummary(lifetime),
        days.Select(day => new TokenUsageDailyBucket(day.Date, day.Tokens)).ToArray(), fetchedAt);
}
