namespace ConWatchTray;

/// <summary>A small modal asking for the server's admin password, with a "remember on this PC" box.</summary>
internal sealed class PasswordPrompt : Form
{
    private readonly TextBox _password = new() { UseSystemPasswordChar = true, Dock = DockStyle.Top };
    private readonly CheckBox _remember = new() { Text = "Remember on this PC (encrypted for your Windows account)", AutoSize = true, Checked = true, Dock = DockStyle.Top };

    public string Password => _password.Text;
    public bool Remember => _remember.Checked;

    public PasswordPrompt(string serverUrl, string? reason)
    {
        Text = "ConnectionWatcher — admin password";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        TopMost = true;
        ClientSize = new Size(420, 150);
        Padding = new Padding(12);

        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Dock = DockStyle.Right, Width = 80 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Dock = DockStyle.Right, Width = 80 };
        var buttons = new Panel { Dock = DockStyle.Bottom, Height = 34 };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        AcceptButton = ok;
        CancelButton = cancel;

        Controls.Add(buttons);
        Controls.Add(_remember);
        Controls.Add(_password);
        Controls.Add(new Label
        {
            Text = (reason is { Length: > 0 } ? reason + "\n" : "") + $"Enter the admin password for {serverUrl}:",
            AutoSize = false, Dock = DockStyle.Top, Height = 48,
        });
    }
}
