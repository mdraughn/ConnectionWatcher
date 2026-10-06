namespace ConWatchTray;

/// <summary>
/// The Settings dialog: server URL override with test-before-save, a way back
/// to the installed default, and the notifications toggle.
/// </summary>
internal sealed class SettingsForm : Form
{
    private readonly TraySettings _settings;
    private readonly HttpClient _http;
    private readonly TextBox _urlBox;
    private readonly Label _sourceLabel;
    private readonly Label _testResult;
    private readonly CheckBox _notifications;
    private readonly Button _useDefault;

    public SettingsForm(TraySettings settings, HttpClient http, ScoutPasswordStore scoutPasswords, Action onForget)
    {
        _settings = settings;
        _http = http;

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
        try
        {
            var url = _urlBox.Text.TrimEnd('/');
            var response = await _http.GetAsync($"{url}/api/v1/health");
            if (response.IsSuccessStatusCode)
            {
                _testResult.ForeColor = Color.FromArgb(46, 158, 82);
                _testResult.Text = "Connected — ConnectionWatcher is answering.";
            }
            else
            {
                _testResult.ForeColor = Color.FromArgb(204, 51, 51);
                _testResult.Text = $"Server answered HTTP {(int)response.StatusCode} — is that the right URL?";
            }
        }
        catch (Exception ex)
        {
            _testResult.ForeColor = Color.FromArgb(204, 51, 51);
            _testResult.Text = $"Could not connect: {ex.Message}";
        }
    }

    private void Apply()
    {
        var url = _urlBox.Text.Trim().TrimEnd('/');
        if (url.Length == 0 || url == _settings.InstalledDefaultUrl ||
            (_settings.InstalledDefaultUrl is null && url == TraySettings.FallbackUrl))
        {
            _settings.ClearOverride();
        }
        else
        {
            _settings.SetOverride(url);
        }
        _settings.SetNotifications(_notifications.Checked);
    }
}
