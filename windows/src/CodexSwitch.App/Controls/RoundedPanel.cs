using System.Drawing.Drawing2D;

namespace CodexSwitch.App.Controls;

public class RoundedPanel : Panel
{
    private int _cornerRadius = 14;
    private Color _borderColor = AppTheme.Border;
    private float _borderWidth = 1f;

    public int CornerRadius
    {
        get => _cornerRadius;
        set
        {
            var radius = Math.Max(0, value);
            if (_cornerRadius == radius) return;
            _cornerRadius = radius;
            UpdateRegion();
            Invalidate();
        }
    }

    public Color BorderColor
    {
        get => _borderColor;
        set { if (_borderColor != value) { _borderColor = value; Invalidate(); } }
    }

    public float BorderWidth
    {
        get => _borderWidth;
        set
        {
            var width = Math.Max(0, value);
            if (_borderWidth == width) return;
            _borderWidth = width;
            Invalidate();
        }
    }

    public RoundedPanel()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw, true);
        Resize += (_, _) => UpdateRegion();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (Width <= 1 || Height <= 1 || BorderWidth <= 0) return;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var inset = BorderWidth / 2;
        using var path = CreatePath(new RectangleF(inset, inset, Width - BorderWidth - 1, Height - BorderWidth - 1), CornerRadius);
        using var pen = new Pen(BorderColor, BorderWidth);
        e.Graphics.DrawPath(pen, path);
    }

    protected static GraphicsPath CreatePath(RectangleF rectangle, float radius)
    {
        var diameter = Math.Min(radius * 2, Math.Min(rectangle.Width, rectangle.Height));
        var path = new GraphicsPath();
        if (diameter <= 0)
        {
            path.AddRectangle(rectangle);
            return path;
        }
        path.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Top, diameter, diameter, 270, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rectangle.Left, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    private void UpdateRegion()
    {
        if (Width <= 0 || Height <= 0)
        {
            return;
        }

        using var path = CreatePath(new RectangleF(0, 0, Width, Height), CornerRadius);
        Region?.Dispose();
        Region = new Region(path);
    }
}
