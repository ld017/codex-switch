using System.Drawing.Drawing2D;
using CodexSwitch.Core.Presentation;

namespace CodexSwitch.App.Controls;

public sealed class UsageBarControl : Control
{
    private int? _percent;
    private UsageLevel _level;

    public UsageBarControl()
    {
        DoubleBuffered = true;
        Height = 8;
        MinimumSize = new Size(80, 8);
        Dock = DockStyle.Fill;
        Margin = new Padding(0, 6, 0, 6);
    }

    public int? Percent
    {
        get => _percent;
        set { _percent = value; Invalidate(); }
    }

    public UsageLevel Level
    {
        get => _level;
        set { _level = value; Invalidate(); }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var track = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
        using var trackPath = RoundedRectangle(track, Height / 2f);
        using var trackBrush = new SolidBrush(Color.FromArgb(222, 226, 232));
        e.Graphics.FillPath(trackBrush, trackPath);

        using var tickPen = new Pen(Color.FromArgb(155, 163, 174), 1);
        for (var index = 1; index < 4; index++)
        {
            var x = Width * index / 4f;
            e.Graphics.DrawLine(tickPen, x, 1, x, Height - 2);
        }

        if (_percent is not int percent || percent <= 0)
        {
            return;
        }

        var fillWidth = Math.Max(Height, (int)Math.Round(track.Width * Math.Clamp(percent, 0, 100) / 100d));
        var fill = new Rectangle(track.X, track.Y, Math.Min(track.Width, fillWidth), track.Height);
        using var fillPath = RoundedRectangle(fill, Height / 2f);
        using var fillBrush = new SolidBrush(LevelColor(_level));
        e.Graphics.FillPath(fillBrush, fillPath);
    }

    private static Color LevelColor(UsageLevel level) => level switch
    {
        UsageLevel.Critical => Color.FromArgb(218, 92, 85),
        UsageLevel.Warning => Color.FromArgb(215, 157, 75),
        UsageLevel.Healthy => Color.FromArgb(84, 174, 158),
        _ => Color.FromArgb(178, 185, 194),
    };

    private static GraphicsPath RoundedRectangle(Rectangle rectangle, float radius)
    {
        var diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Top, diameter, diameter, 270, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rectangle.Left, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
