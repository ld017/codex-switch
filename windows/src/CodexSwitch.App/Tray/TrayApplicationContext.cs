using System.Diagnostics;
using CodexSwitch.App.Accounts;
using CodexSwitch.Core.Auth;
using CodexSwitch.Core.Codex;
using CodexSwitch.Core.Configuration;
using CodexSwitch.Core.Diagnostics;
using CodexSwitch.Core.Presentation;
using CodexSwitch.Core.Usage;

namespace CodexSwitch.App.Tray;

public sealed class TrayApplicationContext : ApplicationContext
{
    private readonly AppServices _services;
    private readonly NotifyIcon _icon;
    private readonly OperationCoordinator _operations = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 5 * 60 * 1000 };
    private readonly SemaphoreSlim _menuGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private bool _exiting;
    private IReadOnlyDictionary<Guid, UsageState> _usage = new Dictionary<Guid, UsageState>();

    public TrayApplicationContext(AppServices services)
    {
        _services = services;
        _icon = new NotifyIcon { Text = "Codex Switch", Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application, Visible = true, ContextMenuStrip = new ContextMenuStrip() };
        _icon.ContextMenuStrip.Opening += (_, _) => _ = RunUiEventAsync("刷新托盘菜单", RefreshMenuAsync);
        _timer.Tick += (_, _) => _ = RunUiEventAsync("后台刷新", () => RefreshUsageAsync(false));
        _timer.Start();
        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        try { await ImportLiveAccountIfNeededAsync(); await RefreshUsageAsync(false); await RefreshMenuAsync(); }
        catch (Exception error) { ShowError("初始化失败", error); }
    }

    private async Task RefreshMenuAsync()
    {
        if (_exiting) return;
        await _menuGate.WaitAsync(_lifetime.Token);
        try
        {
            var menu = _icon.ContextMenuStrip;
            if (_exiting || menu is null || menu.IsDisposed) return;
            var provider = ReadProvider();
            var model = TrayMenuModel.Build(new AppSnapshot(provider, await _services.Sub2Api.IsOnlineAsync(default),
                await _services.Accounts.GetActiveProfileIdAsync(default), await _services.Accounts.ListAsync(default), _usage, _operations.IsBusy));
            if (_exiting || menu.IsDisposed) return;
            menu.Items.Clear();
        menu.Items.Add(new ToolStripMenuItem("Codex Switch") { Enabled = false });
        menu.Items.Add(new ToolStripMenuItem($"当前连接：{(provider == Provider.OpenAI ? "OpenAI 官方" : "Sub2API")}") { Enabled = false });
        menu.Items.Add(new ToolStripMenuItem(model.Sub2ApiStatus) { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());
        foreach (var item in model.ProviderItems)
        {
            var ui = new ToolStripMenuItem("切换到 " + item.Text) { Checked = item.Checked, Enabled = item.Enabled, Tag = item.Provider };
            ui.Click += (_, _) => _ = RunUiEventAsync("切换连接", () => SwitchProviderAsync(item.Provider)); menu.Items.Add(ui);
        }
        menu.Items.Add(new ToolStripSeparator()); menu.Items.Add(new ToolStripMenuItem("OpenAI 账号") { Enabled = false });
        foreach (var item in model.AccountItems)
        {
            var ui = new ToolStripMenuItem($"{item.Label}    {item.Detail}") { Checked = item.Checked, Enabled = item.Enabled, Tag = item.ProfileId };
            ui.Click += (_, _) => _ = RunUiEventAsync("切换账号", () => SwitchAccountAsync(item.ProfileId)); menu.Items.Add(ui);
        }
        if (model.AccountItems.Count == 0) menu.Items.Add(new ToolStripMenuItem("暂无已管理账号") { Enabled = false });
        menu.Items.Add(Action("登录新账号…", () => LoginAsync(LoginMode.Browser), model.MutationsEnabled));
        menu.Items.Add(Action("设备码登录…", () => LoginAsync(LoginMode.DeviceCode), model.MutationsEnabled));
        menu.Items.Add(Action("立即刷新额度", () => RefreshUsageAsync(true), model.MutationsEnabled));
        menu.Items.Add(Action("管理账号…", () => { ShowAccountManager(); return Task.CompletedTask; }, model.MutationsEnabled));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Action("打开 Codex", () => _services.Lifecycle.LaunchAsync(default), true));
        var startup = Action("登录时自动启动", () => { if (_services.Startup.IsEnabled) _services.Startup.Disable(); else _services.Startup.Enable(); return RefreshMenuAsync(); }, true);
        startup.Checked = _services.Startup.IsEnabled; menu.Items.Add(startup);
        menu.Items.Add(Action("打开日志目录", OpenLogsAsync, true));
            menu.Items.Add(Action("退出", () => { ExitThread(); return Task.CompletedTask; }, !_operations.IsBusy));
        }
        finally { _menuGate.Release(); }
    }

    private ToolStripMenuItem Action(string text, Func<Task> action, bool enabled)
    {
        var item = new ToolStripMenuItem(text) { Enabled = enabled };
        item.Click += (_, _) => _ = RunUiEventAsync(text, action);
        return item;
    }

    private Task RunUiEventAsync(string title, Func<Task> action)
        => _exiting ? Task.CompletedTask : UiExceptionGuard.RunAsync(action, error => ShowError(title, error), _lifetime.Token);

    private Provider ReadProvider()
    {
        try { return ProviderConfigEditor.Read(File.ReadAllText(_services.Paths.ConfigPath)); }
        catch { return Provider.OpenAI; }
    }

    private async Task SwitchProviderAsync(Provider provider)
    {
        if (!ConfirmRestart($"切换到 {(provider == Provider.OpenAI ? "OpenAI 官方" : "Sub2API")}？")) return;
        await RunMutationAsync(async token =>
        {
            if (provider == Provider.Sub2Api && !await _services.Sub2Api.IsOnlineAsync(token)) throw new InvalidOperationException("Sub2API 未在 127.0.0.1:8080 响应。");
            if (!await CloseCodexAsync(token)) return;
            await _services.ProviderSwitch.SwitchAsync(provider, token); await _services.Lifecycle.LaunchAsync(token);
        });
    }

    private async Task SwitchAccountAsync(Guid profileId)
    {
        if (!ConfirmRestart("切换 OpenAI 账号？")) return;
        await RunMutationAsync(async token => { await _services.AccountSwitch.ValidateAsync(profileId, token); if (!await CloseCodexAsync(token)) return; await _services.AccountSwitch.SwitchAsync(profileId, token); await _services.Lifecycle.LaunchAsync(token); });
    }

    private async Task<bool> CloseCodexAsync(CancellationToken token)
    {
        var result = await _services.Lifecycle.RequestCloseAsync(TimeSpan.FromSeconds(15), token);
        if (result.Exited) return true;
        throw new InvalidOperationException("Codex 尚未完整退出，本次切换已取消。请在任务结束后手动退出 Codex，再重试切换。");
    }

    private async Task RunMutationAsync(Func<CancellationToken, Task> action)
    {
        try { await _operations.TryRunAsync(async token => { await RefreshMenuAsync(); await action(token); }); }
        catch (Exception error) { ShowError("操作失败", error); }
        finally
        {
            try { if (!_exiting) await RefreshMenuAsync(); }
            catch (OperationCanceledException) when (_exiting) { }
            catch (Exception error) { ShowError("刷新操作结果", error); }
        }
    }

    private async Task LoginAsync(LoginMode mode)
    {
        var label = LoginPrompt.Show(null, "登录新账号", "账号名称："); if (label is null) return;
        await RunMutationAsync(async token =>
        {
            await _services.Login.LoginAsync(label, mode, duplicate =>
                MessageBox.Show($"账号“{duplicate.Label}”已存在，是否更新凭据？", "重复账号", MessageBoxButtons.YesNo) == DialogResult.Yes, token);
            await RefreshUsageAsync(false);
        });
    }

    private void ShowAccountManager()
    {
        var form = new AccountManagerForm(() => _services.Accounts.ListAsync(default), SwitchAccountAsync,
            (id, name) => _services.Accounts.RenameAsync(id, name, default), id => _services.Accounts.RemoveAsync(id, default));
        form.Show();
    }

    private async Task RefreshUsageAsync(bool showErrors)
    {
        try { _usage = await _services.Usage.RefreshAllAsync(default); await RefreshMenuAsync(); }
        catch (Exception error) { if (showErrors) ShowError("额度刷新失败", error); }
    }

    private async Task ImportLiveAccountIfNeededAsync()
    {
        if ((await _services.Accounts.ListAsync(default)).Count != 0 || !File.Exists(_services.Paths.AuthPath)) return;
        try
        {
            var bytes = await File.ReadAllBytesAsync(_services.Paths.AuthPath); _ = AuthBlob.Parse(bytes);
            if (MessageBox.Show("检测到当前 Codex 登录，是否导入为“默认账号”？", "Codex Switch", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                await new AccountImportService(_services.Accounts).ImportAsync("默认账号", bytes, false, default);
        }
        catch (AuthBlobException) { }
    }

    private Task OpenLogsAsync() { Directory.CreateDirectory(_services.Paths.LogDirectory); Process.Start(new ProcessStartInfo("explorer.exe", _services.Paths.LogDirectory) { UseShellExecute = true }); return Task.CompletedTask; }
    private static bool ConfirmRestart(string title) => MessageBox.Show("Codex 将退出并重新打开，正在运行的任务会被中断。", title, MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) == DialogResult.OK;
    private void ShowError(string title, Exception error)
    {
        _ = UiExceptionGuard.TryAppend(_services.Paths.LogDirectory, title, error);
        if (_exiting) return;
        try { MessageBox.Show(SecretRedactor.Redact(error.Message), title, MessageBoxButtons.OK, MessageBoxIcon.Error); }
        catch { }
    }
    protected override void ExitThreadCore()
    {
        _exiting = true;
        try { _lifetime.Cancel(); _timer.Stop(); _timer.Dispose(); _icon.Visible = false; _icon.Dispose(); }
        catch (Exception error) { _ = UiExceptionGuard.TryAppend(_services.Paths.LogDirectory, "关闭托盘", error); }
        base.ExitThreadCore();
    }
}
