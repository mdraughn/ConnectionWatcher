using System.Diagnostics;
using System.Text.Json;

namespace ConTray;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new TrayContext());
    }
}

/// <summary>
/// The tray companion: a verdict-colored icon polling the ConnectionWatcher
/// service. The service itself cannot own a tray icon (session 0), so this
/// little user-session app is the face of it.
/// </summary>
internal sealed class TrayContext : ApplicationContext
{
    private readonly NotifyIcon _icon;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(3) };
    private readonly string _dashboardUrl;
    private readonly Dictionary<TrayState, Icon> _icons = new();
    private TrayState _state = TrayState.Unreachable;

    public TrayContext()
    {
        _dashboardUrl = ReadConfiguredUrl();

        var menu = new ContextMenuStrip();
        menu.Items.Add("Dashboard", null, (_, _) => Open(""));
        menu.Items.Add("History", null, (_, _) => Open("/History"));
        menu.Items.Add("ISP report", null, (_, _) => Open("/Report"));
        menu.Items.Add("Admin", null, (_, _) => Open("/Admin"));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());

        _icon = new NotifyIcon
        {
            Icon = IconFor(TrayState.Unreachable),
            Text = TrayStatus.Describe(TrayState.Unreachable),
            Visible = true,
            ContextMenuStrip = menu,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                Open("");
        };

        _timer = new System.Windows.Forms.Timer { Interval = 5000 };
        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();
        _ = RefreshAsync();
    }

    private static string ReadConfiguredUrl()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "contray.json");
            if (File.Exists(path))
            {
                var config = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(path));
                if (config.TryGetProperty("url", out var url) && url.GetString() is { Length: > 0 } value)
                    return value.TrimEnd('/');
            }
        }
        catch
        {
            // fall through to default
        }
        return "http://localhost:8080";
    }

    private async Task RefreshAsync()
    {
        TrayState next;
        try
        {
            var response = await _http.GetAsync($"{_dashboardUrl}/api/v1/status");
            next = response.IsSuccessStatusCode
                ? TrayStatus.FromStatusJson(await response.Content.ReadAsStringAsync())
                : TrayState.Unreachable;
        }
        catch (Exception)
        {
            next = TrayState.Unreachable;
        }
        if (next != _state)
        {
            _state = next;
            _icon.Icon = IconFor(next);
            _icon.Text = TrayStatus.Describe(next);
        }
    }

    private void Open(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo($"{_dashboardUrl}{path}") { UseShellExecute = true });
        }
        catch (Exception)
        {
            // Browser launch failed; nothing sensible to do from the tray.
        }
    }

    /// <summary>Runtime-drawn colored dot — no icon assets to ship.</summary>
    private Icon IconFor(TrayState state)
    {
        if (_icons.TryGetValue(state, out var cached))
            return cached;
        var color = state switch
        {
            TrayState.Healthy => Color.FromArgb(46, 158, 82),
            TrayState.Degraded => Color.FromArgb(224, 138, 0),
            TrayState.Down => Color.FromArgb(204, 51, 51),
            TrayState.Measuring => Color.FromArgb(106, 168, 138),
            _ => Color.FromArgb(150, 150, 150),
        };
        using var bitmap = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var brush = new SolidBrush(color);
            g.FillEllipse(brush, 2, 2, 12, 12);
        }
        var icon = Icon.FromHandle(bitmap.GetHicon());
        _icons[state] = icon;
        return icon;
    }

    protected override void ExitThreadCore()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _timer.Dispose();
        _http.Dispose();
        base.ExitThreadCore();
    }
}
