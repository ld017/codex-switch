using System.Diagnostics;
using CodexSwitch.App.Accounts;
using CodexSwitch.App.Controls;
using CodexSwitch.Core.Auth;
using CodexSwitch.Core.Codex;
using CodexSwitch.Core.Configuration;
using CodexSwitch.Core.Diagnostics;
using CodexSwitch.Core.Presentation;
using CodexSwitch.Core.Usage;

namespace CodexSwitch.App;

public sealed class MainForm : Form
{
    private static readonly Color WindowColor = AppTheme.Canvas;
    private static readonly Color TextColor = AppTheme.Text;
    private static readonly Color MutedColor = AppTheme.Muted;
    private readonly AppServices _services;
    private readonly Action<string, Exception>? _errorPresenter;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 60 * 1000 };
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _refreshCancellation;
    private Task _refreshTask = Task.CompletedTask;
    private Task _operationTask = Task.CompletedTask;
    private bool _closingAfterCleanup;
    private bool _closingInProgress;
    private bool _refreshing;
    private int _refreshSequence;
    private bool _initialLayoutApplied;
    private bool _sizingCards;
    private readonly Label _providerStatus = StatusLabel();
    private readonly Label _sub2Status = StatusLabel();
    private readonly Label _refreshStatus = new() { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleRight, ForeColor = MutedColor, Font = new Font("Segoe UI", 8.5f) };
    private readonly TokenOverviewControl _tokenOverview = new();
    private readonly Label _totalTokens;
    private readonly Label _monthTokens;
    private readonly Label _tokenCoverage;
    private DashboardButton? _openAiButton;
    private DashboardButton? _sub2Button;
    private readonly Dictionary<Guid, AuthIdentity> _identities = [];
    private readonly Dictionary<Guid, AccountUsageCard> _accountCards = [];
    private Label? _emptyAccountsMessage;
    private readonly FlowLayoutPanel _accountsPanel = new BufferedFlowLayoutPanel()
    {
        Dock = DockStyle.Fill,
        AutoScroll = true,
        FlowDirection = FlowDirection.TopDown,
        WrapContents = false,
        BackColor = WindowColor,
        Padding = Padding.Empty,
        Margin = Padding.Empty,
    };
    private readonly TableLayoutPanel _root;
    private readonly CheckBox _startup = new() { Text = "登录 Windows 时自动启动", AutoSize = true, Font = new Font("Segoe UI", 8.5f), ForeColor = MutedColor };
    private readonly ToolStripStatusLabel _statusText = new() { Text = "就绪" };
    private readonly List<Button> _actionButtons = [];
    private IReadOnlyList<AccountProfile> _accounts = [];
    private IReadOnlyDictionary<Guid, UsageState> _usage = new Dictionary<Guid, UsageState>();
    private Guid? _activeProfileId;
    private Guid? _selectedProfileId;
    private Provider _provider = Provider.OpenAI;
    private bool _busy;
    private bool _syncingStartup;

    public MainForm(AppServices services, bool startMinimized, Action<string, Exception>? errorPresenter = null)
    {
        // Keep the 96-DPI baseline until every static child has been constructed.
        // Otherwise early property setters can consume autoscale before children exist.
        SuspendLayout();
        _services = services;
        _errorPresenter = errorPresenter;
        _totalTokens = _tokenOverview.TotalLabel;
        _monthTokens = _tokenOverview.MonthLabel;
        _tokenCoverage = _tokenOverview.CoverageLabel;
        Text = "Codex Switch 1.0.6";
        DoubleBuffered = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96);
        Width = 420;
        Height = 1040;
        MinimumSize = new Size(360, 600);
        BackColor = WindowColor;
        ForeColor = TextColor;
        Font = new Font("Segoe UI", 9f);
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? Icon;
        StartPosition = FormStartPosition.CenterScreen;
        if (startMinimized) WindowState = FormWindowState.Minimized;

        _root = new BufferedTableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(12, 12, 12, 8),
            BackColor = WindowColor,
        };
        _root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _root.Controls.Add(BuildHeader(), 0, 0);
        _root.Controls.Add(BuildConnectionPanel(), 0, 1);
        _root.Controls.Add(BuildAccountHeading(), 0, 2);
        _root.Controls.Add(_accountsPanel, 0, 3);
        _root.Controls.Add(BuildActionsPanel(), 0, 4);

        var status = new StatusStrip { SizingGrip = false, BackColor = WindowColor, ForeColor = MutedColor, Font = new Font("Segoe UI", 8f), RenderMode = ToolStripRenderMode.System };
        _statusText.Spring = true;
        _statusText.TextAlign = ContentAlignment.MiddleLeft;
        status.Items.Add(_statusText);
        Controls.Add(_root);
        Controls.Add(status);
        _accountsPanel.SizeChanged += (_, _) => LayoutAccountCards();

        Shown += (_, _) => _ = RunUiEventAsync("初始化", InitializeAsync);
        _timer.Tick += (_, _) => _ = RunUiEventAsync("后台刷新", () => RefreshUsageAsync(false), showErrors: false);
        _timer.Start();
        FormClosing += (_, args) =>
        {
            if (_closingAfterCleanup) return;
            args.Cancel = _refreshing || _busy || _closingInProgress;
            if (_closingInProgress) return;
            try
            {
                _timer.Stop();
                _lifetime.Cancel();
                _refreshCancellation?.Cancel();
                if (!args.Cancel) return;
                _closingInProgress = true;
                _ = CompleteCloseAsync();
            }
            catch (Exception error) { WriteLog("关闭窗口", error.ToString()); }
        };
        FormClosed += (_, _) =>
        {
            try { _timer.Dispose(); _lifetime.Cancel(); _refreshCancellation?.Cancel(); }
            catch (Exception error) { WriteLog("关闭清理", error.ToString()); }
        };
        Disposed += (_, _) => _timer.Dispose();
        ResumeLayout(false);
        PerformLayout();
        Resize += (_, _) =>
        {
            try { if (!_initialLayoutApplied && WindowState == FormWindowState.Normal) RenderAccounts(); }
            catch (Exception error) { WriteLog("初始化窗口布局", error.ToString()); }
        };
    }

    private async Task CompleteCloseAsync()
    {
        try { await Task.WhenAll(_refreshTask, _operationTask); }
        catch (Exception error) { WriteLog("关闭前清理", error.ToString()); }
        try
        {
            _closingAfterCleanup = true;
            if (!IsDisposed) Close();
        }
        catch (Exception error) { WriteLog("完成关闭", error.ToString()); }
    }

    private Task RunUiEventAsync(string name, Func<Task> action, bool showErrors = true)
        => UiExceptionGuard.RunAsync(action, error =>
        {
            if (showErrors) ShowError(name, error);
            else WriteLog(name, error.ToString());
        }, _lifetime.Token);

    private Control BuildHeader()
    {
        var header = new BufferedTableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, Margin = new Padding(0, 0, 0, 10) };
        header.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var titleArea = new BufferedFlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = Padding.Empty };
        var title = new Label { Text = "Codex Switch", AutoSize = true, Font = new Font("Segoe UI", 17f, FontStyle.Bold), ForeColor = TextColor, Margin = Padding.Empty };
        var subtitle = new Label { Text = "账号 · 额度 · Token", AutoSize = true, Font = new Font("Segoe UI", 8.5f), ForeColor = MutedColor, Margin = new Padding(1, 2, 0, 0) };
        titleArea.Controls.Add(title);
        titleArea.Controls.Add(subtitle);
        titleArea.SizeChanged += (_, _) =>
        {
            var maximum = new Size(Math.Max(1, titleArea.ClientSize.Width), 0);
            if (title.MaximumSize != maximum) title.MaximumSize = maximum;
            if (subtitle.MaximumSize != maximum) subtitle.MaximumSize = maximum;
        };
        header.Controls.Add(titleArea, 0, 0);
        header.Controls.Add(new Label { Text = "1.0.6", AutoSize = true, Anchor = AnchorStyles.Right, ForeColor = MutedColor, TextAlign = ContentAlignment.MiddleRight, Font = new Font("Segoe UI", 8f) }, 1, 0);
        return header;
    }

    private Control BuildConnectionPanel()
    {
        var stack = new BufferedTableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Margin = Padding.Empty };
        var connection = CardPanel();
        var layout = new BufferedTableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, RowCount = 2, Padding = new Padding(12, 8, 12, 8) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var statuses = new BufferedFlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = Padding.Empty };
        statuses.Controls.Add(_providerStatus);
        statuses.Controls.Add(_sub2Status);
        layout.Controls.Add(statuses, 0, 0);
        var buttons = new BufferedFlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0, 4, 0, 0), WrapContents = false };
        _openAiButton = (DashboardButton)ActionButton("OpenAI", () => SwitchProviderAsync(Provider.OpenAI));
        _sub2Button = (DashboardButton)ActionButton("Sub2API", () => SwitchProviderAsync(Provider.Sub2Api));
        foreach (var button in new[] { _openAiButton, _sub2Button }) { button.MinimumSize = new Size(100, 32); buttons.Controls.Add(button); }
        layout.Controls.Add(buttons, 0, 1);
        connection.Controls.Add(layout);
        stack.Controls.Add(connection, 0, 0);

        _tokenOverview.Dock = DockStyle.Top;
        _tokenOverview.Margin = new Padding(0, 10, 0, 0);
        stack.Controls.Add(_tokenOverview, 0, 1);
        return stack;
    }

    private Control BuildAccountHeading()
    {
        var panel = new BufferedTableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, Margin = new Padding(0, 8, 0, 4), Padding = new Padding(0, 4, 0, 4) };
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        panel.Controls.Add(new Label { Text = "OpenAI 账号", AutoSize = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Font = new Font("Segoe UI", 10f, FontStyle.Bold), ForeColor = TextColor }, 0, 0);
        _refreshStatus.AutoSize = true;
        panel.Controls.Add(_refreshStatus, 1, 0);
        return panel;
    }

    private Control BuildActionsPanel()
    {
        var card = CardPanel();
        card.Margin = new Padding(0, 12, 0, 0);
        var layout = new BufferedTableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 3, RowCount = 4, Padding = new Padding(6, 6, 6, 6) };
        for (var index = 0; index < 3; index++) layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 3));
        for (var index = 0; index < 4; index++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var switchButton = ActionButton("切换账号", SwitchSelectedAccountAsync, true);
        ((DashboardButton)switchButton).Tone = DashboardButtonTone.Primary;
        layout.Controls.Add(switchButton, 0, 0);
        AddAction(layout, "刷新用量", () => RefreshUsageAsync(true), 1, 0);
        AddAction(layout, "Token 详情", () => { ShowTokenDetails(); return Task.CompletedTask; }, 2, 0);
        AddAction(layout, "重新登录", ReauthenticateSelectedAccountAsync, 0, 1);
        AddAction(layout, "设备码登录", () => LoginAsync(LoginMode.DeviceCode), 1, 1);
        AddAction(layout, "账号管理", () => { ShowAccountActions(); return Task.CompletedTask; }, 2, 1);
        AddAction(layout, "添加账号", () => LoginAsync(LoginMode.Browser), 0, 2);
        AddAction(layout, "打开 Codex", () => RunUiOperationAsync("打开 Codex", () => _services.Lifecycle.LaunchAsync(_lifetime.Token)), 1, 2);
        AddAction(layout, "打开日志", () => { OpenLogs(); return Task.CompletedTask; }, 2, 2);

        _startup.CheckedChanged += (_, _) => ToggleStartup();
        _startup.Margin = new Padding(5, 7, 0, 0);
        layout.Controls.Add(_startup, 0, 3);
        layout.SetColumnSpan(_startup, 3);
        card.Controls.Add(layout);
        return card;
    }

    private void ShowTokenDetails()
    {
        using var form = new TokenUsageDetailsForm(_accounts, _usage, _selectedProfileId);
        form.ShowDialog(this);
    }

    private void ShowAccountActions()
    {
        var menu = new ContextMenuStrip { Font = new Font("Segoe UI", 9f) };
        var rename = menu.Items.Add("重命名所选账号");
        rename.Enabled = SelectedProfile() is not null;
        rename.Click += (_, _) => _ = RunUiEventAsync("重命名账号", RenameSelectedAccountAsync);
        var delete = menu.Items.Add("删除所选账号");
        delete.Enabled = SelectedProfile() is not null;
        delete.ForeColor = AppTheme.Danger;
        delete.Click += (_, _) => _ = RunUiEventAsync("删除账号", RemoveSelectedAccountAsync);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("打开日志目录", null, (_, _) => OpenLogs());
        menu.Closed += (_, _) => menu.Dispose();
        menu.Show(Cursor.Position);
    }

    private async Task InitializeAsync()
    {
        try
        {
            await ImportLiveAccountIfNeededAsync();
            _usage = await _services.Usage.LoadCachedAsync(_lifetime.Token);
            await ReloadAsync();
            await RefreshUsageAsync(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) { ShowError("初始化", error); }
    }

    private async Task ReloadAsync()
    {
        if (_lifetime.IsCancellationRequested || IsDisposed) return;
        _provider = ReadProvider();
        _activeProfileId = await ReconcileActiveProfileAsync();
        _accounts = await _services.Accounts.ListAsync(_lifetime.Token);
        _identities.Clear();
        foreach (var account in _accounts)
        {
            try
            {
                var bytes = await _services.Accounts.LoadAuthAsync(account.Id, _lifetime.Token);
                try { _identities[account.Id] = AuthBlob.Parse(bytes).GetIdentity(); }
                finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); }
            }
            catch (Exception error) when (error is not OperationCanceledException) { WriteLog("读取统计账号身份", error.Message); }
        }
        if (!_accounts.Any(account => account.Id == _selectedProfileId))
            _selectedProfileId = _activeProfileId ?? _accounts.FirstOrDefault()?.Id;
        var sub2Online = await _services.Sub2Api.IsOnlineAsync(_lifetime.Token);
        if (_lifetime.IsCancellationRequested || IsDisposed) return;
        _providerStatus.Text = _provider == Provider.OpenAI ? "OpenAI 官方 · 当前连接" : "Sub2API · 当前连接";
        _sub2Status.Text = $"Sub2API 服务{(sub2Online ? "在线" : "离线")}  ·  自动刷新 1 分钟";
        if (_openAiButton is not null) _openAiButton.Tone = _provider == Provider.OpenAI ? DashboardButtonTone.Selected : DashboardButtonTone.Neutral;
        if (_sub2Button is not null) _sub2Button.Tone = _provider == Provider.Sub2Api ? DashboardButtonTone.Selected : DashboardButtonTone.Neutral;
        _syncingStartup = true;
        _startup.Checked = _services.Startup.IsEnabled;
        _syncingStartup = false;
        RenderAccounts();
    }

    private void RenderAccounts()
    {
        var workingArea = Screen.FromControl(this).WorkingArea;
        var scale = DeviceDpi / 96d;
        var plan = DashboardLayoutPlan.Create(_accounts.Count, (int)(workingArea.Width / scale), (int)(workingArea.Height / scale));
        ApplyDashboardLayout(plan, workingArea);
        RenderTokenOverview();
        var wantedIds = _accounts.Select(account => account.Id).ToHashSet();
        var structuralChange = _accountCards.Count != wantedIds.Count
            || _accountCards.Keys.Any(id => !wantedIds.Contains(id))
            || _accounts.Where((account, index) => index >= _accountsPanel.Controls.Count
                || _accountsPanel.Controls[index] is not AccountUsageCard card || card.ProfileId != account.Id).Any();
        var scroll = new Point(-_accountsPanel.AutoScrollPosition.X, -_accountsPanel.AutoScrollPosition.Y);
        if (structuralChange) _accountsPanel.SuspendLayout();
        try
        {
            foreach (var removedId in _accountCards.Keys.Where(id => !wantedIds.Contains(id)).ToArray())
            {
                var removed = _accountCards[removedId];
                _accountsPanel.Controls.Remove(removed);
                removed.Dispose();
                _accountCards.Remove(removedId);
            }
            if (_accounts.Count > 0 && _emptyAccountsMessage is not null)
            {
                _accountsPanel.Controls.Remove(_emptyAccountsMessage);
                _emptyAccountsMessage.Dispose();
                _emptyAccountsMessage = null;
            }
            for (var index = 0; index < _accounts.Count; index++)
            {
                var account = _accounts[index];
                if (!_accountCards.TryGetValue(account.Id, out var card))
                {
                    card = new AccountUsageCard();
                    // Runtime-created controls need their internal geometry scaled once.
                    if (scale != 1d) card.Scale(new SizeF((float)scale, (float)scale));
                    card.Width = (int)Math.Round(plan.CardWidth * scale);
                    card.Height = (int)Math.Round(238 * scale);
                    card.Margin = new Padding(0, 0, 0, (int)Math.Round(12 * scale));
                    card.CardClicked += (_, _) => SelectAccount(card.ProfileId);
                    _accountCards.Add(account.Id, card);
                    _accountsPanel.Controls.Add(card);
                }
                UpdateAccountCard(account, card);
                if (_accountsPanel.Controls.GetChildIndex(card) != index)
                    _accountsPanel.Controls.SetChildIndex(card, index);
            }

            if (_accounts.Count == 0 && _emptyAccountsMessage is null)
            {
                _emptyAccountsMessage = new Label
                {
                    Text = "添加一个 OpenAI 账号\n登录后即可查看剩余额度与 Token 用量",
                    AutoSize = false, Height = (int)Math.Round(90 * scale), Width = (int)Math.Round(plan.CardWidth * scale),
                    TextAlign = ContentAlignment.MiddleCenter, ForeColor = MutedColor, Font = new Font("Segoe UI", 10f),
                };
                _accountsPanel.Controls.Add(_emptyAccountsMessage);
            }
        }
        finally
        {
            if (structuralChange) _accountsPanel.ResumeLayout();
        }
        LayoutAccountCards();
        if (structuralChange && _accountsPanel.AutoScrollPosition != new Point(-scroll.X, -scroll.Y))
            _accountsPanel.AutoScrollPosition = scroll;
    }

    private void UpdateAccountCard(AccountProfile account, AccountUsageCard card)
    {
        _usage.TryGetValue(account.Id, out var state);
        var presentation = AccountUsagePresentation.FromState(state, DateTimeOffset.Now);
        card.Selected = account.Id == _selectedProfileId;
        card.Bind(account.Id, account.Label, presentation, FormatUsageState(state, presentation), account.Id == _activeProfileId, state?.Message);
    }

    private void SelectAccount(Guid profileId)
    {
        _selectedProfileId = profileId;
        foreach (var card in _accountsPanel.Controls.OfType<AccountUsageCard>())
        {
            card.Selected = card.ProfileId == profileId;
        }
    }

    private void ApplyDashboardLayout(DashboardLayoutPlan plan, Rectangle workingArea)
    {
        if (_initialLayoutApplied) return;
        var scale = DeviceDpi / 96d;
        _root.RowStyles[3].SizeType = SizeType.Percent;
        _root.RowStyles[3].Height = 100;
        MinimumSize = new Size((int)Math.Round(Math.Min(380, plan.WindowWidth) * scale), (int)Math.Round(Math.Min(600, plan.WindowHeight) * scale));
        if (WindowState != FormWindowState.Normal)
        {
            return;
        }
        _initialLayoutApplied = true;

        var center = new Point(Left + (Width / 2), Top + (Height / 2));
        Size = new Size((int)Math.Round(plan.WindowWidth * scale), (int)Math.Round(plan.WindowHeight * scale));
        Left = Math.Clamp(center.X - (Width / 2), workingArea.Left, Math.Max(workingArea.Left, workingArea.Right - Width));
        Top = Math.Clamp(center.Y - (Height / 2), workingArea.Top, Math.Max(workingArea.Top, workingArea.Bottom - Height));
    }

    private void LayoutAccountCards()
    {
        if (_sizingCards || _accountsPanel.ClientSize.Width <= 0) return;
        _sizingCards = true;
        try
        {
            var scale = DeviceDpi / 96d;
            var available = _accountsPanel.ClientSize.Width - (int)Math.Round(20 * scale);
            var width = Math.Max(1, available);
            foreach (var card in _accountCards.Values)
            {
                // Very narrow work areas can be smaller than the preferred card width.
                if (card.MinimumSize.Width > width) card.MinimumSize = new Size(width, card.MinimumSize.Height);
                if (card.Width != width) card.Width = width;
            }
        }
        finally { _sizingCards = false; }
    }

    private void RenderTokenOverview()
    {
        var aggregate = TokenUsageAggregate.Build(_accounts, _usage, DateTimeOffset.Now, _identities);
        var total = TokenUsageAggregate.FormatHundredMillions(aggregate.TotalLifetimeTokens).Replace(" 亿", string.Empty, StringComparison.Ordinal);
        var monthly = TokenUsageAggregate.FormatHundredMillions(aggregate.TotalMonthTokens).Replace(" 亿", string.Empty, StringComparison.Ordinal);
        var coverage = $"累计 {aggregate.IncludedAccountCount}/{aggregate.CoverageAccountCount}  ·  本月 {aggregate.MonthIncludedAccountCount}/{aggregate.CoverageAccountCount}  ·  服务统计可能延迟";
        if (aggregate.CachedCount > 0) coverage += $"  ·  {aggregate.CachedCount} 项缓存";
        var coverageDetail = aggregate.HasOverflow ? "Token 合计超出可表示范围，显示为未获取，请查看各账号详情。"
            : $"累计和本月分别按有数据的独立账号合计。未获取项不计为零。\n本月仅汇总服务返回的记录；详情可查看精确数量、日期和读取时间。\n身份未知账号：{aggregate.UnknownIdentityCount}；缓存账号：{aggregate.CachedCount}。";
        _tokenOverview.Bind(total, monthly, coverage, coverageDetail);
    }

    private static string FormatUsageState(UsageState? state, AccountUsagePresentation presentation)
    {
        if (state is null) return "尚未刷新";
        var cached = state.Snapshot is { } snapshot ? $" · 缓存 {snapshot.FetchedAt.LocalDateTime:MM-dd HH:mm}" : string.Empty;
        if (state.Status == UsageStatus.Refreshing) return "正在刷新…" + cached;
        if (state.Status == UsageStatus.AuthenticationRequired) return "登录需更新" + cached;
        if (state.Status == UsageStatus.Error) return "刷新失败" + cached;
        return presentation.StatusText;
    }

    private Task RefreshUsageAsync(bool showErrors)
    {
        if (_busy || _refreshing || _lifetime.IsCancellationRequested) return _refreshTask;
        _refreshTask = RefreshUsageCoreAsync(showErrors);
        return _refreshTask;
    }

    private async Task RefreshUsageCoreAsync(bool showErrors)
    {
        _refreshing = true;
        var sequence = ++_refreshSequence;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _refreshCancellation = cancellation;
        try
        {
            _refreshStatus.Text = "正在刷新…";
            _statusText.Text = "正在刷新额度，账号逐个更新…";
            var progress = new Progress<(Guid Id, UsageState State)>(update =>
            {
                if (IsDisposed || sequence != _refreshSequence || !_refreshing || _busy) return;
                // Leave the last visible values in place during background requests.
                // Only a completed account updates its existing card.
                if (update.State.Status == UsageStatus.Refreshing) return;
                var current = new Dictionary<Guid, UsageState>(_usage) { [update.Id] = update.State };
                _usage = current;
                var account = _accounts.FirstOrDefault(account => account.Id == update.Id);
                if (account is not null && _accountCards.TryGetValue(update.Id, out var card)) UpdateAccountCard(account, card);
                RenderTokenOverview();
            });
            _usage = await _services.Usage.RefreshAllAsync(cancellation.Token,
                (id, state) => ((IProgress<(Guid, UsageState)>)progress).Report((id, state)));
            cancellation.Token.ThrowIfCancellationRequested();
            // Reconcile external login/provider changes without replacing existing cards.
            await ReloadAsync();
            var succeeded = _accounts.Count(account => _usage.GetValueOrDefault(account.Id)?.Status == UsageStatus.Available);
            var tokenSucceeded = _accounts.Count(account => _usage.GetValueOrDefault(account.Id) is { Status: UsageStatus.Available, Snapshot.TokenUsage.Status: TokenUsageStatus.Available });
            var total = _accounts.Count;
            _refreshStatus.Text = total == 0 ? "无账号" : $"{DateTime.Now:HH:mm} · {succeeded}/{total}";
            _statusText.Text = total == 0 ? "请先登录账号" : $"额度 {succeeded}/{total}  ·  Token {tokenSucceeded}/{total}  ·  "
                + (succeeded == total && tokenSucceeded == total ? "刷新完成" : "未获取项保留缓存，可在 Token 详情查看原因");
            foreach (var account in _accounts)
            {
                if (_usage.GetValueOrDefault(account.Id) is { Status: not UsageStatus.Available, Message: { } message })
                    WriteLog($"额度刷新 [{account.Label}]", message);
                if (_usage.GetValueOrDefault(account.Id)?.Snapshot?.TokenUsage is { Status: TokenUsageStatus.Error, Message: { } tokenError })
                    WriteLog($"Token 刷新 [{account.Label}]", tokenError);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            _usage = await _services.Usage.LoadCachedAsync(CancellationToken.None);
            if (!IsDisposed) { _refreshStatus.Text = "刷新已取消"; RenderAccounts(); }
        }
        catch (Exception error)
        {
            _refreshStatus.Text = "刷新失败";
            if (showErrors) ShowError("额度刷新失败", error);
            else { WriteLog("额度刷新失败", error.Message); _statusText.Text = "额度刷新失败"; }
        }
        finally { _refreshing = false; _refreshCancellation = null; }
    }

    private async Task SwitchProviderAsync(Provider provider)
    {
        if (_busy || provider == _provider) return;
        var name = provider == Provider.OpenAI ? "OpenAI 官方" : "Sub2API";
        if (MessageBox.Show($"切换到 {name}？请先结束 Codex 中正在运行的任务。切换会正常退出并重新打开 Codex；退出超时将取消切换。", "切换连接", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
        await RunUiOperationAsync("切换连接", async () =>
        {
            if (provider == Provider.Sub2Api && !await _services.Sub2Api.IsOnlineAsync(_lifetime.Token))
                throw new InvalidOperationException("Sub2API 未在 127.0.0.1:8080 响应。");
            var source = await File.ReadAllTextAsync(_services.Paths.ConfigPath);
            _ = ProviderConfigEditor.Replace(source, provider);
            await CloseCodexAsync();
            try { await _services.ProviderSwitch.SwitchAsync(provider, _lifetime.Token); }
            catch (Exception error) when (error is not ProviderSwitchException { Stage: ProviderSwitchStage.Recovery })
            {
                try { await _services.Lifecycle.LaunchAsync(_lifetime.Token); }
                catch (Exception launchError) { WriteLog("恢复 Codex 窗口", launchError.Message); }
                throw;
            }
            await _services.Lifecycle.LaunchAsync(_lifetime.Token);
        });
    }

    private async Task SwitchSelectedAccountAsync()
    {
        var profile = SelectedProfile();
        if (profile is null || profile.Id == _activeProfileId) return;
        if (_provider != Provider.OpenAI) { MessageBox.Show("请先切换到 OpenAI 官方连接。", "Codex Switch"); return; }
        if (MessageBox.Show($"切换到账号“{profile.Label}”？\n\n确认后将自动关闭并重新打开 Codex。正在运行的任务会被中断；正常退出超时后将强制终止残留 Codex 进程，无需手动退出。", "切换账号", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
        await RunUiOperationAsync("切换账号", async () =>
        {
            await ApplyAccountAsync(profile.Id);
        });
    }

    private async Task LoginAsync(LoginMode mode)
    {
        var label = LoginPrompt.Show(this, mode == LoginMode.Browser ? "登录新账号" : "设备码登录", "账号名称：");
        if (label is null) return;
        await RunUiOperationAsync("登录账号", async () =>
        {
            var profile = await _services.Login.LoginAsync(label, mode, duplicate =>
                MessageBox.Show($"账号“{duplicate.Label}”已存在，是否更新凭据？", "重复账号", MessageBoxButtons.YesNo) == DialogResult.Yes, _lifetime.Token);
            _selectedProfileId = profile.Id;
            if (_provider == Provider.OpenAI
                && MessageBox.Show($"账号“{profile.Label}”登录成功。是否立即切换到该账号并重启 Codex？\n\n正在运行的任务会被中断；正常退出超时后将强制终止残留 Codex 进程，无需手动退出。", "登录完成", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
            {
                await ApplyAccountAsync(profile.Id, preserveTargetCredentials: true);
            }
            _usage = await _services.Usage.RefreshAllAsync(_lifetime.Token);
        });
    }

    private async Task ReauthenticateSelectedAccountAsync()
    {
        var profile = SelectedProfile();
        if (profile is null)
        {
            MessageBox.Show("请先选择要重新登录的账号。", "Codex Switch");
            return;
        }

        var restartNotice = _provider == Provider.OpenAI && profile.Id == _activeProfileId
            ? "\n\n这是当前账号，更新后将自动关闭并重新打开 Codex，正在运行的任务会被中断；正常退出超时后将强制终止残留 Codex 进程。"
            : "";
        if (MessageBox.Show($"将重新登录账号“{profile.Label}”。如果浏览器中选择了不同账号，更新会被拒绝。继续？{restartNotice}", "重新登录", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
        {
            return;
        }

        await RunUiOperationAsync("重新登录账号", async () =>
        {
            await _services.Login.ReauthenticateAsync(profile, LoginMode.Browser, _lifetime.Token);
            if (_provider == Provider.OpenAI && profile.Id == _activeProfileId)
            {
                await ApplyAccountAsync(profile.Id, preserveTargetCredentials: true);
            }
            _usage = await _services.Usage.RefreshAllAsync(_lifetime.Token);
        });
    }

    private async Task RenameSelectedAccountAsync()
    {
        var profile = SelectedProfile();
        if (profile is null) return;
        var name = LoginPrompt.Show(this, "重命名账号", "账号名称：", profile.Label);
        if (name is null) return;
        await RunUiOperationAsync("重命名账号", () => _services.Accounts.RenameAsync(profile.Id, name, _lifetime.Token));
    }

    private async Task RemoveSelectedAccountAsync()
    {
        var profile = SelectedProfile();
        if (profile is null) return;
        if (MessageBox.Show($"删除账号“{profile.Label}”？", "确认删除", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        await RunUiOperationAsync("删除账号", async () =>
        {
            await _services.Accounts.RemoveAsync(profile.Id, _lifetime.Token);
            _selectedProfileId = null;
        });
    }

    private AccountProfile? SelectedProfile() => _selectedProfileId is Guid id
        ? _accounts.FirstOrDefault(account => account.Id == id)
        : null;

    private async Task CloseCodexAsync(bool forceAfterTimeout = false)
    {
        if (forceAfterTimeout)
        {
            _statusText.Text = "正在关闭 Codex，必要时终止残留进程…";
            WriteLog("切换阶段", "已确认中断任务；正常退出超时后自动终止残留 Codex 进程");
            await CodexShutdownCoordinator.CloseAfterConfirmationAsync(_services.Lifecycle, _lifetime.Token);
            return;
        }
        var result = await _services.Lifecycle.RequestCloseAsync(TimeSpan.FromSeconds(20), _lifetime.Token);
        if (!result.Exited)
            throw new InvalidOperationException($"Codex 尚未完整退出，本次切换已取消。{result.FailureReason ?? "请在任务结束后手动退出 Codex，再重试切换。"}");
    }

    private async Task ApplyAccountAsync(Guid profileId, bool preserveTargetCredentials = false)
    {
        var switchId = Guid.NewGuid().ToString("N");
        WriteLog("切换阶段", $"{switchId} 验证目标账号");
        await _services.AccountSwitch.ValidateAsync(profileId, _lifetime.Token);
        WriteLog("切换阶段", $"{switchId} 请求 Codex 正常退出");
        await CloseCodexAsync(forceAfterTimeout: true);
        try
        {
            WriteLog("切换阶段", $"{switchId} Codex 已退出，开始写入账号");
            await _services.AccountSwitch.SwitchAsync(profileId, _lifetime.Token, preserveTargetCredentials);
            WriteLog("切换阶段", $"{switchId} 账号写入完成");
        }
        catch (Exception error) when (error is not AccountSwitchException { Stage: AccountSwitchStage.Recovery })
        {
            // The service has restored and verified the old login. Reopen it so a failed
            // transaction does not unnecessarily leave the user's desktop closed.
            try { await _services.Lifecycle.LaunchAsync(_lifetime.Token); }
            catch (Exception launchError) { WriteLog("恢复 Codex 窗口", launchError.Message); }
            throw;
        }
        try
        {
            WriteLog("切换阶段", $"{switchId} 启动 Codex");
            await _services.Lifecycle.LaunchAsync(_lifetime.Token);
            WriteLog("切换阶段", $"{switchId} 切换完成");
        }
        catch (Exception error)
        {
            throw new InvalidOperationException("账号凭据已切换，但未能确认 Codex 窗口打开。请从开始菜单打开 Codex。", error);
        }
    }

    private Task RunUiOperationAsync(string name, Func<Task> action)
    {
        if (_busy || _lifetime.IsCancellationRequested) return Task.CompletedTask;
        _operationTask = RunUiOperationCoreAsync(name, action);
        return _operationTask;
    }

    private async Task RunUiOperationCoreAsync(string name, Func<Task> action)
    {
        try
        {
            WriteLog("操作阶段", $"{name} 确认通过，准备锁定界面");
            SetBusy(true, $"正在{name}…");
            WriteLog("操作阶段", $"{name} 暂停并等待后台刷新");
            _refreshCancellation?.Cancel();
            await _refreshTask;
            _lifetime.Token.ThrowIfCancellationRequested();
            WriteLog("操作阶段", $"{name} 开始执行");
            await action();
            if (_lifetime.IsCancellationRequested || IsDisposed) return;
            await ReloadAsync();
            _statusText.Text = $"{name}完成";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) { if (!IsDisposed) ShowError(name, error); }
        finally { if (!IsDisposed) SetBusy(false); }
    }

    private void SetBusy(bool busy, string? status = null)
    {
        _busy = busy;
        if (IsDisposed || Disposing) return;
        if (status is not null) _statusText.Text = status;
        UseWaitCursor = busy;
        foreach (var button in _actionButtons)
            if (!button.IsDisposed) button.Enabled = !busy;
    }

    private void ToggleStartup()
    {
        if (_syncingStartup) return;
        try
        {
            if (_startup.Checked) _services.Startup.Enable();
            else _services.Startup.Disable();
            _statusText.Text = _startup.Checked ? "已开启开机自启" : "已关闭开机自启";
        }
        catch (Exception error) { ShowError("开机自启", error); }
    }

    private Provider ReadProvider()
    {
        try { return ProviderConfigEditor.Read(File.ReadAllText(_services.Paths.ConfigPath)); }
        catch { return Provider.OpenAI; }
    }

    private async Task<Guid?> ReconcileActiveProfileAsync()
    {
        if (_provider != Provider.OpenAI || !File.Exists(_services.Paths.AuthPath))
        {
            return await _services.Accounts.GetActiveProfileIdAsync(_lifetime.Token);
        }

        try
        {
            var liveAuth = await File.ReadAllBytesAsync(_services.Paths.AuthPath);
            return await _services.Accounts.ReconcileActiveProfileAsync(liveAuth, _lifetime.Token);
        }
        catch (Exception error) when (error is AuthBlobException or IOException or UnauthorizedAccessException)
        {
            return await _services.Accounts.GetActiveProfileIdAsync(_lifetime.Token);
        }
    }

    private void OpenLogs()
    {
        Directory.CreateDirectory(_services.Paths.LogDirectory);
        Process.Start(new ProcessStartInfo("explorer.exe", _services.Paths.LogDirectory) { UseShellExecute = true });
    }

    private async Task ImportLiveAccountIfNeededAsync()
    {
        if ((await _services.Accounts.ListAsync(_lifetime.Token)).Count != 0 || !File.Exists(_services.Paths.AuthPath)) return;
        try
        {
            var bytes = await File.ReadAllBytesAsync(_services.Paths.AuthPath);
            _ = AuthBlob.Parse(bytes);
            if (MessageBox.Show("检测到当前 Codex 登录，是否导入为“默认账号”？", "Codex Switch", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                await new AccountImportService(_services.Accounts).ImportAsync("默认账号", bytes, false, _lifetime.Token);
        }
        catch (AuthBlobException) { }
    }

    private void ShowError(string title, Exception error)
    {
        if (IsDisposed || _lifetime.IsCancellationRequested) return;
        var safe = SecretRedactor.Redact(error.Message);
        WriteLog(title, error.ToString());
        _ = UiExceptionGuard.TryAppend(_services.Paths.LogDirectory, title, error);
        _statusText.Text = $"{title}失败";
        try
        {
            if (_errorPresenter is not null) _errorPresenter(title, error);
            else MessageBox.Show(this, safe, title, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch (Exception reportingError) { _ = UiExceptionGuard.TryAppend(_services.Paths.LogDirectory, "显示错误失败", reportingError); }
    }

    private void WriteLog(string title, string message)
    {
        try
        {
            Directory.CreateDirectory(_services.Paths.LogDirectory);
            File.AppendAllText(Path.Combine(_services.Paths.LogDirectory, "codex-switch.log"), $"{DateTimeOffset.Now:O} {title}: {SecretRedactor.Redact(message)}{Environment.NewLine}");
        }
        catch { /* Logging must never turn a recoverable switch error into a UI fault. */ }
    }

    private void AddAction(TableLayoutPanel layout, string text, Func<Task> action, int column, int row)
        => layout.Controls.Add(ActionButton(text, action, fullWidth: true), column, row);

    private Button ActionButton(string text, Func<Task> action, bool fullWidth = false)
    {
        var button = new DashboardButton
        {
            Text = text,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = fullWidth ? DockStyle.Fill : DockStyle.None,
            MinimumSize = new Size(0, 34),
            Width = 112,
            BackColor = AppTheme.Surface,
            ForeColor = TextColor,
            Font = new Font("Segoe UI", 9f),
            TextAlign = ContentAlignment.MiddleCenter,
            Margin = new Padding(3),
        };
        button.Click += (_, _) => _ = RunUiEventAsync(text, action);
        _actionButtons.Add(button);
        return button;
    }

    private static RoundedPanel CardPanel() => new()
    {
        Dock = DockStyle.Top,
        AutoSize = true,
        BackColor = AppTheme.Surface,
        BorderColor = AppTheme.Border,
        CornerRadius = 14,
        Margin = Padding.Empty,
    };

    private static Label StatusLabel() => new()
    {
        AutoSize = true,
        Font = new Font("Segoe UI", 8.5f),
        ForeColor = MutedColor,
        Margin = new Padding(0, 2, 0, 2),
    };
}
