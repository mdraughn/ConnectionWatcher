using ConConfig;
using ConWatchCli;

namespace ConWatchCli.Tests;

/// <summary>Runs the real grammar over fakes and a temp config root.</summary>
public sealed class CliHarness : IDisposable
{
    public string Root { get; } = Directory.CreateTempSubdirectory("conwatchcli").FullName;
    public ConfigPaths Paths { get; }
    public FakeServiceManager Services { get; } = new();
    public FakeElevation Elevation { get; }
    public FakeHealthProbe Health { get; } = new();
    public FakePortProbe Ports { get; } = new();
    public FakeTrayLauncher TrayLauncher { get; set; } = new();
    public FakeTrayAutostart TrayAutostart { get; } = new();
    public FakeFolderLockdown Lockdown { get; } = new();
    public Dictionary<string, string?> Env { get; } = new();
    public string[]? ServeArgs { get; private set; }
    /// <summary>What `serve` does after recording its arguments; default returns 0 at once.</summary>
    public Func<CancellationToken, Task<int>> ServeBody { get; set; } = _ => Task.FromResult(0);
    public string AppDir => Path.Combine(Root, "app");
    public string TrayExePath() => Path.Combine(AppDir, OperatingSystem.IsWindows() ? "conwatch-tray.exe" : "conwatch-tray");

    public CliHarness(bool elevated = true)
    {
        Paths = ConfigPaths.UnderTemp(Root);
        Elevation = new FakeElevation(elevated);
        Directory.CreateDirectory(AppDir);
    }

    public Task<(int Code, string Out, string Err)> RunAsync(params string[] args) =>
        RunAsync(CancellationToken.None, args);

    public async Task<(int Code, string Out, string Err)> RunAsync(CancellationToken ct, params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var services = new CliServices(
            Paths, Services, Elevation, Health, Ports, TrayLauncher, TrayAutostart, Lockdown,
            Serve: (a, serveCt) => { ServeArgs = a; return ServeBody(serveCt); },
            Env: name => Env.GetValueOrDefault(name),
            AppDir: AppDir,
            CliExePath: Path.Combine(AppDir, "conwatch.exe"),
            Out: output, Err: error, Version: "9.9.9-test+abc");
        var code = await CliApp.RunAsync(args, services, ct);
        return (code, output.ToString(), error.ToString());
    }

    public void WriteMachine(ConWatchConfig config) => ConfigFile.WriteAtomic(Paths.MachineFile, config);
    public void WriteUser(ConWatchConfig config) => ConfigFile.WriteAtomic(Paths.UserFile, config);
    public ConWatchConfig? Machine => ConfigFile.TryRead(Paths.MachineFile, out _);
    public ConWatchConfig? User => ConfigFile.TryRead(Paths.UserFile, out _);

    public void Dispose() => Directory.Delete(Root, recursive: true);
}
