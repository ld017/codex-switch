using System.Diagnostics;
using CodexSwitch.App.Accounts;
using CodexSwitch.Core.Auth;
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
    private IReadOnlyDictionary<Guid, UsageState> _usage = new Dictionary<Guid, UsageState>();

    public TrayApplicationContext(AppServices services)
    {
        _services = services;
        _icon = new NotifyIcon { Text = "Codex Switch", Icon = SystemIcons.Application, Visible = true, ContextMenuStrip = new ContextMenuStrip() };
        _icon.ContextMenuStrip.Opening += async (_, _) => await RefreshMenuAsync();
        _timer.Tick += async (_, _) => await RefreshUsageAsync(false);
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
        var menu = _icon.ContextMenuStrip!; menu.Items.Clear();
        var provider = ReadProvider();
        var model = TrayMenuModel.Build(new AppSnapshot(provider, await _services.Sub2Api.IsOnlineAsync(default),
            await _services.Accounts.GetActiveProfileIdAsync(default), await _services.Accounts.ListAsync(default), _usage, _operations.IsBusy));
        menu.Items.Add(new ToolStripMenuItem("Codex Switch") { Enabled = false });
        menu.Items.Add(new ToolStripMenuItem($"当前连接：{(provider == Provider.OpenAI ? "OpenAI 官方" : "Sub2API")}") { Enabled = false });
        menu.Items.Add(new ToolStripMenuItem(model.Sub2ApiStatus) { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());
        foreach (var item in model.ProviderItems)
        {
            var ui = new ToolStripMenuItem("切换到 " + item.Text) { Checked = item.Checked, Enabled = item.Enabled, Tag = item.Provider };
            ui.Click += async (_, _) => await SwitchProviderAsync((Provider)ui.Tag!); menu.Items.Add(ui);
        }
        menu.Items.Add(new ToolStripSeparator()); menu.Items.Add(new ToolStripMenuItem("OpenAI 账号") { Enabled = false });
        foreach (var item in model.AccountItems)
        {
            var ui = new ToolStripMenuItem($"{item.Label}    {item.Detail}") { Checked = item.Checked, Enabled = item.Enabled, Tag = item.ProfileId };
            ui.Click += async (_, _) => await SwitchAccountAsync((Guid)ui.Tag!); menu.Items.Add(ui);
        }
        if (model.AccountItems.Count == 0) menu.Items.Add(new ToolStripMenuItem("暂无已管理账号") { Enabled = false });
        menu.Items.Add(Action("登录新账号…", LoginAsync, model.MutationsEnabled));
        menu.Items.Add(Action("立即刷新额度", () => RefreshUsageAsync(true), model.MutationsEnabled));
        menu.Items.Add(Action("管理账号…", () => { ShowAccountManager(); return Task.CompletedTask; }, model.MutationsEnabled));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Action("打开 Codex", () => _services.Lifecycle.LaunchAsync(default), true));
        var startup = Action("登录时自动启动", () => { if (_services.Startup.IsEnabled) _services.Startup.Disable(); else _services.Startup.Enable(); return RefreshMenuAsync(); }, true);
        startup.Checked = _services.Startup.IsEnabled; menu.Items.Add(startup);
        menu.Items.Add(Action("打开日志目录", OpenLogsAsync, true));
        menu.Items.Add(Action("退出", () => { ExitThread(); return Task.CompletedTask; }, !_operations.IsBusy));
    }

    private ToolStripMenuItem Action(string text, Func<Task> action, bool enabled)
    {
        var item = new ToolStripMenuItem(text) { Enabled = enabled };
        item.Click += async (_, _) => { try { await action(); } catch (Exception error) { ShowError(text, error); } };
        return item;
    }

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
        await RunMutationAsync(async token => { if (!await CloseCodexAsync(token)) return; await _services.AccountSwitch.SwitchAsync(profileId, token); await _services.Lifecycle.LaunchAsync(token); });
    }

    private async Task<bool> CloseCodexAsync(CancellationToken token)
    {
        var result = await _services.Lifecycle.RequestCloseAsync(TimeSpan.FromSeconds(15), token);
        if (result.Exited) return true;
        if (MessageBox.Show("Codex 未能正常退出，是否强制结束已捕获的进程？", "Codex Switch", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return false;
        if (!await _services.Lifecycle.ForceCloseCapturedAsync(token)) throw new InvalidOperationException("无法结束 Codex 进程。");
        return true;
    }

    private async Task RunMutationAsync(Func<CancellationToken, Task> action)
    {
        try { await _operations.TryRunAsync(async token => { await RefreshMenuAsync(); await action(token); }); }
        catch (Exception error) { ShowError("操作失败", error); }
        finally { await RefreshMenuAsync(); }
    }

    private async Task LoginAsync()
    {
        var label = LoginPrompt.Show(null, "登录新账号", "账号名称："); if (label is null) return;
        await RunMutationAsync(async token =>
        {
            await _services.Login.LoginAsync(label, duplicate =>
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
    private void ShowError(string title, Exception error) { var safe = SecretRedactor.Redact(error.Message); Directory.CreateDirectory(_services.Paths.LogDirectory); File.AppendAllText(Path.Combine(_services.Paths.LogDirectory, "codex-switch.log"), $"{DateTimeOffset.Now:O} {title}: {safe}{Environment.NewLine}"); MessageBox.Show(safe, title, MessageBoxButtons.OK, MessageBoxIcon.Error); }
    protected override void ExitThreadCore() { _timer.Stop(); _timer.Dispose(); _icon.Visible = false; _icon.Dispose(); base.ExitThreadCore(); }
}
