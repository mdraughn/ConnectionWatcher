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
