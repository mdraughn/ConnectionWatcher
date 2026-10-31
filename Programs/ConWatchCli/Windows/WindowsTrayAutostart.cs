using System.Runtime.Versioning;
using ConWatchCli.Services;
using Microsoft.Win32;

namespace ConWatchCli.Windows;

[SupportedOSPlatform("windows")]
public sealed class WindowsTrayAutostart : ITrayAutostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ConnectionWatcherTray";

    public void Install(string trayExePath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        key.SetValue(ValueName, $"\"{trayExePath}\"");
    }

    public void Uninstall()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
