using System.Diagnostics;
using CodexSwitch.App.Accounts;
using CodexSwitch.Core.Auth;
using CodexSwitch.Core.Codex;
using CodexSwitch.Core.Configuration;
using CodexSwitch.Core.Diagnostics;
using CodexSwitch.Core.Usage;

namespace CodexSwitch.App;

public sealed class MainForm : Form
{
    private readonly AppServices _services;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 5 * 60 * 1000 };
    private readonly Label _providerStatus = new() { AutoSize = true, Font = new Font("Segoe UI", 11, FontStyle.Bold) };
    private readonly Label _sub2Status = new() { AutoSize = true };
    private readonly DataGridView _accountsGrid = new();
    private readonly CheckBox _startup = new() { Text = "登录 Windows 时自动启动", AutoSize = true };
    private readonly ToolStripStatusLabel _statusText = new() { Text = "就绪" };
    private IReadOnlyList<AccountProfile> _accounts = [];
    private IReadOnlyDictionary<Guid, UsageState> _usage = new Dictionary<Guid, UsageState>();
    private Guid? _activeProfileId;
    private Provider _provider = Provider.OpenAI;
    private bool _busy;
    private bool _syncingStartup;

    public MainForm(AppServices services, bool startMinimized)
    {
        _services = services;
        Text = "Codex Switch"; Width = 980; Height = 640; MinimumSize = new Size(820, 520);
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? Icon;
        StartPosition = FormStartPosition.CenterScreen; Font = new Font("Segoe UI", 9F);
        if (startMinimized) WindowState = FormWindowState.Minimized;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(18) };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var title = new Label { Text = "Codex Switch", AutoSize = true, Font = new Font("Segoe UI", 22, FontStyle.Bold), Margin = new Padding(0, 0, 0, 12) };
        root.Controls.Add(title, 0, 0);
        root.Controls.Add(BuildProviderPanel(), 0, 1);
        root.Controls.Add(BuildAccountsPanel(), 0, 2);
        root.Controls.Add(BuildFooterPanel(), 0, 3);

        var status = new StatusStrip();
        status.Items.Add(_statusText);
        Controls.Add(root);
        Controls.Add(status);

        Shown += async (_, _) => await InitializeAsync();
        _timer.Tick += async (_, _) => await RefreshUsageAsync(false);
        _timer.Start();
        FormClosed += (_, _) => _timer.Dispose();
    }

    private Control BuildProviderPanel()
    {
        var group = new GroupBox { Text = "连接", Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(14) };
        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        flow.Controls.Add(_providerStatus);
        flow.Controls.Add(new Label { Text = "   ", AutoSize = true });
        flow.Controls.Add(_sub2Status);
        flow.Controls.Add(new Label { Text = "   ", AutoSize = true });
        flow.Controls.Add(MakeButton("切换到 OpenAI 官方", async (_, _) => await SwitchProviderAsync(Provider.OpenAI)));
        flow.Controls.Add(MakeButton("切换到 Sub2API", async (_, _) => await SwitchProviderAsync(Provider.Sub2Api)));
        group.Controls.Add(flow);
        return group;
    }

    private Control BuildAccountsPanel()
    {
        var group = new GroupBox { Text = "OpenAI 账号与额度", Dock = DockStyle.Fill, Padding = new Padding(10), Margin = new Padding(0, 14, 0, 14) };
        _accountsGrid.Dock = DockStyle.Fill; _accountsGrid.ReadOnly = true; _accountsGrid.AllowUserToAddRows = false;
        _accountsGrid.AllowUserToDeleteRows = false; _accountsGrid.MultiSelect = false; _accountsGrid.RowHeadersVisible = false;
        _accountsGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect; _accountsGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _accountsGrid.Columns.Add("account", "账号");
        _accountsGrid.Columns.Add("active", "当前");
        _accountsGrid.Columns.Add("plan", "套餐");
        _accountsGrid.Columns.Add("primary", "主额度");
        _accountsGrid.Columns.Add("secondary", "次额度");
        _accountsGrid.Columns.Add("credits", "重置卡");
        _accountsGrid.Columns.Add("state", "状态");

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(0, 8, 0, 0) };
        buttons.Controls.Add(MakeButton("登录新账号", async (_, _) => await LoginAsync(LoginMode.Browser)));
        buttons.Controls.Add(MakeButton("设备码登录", async (_, _) => await LoginAsync(LoginMode.DeviceCode)));
        buttons.Controls.Add(MakeButton("切换账号", async (_, _) => await SwitchSelectedAccountAsync()));
        buttons.Controls.Add(MakeButton("重命名", async (_, _) => await RenameSelectedAccountAsync()));
        buttons.Controls.Add(MakeButton("删除", async (_, _) => await RemoveSelectedAccountAsync()));
        buttons.Controls.Add(MakeButton("刷新额度", async (_, _) => await RefreshUsageAsync(true)));
        group.Controls.Add(_accountsGrid);
        group.Controls.Add(buttons);
        return group;
    }

    private Control BuildFooterPanel()
    {
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = true };
        panel.Controls.Add(MakeButton("打开 Codex", async (_, _) => await RunUiOperationAsync("打开 Codex", () => _services.Lifecycle.LaunchAsync(default))));
        panel.Controls.Add(MakeButton("打开日志目录", (_, _) => { OpenLogs(); return Task.CompletedTask; }));
        _startup.CheckedChanged += (_, _) => ToggleStartup();
        panel.Controls.Add(_startup);
        return panel;
    }

    private static Button MakeButton(string text, Func<object?, EventArgs, Task> handler)
    {
        var button = new Button { Text = text, AutoSize = true, Padding = new Padding(8, 3, 8, 3), Margin = new Padding(0, 0, 8, 0) };
        button.Click += async (sender, args) => await handler(sender, args);
        return button;
    }

    private async Task InitializeAsync()
    {
        await ImportLiveAccountIfNeededAsync();
        await RefreshUsageAsync(false);
    }

    private async Task ReloadAsync()
    {
        _provider = ReadProvider();
        _activeProfileId = await _services.Accounts.GetActiveProfileIdAsync(default);
        _accounts = await _services.Accounts.ListAsync(default);
        var sub2Online = await _services.Sub2Api.IsOnlineAsync(default);
        _providerStatus.Text = $"当前连接：{(_provider == Provider.OpenAI ? "OpenAI 官方" : "Sub2API")}";
        _sub2Status.Text = $"Sub2API：{(sub2Online ? "在线" : "离线")}";
        _syncingStartup = true;
        _startup.Checked = _services.Startup.IsEnabled;
        _syncingStartup = false;
        RenderAccounts();
    }

    private void RenderAccounts()
    {
        _accountsGrid.Rows.Clear();
        foreach (var account in _accounts)
        {
            _usage.TryGetValue(account.Id, out var state);
            var snapshot = state?.Snapshot;
            var row = _accountsGrid.Rows[_accountsGrid.Rows.Add(
                account.Label,
                _activeProfileId == account.Id ? "●" : "",
                snapshot?.PlanType ?? "—",
                FormatPercent(snapshot?.PrimaryUsedPercent),
                FormatPercent(snapshot?.SecondaryUsedPercent),
                snapshot?.ResetCreditsAvailable?.ToString() ?? "—",
                FormatUsageState(state))];
            row.Tag = account.Id;
        }
    }

    private static string FormatPercent(int? value) => value is int percent ? $"已用 {percent}%" : "—";

    private static string FormatUsageState(UsageState? state)
    {
        if (state is null) return "尚未刷新";
        if (state.Status == UsageStatus.Refreshing) return "刷新中…";
        if (state.Status == UsageStatus.AuthenticationRequired) return "登录已失效";
        if (state.Status == UsageStatus.Error) return state.Message ?? "刷新失败";
        if (state.Snapshot is not { } snapshot) return "不可用";
        var reset = snapshot.PrimaryResetAt is { } time ? $" · 主额度重置 {time.LocalDateTime:g}" : string.Empty;
        return $"正常{reset}";
    }

    private async Task RefreshUsageAsync(bool showErrors)
    {
        if (_busy) return;
        try
        {
            SetBusy(true, "正在刷新额度…");
            _usage = await _services.Usage.RefreshAllAsync(default);
            await ReloadAsync();
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
        var label = LoginPrompt.Show(this, "登录新账号", "账号名称：");
        if (label is null) return;
        await RunUiOperationAsync("登录账号", async () =>
        {
            await _services.Login.LoginAsync(label, mode, duplicate =>
                MessageBox.Show($"账号“{duplicate.Label}”已存在，是否更新凭据？", "重复账号", MessageBoxButtons.YesNo) == DialogResult.Yes, default);
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
        await RunUiOperationAsync("删除账号", () => _services.Accounts.RemoveAsync(profile.Id, default));
    }

    private AccountProfile? SelectedProfile()
    {
        if (_accountsGrid.SelectedRows.Count == 0 || _accountsGrid.SelectedRows[0].Tag is not Guid id) return null;
        return _accounts.FirstOrDefault(account => account.Id == id);
    }

    private async Task<bool> CloseCodexAsync()
    {
        var result = await _services.Lifecycle.RequestCloseAsync(TimeSpan.FromSeconds(15), default);
        if (result.Exited) return true;
        if (MessageBox.Show("Codex 未能正常退出，是否强制结束已捕获的进程？", "Codex Switch", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return false;
        if (!await _services.Lifecycle.ForceCloseCapturedAsync(default))
            throw new InvalidOperationException("无法结束 Codex 进程。");
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
        File.AppendAllText(Path.Combine(_services.Paths.LogDirectory, "codex-switch.log"),
            $"{DateTimeOffset.Now:O} {title}: {safe}{Environment.NewLine}");
        _statusText.Text = $"{title}失败";
        MessageBox.Show(safe, title, MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
}
