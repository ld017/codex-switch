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
    private static readonly Color WindowColor = Color.FromArgb(245, 247, 250);
    private static readonly Color TextColor = Color.FromArgb(39, 43, 49);
    private static readonly Color MutedColor = Color.FromArgb(112, 119, 129);
    private readonly AppServices _services;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 5 * 60 * 1000 };
    private readonly Label _providerStatus = StatusLabel();
    private readonly Label _sub2Status = StatusLabel();
    private readonly Label _refreshStatus = new() { AutoSize = true, ForeColor = MutedColor, Font = new Font("Segoe UI", 8.5f) };
    private readonly FlowLayoutPanel _accountsPanel = new()
    {
        Dock = DockStyle.Fill,
        AutoScroll = true,
        FlowDirection = FlowDirection.TopDown,
        WrapContents = false,
        BackColor = WindowColor,
        Padding = new Padding(0, 0, 4, 0),
    };
    private readonly CheckBox _startup = new() { Text = "登录 Windows 时自动启动", AutoSize = true, Font = new Font("Segoe UI", 10f) };
    private readonly ToolStripStatusLabel _statusText = new() { Text = "就绪" };
    private readonly List<Button> _actionButtons = [];
    private IReadOnlyList<AccountProfile> _accounts = [];
    private IReadOnlyDictionary<Guid, UsageState> _usage = new Dictionary<Guid, UsageState>();
    private Guid? _activeProfileId;
    private Guid? _selectedProfileId;
    private Provider _provider = Provider.OpenAI;
    private bool _busy;
    private bool _syncingStartup;

    public MainForm(AppServices services, bool startMinimized)
    {
        _services = services;
        Text = "Codex Switch";
        Width = 560;
        Height = 820;
        MinimumSize = new Size(500, 640);
        BackColor = WindowColor;
        ForeColor = TextColor;
        Font = new Font("Segoe UI", 9f);
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? Icon;
        StartPosition = FormStartPosition.CenterScreen;
        if (startMinimized) WindowState = FormWindowState.Minimized;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(20, 16, 20, 10),
            BackColor = WindowColor,
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(BuildHeader(), 0, 0);
        root.Controls.Add(BuildConnectionPanel(), 0, 1);
        root.Controls.Add(BuildAccountHeading(), 0, 2);
        root.Controls.Add(_accountsPanel, 0, 3);
        root.Controls.Add(BuildActionsPanel(), 0, 4);

        var status = new StatusStrip { SizingGrip = false, BackColor = Color.White };
        status.Items.Add(_statusText);
        Controls.Add(root);
        Controls.Add(status);

        _accountsPanel.SizeChanged += (_, _) => ResizeAccountCards();
        Shown += async (_, _) => await InitializeAsync();
        _timer.Tick += async (_, _) => await RefreshUsageAsync(false);
        _timer.Start();
        FormClosed += (_, _) => _timer.Dispose();
    }

    private Control BuildHeader()
    {
        var titleArea = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(4, 0, 0, 12) };
        titleArea.Controls.Add(new Label { Text = "Codex Switch", AutoSize = true, Font = new Font("Segoe UI", 18f, FontStyle.Bold), ForeColor = TextColor });
        titleArea.Controls.Add(new Label { Text = "Provider 与 OpenAI 账号控制台", AutoSize = true, Font = new Font("Segoe UI", 9f), ForeColor = MutedColor });
        return titleArea;
    }

    private Control BuildConnectionPanel()
    {
        var card = CardPanel();
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1, Padding = new Padding(16, 12, 16, 12) };
        layout.Controls.Add(_providerStatus);
        layout.Controls.Add(_sub2Status);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Margin = new Padding(0, 8, 0, 0) };
        buttons.Controls.Add(ActionButton("切换到 OpenAI 官方", async () => await SwitchProviderAsync(Provider.OpenAI)));
        buttons.Controls.Add(ActionButton("切换到 Sub2API", async () => await SwitchProviderAsync(Provider.Sub2Api)));
        layout.Controls.Add(buttons);
        card.Controls.Add(layout);
        return card;
    }

    private Control BuildAccountHeading()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Margin = new Padding(0, 18, 0, 8) };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.Controls.Add(new Label { Text = "OpenAI 账号额度", AutoSize = true, Font = new Font("Segoe UI", 12f, FontStyle.Bold), ForeColor = TextColor }, 0, 0);
        panel.Controls.Add(_refreshStatus, 1, 0);
        return panel;
    }

    private Control BuildActionsPanel()
    {
        var card = CardPanel();
        card.Margin = new Padding(0, 10, 0, 0);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Padding = new Padding(12) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        AddAction(layout, "刷新账号额度", () => RefreshUsageAsync(true), 0, 0);
        AddAction(layout, "登录新账号", () => LoginAsync(LoginMode.Browser), 1, 0);
        AddAction(layout, "设备码登录", () => LoginAsync(LoginMode.DeviceCode), 0, 1);
        AddAction(layout, "重新登录选中账号", ReauthenticateSelectedAccountAsync, 1, 1);
        AddAction(layout, "切换选中账号", SwitchSelectedAccountAsync, 0, 2);
        AddAction(layout, "重命名", RenameSelectedAccountAsync, 1, 2);
        AddAction(layout, "删除账号", RemoveSelectedAccountAsync, 0, 3);
        AddAction(layout, "打开 Codex", () => RunUiOperationAsync("打开 Codex", () => _services.Lifecycle.LaunchAsync(default)), 1, 3);
        AddAction(layout, "打开日志目录", () => { OpenLogs(); return Task.CompletedTask; }, 0, 4);

        _startup.CheckedChanged += (_, _) => ToggleStartup();
        _startup.Margin = new Padding(8, 10, 8, 4);
        layout.Controls.Add(_startup, 0, 5);
        layout.SetColumnSpan(_startup, 2);
        card.Controls.Add(layout);
        return card;
    }

    private async Task InitializeAsync()
    {
        await ImportLiveAccountIfNeededAsync();
        await RefreshUsageAsync(false);
    }

    private async Task ReloadAsync()
    {
        _provider = ReadProvider();
        _activeProfileId = await ReconcileActiveProfileAsync();
        _accounts = await _services.Accounts.ListAsync(default);
        _selectedProfileId ??= _activeProfileId ?? _accounts.FirstOrDefault()?.Id;
        var sub2Online = await _services.Sub2Api.IsOnlineAsync(default);
        _providerStatus.Text = $"当前连接：{(_provider == Provider.OpenAI ? "OpenAI 官方" : "Sub2API")}";
        _sub2Status.Text = $"Sub2API 服务：{(sub2Online ? "在线" : "离线")}";
        _syncingStartup = true;
        _startup.Checked = _services.Startup.IsEnabled;
        _syncingStartup = false;
        RenderAccounts();
    }

    private void RenderAccounts()
    {
        _accountsPanel.SuspendLayout();
        _accountsPanel.Controls.Clear();
        foreach (var account in _accounts)
        {
            _usage.TryGetValue(account.Id, out var state);
            var presentation = AccountUsagePresentation.From(state?.Snapshot, DateTimeOffset.Now);
            var card = new AccountUsageCard { Selected = account.Id == _selectedProfileId };
            card.Bind(account.Id, account.Label, presentation, FormatUsageState(state, presentation), account.Id == _activeProfileId);
            card.CardClicked += (_, _) => SelectAccount(card.ProfileId);
            _accountsPanel.Controls.Add(card);
        }

        if (_accounts.Count == 0)
        {
            _accountsPanel.Controls.Add(new Label
            {
                Text = "还没有已管理账号\n点击下方“登录新账号”开始",
                AutoSize = false,
                Height = 90,
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = MutedColor,
                Font = new Font("Segoe UI", 10f),
            });
        }
        ResizeAccountCards();
        _accountsPanel.ResumeLayout();
    }

    private void SelectAccount(Guid profileId)
    {
        _selectedProfileId = profileId;
        foreach (var card in _accountsPanel.Controls.OfType<AccountUsageCard>())
        {
            card.Selected = card.ProfileId == profileId;
        }
    }

    private void ResizeAccountCards()
    {
        var width = Math.Max(280, _accountsPanel.ClientSize.Width - _accountsPanel.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth - 2);
        foreach (Control control in _accountsPanel.Controls)
        {
            control.Width = width;
        }
    }

    private static string FormatUsageState(UsageState? state, AccountUsagePresentation presentation)
    {
        if (state is null) return "尚未刷新";
        if (state.Status == UsageStatus.Refreshing) return "正在刷新…";
        if (state.Status == UsageStatus.AuthenticationRequired) return "登录已失效，请重新登录";
        if (state.Status == UsageStatus.Error) return "刷新失败";
        return presentation.StatusText;
    }

    private async Task RefreshUsageAsync(bool showErrors)
    {
        if (_busy) return;
        try
        {
            SetBusy(true, "正在刷新额度…");
            _usage = await _services.Usage.RefreshAllAsync(default);
            await ReloadAsync();
            _refreshStatus.Text = $"刚刚更新 · {DateTime.Now:HH:mm}";
            _statusText.Text = "额度已刷新";
        }
        catch (Exception error)
        {
            if (showErrors) ShowError("额度刷新失败", error);
            else _statusText.Text = "额度刷新失败";
        }
        finally { SetBusy(false); }
    }

    private async Task SwitchProviderAsync(Provider provider)
    {
        if (_busy || provider == _provider) return;
        var name = provider == Provider.OpenAI ? "OpenAI 官方" : "Sub2API";
        if (MessageBox.Show($"切换到 {name}？Codex 将退出并重新打开。", "切换连接", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
        await RunUiOperationAsync("切换连接", async () =>
        {
            if (provider == Provider.Sub2Api && !await _services.Sub2Api.IsOnlineAsync(default))
                throw new InvalidOperationException("Sub2API 未在 127.0.0.1:8080 响应。");
            if (!await CloseCodexAsync()) return;
            await _services.ProviderSwitch.SwitchAsync(provider, default);
            await _services.Lifecycle.LaunchAsync(default);
        });
    }

    private async Task SwitchSelectedAccountAsync()
    {
        var profile = SelectedProfile();
        if (profile is null || profile.Id == _activeProfileId) return;
        if (_provider != Provider.OpenAI) { MessageBox.Show("请先切换到 OpenAI 官方连接。", "Codex Switch"); return; }
        if (MessageBox.Show($"切换到账号“{profile.Label}”？Codex 将退出并重新打开。", "切换账号", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
        await RunUiOperationAsync("切换账号", async () =>
        {
            if (!await CloseCodexAsync()) return;
            await _services.AccountSwitch.SwitchAsync(profile.Id, default);
            await _services.Lifecycle.LaunchAsync(default);
        });
    }

    private async Task LoginAsync(LoginMode mode)
    {
        var label = LoginPrompt.Show(this, mode == LoginMode.Browser ? "登录新账号" : "设备码登录", "账号名称：");
        if (label is null) return;
        await RunUiOperationAsync("登录账号", async () =>
        {
            var profile = await _services.Login.LoginAsync(label, mode, duplicate =>
                MessageBox.Show($"账号“{duplicate.Label}”已存在，是否更新凭据？", "重复账号", MessageBoxButtons.YesNo) == DialogResult.Yes, default);
            _selectedProfileId = profile.Id;
            if (_provider == Provider.OpenAI
                && MessageBox.Show($"账号“{profile.Label}”登录成功。是否立即切换到该账号并重启 Codex？", "登录完成", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                if (await CloseCodexAsync())
                {
                    await _services.AccountSwitch.SwitchAsync(profile.Id, default);
                    await _services.Lifecycle.LaunchAsync(default);
                }
            }
            _usage = await _services.Usage.RefreshAllAsync(default);
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

        if (MessageBox.Show($"将重新登录账号“{profile.Label}”。如果浏览器中选择了不同账号，更新会被拒绝。继续？", "重新登录", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK)
        {
            return;
        }

        await RunUiOperationAsync("重新登录账号", async () =>
        {
            await _services.Login.ReauthenticateAsync(profile, LoginMode.Browser, default);
            if (_provider == Provider.OpenAI && await CloseCodexAsync())
            {
                await _services.AccountSwitch.SwitchAsync(profile.Id, default);
                await _services.Lifecycle.LaunchAsync(default);
            }
            _usage = await _services.Usage.RefreshAllAsync(default);
        });
    }

    private async Task RenameSelectedAccountAsync()
    {
        var profile = SelectedProfile();
        if (profile is null) return;
        var name = LoginPrompt.Show(this, "重命名账号", "账号名称：", profile.Label);
        if (name is null) return;
        await RunUiOperationAsync("重命名账号", () => _services.Accounts.RenameAsync(profile.Id, name, default));
    }

    private async Task RemoveSelectedAccountAsync()
    {
        var profile = SelectedProfile();
        if (profile is null) return;
        if (MessageBox.Show($"删除账号“{profile.Label}”？", "确认删除", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        await RunUiOperationAsync("删除账号", async () =>
        {
            await _services.Accounts.RemoveAsync(profile.Id, default);
            _selectedProfileId = null;
        });
    }

    private AccountProfile? SelectedProfile() => _selectedProfileId is Guid id
        ? _accounts.FirstOrDefault(account => account.Id == id)
        : null;

    private async Task<bool> CloseCodexAsync()
    {
        var result = await _services.Lifecycle.RequestCloseAsync(TimeSpan.FromSeconds(15), default);
        if (result.Exited) return true;
        if (MessageBox.Show("Codex 未能正常退出，是否强制结束已捕获的进程？", "Codex Switch", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return false;
        if (!await _services.Lifecycle.ForceCloseCapturedAsync(default)) throw new InvalidOperationException("无法结束 Codex 进程。");
        return true;
    }

    private async Task RunUiOperationAsync(string name, Func<Task> action)
    {
        if (_busy) return;
        try
        {
            SetBusy(true, $"正在{name}…");
            await action();
            await ReloadAsync();
            _statusText.Text = $"{name}完成";
        }
        catch (Exception error) { ShowError(name, error); }
        finally { SetBusy(false); }
    }

    private void SetBusy(bool busy, string? status = null)
    {
        _busy = busy;
        if (status is not null) _statusText.Text = status;
        UseWaitCursor = busy;
        foreach (var button in _actionButtons) button.Enabled = !busy;
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
            return await _services.Accounts.GetActiveProfileIdAsync(default);
        }

        try
        {
            var liveAuth = await File.ReadAllBytesAsync(_services.Paths.AuthPath);
            return await _services.Accounts.ReconcileActiveProfileAsync(liveAuth, default);
        }
        catch (Exception error) when (error is AuthBlobException or IOException or UnauthorizedAccessException)
        {
            return await _services.Accounts.GetActiveProfileIdAsync(default);
        }
    }

    private void OpenLogs()
    {
        Directory.CreateDirectory(_services.Paths.LogDirectory);
        Process.Start(new ProcessStartInfo("explorer.exe", _services.Paths.LogDirectory) { UseShellExecute = true });
    }

    private async Task ImportLiveAccountIfNeededAsync()
    {
        if ((await _services.Accounts.ListAsync(default)).Count != 0 || !File.Exists(_services.Paths.AuthPath)) return;
        try
        {
            var bytes = await File.ReadAllBytesAsync(_services.Paths.AuthPath);
            _ = AuthBlob.Parse(bytes);
            if (MessageBox.Show("检测到当前 Codex 登录，是否导入为“默认账号”？", "Codex Switch", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                await new AccountImportService(_services.Accounts).ImportAsync("默认账号", bytes, false, default);
        }
        catch (AuthBlobException) { }
    }

    private void ShowError(string title, Exception error)
    {
        var safe = SecretRedactor.Redact(error.Message);
        Directory.CreateDirectory(_services.Paths.LogDirectory);
        File.AppendAllText(Path.Combine(_services.Paths.LogDirectory, "codex-switch.log"), $"{DateTimeOffset.Now:O} {title}: {safe}{Environment.NewLine}");
        _statusText.Text = $"{title}失败";
        MessageBox.Show(safe, title, MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    private void AddAction(TableLayoutPanel layout, string text, Func<Task> action, int column, int row)
        => layout.Controls.Add(ActionButton(text, action, fullWidth: true), column, row);

    private Button ActionButton(string text, Func<Task> action, bool fullWidth = false)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = !fullWidth,
            Dock = fullWidth ? DockStyle.Fill : DockStyle.None,
            Height = 38,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.White,
            ForeColor = TextColor,
            Font = new Font("Segoe UI", 9.5f),
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(10, 0, 8, 0),
            Margin = new Padding(4),
        };
        button.FlatAppearance.BorderColor = Color.FromArgb(218, 223, 230);
        button.FlatAppearance.MouseOverBackColor = Color.FromArgb(235, 243, 250);
        button.Click += async (_, _) => await action();
        _actionButtons.Add(button);
        return button;
    }

    private static RoundedPanel CardPanel() => new()
    {
        Dock = DockStyle.Top,
        AutoSize = true,
        BackColor = Color.White,
        CornerRadius = 10,
        Margin = Padding.Empty,
    };

    private static Label StatusLabel() => new()
    {
        AutoSize = true,
        Font = new Font("Segoe UI", 10f),
        ForeColor = MutedColor,
        Margin = new Padding(0, 2, 0, 2),
    };
}
