using CodexSwitch.Core.Presentation;

namespace CodexSwitch.Core.Tests.Presentation;

public sealed class DashboardLayoutPlanTests
{
    [Fact]
    public void Create_spreads_three_accounts_without_scrolling()
    {
        var plan = DashboardLayoutPlan.Create(3, 1920, 1040);

        Assert.Equal(2, plan.Columns);
        Assert.Equal(2, plan.Rows);
        Assert.True(plan.CardWidth >= 360);
        Assert.Equal(0, plan.VerticalScrollExtent);
    }

    [Fact]
    public void Create_uses_three_columns_for_six_accounts()
    {
        var plan = DashboardLayoutPlan.Create(6, 1920, 1040);

        Assert.Equal(3, plan.Columns);
        Assert.Equal(2, plan.Rows);
        Assert.True(plan.WindowWidth <= 1896);
        Assert.True(plan.WindowHeight <= 1016);
    }
}
