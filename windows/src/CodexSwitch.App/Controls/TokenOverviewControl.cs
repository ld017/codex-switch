namespace CodexSwitch.App.Controls;

/// <summary>
/// A width-aware token summary. Text determines its height so increasing the
/// Windows display scale never squeezes the value into a fixed-height row.
/// </summary>
public sealed class TokenOverviewControl : RoundedPanel
{
    private readonly ToolTip _details = new() { AutoPopDelay = 30000, InitialDelay = 300 };
    private bool _arranging;

    public TokenOverviewControl()
    {
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Dock = DockStyle.Top;
        BackColor = AppTheme.Surface;
        Padding = new Padding(14, 10, 14, 10);
        Margin = new Padding(0, 8, 0, 0);
        Width = 388;

        TotalCaptionLabel = CreateLabel("累计用量 · 亿 Token", 8.5f, FontStyle.Regular, AppTheme.Muted);
        MonthCaptionLabel = CreateLabel("本月记录 · 亿 Token", 8.5f, FontStyle.Regular, AppTheme.Muted);
        TotalLabel = CreateLabel("—", 16f, FontStyle.Bold, AppTheme.Text);
        MonthLabel = CreateLabel("—", 16f, FontStyle.Bold, AppTheme.Text);
        CoverageLabel = CreateLabel("服务统计可能延迟", 8f, FontStyle.Regular, AppTheme.Muted);

        foreach (var label in new[] { TotalCaptionLabel, MonthCaptionLabel, TotalLabel, MonthLabel, CoverageLabel })
        {
            Controls.Add(label);
            label.TextChanged += (_, _) => RefreshLayout();
            label.FontChanged += (_, _) => RefreshLayout();
        }
        RefreshLayout();
    }

    public Label TotalCaptionLabel { get; }
    public Label MonthCaptionLabel { get; }
    public Label TotalLabel { get; }
    public Label MonthLabel { get; }
    public Label CoverageLabel { get; }

    public void Bind(string total, string month, string coverage, string? details = null)
    {
        SuspendLayout();
        try
        {
            SetText(TotalLabel, string.IsNullOrWhiteSpace(total) ? "—" : total);
            SetText(MonthLabel, string.IsNullOrWhiteSpace(month) ? "—" : month);
            SetText(CoverageLabel, coverage);
            if (_details.GetToolTip(this) != details)
                foreach (var label in Controls.Cast<Control>().Prepend(this))
                    _details.SetToolTip(label, details);
        }
        finally { ResumeLayout(true); }
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        var width = proposedSize.Width > 0 && proposedSize.Width < int.MaxValue
            ? proposedSize.Width : Math.Max(Width, 300);
        if (TotalLabel is null || MonthLabel is null || CoverageLabel is null)
            return new Size(width, Padding.Vertical);
        var layout = MeasureLayout(width);
        return new Size(width, layout.Height);
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        if (_arranging || TotalLabel is null) return;
        _arranging = true;
        try
        {
            var layout = MeasureLayout(ClientSize.Width);
            SetBoundsIfChanged(TotalCaptionLabel, layout.TotalCaption);
            SetBoundsIfChanged(TotalLabel, layout.TotalValue);
            SetBoundsIfChanged(MonthCaptionLabel, layout.MonthCaption);
            SetBoundsIfChanged(MonthLabel, layout.MonthValue);
            SetBoundsIfChanged(CoverageLabel, layout.Coverage);
            if (AutoSize && Height != layout.Height) Height = layout.Height;
        }
        finally { _arranging = false; }
    }

    private SummaryLayout MeasureLayout(int width)
    {
        var innerWidth = Math.Max(1, width - Padding.Horizontal);
        var gap = Math.Max(6, TotalCaptionLabel.Font.Height / 2);
        var lineGap = Math.Max(2, TotalCaptionLabel.Font.Height / 6);
        var columnWidth = Math.Max(1, (innerWidth - gap) / 2);
        var valueWidth = Math.Max(SingleLineSize(TotalLabel).Width, SingleLineSize(MonthLabel).Width);
        var stacked = valueWidth > columnWidth;
        var metricWidth = stacked ? innerWidth : columnWidth;
        var captionHeight = Math.Max(TextHeight(TotalCaptionLabel, metricWidth), TextHeight(MonthCaptionLabel, metricWidth));
        var valueHeight = Math.Max(SingleLineSize(TotalLabel).Height, SingleLineSize(MonthLabel).Height);
        var metricHeight = captionHeight + lineGap + valueHeight;
        var top = Padding.Top;
        var secondLeft = stacked ? Padding.Left : Padding.Left + columnWidth + gap;
        var secondTop = stacked ? top + metricHeight + gap : top;
        var coverageTop = (stacked ? secondTop : top) + metricHeight + gap;
        var coverageHeight = TextHeight(CoverageLabel, innerWidth);

        return new SummaryLayout(
            new Rectangle(Padding.Left, top, metricWidth, captionHeight),
            new Rectangle(Padding.Left, top + captionHeight + lineGap, metricWidth, valueHeight),
            new Rectangle(secondLeft, secondTop, metricWidth, captionHeight),
            new Rectangle(secondLeft, secondTop + captionHeight + lineGap, metricWidth, valueHeight),
            new Rectangle(Padding.Left, coverageTop, innerWidth, coverageHeight),
            coverageTop + coverageHeight + Padding.Bottom);
    }

    private void RefreshLayout()
    {
        if (IsDisposed || TotalLabel is null || MonthLabel is null || CoverageLabel is null) return;
        PerformLayout();
        Parent?.PerformLayout(this, nameof(PreferredSize));
    }

    private static Size SingleLineSize(Label label)
    {
        // Label can use either GDI or GDI+ depending on the application setting.
        // Reserve enough space for its own renderer as well as the GDI glyph box.
        var gdi = TextRenderer.MeasureText(label.Text, label.Font, Size.Empty,
            TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        var preferred = label.GetPreferredSize(Size.Empty);
        return new Size(Math.Max(gdi.Width, preferred.Width), Math.Max(label.Font.Height, Math.Max(gdi.Height, preferred.Height)));
    }

    private static int TextHeight(Label label, int width) => Math.Max(label.Font.Height,
        label.GetPreferredSize(new Size(Math.Max(1, width), 0)).Height);

    private static Label CreateLabel(string text, float size, FontStyle style, Color color) => new()
    {
        Text = text,
        Font = AppTheme.Font(size, style),
        ForeColor = color,
        BackColor = Color.Transparent,
        Margin = Padding.Empty,
        Padding = Padding.Empty,
        TextAlign = ContentAlignment.MiddleLeft,
        AutoSize = false,
        AutoEllipsis = false,
        UseMnemonic = false,
    };

    private static void SetText(Label label, string text)
    {
        if (label.Text != text) label.Text = text;
    }

    private static void SetBoundsIfChanged(Control control, Rectangle bounds)
    {
        if (control.Bounds != bounds) control.Bounds = bounds;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _details.Dispose();
        base.Dispose(disposing);
    }

    private sealed record SummaryLayout(Rectangle TotalCaption, Rectangle TotalValue,
        Rectangle MonthCaption, Rectangle MonthValue, Rectangle Coverage, int Height);
}
