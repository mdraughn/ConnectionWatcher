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
    var version = typeof(ConWatchCli.CliApp).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
    var appDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
    IServiceManager serviceManager = OperatingSystem.IsWindows() ? new ConWatchCli.Windows.WindowsServiceManager(output) : new UnsupportedServiceManager();
    IElevation elevation = OperatingSystem.IsWindows() ? new ConWatchCli.Windows.WindowsElevation(output) : new NotElevatable();
    ITrayAutostart trayAutostart = OperatingSystem.IsWindows() ? new ConWatchCli.Windows.WindowsTrayAutostart() : new UnsupportedTrayAutostart();
    var services = new CliServices(
        Paths: ConfigPaths.Default(),
        Services: serviceManager,
        Elevation: elevation,
        Health: new HttpHealthProbe(),
        Ports: new TcpPortProbe(),
        TrayLauncher: new ProcessTrayLauncher(),
        TrayAutostart: trayAutostart,
        Serve: (hostArgs, ct) => ConWatch.ConWatchApp.RunAsync(hostArgs, pinContentRoot: true, ct),
        Env: Environment.GetEnvironmentVariable,
        AppDir: appDir,
        CliExePath: Environment.ProcessPath ?? Path.Combine(appDir, "conwatch.exe"),
        Out: output, Err: error, Version: version);

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
    try
    {
        return await CliApp.RunAsync(args2, services, cts.Token);
    }
    catch (Exception ex)
    {
        error.WriteLine(ex.ToString());
        return ConWatchCli.ExitCodes.ServiceControlFailed;
    }
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

/// <summary>Non-Windows: there is no UAC; run inline and let the OS refuse.</summary>
file sealed class NotElevatable : IElevation
{
    public bool IsElevated => true;
    public Task<int> RelaunchElevatedAsync(string[] args, CancellationToken ct) => Task.FromResult(ExitCodes.Ok);
}
file sealed class UnsupportedTrayAutostart : ITrayAutostart
{
    public bool IsInstalled => false;
    public void Install(string trayExePath) => throw new PlatformNotSupportedException("tray autostart is not implemented on this platform");
    public void Uninstall() => throw new PlatformNotSupportedException("tray autostart is not implemented on this platform");
}
