using ConConfig;

namespace ConWatchTray;

/// <summary>
/// The Settings dialog: server URL override with test-before-save, a way back
/// to the installed default, and the notifications toggle.
/// </summary>
internal sealed class SettingsForm : Form
{
    private readonly TraySettings _settings;
    private readonly IHealthProbe _probe;
    private readonly TextBox _urlBox;
    private readonly Label _sourceLabel;
    private readonly Label _testResult;
    private readonly CheckBox _notifications;
    private readonly Button _useDefault;

    public SettingsForm(TraySettings settings, IHealthProbe probe, ScoutPasswordStore scoutPasswords, Action onForget)
    {
        _settings = settings;
        _probe = probe;

        Text = "ConnectionWatcher Tray — Settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(430, 245);

        var urlLabel = new Label { Text = "Server URL:", Left = 12, Top = 15, Width = 80 };
        _urlBox = new TextBox { Left = 95, Top = 12, Width = 320, Text = _settings.EffectiveUrl };
        _sourceLabel = new Label { Left = 95, Top = 38, Width = 320, ForeColor = SystemColors.GrayText };

        var testButton = new Button { Text = "Test", Left = 95, Top = 62, Width = 70 };
        _testResult = new Label { Left = 175, Top = 66, Width = 240, AutoEllipsis = true };
        testButton.Click += async (_, _) => await TestAsync();

        _useDefault = new Button
        {
            Text = "Use installed default",
            Left = 95, Top = 95, Width = 150,
            Enabled = _settings.InstalledDefaultUrl is not null,
        };
        _useDefault.Click += (_, _) =>
        {
            _urlBox.Text = _settings.InstalledDefaultUrl ?? TraySettings.FallbackUrl;
            _testResult.Text = "";
            UpdateSourceLabel(pendingDefault: true);
        };

        _notifications = new CheckBox
        {
            Text = "Show notifications when the connection state changes",
            Left = 95, Top = 128, Width = 320,
            Checked = _settings.NotificationsEnabled,
        };

        var forget = new Button
        {
            Text = "Forget saved admin password",
            Left = 95, Top = 160, Width = 200,
            Enabled = scoutPasswords.HasSaved,
        };
        forget.Click += (_, _) =>
        {
            scoutPasswords.Forget();
            onForget();
            forget.Enabled = false;
        };

        var ok = new Button { Text = "OK", Left = 255, Top = 205, Width = 75, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", Left = 340, Top = 205, Width = 75, DialogResult = DialogResult.Cancel };
        ok.Click += (_, _) => Apply();
        AcceptButton = ok;
        CancelButton = cancel;

        Controls.AddRange([urlLabel, _urlBox, _sourceLabel, testButton, _testResult,
            _useDefault, _notifications, forget, ok, cancel]);
        UpdateSourceLabel(pendingDefault: false);
    }

    private void UpdateSourceLabel(bool pendingDefault)
    {
        _sourceLabel.Text = pendingDefault
            ? "will use: installed default"
            : $"currently using: {_settings.EffectiveSource}";
    }

    private async Task TestAsync()
    {
        _testResult.ForeColor = SystemColors.GrayText;
        _testResult.Text = "testing…";
        var url = ConfigResolver.NormalizeServerUrl(_urlBox.Text);
        HealthResult result;
        try { result = await _probe.ProbeAsync(url, CancellationToken.None); }
        catch (Exception)
        {
            // Runs from an async-void click handler: nothing may reach WinForms' unhandled-exception dialog.
            _testResult.ForeColor = Color.FromArgb(204, 51, 51);
            _testResult.Text = "Could not connect: unexpected error.";
            return;
        }
        if (result.Failure is null)
        {
            _testResult.ForeColor = Color.FromArgb(46, 158, 82);
            _testResult.Text = "Connected — ConnectionWatcher is answering.";
        }
        else
        {
            _testResult.ForeColor = Color.FromArgb(204, 51, 51);
            _testResult.Text = result.Failure.StartsWith("HTTP ", StringComparison.Ordinal)
                ? $"Server answered {result.Failure} — is that the right URL?"
                : $"Could not connect: {result.Failure}.";
        }
    }

    private void Apply()
    {
        // Typing the default clears the override instead of pinning it (TraySettings.ApplyUrl).
        if (TraySettings.ApplyUrl(_urlBox.Text, _settings.InstalledDefaultUrl, TraySettings.FallbackUrl) is { } url)
            _settings.SetOverride(url);
        else
            _settings.ClearOverride();
        _settings.SetNotifications(_notifications.Checked);
    }
}
