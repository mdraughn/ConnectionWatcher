using ConConfig;
using ConWatchCli.Services;

namespace ConWatchCli;

public static class ExitCodes
{
    public const int Ok = 0;
    public const int Usage = 1;
    public const int ConfigInvalid = 2;
    public const int NotInstalled = 3;
    public const int ServiceControlFailed = 4;
    public const int ElevationDeclined = 5;
}

/// <summary>
/// Everything a command needs, so tests can run the real grammar over fakes.
/// </summary>
/// <param name="Serve">Runs the web host with host arguments; production binds ConWatchApp.RunAsync.</param>
/// <param name="Env">Environment lookup; production binds Environment.GetEnvironmentVariable.</param>
/// <param name="AppDir">Folder holding conwatch.exe and conwatch-tray.exe.</param>
public sealed record CliServices(
    ConfigPaths Paths,
    IServiceManager Services,
    IElevation Elevation,
    IHealthProbe Health,
    IPortProbe Ports,
    ITrayLauncher TrayLauncher,
    ITrayAutostart TrayAutostart,
    Func<string[], CancellationToken, Task<int>> Serve,
    Func<string, string?> Env,
    string AppDir,
    string CliExePath,
    TextWriter Out,
    TextWriter Err,
    string Version)
{
    public string TrayExePath => Path.Combine(AppDir, OperatingSystem.IsWindows() ? "conwatch-tray.exe" : "conwatch-tray");
}
