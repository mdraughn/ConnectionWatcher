using ConWatchCli.Services;

namespace ConWatchCli.Tests;

public sealed class FakeServiceManager : IServiceManager
{
    public List<string> Calls { get; } = [];
    public ServiceSpec? Installed { get; private set; }
    public ServiceStatus Status { get; set; } = ServiceStatus.NotInstalled;
    public Exception? Throw { get; set; }

    public Task InstallAsync(ServiceSpec spec, CancellationToken ct) { Record("install"); Installed = spec; return Task.CompletedTask; }
    public Task UninstallAsync(CancellationToken ct) { Record("uninstall"); return Task.CompletedTask; }
    public Task StartAsync(CancellationToken ct) { Record("start"); return Task.CompletedTask; }
    public Task StopAsync(CancellationToken ct) { Record("stop"); return Task.CompletedTask; }
    public Task<ServiceStatus> StatusAsync(CancellationToken ct) { Calls.Add("status"); return Task.FromResult(Status); }
    private void Record(string call) { Calls.Add(call); if (Throw is not null) throw Throw; }
}

public sealed class FakeElevation(bool elevated) : IElevation
{
    public bool IsElevated => elevated;
    public string[]? RelaunchArgs { get; private set; }
    public int RelaunchCode { get; set; } = 0;
    public Task<int> RelaunchElevatedAsync(string[] args, CancellationToken ct) { RelaunchArgs = args; return Task.FromResult(RelaunchCode); }
}

public sealed class FakeHealthProbe : IHealthProbe
{
    public HealthResult Result { get; set; } = new("Healthy", null, 41);
    public string? ProbedUrl { get; private set; }
    public Task<HealthResult> ProbeAsync(string serverUrl, CancellationToken ct) { ProbedUrl = serverUrl; return Task.FromResult(Result); }
}

public sealed class FakePortProbe : IPortProbe
{
    public HashSet<int> Busy { get; } = [];
    public bool IsFree(int port) => !Busy.Contains(port);
}

public sealed class FakeTrayLauncher(bool present = true) : ITrayLauncher
{
    public string? Launched { get; private set; }
    public string? Running { get; set; }
    public bool Launch(string trayExePath) { if (!present) return false; Launched = trayExePath; return true; }
    public string? RunningInstance() => Running;
}

public sealed class FakeTrayAutostart : ITrayAutostart
{
    public string? InstalledPath { get; private set; }
    public bool IsInstalled => InstalledPath is not null;
    public void Install(string trayExePath) => InstalledPath = trayExePath;
    public void Uninstall() => InstalledPath = null;
}
