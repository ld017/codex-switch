using CodexSwitch.Core.Presentation;

namespace CodexSwitch.App.Controls;

public sealed class AccountUsageCard : RoundedPanel
{
    private readonly Label _name = Label(FontStyle.Bold, 11f);
    private readonly Label _plan = Label(FontStyle.Bold, 8f, AppTheme.Accent);
    private readonly Label _activeTag = Label(FontStyle.Bold, 8f, AppTheme.Success);
    private readonly Label _primaryPercent = Label(FontStyle.Bold, 10f);
    private readonly Label _primaryReset = Label(FontStyle.Regular, 8.5f, AppTheme.Muted);
    private readonly Label _secondaryPercent = Label(FontStyle.Bold, 10f);
    private readonly Label _secondaryReset = Label(FontStyle.Regular, 8.5f, AppTheme.Muted);
    private readonly Label _resetCards = Label(FontStyle.Regular, 8.5f, AppTheme.Muted);
    private readonly Label _resetExpiry = Label(FontStyle.Regular, 8f, AppTheme.Muted);
    private readonly Label _credits = Label(FontStyle.Regular, 8.5f, AppTheme.Muted);
    private readonly Label _lifetimeTokens = SingleLineLabel(FontStyle.Bold, 11f, AppTheme.Accent);
    private readonly Label _monthTokens = SingleLineLabel(FontStyle.Bold, 11f, AppTheme.Accent);
    private readonly Label _tokenStatus = SingleLineLabel(FontStyle.Regular, 7.5f, AppTheme.Muted);
    private readonly Label _status = Label(FontStyle.Regular, 8f, AppTheme.Muted);
    private readonly UsageBarControl _primaryBar = new();
    private readonly UsageBarControl _secondaryBar = new();
    private readonly ToolTip _details = new() { AutoPopDelay = 30000, InitialDelay = 300, ReshowDelay = 100 };
    private readonly RoundedPanel _activeBadge;
    private bool _selected;
    private bool _active;
    private bool _hovered;
    private BindingState? _binding;

    public AccountUsageCard()
    {
        Height = 238;
        MinimumSize = new Size(320, 238);
        Margin = new Padding(0, 0, 12, 12);
        Padding = new Padding(16, 10, 16, 10);
        BackColor = AppTheme.Surface;
        CornerRadius = 14;
        Cursor = Cursors.Hand;
        DoubleBuffered = true;

        var layout = Table(1, 6);
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var height in new[] { 36, 29, 29, 28, 70, 24 })
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));

        var header = Table(2, 1);
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _name.AutoEllipsis = true;
        header.Controls.Add(_name, 0, 0);
        var badges = new BufferedFlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Color.Transparent,
            Margin = Padding.Empty,
            Padding = new Padding(0, 5, 0, 0),
        };
        badges.Controls.Add(Badge(_plan, AppTheme.AccentSoft, 56));
        _activeBadge = Badge(_activeTag, AppTheme.SuccessSoft, 44);
        _activeTag.Text = "当前";
        badges.Controls.Add(_activeBadge);
        header.Controls.Add(badges, 1, 0);
        layout.Controls.Add(header, 0, 0);
        layout.Controls.Add(QuotaRow("短期", _primaryBar, _primaryPercent, _primaryReset), 0, 1);
        layout.Controls.Add(QuotaRow("周额度", _secondaryBar, _secondaryPercent, _secondaryReset), 0, 2);

        var reset = Table(3, 1);
        reset.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104));
        reset.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        reset.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
        _resetExpiry.AutoEllipsis = true;
        _credits.TextAlign = ContentAlignment.MiddleRight;
        reset.Controls.Add(_resetCards, 0, 0);
        reset.Controls.Add(_resetExpiry, 1, 0);
        reset.Controls.Add(_credits, 2, 0);
        layout.Controls.Add(reset, 0, 3);

        var tokenPanel = new RoundedPanel
        {
            Dock = DockStyle.Fill,
            BackColor = AppTheme.Canvas,
            BorderWidth = 0,
            CornerRadius = 10,
            Margin = new Padding(0, 2, 0, 2),
            Padding = new Padding(10, 2, 10, 2),
        };
        var tokens = Table(2, 3);
        tokens.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        tokens.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        tokens.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
        tokens.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        tokens.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        tokens.Controls.Add(Caption("累计 Token"), 0, 0);
        tokens.Controls.Add(Caption("本月 Token"), 1, 0);
        tokens.Controls.Add(_lifetimeTokens, 0, 1);
        tokens.Controls.Add(_monthTokens, 1, 1);
        tokens.Controls.Add(_tokenStatus, 0, 2);
        tokens.SetColumnSpan(_tokenStatus, 2);
        _tokenStatus.AutoEllipsis = true;
        tokenPanel.Controls.Add(tokens);
        layout.Controls.Add(tokenPanel, 0, 4);
        _status.AutoEllipsis = true;
        layout.Controls.Add(_status, 0, 5);
        Controls.Add(layout);
        WireClick(this);
        UpdateBorder();
    }

    public Guid ProfileId { get; private set; }

    public bool Selected
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            _selected = value;
            UpdateBorder();
        }
    }

    public event EventHandler? CardClicked;

    public void Bind(
        Guid profileId,
        string accountName,
        AccountUsagePresentation presentation,
        string statusText,
        bool active,
        string? errorDetail = null)
    {
        var binding = new BindingState(profileId, accountName, presentation, statusText, active, errorDetail);
        if (_binding == binding) return;

        ProfileId = profileId;
        if (_binding is null || _active != active)
        {
            _active = active;
            _activeBadge.Visible = active;
            UpdateBorder();
        }
        SetText(_name, accountName);
        SetText(_plan, DisplayPlan(presentation.PlanText));
        BindBar(_primaryBar, _primaryPercent, _primaryReset, presentation.Primary);
        BindBar(_secondaryBar, _secondaryPercent, _secondaryReset, presentation.Secondary);
        SetText(_resetCards, presentation.ResetCreditsText);
        SetText(_resetExpiry, presentation.ResetCreditExpiryText);
        SetText(_credits, presentation.CreditsText);
        SetText(_lifetimeTokens, presentation.TokenUsage.LifetimeText);
        SetText(_monthTokens, presentation.TokenUsage.MonthText);
        SetText(_tokenStatus, presentation.TokenUsage.StatusText);
        SetForeColor(_tokenStatus, presentation.TokenUsage.HasError ? AppTheme.Warning : AppTheme.Muted);
        SetText(_status, statusText);
        SetForeColor(_status, statusText.Contains("失败", StringComparison.Ordinal) || statusText.Contains("登录", StringComparison.Ordinal)
            ? AppTheme.Warning : AppTheme.Muted);
        UpdateDetails(this, binding);
        _binding = binding;
    }

    private void UpdateBorder()
    {
        BorderColor = _active || _selected ? AppTheme.Accent : _hovered ? Color.FromArgb(184, 202, 235) : AppTheme.Border;
        BorderWidth = _active || _selected ? 1.7f : 1;
    }

    private void WireClick(Control control)
    {
        control.Click += (_, _) => CardClicked?.Invoke(this, EventArgs.Empty);
        control.MouseEnter += (_, _) => SetHovered(true);
        control.MouseLeave += (_, _) => SetHovered(ClientRectangle.Contains(PointToClient(Cursor.Position)));
        foreach (Control child in control.Controls) WireClick(child);
    }

    private void SetHovered(bool hovered)
    {
        if (_hovered == hovered) return;
        _hovered = hovered;
        UpdateBorder();
    }

    private void UpdateDetails(Control control, BindingState binding)
    {
        var text = control == _name ? binding.AccountName
            : control == _plan ? $"{binding.Presentation.CreditsText} · {binding.Presentation.PlanText}"
            : control == _primaryPercent ? "短期额度剩余百分比"
            : control == _secondaryPercent ? "周期额度剩余百分比"
            : control == _primaryReset ? "短期额度重置倒计时"
            : control == _secondaryReset ? "周期额度重置倒计时"
            : control == _lifetimeTokens || control == _monthTokens || control == _tokenStatus ? binding.Presentation.TokenUsage.DetailText
            : control == _resetExpiry ? binding.Presentation.ResetCreditExpiryText
            : string.IsNullOrWhiteSpace(binding.ErrorDetail) ? binding.StatusText : $"{binding.StatusText}\n{binding.ErrorDetail}";
        if (_details.GetToolTip(control) != text) _details.SetToolTip(control, text);
        foreach (Control child in control.Controls) UpdateDetails(child, binding);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _details.Dispose();
        base.Dispose(disposing);
    }

    private static void BindBar(UsageBarControl bar, Label percent, Label reset, UsageBarPresentation value)
    {
        bar.Percent = value.Percent;
        bar.Level = value.Level;
        SetText(percent, value.PercentText);
        SetForeColor(percent, value.Level switch
        {
            UsageLevel.Critical => AppTheme.Danger,
            UsageLevel.Warning => AppTheme.Warning,
            _ => AppTheme.Text,
        });
        SetText(reset, value.ResetText);
    }

    private static Control QuotaRow(string caption, UsageBarControl bar, Label percent, Label reset)
    {
        percent.TextAlign = ContentAlignment.MiddleRight;
        reset.TextAlign = ContentAlignment.MiddleRight;
        var row = Table(4, 1);
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 44));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 48));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
        row.Controls.Add(Caption(caption), 0, 0);
        row.Controls.Add(bar, 1, 0);
        row.Controls.Add(percent, 2, 0);
        row.Controls.Add(reset, 3, 0);
        return row;
    }

    private static RoundedPanel Badge(Label text, Color color, int width)
    {
        var badge = new RoundedPanel
        {
            Size = new Size(width, 24),
            BackColor = color,
            BorderWidth = 0,
            CornerRadius = 8,
            Margin = new Padding(6, 0, 0, 0),
        };
        text.TextAlign = ContentAlignment.MiddleCenter;
        badge.Controls.Add(text);
        return badge;
    }

    private static string DisplayPlan(string plan) => plan.ToLowerInvariant() switch
    {
        "plus" => "Plus",
        "pro" => "Pro",
        "team" => "Team",
        "business" => "Business",
        "free" => "Free",
        _ => plan,
    };

    private static BufferedTableLayoutPanel Table(int columns, int rows) => new()
    {
        Dock = DockStyle.Fill,
        ColumnCount = columns,
        RowCount = rows,
        BackColor = Color.Transparent,
        Margin = Padding.Empty,
        Padding = Padding.Empty,
    };

    private static Label Caption(string text)
    {
        var caption = Label(FontStyle.Regular, 8.5f, AppTheme.Muted);
        caption.Text = text;
        return caption;
    }

    private static Label SingleLineLabel(FontStyle style, float size, Color color) => new SingleLineTextLabel
    {
        AutoEllipsis = true,
        Dock = DockStyle.Fill,
        Margin = Padding.Empty,
        TextAlign = ContentAlignment.MiddleLeft,
        Font = AppTheme.Font(size, style),
        ForeColor = color,
    };

    private sealed class SingleLineTextLabel : Label
    {
        // A Label normally wraps at the space before "亿" when the card narrows.
        // Keep every value on its own line; the tooltip retains the exact count.
        protected override void OnPaint(PaintEventArgs e)
            => TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, ForeColor,
                TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis |
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix |
                TextFormatFlags.PreserveGraphicsClipping);
    }

    private static Label Label(FontStyle style, float size, Color? color = null) => new()
    {
        AutoEllipsis = false,
        Dock = DockStyle.Fill,
        Margin = Padding.Empty,
        TextAlign = ContentAlignment.MiddleLeft,
        Font = AppTheme.Font(size, style),
        ForeColor = color ?? AppTheme.Text,
    };

    private static void SetText(Control control, string text)
    {
        if (control.Text != text) control.Text = text;
    }

    private static void SetForeColor(Control control, Color color)
    {
        if (control.ForeColor != color) control.ForeColor = color;
    }

    private sealed record BindingState(
        Guid ProfileId,
        string AccountName,
        AccountUsagePresentation Presentation,
        string StatusText,
        bool Active,
        string? ErrorDetail);
}
