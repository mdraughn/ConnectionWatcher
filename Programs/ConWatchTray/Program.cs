using System.Diagnostics;
using ConConfig;
using ConInfrastructure.Discovery;
using ConInfrastructure.Tray;

namespace ConWatchTray;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // One dot is plenty: a second launch (login autostart + manual start)
        // exits quietly.
        using var mutex = new Mutex(initiallyOwned: true, "ConnectionWatcherTray", out var isFirst);
        if (!isFirst)
            return;

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayContext());
    }
}

/// <summary>
/// The tray companion: a verdict-colored icon polling a ConnectionWatcher
/// server. Standalone-installable — the server can be anywhere; see
/// TraySettings for how the URL is chosen.
/// </summary>
internal sealed class TrayContext : ApplicationContext
{
    private readonly NotifyIcon _icon;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly System.Windows.Forms.Timer _clickTimer;
    private long _lastDoubleClick = long.MinValue / 2;
    // Races ::1 and 127.0.0.1: the default service listens on IPv4 only, and trying
    // ::1 first cost ~2 s per new connection (and made a dead server read as a timeout).
    private readonly HttpClient _http = new(ConConfig.LocalServerHttp.CreateHandler()) { Timeout = TimeSpan.FromSeconds(3) };
    private readonly TraySettings _settings = new();
    private readonly Dictionary<(TrayState, bool), Icon> _icons = new();
    private readonly TrayModel _model = new();
    private ToolStripItem _notesItem = null!;
    private readonly ScoutRunner _scout = new();
    private readonly ScoutPasswordStore _scoutPasswords = new();
    // One cookie-enabled, no-redirect client for the scout (its own, longer timeout): a sign-in is reused by later sends.
    private readonly HttpClient _scoutHttp = new(ScoutClient.CreateHandler()) { Timeout = TimeSpan.FromSeconds(20) };
    private readonly ScoutSession _scoutSession;   // the send loop; holds this session's plain-HTTP yes
    private bool _scouting;
    private string? _balloonUrl;

    public TrayContext()
    {
        _scoutSession = new ScoutSession(_scoutPasswords, PromptForPassword, ConfirmInsecure);
        var menu = new ContextMenuStrip();
        var open = menu.Items.Add("Open ConnectionWatcher", null, (_, _) => Open(""));
        open.Font = new Font(open.Font, FontStyle.Bold);
        _notesItem = menu.Items.Add("ConnectionWatcher notes", null, (_, _) => Open("/#notes"));
        _notesItem.Visible = false;
        menu.Items.Add("History", null, (_, _) => Open("/History"));
        menu.Items.Add("ISP report", null, (_, _) => Open("/Report"));
        menu.Items.Add("Server settings", null, (_, _) => Open("/Settings"));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Find my router", null, async (_, _) => await ScoutAsync(ScoutKind.Router));
        menu.Items.Add("Find the network edge", null, async (_, _) => await ScoutAsync(ScoutKind.NetworkEdge));
        menu.Items.Add("Scan this network", null, async (_, _) => await ScoutAsync(ScoutKind.Lan));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Tray settings…", null, (_, _) => ShowSettings());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());

        _icon = new NotifyIcon
        {
            Icon = IconFor(TrayState.Unreachable, badge: false),
            Text = TrayStatus.Describe(TrayState.Unreachable),
            Visible = true,
            ContextMenuStrip = menu,
        };
        _icon.BalloonTipClicked += (_, _) => { if (_balloonUrl is { } url) Open(url); };
        // Left button: a single click opens the menu, a double click opens the
        // dashboard. The menu grabs the mouse as soon as it shows, which would
        // swallow the second click, so the single-click action waits one
        // double-click interval and a double click cancels it.
        _clickTimer = new System.Windows.Forms.Timer { Interval = SystemInformation.DoubleClickTime };
        _clickTimer.Tick += (_, _) =>
        {
            _clickTimer.Stop();
            ShowMenu();
        };
        // The second click of a double click raises MouseClick too, after
        // MouseDoubleClick, so a click that closely follows a double click is
        // part of it and must not re-arm the menu.
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button != MouseButtons.Left)
                return;
            if (Environment.TickCount64 - _lastDoubleClick < SystemInformation.DoubleClickTime)
                return;
            _clickTimer.Start();
        };
        _icon.MouseDoubleClick += (_, e) =>
        {
            if (e.Button != MouseButtons.Left)
                return;
            _clickTimer.Stop();
            _lastDoubleClick = Environment.TickCount64;
            Open("");
        };

        _timer = new System.Windows.Forms.Timer { Interval = 5000 };
        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();
        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        TrayInfo next;
        try
        {
            var response = await _http.GetAsync($"{_settings.EffectiveUrl}/api/v1/status");
            next = response.IsSuccessStatusCode
                ? TrayStatus.FromStatusJson(await response.Content.ReadAsStringAsync())
                : new TrayInfo(TrayState.Unreachable, null, []);
        }
        catch (Exception)
        {
            next = new TrayInfo(TrayState.Unreachable, null, []);
        }

        var (redraw, toast) = _model.Apply(next, _settings.NotificationsEnabled, _settings.EffectiveUrl);
        if (redraw)
        {
            _icon.Icon = IconFor(next.State, badge: next.Notes.Count > 0);
            _icon.Text = TrayStatus.Tooltip(next);
            _notesItem.Text = $"ConnectionWatcher notes ({next.Notes.Count})";
            _notesItem.Visible = next.Notes.Count > 0;
        }

        // One balloon per poll: a second would replace the first.
        if (toast is not null)
            Balloon(toast.Title, Truncate(toast.Body, 250),
                toast.Level switch
                {
                    ToastLevel.Error => ToolTipIcon.Error,
                    ToastLevel.Warning => ToolTipIcon.Warning,
                    _ => ToolTipIcon.Info,
                },
                toast.OpenNotes ? "/#notes" : null);
    }

    /// <summary>
    /// Pops the context menu at the cursor. NotifyIcon does this itself only
    /// for the right button; its private ShowContextMenu is how WinForms
    /// positions the menu and gives it focus so it closes when clicked away.
    /// </summary>
    private void ShowMenu() =>
        typeof(NotifyIcon).GetMethod("ShowContextMenu",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?
            .Invoke(_icon, null);

    private async Task ScoutAsync(ScoutKind kind)
    {
        if (_scouting)
        {
            Balloon("Scouting", "A search is already running — wait for it to finish.", ToolTipIcon.Info);
            return;
        }
        _scouting = true;
        try
        {
            var label = kind switch { ScoutKind.Router => "your router", ScoutKind.NetworkEdge => "the network edge", _ => "this network" };
            Balloon("Scouting", $"Looking for {label}…", ToolTipIcon.Info);

            var (items, partial) = await _scout.RunAsync(kind);
            if (items.Count == 0)
            {
                Balloon("Nothing found", kind == ScoutKind.NetworkEdge
                    ? "The hops past your router did not answer. Try again in a moment."
                    : "Nothing was found from this PC.", ToolTipIcon.Info);
                return;
            }

            var server = _settings.EffectiveUrl;
            if (!Uri.TryCreate(ConfigResolver.NormalizeServerUrl(server) + "/", UriKind.Absolute, out var serverUri))
            {
                Balloon("Could not send", "The server address in the tray settings is not a valid URL.", ToolTipIcon.Warning);
                return;
            }
            var outcome = await _scoutSession.SendAsync(new ScoutClient(_scoutHttp, serverUri), items, CancellationToken.None);
            switch (outcome.Kind)
            {
                case ScoutOutcomeKind.Sent:
                    Balloon("Sent to ConnectionWatcher",
                        $"{items.Count} found, {outcome.Result.Created} new{(partial ? " (the scan was cut short)" : "")}. Click to review.",
                        ToolTipIcon.Info, "/Config/Discoveries");
                    break;
                case ScoutOutcomeKind.PasswordNotAccepted:
                    Balloon("Could not send", "The admin password was not accepted.", ToolTipIcon.Warning);
                    break;
                case ScoutOutcomeKind.Cancelled:
                    break;                                // the user said no; nothing to report
                default:
                    Balloon("Could not send", outcome.Result.Message, ToolTipIcon.Warning);
                    break;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Reached from an async-void menu handler: nothing may escape and end the tray. Never includes the password.
            Balloon("Could not scan or send", ex.Message, ToolTipIcon.Warning);
        }
        finally
        {
            _scouting = false;
        }
    }

    /// <summary>Asks for the password; the session saves it (DPAPI) only if Remember was ticked and the server accepts it.</summary>
    private static ScoutPasswordAnswer? PromptForPassword(Uri server, bool rejected)
    {
        using var prompt = new PasswordPrompt(ScoutServerKey.Of(server), rejected ? "That password was not accepted." : null);
        if (prompt.ShowDialog() != DialogResult.OK || prompt.Password.Length == 0)
            return null;
        return new ScoutPasswordAnswer(prompt.Password, prompt.Remember);
    }

    /// <summary>Asks before the admin password crosses plain HTTP to another machine; default No.</summary>
    private static bool ConfirmInsecure(Uri server) =>
        MessageBox.Show(
            $"Signing in would send your admin password to {server.Host} unencrypted over plain HTTP." +
            "\n\nSend it anyway? (Anyone on your network could read it. Prefer an https:// server address.)",
            "ConnectionWatcher — unencrypted password",
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;

    private void Balloon(string title, string text, ToolTipIcon icon, string? openPath = null)
    {
        _balloonUrl = openPath;
        _icon.ShowBalloonTip(6000, title, Truncate(text, 250), icon);
    }

    private void ShowSettings()
    {
        using var dialog = new SettingsForm(_settings, new ConConfig.HttpHealthProbe(), _scoutPasswords, _scoutSession.ForgetConsent);
        if (dialog.ShowDialog() == DialogResult.OK)
            _ = RefreshAsync(); // apply immediately
    }

    private void Open(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo($"{_settings.EffectiveUrl}{path}") { UseShellExecute = true });
        }
        catch (Exception)
        {
            // Browser launch failed; nothing sensible to do from the tray.
        }
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..(max - 1)] + "…";

    /// <summary>
    /// Runtime-drawn Signal glyph — no icon assets to ship. Same ladder as
    /// the web verdict slab: reach shrinks as things get worse (both arc
    /// pairs → inner pair only → dimmed glyph under a slash), so the states
    /// differ in FORM, not only color, and survive a greyscale tray.
    /// </summary>
    private Icon IconFor(TrayState state, bool badge)
    {
        if (_icons.TryGetValue((state, badge), out var cached))
            return cached;
        var (color, arcPairs, slash) = state switch
        {
            TrayState.Healthy => (Color.FromArgb(46, 158, 82), 2, false),
            TrayState.Degraded => (Color.FromArgb(224, 138, 0), 1, false),
            TrayState.Down => (Color.FromArgb(204, 51, 51), 2, true),
            TrayState.Measuring => (Color.FromArgb(106, 168, 138), 2, false),
            _ => (Color.FromArgb(150, 150, 150), 2, false),
        };

        // Drawn at 32px in the favicon's 24-unit space; Windows scales down.
        const int size = 32;
        const float scale = size / 24f;
        using var bitmap = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            var glyph = slash ? Color.FromArgb(82, color) : color;
            using var brush = new SolidBrush(glyph);
            // Heavier stroke than the web glyph: tray icons render tiny.
            using var pen = new Pen(glyph, 2.8f * scale)
            {
                StartCap = System.Drawing.Drawing2D.LineCap.Round,
                EndCap = System.Drawing.Drawing2D.LineCap.Round,
            };

            void Arcs(float radius)
            {
                var box = new RectangleF((12 - radius) * scale, (12 - radius) * scale,
                    radius * 2 * scale, radius * 2 * scale);
                g.DrawArc(pen, box, -38, 76);  // right of the dot
                g.DrawArc(pen, box, 142, 76);  // left of the dot
            }

            const float r = 3.0f * scale;
            g.FillEllipse(brush, 12 * scale - r, 12 * scale - r, r * 2, r * 2);
            if (arcPairs >= 1) Arcs(5.9f);
            if (arcPairs >= 2) Arcs(9.4f);

            if (slash)
            {
                using var slashPen = new Pen(color, 3.2f * scale)
                {
                    StartCap = System.Drawing.Drawing2D.LineCap.Round,
                    EndCap = System.Drawing.Drawing2D.LineCap.Round,
                };
                g.DrawLine(slashPen, 5f * scale, 19f * scale, 19f * scale, 5f * scale);
            }

            if (badge)
            {
                // Subtle note marker: a small neutral dot, bottom-right, ringed
                // so it reads on light and dark taskbars. Never a state colour.
                const float d = 9f;
                var box = new RectangleF(size - d - 0.5f, size - d - 0.5f, d, d);
                using var ring = new SolidBrush(Color.White);
                using var fill = new SolidBrush(Color.FromArgb(96, 112, 140));
                g.FillEllipse(ring, RectangleF.Inflate(box, 1.2f, 1.2f));
                g.FillEllipse(fill, box);
            }
        }
        var icon = Icon.FromHandle(bitmap.GetHicon());
        _icons[(state, badge)] = icon;
        return icon;
    }

    protected override void ExitThreadCore()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _timer.Dispose();
        _clickTimer.Dispose();
        _http.Dispose();
        _scoutHttp.Dispose();
        base.ExitThreadCore();
    }
}
