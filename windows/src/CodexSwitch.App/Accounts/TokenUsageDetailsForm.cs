using System.Globalization;
using CodexSwitch.App.Controls;
using CodexSwitch.Core.Auth;
using CodexSwitch.Core.Presentation;
using CodexSwitch.Core.Usage;

namespace CodexSwitch.App.Accounts;

public sealed class TokenUsageDetailsForm : Form
{
    private readonly IReadOnlyDictionary<Guid, UsageState> _usage;
    private readonly ComboBox _account = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly Label _totals = new() { Dock = DockStyle.Fill, ForeColor = AppTheme.Text, Font = AppTheme.Font(11, FontStyle.Bold) };
    private readonly Label _source = new() { Dock = DockStyle.Fill, ForeColor = AppTheme.Muted, Font = AppTheme.Font(9) };
    private readonly DataGridView _days = new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        RowHeadersVisible = false, BackgroundColor = AppTheme.Surface, BorderStyle = BorderStyle.None,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        EnableHeadersVisualStyles = false, MultiSelect = false };

    public TokenUsageDetailsForm(IReadOnlyList<AccountProfile> accounts, IReadOnlyDictionary<Guid, UsageState> usage, Guid? selected)
    {
        _usage = usage;
        Text = "Token 用量详情";
        Font = AppTheme.Font();
        BackColor = AppTheme.Canvas;
        ForeColor = AppTheme.Text;
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(760, 670);
        MinimumSize = new Size(580, 500);
        AutoScaleMode = AutoScaleMode.Dpi;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new Padding(22) };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        root.Controls.Add(new Label { Text = "Token 用量详情", Dock = DockStyle.Fill, Font = AppTheme.Font(17, FontStyle.Bold) }, 0, 0);
        root.Controls.Add(_account, 0, 1);
        root.Controls.Add(_totals, 0, 2);
        root.Controls.Add(_source, 0, 3);
        _totals.Padding = new Padding(0, 12, 0, 0);
        _source.AutoEllipsis = true;
        _days.Columns.Add("date", "服务记录日期");
        _days.Columns.Add("millions", "用量 · 百万 Token");
        _days.Columns.Add("exact", "精确 Token 数量");
        _days.ColumnHeadersDefaultCellStyle.BackColor = AppTheme.AccentSoft;
        _days.ColumnHeadersDefaultCellStyle.ForeColor = AppTheme.Text;
        _days.ColumnHeadersHeight = 36;
        _days.RowTemplate.Height = 32;
        _days.DefaultCellStyle.SelectionBackColor = AppTheme.AccentSoft;
        _days.DefaultCellStyle.SelectionForeColor = AppTheme.Text;
        _days.AlternatingRowsDefaultCellStyle.BackColor = AppTheme.Canvas;
        _days.GridColor = AppTheme.Border;
        root.Controls.Add(_days, 0, 4);
        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = new Padding(0, 10, 0, 0) };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        footer.Controls.Add(new Label { Text = "仅保留最近 366 个有效日期；本月仅汇总已返回记录，缺失不视为零。", Dock = DockStyle.Fill, ForeColor = AppTheme.Muted, Font = AppTheme.Font(8.5f), TextAlign = ContentAlignment.MiddleLeft });
        var close = new DashboardButton { Text = "关闭", Dock = DockStyle.Fill, DialogResult = DialogResult.OK };
        footer.Controls.Add(close, 1, 0);
        root.Controls.Add(footer, 0, 5);
        Controls.Add(root);
        AcceptButton = close;
        CancelButton = close;
        _account.DisplayMember = nameof(AccountChoice.Label);
        foreach (var profile in accounts) _account.Items.Add(new AccountChoice(profile.Id, profile.Label));
        _account.SelectedIndexChanged += (_, _) => RenderUsage();
        var selectedIndex = accounts.ToList().FindIndex(profile => profile.Id == selected);
        if (_account.Items.Count > 0) _account.SelectedIndex = Math.Max(0, selectedIndex);
        else RenderUsage();
    }

    private void RenderUsage()
    {
        _days.Rows.Clear();
        var state = _account.SelectedItem is AccountChoice choice ? _usage.GetValueOrDefault(choice.Id) : null;
        var tokenState = state?.Snapshot?.TokenUsage;
        var tokens = tokenState?.Snapshot;
        if (tokens is null)
        {
            _totals.Text = "累计 Token：未获取\n本月 Token：未获取";
            _source.Text = tokenState?.Message ?? "刷新用量后可查看服务端返回的统计。";
            return;
        }
        var now = DateTimeOffset.Now;
        var month = TokenUsageAggregate.MonthUsage(tokens, now);
        var lifetime = tokens.Summary?.LifetimeTokens;
        _totals.Text = $"累计 Token  {Exact(lifetime)}    ·    {TokenUsageAggregate.FormatHundredMillions(lifetime)}\n本月记录  {Exact(month?.Tokens)}    ·    {TokenUsageAggregate.FormatHundredMillions(month?.Tokens)}";
        var cache = tokenState?.Status == TokenUsageStatus.Error || state?.Status != UsageStatus.Available;
        _source.Text = $"{(cache ? "缓存读取" : "读取时间")}  {tokens.FetchedAt.LocalDateTime:yyyy-MM-dd HH:mm:ss}\n"
            + (month is null ? "本月没有已返回记录" : $"本月返回日期截至 {month.ThroughDate:yyyy-MM-dd}；服务统计可能延迟")
            + "\n" + (tokenState?.Status == TokenUsageStatus.Error ? tokenState.Message : "来源：官方 Codex Token 活动；不包含 Sub2API，也不等同全部 ChatGPT 聊天用量。");
        var today = DateOnly.FromDateTime(now.LocalDateTime);
        var valid = (tokens.DailyUsageBuckets ?? []).Where(day => day.Tokens >= 0)
            .Select(day => (Date: ParseDate(day.StartDate), day.Tokens))
            .Where(day => day.Date.HasValue && day.Date.Value <= today)
            .GroupBy(day => day.Date!.Value)
            .Select(group => (Date: group.Key, Tokens: group.Max(day => day.Tokens)))
            .OrderByDescending(day => day.Date)
            .Take(366);
        foreach (var day in valid)
        {
            var value = day.Tokens is > 0 and < 5000 ? "<0.01" : (day.Tokens / 1_000_000m).ToString("0.00", CultureInfo.InvariantCulture);
            _days.Rows.Add(day.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), value, Exact(day.Tokens));
        }
    }

    private static DateOnly? ParseDate(string text) => DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;
    private static string Exact(long? value) => value is >= 0 ? value.Value.ToString("N0", CultureInfo.InvariantCulture) : "未获取";
    private sealed record AccountChoice(Guid Id, string Label);
}
