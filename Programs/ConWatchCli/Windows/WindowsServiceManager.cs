using System.Diagnostics;
using System.Runtime.Versioning;
using System.ServiceProcess;
using ConConfig;
using ConWatchCli.Services;
using Microsoft.Win32;

namespace ConWatchCli.Windows;

/// <summary>
/// sc.exe for registration and recovery, ServiceController for state,
/// netsh for the firewall rule, a .url file for the Start Menu shortcut.
/// Mirrors what deploy/windows/install-service.ps1 used to do.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsServiceManager(TextWriter log) : IServiceManager
{
    private const string Name = CliApp.ServiceName;
    private static readonly TimeSpan StateTimeout = TimeSpan.FromSeconds(30);

    private static string ShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "ConnectionWatcher Dashboard.url");
    private static string LegacyShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "ConnectionWatcher Dashboard.lnk");

    public async Task InstallAsync(ServiceSpec spec, CancellationToken ct)
    {
        if ((await StatusAsync(ct)).State != ServiceState.NotInstalled)
        {
            log.WriteLine("Service exists - reconfiguring.");
            await StopIfRunningAsync(ct);
            Sc("delete", Name);
            await WaitUntilGoneAsync(ct);
        }

        var port = ConfigResolver.PortOf(spec.ListenUrl);
        Sc("create", Name, "binPath=", $"\"{spec.ExePath}\" serve", "start=", "auto", "DisplayName=", Name);
        Sc("description", Name, $"Home internet connectivity monitor (dashboard at {spec.ServerUrl})");
        Sc("failure", Name, "reset=", "86400", "actions=", "restart/5000/restart/30000/restart/60000");

        Run("netsh", ["advfirewall", "firewall", "delete", "rule", $"name={Name}"], allowFailure: true);
        Run("netsh", ["advfirewall", "firewall", "add", "rule", $"name={Name}", "dir=in", "action=allow",
            "protocol=TCP", $"localport={port}", "profile=private,domain"]);

        if (File.Exists(LegacyShortcutPath)) File.Delete(LegacyShortcutPath);
        File.WriteAllText(ShortcutPath, $"[InternetShortcut]\r\nURL={spec.ServerUrl}\r\n");

        if (spec.Start)
            await StartAsync(ct);
    }

    public async Task UninstallAsync(CancellationToken ct)
    {
        await StopIfRunningAsync(ct);
        Sc("delete", Name);
        Run("netsh", ["advfirewall", "firewall", "delete", "rule", $"name={Name}"], allowFailure: true);
        if (File.Exists(ShortcutPath)) File.Delete(ShortcutPath);
        if (File.Exists(LegacyShortcutPath)) File.Delete(LegacyShortcutPath);
        await WaitUntilGoneAsync(ct);
    }

    public Task StartAsync(CancellationToken ct)
    {
        using var sc = new ServiceController(Name);
        if (sc.Status != ServiceControllerStatus.Running)
        {
            sc.Start();
            try { sc.WaitForStatus(ServiceControllerStatus.Running, StateTimeout); }
            catch (System.ServiceProcess.TimeoutException) { throw new ServiceControlException($"{Name} did not reach Running within {StateTimeout.TotalSeconds:0} s; check the Windows event log."); }
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct) => StopIfRunningAsync(ct);

    public Task<ServiceStatus> StatusAsync(CancellationToken ct)
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{Name}");
        if (key is null)
            return Task.FromResult(ServiceStatus.NotInstalled);
        var binary = key.GetValue("ImagePath") as string;
        var autoStart = key.GetValue("Start") is int start && start == 2;
        using var sc = new ServiceController(Name);
        try
        {
            var state = sc.Status switch
            {
                ServiceControllerStatus.Running => ServiceState.Running,
                ServiceControllerStatus.Stopped => ServiceState.Stopped,
                _ => ServiceState.Other,
            };
            return Task.FromResult(new ServiceStatus(state, binary, autoStart, state == ServiceState.Other ? sc.Status.ToString() : null));
        }
        catch (InvalidOperationException)
        {
            // The registry key exists but the SCM can't report status, e.g. ERROR_SERVICE_MARKED_FOR_DELETE (1072)
            // while a prior delete is still pending. Report it rather than throwing so callers like
            // WaitUntilGoneAsync can keep polling until the entry actually disappears.
            return Task.FromResult(new ServiceStatus(ServiceState.Other, binary, autoStart, "marked for deletion"));
        }
    }

    private Task StopIfRunningAsync(CancellationToken ct)
    {
        using var sc = new ServiceController(Name);
        if (sc.Status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending or ServiceControllerStatus.Paused)
        {
            sc.Stop();
            try { sc.WaitForStatus(ServiceControllerStatus.Stopped, StateTimeout); }
            catch (System.ServiceProcess.TimeoutException) { throw new ServiceControlException($"{Name} did not stop within {StateTimeout.TotalSeconds:0} s."); }
        }
        return Task.CompletedTask;
    }

    private async Task WaitUntilGoneAsync(CancellationToken ct)
    {
        // sc delete is asynchronous: the entry lingers until every handle closes.
        for (var i = 0; i < 50; i++)
        {
            if ((await StatusAsync(ct)).State == ServiceState.NotInstalled) return;
            await Task.Delay(200, ct);
        }
        throw new ServiceControlException($"{Name} is marked for deletion but still present; close services.msc and retry.");
    }

    private void Sc(params string[] args) => Run("sc.exe", args);

    private void Run(string exe, string[] args, bool allowFailure = false)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new ServiceControlException($"could not start {exe}");
        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        var stdout = stdoutTask.GetAwaiter().GetResult();
        var stderr = stderrTask.GetAwaiter().GetResult();
        if (p.ExitCode != 0 && !allowFailure)
            throw new ServiceControlException($"{exe} {string.Join(' ', args)} failed (exit {p.ExitCode}): {(stderr + stdout).Trim()}");
    }
}
