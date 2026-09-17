using CodexSwitch.Core.Presentation;

namespace CodexSwitch.App.Controls;

public sealed class AccountUsageCard : RoundedPanel
{
    private readonly Label _name = Label(FontStyle.Bold, 11f);
    private readonly Label _plan = Label(FontStyle.Regular, 9f, Color.FromArgb(92, 99, 110));
    private readonly Label _primaryPercent = Label(FontStyle.Bold, 10f);
    private readonly Label _primaryReset = Label(FontStyle.Regular, 9f, Color.FromArgb(110, 116, 126));
    private readonly Label _secondaryPercent = Label(FontStyle.Bold, 10f);
    private readonly Label _secondaryReset = Label(FontStyle.Regular, 9f, Color.FromArgb(110, 116, 126));
    private readonly Label _resetCards = Label(FontStyle.Regular, 9f, Color.FromArgb(69, 132, 190));
    private readonly Label _status = Label(FontStyle.Regular, 8.5f, Color.FromArgb(120, 126, 136));
    private readonly UsageBarControl _primaryBar = new();
    private readonly UsageBarControl _secondaryBar = new();
    private bool _selected;
    private bool _active;

    public AccountUsageCard()
    {
        Height = 154;
        Margin = new Padding(0, 0, 0, 8);
        Padding = new Padding(14, 10, 12, 8);
        BackColor = Color.White;
        CornerRadius = 10;
        Cursor = Cursors.Hand;
        DoubleBuffered = true;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 5,
            BackColor = Color.Transparent,
            Margin = Padding.Empty,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 24));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 58));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 94));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var activeMark = Label(FontStyle.Bold, 13f, Color.FromArgb(48, 119, 184));
        activeMark.Name = "activeMark";
        activeMark.TextAlign = ContentAlignment.MiddleCenter;
        layout.Controls.Add(activeMark, 0, 0);
        layout.Controls.Add(_name, 1, 0);
        layout.SetColumnSpan(_name, 2);
        _plan.TextAlign = ContentAlignment.MiddleRight;
        _plan.AutoEllipsis = true;
        layout.Controls.Add(_plan, 3, 0);

        layout.Controls.Add(RowLabel("短"), 0, 1);
        layout.Controls.Add(_primaryBar, 1, 1);
        layout.Controls.Add(_primaryPercent, 2, 1);
        layout.Controls.Add(_primaryReset, 3, 1);
        layout.Controls.Add(RowLabel("周"), 0, 2);
        layout.Controls.Add(_secondaryBar, 1, 2);
        layout.Controls.Add(_secondaryPercent, 2, 2);
        layout.Controls.Add(_secondaryReset, 3, 2);
        layout.Controls.Add(_resetCards, 0, 3);
        layout.SetColumnSpan(_resetCards, 4);
        layout.Controls.Add(_status, 0, 4);
        layout.SetColumnSpan(_status, 4);
        Controls.Add(layout);
        WireClick(this);
    }

    public Guid ProfileId { get; private set; }

    public bool Selected
    {
        get => _selected;
        set { _selected = value; Invalidate(); }
    }

    public event EventHandler? CardClicked;

    public void Bind(
        Guid profileId,
        string accountName,
        AccountUsagePresentation presentation,
        string statusText,
        bool active)
    {
        ProfileId = profileId;
        _active = active;
        _name.Text = accountName;
        _plan.Text = $"{presentation.CreditsText} · {presentation.PlanText}";
        _plan.Tag = _plan.Text;
        BindBar(_primaryBar, _primaryPercent, _primaryReset, presentation.Primary);
        BindBar(_secondaryBar, _secondaryPercent, _secondaryReset, presentation.Secondary);
        _resetCards.Text = "↻  " + presentation.ResetCreditsText;
        _status.Text = statusText;
        if (Controls[0].Controls.Find("activeMark", true).FirstOrDefault() is Label mark)
        {
            mark.Text = active ? "✓" : string.Empty;
        }
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        BorderColor = _active
            ? Color.FromArgb(80, 146, 207)
            : _selected ? Color.FromArgb(100, 155, 204) : Color.FromArgb(222, 226, 232);
        BorderWidth = _active || _selected ? 2 : 1;
        base.OnPaint(e);
        if (_active)
        {
            using var accent = new SolidBrush(Color.FromArgb(80, 146, 207));
            e.Graphics.FillRectangle(accent, 0, 0, 4, Height);
        }
    }

    private void WireClick(Control control)
    {
        control.Click += (_, _) => CardClicked?.Invoke(this, EventArgs.Empty);
        foreach (Control child in control.Controls)
        {
            WireClick(child);
        }
    }

    private static void BindBar(UsageBarControl bar, Label percent, Label reset, UsageBarPresentation value)
    {
        bar.Percent = value.Percent;
        bar.Level = value.Level;
        percent.Text = value.PercentText;
        percent.TextAlign = ContentAlignment.MiddleRight;
        reset.Text = value.ResetText;
        reset.TextAlign = ContentAlignment.MiddleRight;
    }

    private static Label RowLabel(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft,
        ForeColor = Color.FromArgb(115, 121, 130),
        Font = new Font("Segoe UI", 9f),
    };

    private static Label Label(FontStyle style, float size, Color? color = null) => new()
    {
        AutoEllipsis = false,
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft,
        Font = new Font("Segoe UI", size, style),
        ForeColor = color ?? Color.FromArgb(39, 43, 49),
    };
}
