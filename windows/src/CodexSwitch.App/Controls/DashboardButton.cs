using System.Drawing.Drawing2D;

namespace CodexSwitch.App.Controls;

public enum DashboardButtonTone { Neutral, Primary, Selected, Quiet, Danger }

public sealed class DashboardButton : Button
{
    private bool _hover;
    private bool _pressed;
    private DashboardButtonTone _tone;
    public DashboardButtonTone Tone
    {
        get => _tone;
        set
        {
            if (_tone == value) return;
            _tone = value;
            Invalidate();
        }
    }

    public DashboardButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Font = new Font(AppTheme.FontFamily, 9f);
        Cursor = Cursors.Hand;
        Height = 36;
        AutoSize = false;
        TabStop = true;
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _pressed = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (Width < 4 || Height < 4) return;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var primary = Tone == DashboardButtonTone.Primary;
        var fill = primary ? AppTheme.Accent : Tone == DashboardButtonTone.Selected ? AppTheme.AccentSoft : AppTheme.Surface;
        var ink = primary ? Color.White : Tone == DashboardButtonTone.Danger ? AppTheme.Danger : AppTheme.Text;
        if (!Enabled) { fill = AppTheme.Canvas; ink = AppTheme.Muted; }
        else if (_pressed) fill = primary ? ControlPaint.Dark(fill, .08f) : AppTheme.AccentSoft;
        else if (_hover) fill = primary ? ControlPaint.Light(fill, .08f) : AppTheme.AccentSoft;
        using var brush = new SolidBrush(fill);
        using var path = Shape(new RectangleF(.5f, .5f, Width - 1.5f, Height - 1.5f), 8f * DeviceDpi / 96f);
        e.Graphics.FillPath(brush, path);
        if (Tone is not DashboardButtonTone.Quiet)
        {
            using var border = new Pen(Tone == DashboardButtonTone.Selected ? AppTheme.Accent : primary ? fill : AppTheme.Border);
            e.Graphics.DrawPath(border, path);
        }
        TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, ink,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -5, -5), ink, fill);
    }

    private static GraphicsPath Shape(RectangleF rect, float radius)
    {
        var diameter = Math.Min(radius * 2, Math.Min(rect.Width, rect.Height));
        var path = new GraphicsPath();
        path.AddArc(rect.Left, rect.Top, diameter, diameter, 180, 90);
        path.AddArc(rect.Right - diameter, rect.Top, diameter, diameter, 270, 90);
        path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rect.Left, rect.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
