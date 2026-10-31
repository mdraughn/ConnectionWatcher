namespace ConWatchCli.Services;

public enum ServiceState { NotInstalled, Stopped, Running, Other }

/// <param name="BinaryPath">The registered command line, e.g. "\"C:\...\conwatch.exe\" serve".</param>
public sealed record ServiceStatus(ServiceState State, string? BinaryPath = null, bool AutoStart = false, string? Detail = null)
{
    public static readonly ServiceStatus NotInstalled = new(ServiceState.NotInstalled);
}

/// <param name="ExePath">Full path of conwatch.exe.</param>
/// <param name="ServerUrl">Goes into the service description and the Start Menu shortcut.</param>
/// <param name="Start">Start the service after installing.</param>
public sealed record ServiceSpec(string ExePath, string ListenUrl, string DataDir, string ServerUrl, bool Start);

/// <summary>Thrown by service managers for any SCM/firewall/shortcut failure → exit 4.</summary>
public sealed class ServiceControlException(string message) : Exception(message);

/// <summary>Thrown by commands for invalid config values → exit 2.</summary>
public sealed class ConfigException(string message) : Exception(message);

public interface IServiceManager
{
    /// <summary>Idempotent: an existing service is stopped, deleted and recreated.</summary>
    Task InstallAsync(ServiceSpec spec, CancellationToken ct);
    /// <summary>Removes service, firewall rule and shortcut; leaves data and config.</summary>
    Task UninstallAsync(CancellationToken ct);
    Task StartAsync(CancellationToken ct);
    Task StopAsync(CancellationToken ct);
    Task<ServiceStatus> StatusAsync(CancellationToken ct);
}

public interface IElevation
{
    bool IsElevated { get; }
    /// <summary>Relaunches this exe elevated with <paramref name="args"/>, echoes its output, returns its exit code (5 if declined).</summary>
    Task<int> RelaunchElevatedAsync(string[] args, CancellationToken ct);
}

public interface IPortProbe
{
    bool IsFree(int port);
}

public interface ITrayLauncher
{
    /// <summary>Starts the tray exe and returns; false when the exe is missing.</summary>
    bool Launch(string trayExePath);

    /// <summary>A description like "conwatch-tray.exe (PID 1234)" when a tray process is already running, else null.</summary>
    string? RunningInstance();
}

/// <summary>
/// Replaces a folder's inherited permissions (ProgramData lets every local user
/// read, and create files in, anything below it) with SYSTEM and Administrators
/// only, plus read-only for Users when <c>usersMayRead</c>. Creates the folder
/// if missing; files already inside take on the new permissions.
/// </summary>
public interface IFolderLockdown
{
    void Lock(string directory, bool usersMayRead);
}

public interface ITrayAutostart
{
    void Install(string trayExePath);
    void Uninstall();
}
