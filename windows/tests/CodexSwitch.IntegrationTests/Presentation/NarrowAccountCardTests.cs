using System.Drawing;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows.Forms;
using CodexSwitch.App.Controls;
using CodexSwitch.Core.Presentation;

namespace CodexSwitch.IntegrationTests.Presentation;

public sealed class NarrowAccountCardTests
{
    [Theory]
    [InlineData(320, 96)]
    [InlineData(356, 96)]
    [InlineData(372, 96)]
    [InlineData(320, 144)]
    [InlineData(356, 144)]
    [InlineData(372, 144)]
    [InlineData(320, 192)]
    [InlineData(356, 192)]
    [InlineData(372, 192)]
    public void Compact_card_keeps_token_captions_values_and_status_in_separate_readable_rows(int logicalWidth, int dpi)
        => OnSta(() =>
        {
            using var card = Card(logicalWidth, dpi);
            var lifetime = Field<Label>(card, "_lifetimeTokens");
            var month = Field<Label>(card, "_monthTokens");
            var status = Field<Label>(card, "_tokenStatus");
            var table = Assert.IsAssignableFrom<TableLayoutPanel>(lifetime.Parent);
            var lifetimeCaption = Assert.IsAssignableFrom<Label>(table.GetControlFromPosition(0, 0));
            var monthCaption = Assert.IsAssignableFrom<Label>(table.GetControlFromPosition(1, 0));

            Assert.Equal((int)Math.Round(logicalWidth * dpi / 96d), card.Width);
            foreach (var label in new[] { lifetimeCaption, monthCaption, lifetime, month, status })
            {
                var measured = TextRenderer.MeasureText(label.Text, label.Font, Size.Empty, TextFormatFlags.SingleLine);
                Assert.True(measured.Height <= label.ClientSize.Height,
                    $"At {dpi} DPI, '{label.Text}' needs {measured.Height}px but has {label.ClientSize.Height}px of height.");
                Assert.True(measured.Width <= label.ClientSize.Width,
                    $"At {dpi} DPI, '{label.Text}' needs {measured.Width}px but has {label.ClientSize.Width}px of width.");
                var bounds = BoundsInCard(card, label);
                Assert.True(card.ClientRectangle.Contains(bounds), $"Label outside compact card: {bounds}; card {card.ClientRectangle}");
            }
            Assert.True(lifetimeCaption.Bottom <= lifetime.Top);
            Assert.True(monthCaption.Bottom <= month.Top);
            Assert.True(lifetime.Bottom <= status.Top);
            Assert.True(month.Bottom <= status.Top);
            Assert.True(lifetime.Right <= month.Left);
            Assert.True(status.Bottom <= table.ClientSize.Height);

            using var bitmap = new Bitmap(card.Width, card.Height);
            card.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
        });

    [Theory]
    [InlineData(96)]
    [InlineData(144)]
    [InlineData(192)]
    public void Very_large_token_values_retain_exact_tooltips_without_displacing_adjacent_rows(int dpi)
        => OnSta(() =>
        {
            using var card = Card(320, dpi, huge: true);
            var lifetime = Field<Label>(card, "_lifetimeTokens");
            var month = Field<Label>(card, "_monthTokens");
            var status = Field<Label>(card, "_tokenStatus");
            var tooltips = Field<ToolTip>(card, "_details");

            Assert.Contains("9,223,372,036,854,775,807", tooltips.GetToolTip(lifetime));
            Assert.Contains("9,223,372,036,854,775,807", tooltips.GetToolTip(month));
            Assert.True(lifetime.AutoEllipsis);
            Assert.True(month.AutoEllipsis);
            Assert.True(lifetime.Bottom <= status.Top);
            Assert.True(month.Bottom <= status.Top);
            var measuredHeight = TextRenderer.MeasureText(lifetime.Text, lifetime.Font, Size.Empty,
                TextFormatFlags.SingleLine).Height;
            Assert.True(measuredHeight <= lifetime.ClientSize.Height,
                $"Large value has insufficient single-line height: {measuredHeight} > {lifetime.ClientSize.Height}");

            using var bitmap = new Bitmap(card.Width, card.Height);
            card.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
        });

    private static AccountUsageCard Card(int logicalWidth, int dpi, bool huge = false)
    {
        var card = new AccountUsageCard();
        // Simulate physical glyph sizes as well as geometry. Control.Scale only scales
        // bounds; otherwise a 192-DPI regression could pass while drawing 96-DPI text.
        var originalFonts = Descendants(card).ToDictionary(control => control, control => control.Font);
        var sourceDpi = card.DeviceDpi;
        var scale = dpi / 96f;
        card.Scale(new SizeF(scale, scale));
        foreach (var (control, font) in originalFonts)
            control.Font = new Font(font.FontFamily, font.Size * dpi / sourceDpi, font.Style, GraphicsUnit.Point);
        card.Width = (int)Math.Round(logicalWidth * scale);
        var presentation = new AccountUsagePresentation("plus", "0 cr",
            new(100, "100%", "23时59分", UsageLevel.Healthy),
            new(100, "100%", "6天23时", UsageLevel.Healthy),
            "重置卡 3 张", "最近到期 10月23日 03:57", "刷新完成")
        {
            TokenUsage = new(huge ? "92233720368.55 亿" : "1234.56 亿", huge ? "92233720368.55 亿" : "56.78 亿",
                "Token 更新于 10/15 23:59",
                "累计 Token：9,223,372,036,854,775,807；本月 Token：9,223,372,036,854,775,807", false),
        };
        card.Bind(Guid.NewGuid(), "合成测试账号", presentation, "更新于 10/15 23:59", true);
        foreach (var control in Descendants(card)) _ = control.Handle;
        foreach (var control in Descendants(card)) control.PerformLayout();
        return card;
    }

    private static Rectangle BoundsInCard(Control card, Control child)
    {
        var bounds = child.Bounds;
        for (var parent = child.Parent; parent != card; parent = parent!.Parent)
            bounds.Offset(parent!.Location);
        return bounds;
    }

    private static IEnumerable<Control> Descendants(Control parent)
    {
        yield return parent;
        foreach (Control child in parent.Controls)
            foreach (var descendant in Descendants(child)) yield return descendant;
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
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "Compact account card did not render within 20 seconds.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
