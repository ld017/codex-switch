namespace CodexSwitch.App.Accounts;

public static class LoginPrompt
{
    public static string? Show(IWin32Window? owner, string title, string prompt, string initial = "")
    {
        using var form = new Form { Text = title, Width = 420, Height = 160, FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterScreen, MinimizeBox = false, MaximizeBox = false };
        var label = new Label { Text = prompt, Left = 12, Top = 15, Width = 380 };
        var input = new TextBox { Text = initial, Left = 12, Top = 42, Width = 380 };
        var ok = new Button { Text = "确定", DialogResult = DialogResult.OK, Left = 236, Top = 76, Width = 75 };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Left = 317, Top = 76, Width = 75 };
        form.Controls.AddRange([label, input, ok, cancel]); form.AcceptButton = ok; form.CancelButton = cancel;
        return form.ShowDialog(owner) == DialogResult.OK && !string.IsNullOrWhiteSpace(input.Text) ? input.Text.Trim() : null;
    }
}
