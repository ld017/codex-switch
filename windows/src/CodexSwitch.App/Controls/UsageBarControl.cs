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
        Margin = new Padding(0, 9, 0, 9);
        SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.ResizeRedraw, true);
        BackColor = Color.Transparent;
    }

    public int? Percent
    {
        get => _percent;
        set
        {
            if (_percent == value) return;
            _percent = value;
            Invalidate();
        }
    }

    public UsageLevel Level
    {
        get => _level;
        set
        {
            if (_level == value) return;
            _level = value;
            Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        if (Width < 2 || Height < 2) return;
        var trackHeight = Math.Min(7, Height - 1);
        var track = new Rectangle(0, (Height - trackHeight) / 2, Width - 1, trackHeight);
        using var trackPath = RoundedRectangle(track, trackHeight / 2f);
        using var trackBrush = new SolidBrush(AppTheme.Track);
        e.Graphics.FillPath(trackBrush, trackPath);

        if (_percent is not int percent || percent <= 0)
        {
            return;
        }

        var fillWidth = Math.Max(1, (int)Math.Round(track.Width * Math.Clamp(percent, 0, 100) / 100d));
        var fill = new Rectangle(track.X, track.Y, Math.Min(track.Width, fillWidth), track.Height);
        using var fillPath = RoundedRectangle(fill, Math.Min(fill.Width, trackHeight) / 2f);
        using var fillBrush = new SolidBrush(LevelColor(_level));
        e.Graphics.FillPath(fillBrush, fillPath);
    }

    private static Color LevelColor(UsageLevel level) => level switch
    {
        UsageLevel.Critical => AppTheme.Danger,
        UsageLevel.Warning => AppTheme.Warning,
        UsageLevel.Healthy => AppTheme.Success,
        _ => AppTheme.Quiet,
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
