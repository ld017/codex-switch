namespace CodexSwitch.App.Controls;

public static class AppTheme
{
    public const string FontFamily = "Segoe UI";
    public static readonly Color Canvas = Color.FromArgb(244, 247, 251);
    public static readonly Color Surface = Color.White;
    public static readonly Color Text = Color.FromArgb(23, 43, 77);
    public static readonly Color Muted = Color.FromArgb(107, 122, 144);
    public static readonly Color Accent = Color.FromArgb(53, 104, 232);
    public static readonly Color AccentSoft = Color.FromArgb(234, 240, 255);
    public static readonly Color Border = Color.FromArgb(225, 232, 242);
    public static readonly Color Success = Color.FromArgb(32, 162, 134);
    public static readonly Color SuccessSoft = Color.FromArgb(232, 247, 242);
    public static readonly Color Warning = Color.FromArgb(212, 151, 54);
    public static readonly Color Danger = Color.FromArgb(216, 93, 103);
    public static readonly Color Track = Color.FromArgb(232, 237, 245);
    public static readonly Color Quiet = Color.FromArgb(154, 165, 182);

    public static Font Font(float size = 9f, FontStyle style = FontStyle.Regular) => new(FontFamily, size, style);
}
