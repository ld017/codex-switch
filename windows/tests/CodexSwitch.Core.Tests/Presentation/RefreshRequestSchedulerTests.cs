using CodexSwitch.Core.Presentation;

namespace CodexSwitch.Core.Tests.Presentation;

public sealed class RefreshRequestSchedulerTests
{
    [Fact]
    public void Automatic_requests_merge_into_the_running_round_without_a_follow_up()
    {
        var scheduler = new RefreshRequestScheduler();
        Assert.True(scheduler.TryStart(false));
        for (var index = 0; index < 50; index++) Assert.False(scheduler.TryStart(false));

        Assert.False(scheduler.CompleteRound());
        Assert.False(scheduler.IsRunning);
        Assert.True(scheduler.TryStart(false));
    }

    [Fact]
    public void Manual_requests_keep_at_most_one_forced_follow_up_round()
    {
        var scheduler = new RefreshRequestScheduler();
        Assert.True(scheduler.TryStart(false));
        for (var index = 0; index < 50; index++) Assert.False(scheduler.TryStart(true));
        Assert.True(scheduler.HasPendingForcedRequest);

        Assert.True(scheduler.CompleteRound());
        Assert.True(scheduler.IsRunning);
        Assert.False(scheduler.HasPendingForcedRequest);
        Assert.False(scheduler.CompleteRound());
        Assert.False(scheduler.IsRunning);
    }

    [Fact]
    public void Blocking_clears_pending_work_and_waits_for_the_active_round_to_finish()
    {
        var scheduler = new RefreshRequestScheduler();
        scheduler.TryStart(false);
        scheduler.TryStart(true);
        scheduler.SetBlocked(true);

        Assert.True(scheduler.IsRunning);
        Assert.False(scheduler.HasPendingForcedRequest);
        Assert.False(scheduler.TryStart(true));
        Assert.False(scheduler.CompleteRound());
        scheduler.SetBlocked(false);
        Assert.True(scheduler.TryStart(true));
    }

    [Fact]
    public void Closing_permanently_rejects_requests_and_discards_pending_work()
    {
        var scheduler = new RefreshRequestScheduler();
        scheduler.TryStart(false);
        scheduler.TryStart(true);
        scheduler.Close();
        scheduler.SetBlocked(false);

        Assert.False(scheduler.CompleteRound());
        Assert.False(scheduler.HasPendingForcedRequest);
        Assert.False(scheduler.TryStart(true));
    }

    [Fact]
    public void Aborting_a_failed_drain_allows_a_later_request_without_retaining_pending_work()
    {
        var scheduler = new RefreshRequestScheduler();
        scheduler.TryStart(false);
        scheduler.TryStart(true);
        scheduler.Abandon();

        Assert.False(scheduler.IsRunning);
        Assert.False(scheduler.HasPendingForcedRequest);
        Assert.True(scheduler.TryStart(false));
        Assert.False(scheduler.CompleteRound());
    }

    [Theory]
    [InlineData(false, 60)]
    [InlineData(true, 300)]
    public void Automatic_interval_depends_on_window_visibility(bool minimized, int seconds)
        => Assert.Equal(TimeSpan.FromSeconds(seconds), RefreshPolicy.AutomaticInterval(minimized));

    [Theory]
    [InlineData(-10, false)]
    [InlineData(59, false)]
    [InlineData(60, true)]
    [InlineData(301, true)]
    public void Restoring_only_refreshes_after_the_visible_interval(int ageSeconds, bool expected)
    {
        var now = new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(expected, RefreshPolicy.ShouldRefreshAfterRestore(now.AddSeconds(-ageSeconds), now));
    }

    [Fact]
    public void Restoring_without_a_previous_attempt_requests_an_initial_refresh()
        => Assert.True(RefreshPolicy.ShouldRefreshAfterRestore(null, DateTimeOffset.UtcNow));
}
