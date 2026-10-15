using System.Drawing;
using System.Runtime.ExceptionServices;
using System.Windows.Forms;
using CodexSwitch.App.Controls;

namespace CodexSwitch.IntegrationTests.Presentation;

public sealed class TokenOverviewLayoutTests
{
    [Theory]
    [InlineData(96, 300)]
    [InlineData(96, 360)]
    [InlineData(96, 420)]
    [InlineData(144, 300)]
    [InlineData(144, 360)]
    [InlineData(144, 420)]
    [InlineData(192, 300)]
    [InlineData(192, 360)]
    [InlineData(192, 420)]
    public void Large_and_small_values_and_wrapped_coverage_fit_at_scaled_font_sizes(int dpi, int logicalWidth)
        => OnSta(() =>
        {
            var scale = dpi / 96f;
            using var host = new Form
            {
                AutoScaleMode = AutoScaleMode.None,
                ClientSize = new Size((int)Math.Round(logicalWidth * scale), (int)Math.Round(700 * scale)),
            };
            using var overview = new TokenOverviewControl();
            // Increase the actual font sizes as well as the window geometry.
            // Merely scaling control bounds cannot detect a clipped glyph row.
            foreach (Control label in overview.Controls)
                label.Font = new Font(label.Font.FontFamily, label.Font.SizeInPoints * scale, label.Font.Style);
            overview.Padding = new Padding((int)Math.Round(14 * scale), (int)Math.Round(10 * scale),
                (int)Math.Round(14 * scale), (int)Math.Round(10 * scale));
            host.Controls.Add(overview);
            overview.Bind("123456.78", "<0.01",
                "按账号身份去重 · 累计 4/4 · 本月 4/4 · 服务统计可能延迟 · 2 项缓存");
            CreateHandles(host);
            host.PerformLayout();
            overview.PerformLayout();

            AssertTextFits(overview.TotalLabel, singleLine: true);
            AssertTextFits(overview.MonthLabel, singleLine: true);
            AssertTextFits(overview.TotalCaptionLabel);
            AssertTextFits(overview.MonthCaptionLabel);
            AssertTextFits(overview.CoverageLabel);
            var labels = overview.Controls.Cast<Control>().ToArray();
            foreach (var label in labels)
                Assert.True(overview.ClientRectangle.Contains(label.Bounds), $"{dpi} DPI / {logicalWidth}: {label.Text} leaves summary bounds: {label.Bounds} / {overview.ClientRectangle}");
            for (var left = 0; left < labels.Length; left++)
                for (var right = left + 1; right < labels.Length; right++)
                    Assert.False(labels[left].Bounds.IntersectsWith(labels[right].Bounds),
                        $"{dpi} DPI / {logicalWidth}: {labels[left].Text} overlaps {labels[right].Text}");
            Assert.Equal(overview.Height, overview.GetPreferredSize(new Size(overview.Width, 0)).Height);

            // The control also paints with the real enlarged fonts in these
            // bounds, rather than validating table row declarations alone.
            using var bitmap = new Bitmap(overview.Width, overview.Height);
            overview.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
        });

    [Fact]
    public void Narrow_width_stacks_large_values_and_grows_before_the_coverage_row()
        => OnSta(() =>
        {
            using var overview = new TokenOverviewControl { Dock = DockStyle.None, Width = 300 };
            overview.Bind("123456789012.78", "123456789012.34", "已加载 4 个账号，服务统计可能延迟");
            CreateHandles(overview);
            overview.PerformLayout();

            Assert.True(overview.MonthCaptionLabel.Top > overview.TotalLabel.Bottom);
            Assert.True(overview.CoverageLabel.Top > overview.MonthLabel.Bottom);
            AssertTextFits(overview.TotalLabel, singleLine: true);
            AssertTextFits(overview.MonthLabel, singleLine: true);
            Assert.True(overview.CoverageLabel.Bottom <= overview.ClientSize.Height - overview.Padding.Bottom);
        });

    [Fact]
    public void Unavailable_values_show_dash_and_rebinding_equal_data_does_not_invalidate()
        => OnSta(() =>
        {
            using var overview = new TokenOverviewControl { Width = 360 };
            overview.Bind("", " ", "正在获取服务统计");
            CreateHandles(overview);
            for (var pass = 0; pass < 3; pass++) Application.DoEvents();
            var invalidations = 0;
            foreach (var control in overview.Controls.Cast<Control>().Prepend(overview))
                control.Invalidated += (_, _) => invalidations++;

            overview.Bind("", " ", "正在获取服务统计");
            for (var pass = 0; pass < 3; pass++) Application.DoEvents();

            Assert.Equal("—", overview.TotalLabel.Text);
            Assert.Equal("—", overview.MonthLabel.Text);
            Assert.Equal(0, invalidations);
        });

    private static void AssertTextFits(Label label, bool singleLine = false)
    {
        var measured = singleLine
            ? TextRenderer.MeasureText(label.Text, label.Font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix)
            : label.GetPreferredSize(new Size(label.ClientSize.Width, 0));
        Assert.True(label.ClientSize.Height >= measured.Height,
            $"{label.Text}: font needs {measured.Height}px height, label has {label.ClientSize.Height}px");
        if (singleLine)
            Assert.True(label.ClientSize.Width >= measured.Width,
                $"{label.Text}: font needs {measured.Width}px width, label has {label.ClientSize.Width}px");
    }

    private static void CreateHandles(Control control)
    {
        _ = control.Handle;
        foreach (Control child in control.Controls) CreateHandles(child);
        control.PerformLayout();
    }

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
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "Token summary layout checks did not finish within 20 seconds.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
