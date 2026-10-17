namespace CodexSwitch.Core.Presentation;

public static class RefreshPolicy
{
    public static readonly TimeSpan VisibleInterval = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan MinimizedInterval = TimeSpan.FromSeconds(300);
    public static readonly TimeSpan MaintenanceInterval = TimeSpan.FromHours(6);

    public static TimeSpan AutomaticInterval(bool minimized) => minimized ? MinimizedInterval : VisibleInterval;

    public static bool ShouldRefreshAfterRestore(DateTimeOffset? lastAttemptAt, DateTimeOffset now)
        => lastAttemptAt is null || now - lastAttemptAt.Value >= VisibleInterval;
}
