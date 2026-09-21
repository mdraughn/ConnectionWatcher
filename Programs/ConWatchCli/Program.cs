using System.Reflection;
using ConConfig;
using ConWatchCli;
using ConWatchCli.Services;

// `--__output <file>`: written by the elevated relaunch so the parent can echo
// our output into the caller's terminal. Stripped before parsing.
var (args2, outputFile) = StripOutputOption(args);
TextWriter output = Console.Out, error = Console.Error;
StreamWriter? capture = null;
if (outputFile is not null)
{
    capture = new StreamWriter(outputFile) { AutoFlush = true };
    output = error = capture;
}

try
{
    var version = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
    var appDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
    var services = new CliServices(
        Paths: ConfigPaths.Default(),
        Services: new UnsupportedServiceManager(),      // Task 6 swaps in WindowsServiceManager on Windows
        Elevation: new AlwaysElevated(),                // Task 6 swaps in WindowsElevation on Windows
        Health: new UnsupportedHealthProbe(),           // Task 6 swaps in HttpHealthProbe
        Ports: new UnsupportedPortProbe(),              // Task 6 swaps in TcpPortProbe
        TrayLauncher: new UnsupportedTrayLauncher(),    // Task 8
        TrayAutostart: new UnsupportedTrayAutostart(),  // Task 8
        Serve: (hostArgs, ct) => ConWatch.ConWatchApp.RunAsync(hostArgs, pinContentRoot: true, ct),
        Env: Environment.GetEnvironmentVariable,
        AppDir: appDir,
        CliExePath: Environment.ProcessPath ?? Path.Combine(appDir, "conwatch.exe"),
        Out: output, Err: error, Version: version);

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
    return await CliApp.RunAsync(args2, services, cts.Token);
}
finally
{
    capture?.Dispose();
}

static (string[] Args, string? OutputFile) StripOutputOption(string[] args)
{
    var at = Array.IndexOf(args, "--__output");
    if (at < 0 || at + 1 >= args.Length)
        return (args, null);
    return (args.Where((_, i) => i != at && i != at + 1).ToArray(), args[at + 1]);
}

public partial class Program { }

file sealed class AlwaysElevated : IElevation
{
    public bool IsElevated => true;
    public Task<int> RelaunchElevatedAsync(string[] args, CancellationToken ct) => Task.FromResult(ExitCodes.Ok);
}
file sealed class UnsupportedHealthProbe : IHealthProbe
{
    public Task<HealthResult> ProbeAsync(string serverUrl, CancellationToken ct) => Task.FromResult(new HealthResult(null, "not implemented", 0));
}
file sealed class UnsupportedPortProbe : IPortProbe { public bool IsFree(int port) => true; }
file sealed class UnsupportedTrayLauncher : ITrayLauncher { public bool Launch(string trayExePath) => false; }
file sealed class UnsupportedTrayAutostart : ITrayAutostart
{
    public bool IsInstalled => false;
    public void Install(string trayExePath) => throw new PlatformNotSupportedException("tray autostart is not implemented on this platform");
    public void Uninstall() => throw new PlatformNotSupportedException("tray autostart is not implemented on this platform");
}
