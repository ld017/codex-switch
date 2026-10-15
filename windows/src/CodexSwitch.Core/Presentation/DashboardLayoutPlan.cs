namespace CodexSwitch.Core.Presentation;

public sealed record DashboardLayoutPlan(
    int Columns,
    int Rows,
    int CardWidth,
    int AccountAreaHeight,
    int WindowWidth,
    int WindowHeight,
    int VerticalScrollExtent)
{
    private const int CardHeight = 238;
    private const int Gap = 12;
    private const int HorizontalChrome = 80;
    private const int VerticalChrome = 500;
    private const int DefaultWindowWidth = 420;
    private const int DefaultWindowHeight = 1040;
    private const int MaximumCardWidth = 360;

    public static DashboardLayoutPlan Create(int accountCount, int workingWidth, int workingHeight)
    {
        var safeCount = Math.Max(0, accountCount);
        // Account count only changes the scrollable content, never the window's
        // default dimensions or the number of columns.
        var windowWidth = Math.Min(DefaultWindowWidth, Math.Max(1, workingWidth - 24));
        var windowHeight = Math.Min(DefaultWindowHeight, Math.Max(1, workingHeight - 24));
        var cardWidth = Math.Min(MaximumCardWidth, Math.Max(1, windowWidth - HorizontalChrome));
        var contentHeight = safeCount == 0 ? 110L : (long)safeCount * (CardHeight + Gap);
        var accountHeight = Math.Max(1, windowHeight - VerticalChrome);
        var scrollExtent = (int)Math.Min(int.MaxValue, Math.Max(0, contentHeight - accountHeight));

        return new DashboardLayoutPlan(1, safeCount, cardWidth, accountHeight, windowWidth, windowHeight, scrollExtent);
    }
}
