using ConConfig;
using ConWatchCli.Services;

namespace ConWatchCli.Tests;

public sealed class ServiceCommandTests
{
    [Fact]
    public async Task Install_probes_a_free_port_writes_the_machine_file_and_installs()
    {
        using var h = new CliHarness();
        h.Ports.Busy.Add(8080);
        h.Ports.Busy.Add(8081);
        var (code, output, _) = await h.RunAsync("service", "install");
        Assert.Equal(0, code);
        var spec = h.Services.Installed!;
        Assert.Equal("http://0.0.0.0:8082", spec.ListenUrl);
        Assert.Equal(h.Paths.DefaultDataDir, spec.DataDir);
        Assert.Equal("http://localhost:8082", spec.ServerUrl);
        Assert.Equal(Path.Combine(h.AppDir, "conwatch.exe"), spec.ExePath);
        Assert.True(spec.Start);
        Assert.Equal("http://0.0.0.0:8082", h.Machine!.ListenUrl);
        Assert.Equal(h.Paths.DefaultDataDir, h.Machine.DataDir);
        Assert.Equal("http://localhost:8082", h.Machine.ServerUrl);
        Assert.Contains("http://localhost:8082", output);
    }

    [Fact]
    public async Task Install_with_port_pins_it_even_when_busy_and_no_start_is_honored()
    {
        using var h = new CliHarness();
        h.Ports.Busy.Add(9000);
        var (code, _, error) = await h.RunAsync("service", "install", "--port", "9000", "--no-start");
        Assert.Equal(0, code);
        Assert.Equal("http://0.0.0.0:9000", h.Services.Installed!.ListenUrl);
        Assert.False(h.Services.Installed.Start);
        Assert.Contains("in use", error);
    }

    [Fact]
    public async Task Install_keeps_an_existing_machine_file_port_and_server_url()
    {
        using var h = new CliHarness();
        h.WriteMachine(new ConWatchConfig(ListenUrl: "http://0.0.0.0:7000", ServerUrl: "http://nas:7000"));
        h.Ports.Busy.Add(7000); // the old service is still holding it; that is fine
        await h.RunAsync("service", "install");
        Assert.Equal("http://0.0.0.0:7000", h.Services.Installed!.ListenUrl);
        Assert.Equal("http://nas:7000", h.Services.Installed.ServerUrl);
    }

    [Fact]
    public async Task Install_url_and_port_together_is_a_usage_error()
    {
        using var h = new CliHarness();
        var (code, _, _) = await h.RunAsync("service", "install", "--port", "1", "--url", "http://0.0.0.0:2");
        Assert.Equal(ExitCodes.Usage, code);
        Assert.Empty(h.Services.Calls);
    }

    [Fact]
    public async Task Install_rejects_a_bad_url_with_config_invalid()
    {
        using var h = new CliHarness();
        var (code, _, error) = await h.RunAsync("service", "install", "--url", "nas:1");
        Assert.Equal(ExitCodes.ConfigInvalid, code);
        Assert.Contains("nas:1", error);
        Assert.Null(h.Machine);
        Assert.DoesNotContain("install", h.Services.Calls);
    }

    [Fact]
    public async Task Install_migrates_legacy_values_once()
    {
        using var h = new CliHarness();
        Directory.CreateDirectory(Path.Combine(h.AppDir, "Watcher"));
        File.WriteAllText(Path.Combine(h.AppDir, "Watcher", "contray.json"), """{ "url": "http://nas:8085" }""");
        h.Services.Status = new ServiceStatus(ServiceState.Running, "\"C:\\old\\Watcher\\ConWatch.exe\" --urls http://0.0.0.0:8085", true);
        await h.RunAsync("service", "install");
        Assert.Equal("http://0.0.0.0:8085", h.Services.Installed!.ListenUrl);
        Assert.Equal("http://nas:8085", h.Services.Installed.ServerUrl);

        // Second run: the machine file now exists; legacy files are ignored even if changed.
        File.WriteAllText(Path.Combine(h.AppDir, "Watcher", "contray.json"), """{ "url": "http://other:1" }""");
        await h.RunAsync("service", "install");
        Assert.Equal("http://nas:8085", h.Services.Installed!.ServerUrl);
    }

    [Fact]
    public async Task Elevated_commands_relaunch_when_not_elevated_and_return_the_child_code()
    {
        using var h = new CliHarness(elevated: false);
        h.Elevation.RelaunchCode = ExitCodes.ElevationDeclined;
        foreach (var args in new[] { new[] { "service", "install", "--port", "1" }, ["service", "uninstall"], ["service", "start"], ["service", "stop"] })
        {
            var (code, _, _) = await h.RunAsync(args);
            Assert.Equal(ExitCodes.ElevationDeclined, code);
            Assert.Equal(args, h.Elevation.RelaunchArgs);
        }
        Assert.Empty(h.Services.Calls);
    }

    [Fact]
    public async Task Status_never_elevates()
    {
        using var h = new CliHarness(elevated: false);
        await h.RunAsync("service", "status");
        Assert.Null(h.Elevation.RelaunchArgs);
    }

    [Fact]
    public async Task Start_stop_uninstall_route_and_report_not_installed()
    {
        using var h = new CliHarness();
        Assert.Equal(ExitCodes.NotInstalled, (await h.RunAsync("service", "start")).Code);
        Assert.Equal(ExitCodes.NotInstalled, (await h.RunAsync("service", "stop")).Code);
        Assert.Equal(ExitCodes.NotInstalled, (await h.RunAsync("service", "uninstall")).Code);
        Assert.DoesNotContain("start", h.Services.Calls);

        h.Services.Status = new ServiceStatus(ServiceState.Stopped, "\"x\" serve", true);
        Assert.Equal(0, (await h.RunAsync("service", "start")).Code);
        Assert.Equal(0, (await h.RunAsync("service", "stop")).Code);
        Assert.Equal(0, (await h.RunAsync("service", "uninstall")).Code);
        Assert.Equal(["start", "stop", "uninstall"], h.Services.Calls.Where(c => c != "status"));
    }

    [Fact]
    public async Task Control_failures_map_to_exit_four()
    {
        using var h = new CliHarness();
        h.Services.Status = new ServiceStatus(ServiceState.Stopped, "\"x\" serve", true);
        h.Services.Throw = new ServiceControlException("sc.exe start failed: 1053");
        var (code, _, error) = await h.RunAsync("service", "start");
        Assert.Equal(ExitCodes.ServiceControlFailed, code);
        Assert.Contains("1053", error);
    }

    [Fact]
    public async Task Status_prints_every_line_and_probes_the_server_url()
    {
        using var h = new CliHarness();
        h.WriteMachine(new ConWatchConfig(ListenUrl: "http://0.0.0.0:8080", DataDir: @"C:\d", ServerUrl: "http://localhost:8080"));
        h.Services.Status = new ServiceStatus(ServiceState.Running, "\"C:\\app\\conwatch.exe\" serve", true);
        var (code, output, _) = await h.RunAsync("service", "status");
        Assert.Equal(0, code);
        Assert.Contains("Running (auto-start)", output);
        Assert.Contains("\"C:\\app\\conwatch.exe\" serve", output);
        Assert.Contains("http://0.0.0.0:8080", output);
        Assert.Contains(@"C:\d", output);
        Assert.Contains("Healthy", output);
        Assert.Contains("41 ms", output);
        Assert.Equal("http://localhost:8080", h.Health.ProbedUrl);
    }

    [Fact]
    public async Task Status_reports_probe_failures_and_not_installed()
    {
        using var h = new CliHarness();
        h.Services.Status = new ServiceStatus(ServiceState.Stopped, "\"x\" serve", true);
        h.Health.Result = new HealthResult(null, "refused", 3);
        var (_, output, _) = await h.RunAsync("service", "status");
        Assert.Contains("Stopped", output);
        Assert.Contains("refused", output);

        h.Services.Status = ServiceStatus.NotInstalled;
        var (code, output2, _) = await h.RunAsync("service", "status");
        Assert.Equal(ExitCodes.NotInstalled, code);
        Assert.Contains("not installed", output2);
    }
}
