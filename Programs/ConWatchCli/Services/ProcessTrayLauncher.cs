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
}
