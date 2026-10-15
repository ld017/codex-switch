using System.Drawing;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows.Forms;
using CodexSwitch.App.Controls;
using CodexSwitch.Core.Presentation;

namespace CodexSwitch.IntegrationTests.Presentation;

public sealed class PaintStabilityTests
{
    [Fact]
    public void Rebinding_equal_values_and_reassigning_visual_properties_do_not_invalidate_controls()
        => OnSta(() =>
        {
            using var card = new AccountUsageCard { Width = 440 };
            using var button = new DashboardButton { Tone = DashboardButtonTone.Primary };
            var id = Guid.NewGuid();
            var presentation = Presentation();
            card.Bind(id, "合成账号", presentation, "刷新完成", true);
            card.Selected = true;
            CreateHandles(card);
            CreateHandles(button);
            DrainMessages();

            var invalidations = 0;
            foreach (var control in Descendants(card).Append(button))
                control.Invalidated += (_, _) => invalidations++;
            var primary = Field<UsageBarControl>(card, "_primaryBar");
            var secondary = Field<UsageBarControl>(card, "_secondaryBar");

            for (var repeat = 0; repeat < 5; repeat++)
            {
                // A fresh record instance must compare by its presentation values.
                card.Bind(id, "合成账号", presentation with { Primary = presentation.Primary with { } }, "刷新完成", true);
                card.Selected = true;
                primary.Percent = 72;
                primary.Level = UsageLevel.Healthy;
                secondary.Percent = 18;
                secondary.Level = UsageLevel.Warning;
                button.Tone = DashboardButtonTone.Primary;
            }

            DrainMessages();
            Assert.Equal(0, invalidations);
        });

    [Fact]
    public void Painting_a_changed_selected_border_does_not_enqueue_further_invalidations()
        => OnSta(() =>
        {
            using var card = new AccountUsageCard { Width = 440 };
            card.Bind(Guid.NewGuid(), "合成账号", Presentation(), "刷新完成", false);
            CreateHandles(card);
            DrainMessages();

            var invalidations = 0;
            card.Invalidated += (_, _) => invalidations++;
            card.Selected = true;
            // The selection change may invalidate once. Painting that state must
            // consume the update without changing properties and scheduling more.
            invalidations = 0;
            using var bitmap = new Bitmap(card.Width, card.Height);
            using (var graphics = Graphics.FromImage(bitmap))
            using (var paint = new PaintEventArgs(graphics, card.ClientRectangle))
            {
                var onPaint = typeof(AccountUsageCard).GetMethod("OnPaint", BindingFlags.Instance | BindingFlags.NonPublic)!;
                onPaint.Invoke(card, [paint]);
            }
            card.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
            DrainMessages();

            Assert.Equal(0, invalidations);
        });

    private static AccountUsagePresentation Presentation() => new(
        "plus", "0 cr", new(72, "72%", "1时30分", UsageLevel.Healthy),
        new(18, "18%", "3天4时", UsageLevel.Warning), "重置卡 2 张", "最近到期 10月23日 03:57", "刷新完成")
    {
        TokenUsage = new("1.23 亿", "0.12 亿", "Token 更新完成", "合成 Token 详情", false),
    };

    private static IEnumerable<Control> Descendants(Control parent)
    {
        yield return parent;
        foreach (Control child in parent.Controls)
            foreach (var descendant in Descendants(child)) yield return descendant;
    }

    private static void CreateHandles(Control control)
    {
        foreach (var descendant in Descendants(control)) _ = descendant.Handle;
        control.PerformLayout();
    }

    private static void DrainMessages()
    {
        for (var pass = 0; pass < 3; pass++) Application.DoEvents();
    }

    private static T Field<T>(object instance, string name) =>
        (T)instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;

    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception error) { failure = error; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "Synthetic painting checks did not finish within 20 seconds.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
