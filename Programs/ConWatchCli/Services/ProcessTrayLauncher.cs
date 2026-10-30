using System.Diagnostics;

namespace ConWatchCli.Services;

public sealed class ProcessTrayLauncher : ITrayLauncher
{
    public bool Launch(string trayExePath)
    {
        if (!File.Exists(trayExePath)) return false;
        using var p = Process.Start(new ProcessStartInfo(trayExePath) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(trayExePath) });
        return true;
    }

    /// <summary>
    /// "conwatch-tray" is the current tray; "ConTray" is the 0.9 tray, which
    /// shares the same single-instance mutex name, so after a zip upgrade the
    /// new tray can exit immediately while the old one keeps running.
    /// </summary>
    public string? RunningInstance()
    {
        foreach (var (name, legacy) in new[] { (CliApp.TrayProcessName, false), ("ConTray", true) })
        {
            var processes = Process.GetProcessesByName(name);
            try
            {
                if (processes.Length == 0) continue;
                var pid = processes[0].Id;
                return legacy ? $"ConTray.exe (PID {pid}, the 0.9 tray)" : $"{CliApp.TrayProcessName}.exe (PID {pid})";
            }
            finally
            {
                foreach (var p in processes) p.Dispose();
            }
        }
        return null;
    }
}
