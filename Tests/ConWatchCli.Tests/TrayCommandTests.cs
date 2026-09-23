namespace ConWatchCli.Tests;

public sealed class TrayCommandTests
{
    [Fact]
    public async Task Tray_launches_the_exe_beside_the_cli()
    {
        using var h = new CliHarness(elevated: false);
        var (code, _, _) = await h.RunAsync("tray");
        Assert.Equal(0, code);
        Assert.Equal(h.TrayExePath(), h.TrayLauncher.Launched);
        Assert.Null(h.Elevation.RelaunchArgs);
    }

    [Fact]
    public async Task Tray_already_running_reports_it_and_does_not_launch()
    {
        using var h = new CliHarness(elevated: false);
        h.TrayLauncher.Running = "conwatch-tray.exe (PID 1234)";
        var (code, output, _) = await h.RunAsync("tray");
        Assert.Equal(0, code);
        Assert.Null(h.TrayLauncher.Launched);
        Assert.Contains("The tray is already running: conwatch-tray.exe (PID 1234)", output);
    }

    [Fact]
    public async Task Tray_already_running_as_the_09_tray_tells_the_user_to_close_it()
    {
        using var h = new CliHarness(elevated: false);
        h.TrayLauncher.Running = "ConTray.exe (PID 1234, the 0.9 tray)";
        var (code, output, _) = await h.RunAsync("tray");
        Assert.Equal(0, code);
        Assert.Null(h.TrayLauncher.Launched);
        Assert.Contains("The tray is already running: ConTray.exe (PID 1234, the 0.9 tray)", output);
        Assert.Contains("Close it (right-click, Exit) and run conwatch tray again to start the new tray.", output);
    }

    [Fact]
    public async Task Tray_missing_exe_is_exit_three()
    {
        using var h = new CliHarness { TrayLauncher = new FakeTrayLauncher(present: false) };
        var (code, _, error) = await h.RunAsync("tray");
        Assert.Equal(ExitCodes.NotInstalled, code);
        Assert.Contains("conwatch-tray", error);
    }

    [Fact]
    public async Task Tray_install_registers_autostart_and_optionally_writes_the_user_url()
    {
        using var h = new CliHarness(elevated: false);
        File.WriteAllText(h.TrayExePath(), "");
        Assert.Equal(0, (await h.RunAsync("tray", "install")).Code);
        Assert.Equal(h.TrayExePath(), h.TrayAutostart.InstalledPath);
        Assert.Null(h.User);

        Assert.Equal(0, (await h.RunAsync("tray", "install", "--url", "http://nas:8080/")).Code);
        Assert.Equal("http://nas:8080", h.User!.ServerUrl);
        Assert.Null(h.Elevation.RelaunchArgs);
    }

    [Fact]
    public async Task Tray_install_rejects_a_bad_url_and_a_missing_exe()
    {
        using var h = new CliHarness();
        Assert.Equal(ExitCodes.NotInstalled, (await h.RunAsync("tray", "install")).Code);
        File.WriteAllText(h.TrayExePath(), "");
        Assert.Equal(ExitCodes.ConfigInvalid, (await h.RunAsync("tray", "install", "--url", "nas")).Code);
        Assert.False(h.TrayAutostart.IsInstalled);
    }

    [Fact]
    public async Task Tray_uninstall_removes_autostart()
    {
        using var h = new CliHarness();
        h.TrayAutostart.Install("x");
        Assert.Equal(0, (await h.RunAsync("tray", "uninstall")).Code);
        Assert.False(h.TrayAutostart.IsInstalled);
    }
}
