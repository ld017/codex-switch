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
    private const int CardHeight = 154;
    private const int Gap = 8;
    private const int HorizontalChrome = 80;
    private const int VerticalChrome = 650;
    private const int MinimumCardWidth = 360;
    private const int MaximumCardWidth = 440;

    public static DashboardLayoutPlan Create(int accountCount, int workingWidth, int workingHeight)
    {
        var safeCount = Math.Max(0, accountCount);
        var usableWidth = Math.Max(MinimumCardWidth, workingWidth - HorizontalChrome);
        var maxColumns = Math.Max(1, usableWidth / (MinimumCardWidth + Gap));
        var maxRows = Math.Max(1, (workingHeight - VerticalChrome) / (CardHeight + Gap));
        var columns = safeCount == 0
            ? 1
            : Math.Min(maxColumns, Math.Max(1, (int)Math.Ceiling(safeCount / (double)maxRows)));
        var rows = safeCount == 0 ? 0 : (int)Math.Ceiling(safeCount / (double)columns);
        var cardWidth = Math.Clamp(
            (usableWidth - (Gap * Math.Max(0, columns - 1))) / columns,
            MinimumCardWidth,
            MaximumCardWidth);
        var accountHeight = rows == 0 ? 90 : (rows * CardHeight) + (Math.Max(0, rows - 1) * Gap);
        var windowWidth = Math.Min(workingWidth - 24, HorizontalChrome + (columns * cardWidth) + (Math.Max(0, columns - 1) * Gap));
        var windowHeight = Math.Min(workingHeight - 24, VerticalChrome + accountHeight);

        return new DashboardLayoutPlan(columns, rows, cardWidth, accountHeight, windowWidth, windowHeight, 0);
    }
}
