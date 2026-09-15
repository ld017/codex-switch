using CodexSwitch.Core.Auth;

namespace CodexSwitch.App.Accounts;

public sealed class AccountManagerForm : Form
{
    private readonly ListBox _accounts = new() { Dock = DockStyle.Fill, DisplayMember = nameof(AccountProfile.Label) };
    private readonly Func<Task<IReadOnlyList<AccountProfile>>> _load;

    public AccountManagerForm(Func<Task<IReadOnlyList<AccountProfile>>> load, Func<Guid, Task> switchAccount, Func<Guid, string, Task> rename, Func<Guid, Task> remove)
    {
        _load = load; Text = "管理 OpenAI 账号"; Width = 520; Height = 360; StartPosition = FormStartPosition.CenterScreen;
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
        buttons.Controls.Add(MakeButton("关闭", (_, _) => Close()));
        buttons.Controls.Add(MakeButton("删除", async (_, _) => { if (Selected() is { } p && MessageBox.Show($"删除账号“{p.Label}”？", "确认", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes) { await remove(p.Id); await ReloadAsync(); } }));
        buttons.Controls.Add(MakeButton("重命名", async (_, _) => { if (Selected() is { } p && LoginPrompt.Show(this, "重命名账号", "账号名称：", p.Label) is { } name) { await rename(p.Id, name); await ReloadAsync(); } }));
        buttons.Controls.Add(MakeButton("切换", async (_, _) => { if (Selected() is { } p) await switchAccount(p.Id); }));
        Controls.Add(_accounts); Controls.Add(buttons); Shown += async (_, _) => await ReloadAsync();
    }

    private static Button MakeButton(string text, EventHandler handler) { var button = new Button { Text = text, AutoSize = true }; button.Click += handler; return button; }
    private AccountProfile? Selected() => _accounts.SelectedItem as AccountProfile;
    private async Task ReloadAsync() => _accounts.DataSource = (await _load()).ToArray();
}
