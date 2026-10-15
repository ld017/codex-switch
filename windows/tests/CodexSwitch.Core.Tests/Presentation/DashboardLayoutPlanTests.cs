using CodexSwitch.Core.Presentation;

namespace CodexSwitch.Core.Tests.Presentation;

public sealed class DashboardLayoutPlanTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(12)]
    public void Create_keeps_default_portrait_size_and_one_column_regardless_of_account_count(int accountCount)
    {
        var plan = DashboardLayoutPlan.Create(accountCount, 1920, 1400);

        Assert.Equal(1, plan.Columns);
        Assert.Equal(accountCount, plan.Rows);
        Assert.Equal(420, plan.WindowWidth);
        Assert.Equal(1040, plan.WindowHeight);
        Assert.InRange(plan.CardWidth, 320, plan.WindowWidth);
        Assert.Equal(540, plan.AccountAreaHeight);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(12)]
    public void Create_scrolls_all_accounts_without_expanding_window(int accountCount)
    {
        var plan = DashboardLayoutPlan.Create(accountCount, 1920, 1400);

        // Cards include their 12-pixel bottom margin in the scrolling flow.
        Assert.Equal(accountCount * (238 + 12) - plan.AccountAreaHeight, plan.VerticalScrollExtent);
        Assert.Equal(420, plan.WindowWidth);
        Assert.Equal(1040, plan.WindowHeight);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Create_does_not_scroll_when_content_fits(int accountCount)
    {
        var plan = DashboardLayoutPlan.Create(accountCount, 1920, 1400);

        Assert.Equal(0, plan.VerticalScrollExtent);
    }

    [Theory]
    [InlineData(1366, 768)]
    [InlineData(1280, 720)]
    [InlineData(800, 600)]
    [InlineData(640, 480)]
    [InlineData(480, 640)]
    [InlineData(320, 480)]
    public void Create_keeps_small_screens_within_work_area_and_scrolls_cards(int width, int height)
    {
        var plan = DashboardLayoutPlan.Create(4, width, height);
        Assert.True(plan.WindowWidth <= width - 24);
        Assert.True(plan.WindowHeight <= height - 24);
        Assert.Equal(1, plan.Columns);
        Assert.Equal(4, plan.Rows);
        Assert.True(plan.VerticalScrollExtent > 0);
        Assert.True(plan.CardWidth > 0);
        Assert.True(plan.CardWidth + 80 <= plan.WindowWidth);
        Assert.True(plan.AccountAreaHeight > 0);
        Assert.True(plan.AccountAreaHeight <= plan.WindowHeight);
        Assert.Equal(4 * (238 + 12) - plan.AccountAreaHeight, plan.VerticalScrollExtent);
    }

    [Fact]
    public void Create_treats_negative_account_count_as_empty()
    {
        var plan = DashboardLayoutPlan.Create(-1, 1920, 1400);

        Assert.Equal(1, plan.Columns);
        Assert.Equal(0, plan.Rows);
        Assert.Equal(0, plan.VerticalScrollExtent);
    }

    [Fact]
    public void Create_does_not_overflow_scroll_extent_for_large_account_counts()
    {
        var plan = DashboardLayoutPlan.Create(int.MaxValue, 1920, 1400);

        Assert.Equal(int.MaxValue, plan.Rows);
        Assert.Equal(int.MaxValue, plan.VerticalScrollExtent);
    }
}
