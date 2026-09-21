using ConConfig;

namespace ConWatchCli.Tests;

public sealed class ConfigCommandTests
{
    [Fact]
    public async Task Show_prints_each_value_with_its_source()
    {
        using var h = new CliHarness();
        h.WriteMachine(new ConWatchConfig(ListenUrl: "http://0.0.0.0:9000", ServerUrl: "http://nas:9000"));
        h.WriteUser(new ConWatchConfig(ServerUrl: "http://user:1", Notifications: false));
        h.Env[ConfigResolver.EnvDataDir] = @"E:\env";
        var (code, output, _) = await h.RunAsync("config", "show");
        Assert.Equal(0, code);
        Assert.Contains($"listen-url     http://0.0.0.0:9000  (machine: {h.Paths.MachineFile})", output);
        Assert.Contains(@"data-dir       E:\env  (environment)", output);
        Assert.Contains($"server-url     http://user:1  (user: {h.Paths.UserFile})", output);
        Assert.Contains($"notifications  false  (user: {h.Paths.UserFile})", output);
    }

    [Fact]
    public async Task Show_with_nothing_configured_says_default_everywhere()
    {
        using var h = new CliHarness();
        var (_, output, _) = await h.RunAsync("config", "show");
        Assert.Equal(4, output.Split("(default)").Length - 1);
    }

    [Fact]
    public async Task Show_warns_about_a_malformed_file_and_continues()
    {
        using var h = new CliHarness();
        Directory.CreateDirectory(Path.GetDirectoryName(h.Paths.MachineFile)!);
        File.WriteAllText(h.Paths.MachineFile, "{ nope");
        var (code, output, error) = await h.RunAsync("config", "show");
        Assert.Equal(0, code);
        Assert.Contains(h.Paths.MachineFile, error);
        Assert.Contains("(default)", output);
    }

    [Fact]
    public async Task Set_listen_url_writes_the_machine_file_and_keeps_other_values()
    {
        using var h = new CliHarness();
        h.WriteMachine(new ConWatchConfig(DataDir: @"D:\keep", ServerUrl: "http://keep:1"));
        var (code, output, _) = await h.RunAsync("config", "set", "listen-url", "http://0.0.0.0:7070/");
        Assert.Equal(0, code);
        Assert.Equal("http://0.0.0.0:7070", h.Machine!.ListenUrl);
        Assert.Equal(@"D:\keep", h.Machine.DataDir);
        Assert.Equal("http://keep:1", h.Machine.ServerUrl);
        Assert.Contains(h.Paths.MachineFile, output);
        Assert.Contains("restart", output);
    }

    [Fact]
    public async Task Set_data_dir_creates_it()
    {
        using var h = new CliHarness();
        var dir = Path.Combine(h.Root, "newdata");
        Assert.Equal(0, (await h.RunAsync("config", "set", "data-dir", dir)).Code);
        Assert.Equal(dir, h.Machine!.DataDir);
        Assert.True(Directory.Exists(dir));
    }

    [Fact]
    public async Task Set_server_url_goes_to_machine_or_user_file()
    {
        using var h = new CliHarness();
        Assert.Equal(0, (await h.RunAsync("config", "set", "server-url", "http://nas:8080")).Code);
        Assert.Equal("http://nas:8080", h.Machine!.ServerUrl);
        Assert.Null(h.User);

        Assert.Equal(0, (await h.RunAsync("config", "set", "server-url", "http://me:8080", "--user")).Code);
        Assert.Equal("http://me:8080", h.User!.ServerUrl);
        Assert.Equal("http://nas:8080", h.Machine!.ServerUrl);
    }

    [Fact]
    public async Task Set_rejects_bad_values_without_writing()
    {
        using var h = new CliHarness();
        Assert.Equal(ExitCodes.ConfigInvalid, (await h.RunAsync("config", "set", "listen-url", "nope")).Code);
        Assert.Equal(ExitCodes.ConfigInvalid, (await h.RunAsync("config", "set", "data-dir", "relative")).Code);
        Assert.Null(h.Machine);
        Assert.Equal(ExitCodes.Usage, (await h.RunAsync("config", "set", "colour", "red")).Code);
        Assert.Equal(ExitCodes.Usage, (await h.RunAsync("config", "set", "listen-url", "http://x", "--user")).Code);
    }

    [Fact]
    public async Task Set_elevates_unless_user_scoped()
    {
        using var h = new CliHarness(elevated: false);
        h.Elevation.RelaunchCode = 7;
        Assert.Equal(7, (await h.RunAsync("config", "set", "server-url", "http://nas:8080")).Code);
        Assert.NotNull(h.Elevation.RelaunchArgs);
        Assert.Equal(["config", "set", "server-url", "http://nas:8080"], h.Elevation.RelaunchArgs);
        Assert.Null(h.Machine);

        Assert.Equal(0, (await h.RunAsync("config", "set", "server-url", "http://me:8080", "--user")).Code);
        Assert.Equal("http://me:8080", h.User!.ServerUrl);

        Assert.Equal(0, (await h.RunAsync("config", "show")).Code);
    }

    [Fact]
    public async Task Set_fails_with_config_invalid_when_the_machine_file_cannot_be_opened()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var h = new CliHarness();
        h.WriteMachine(new ConWatchConfig(ServerUrl: "http://keep:1"));
        FileStream? locker = null;
        try
        {
            locker = new FileStream(h.Paths.MachineFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var (code, _, error) = await h.RunAsync("config", "set", "server-url", "http://nas:8080");
            Assert.Equal(ExitCodes.ConfigInvalid, code);
            Assert.Contains(h.Paths.MachineFile, error);
            Assert.False(File.Exists(h.Paths.MachineFile + ".corrupt"));
        }
        finally
        {
            locker?.Dispose();
        }
    }

    [Fact]
    public async Task Set_preserves_unknown_properties_in_the_machine_file()
    {
        using var h = new CliHarness();
        Directory.CreateDirectory(Path.GetDirectoryName(h.Paths.MachineFile)!);
        File.WriteAllText(h.Paths.MachineFile, """{ "serverUrl": "http://keep:1", "future": { "x": 1 } }""");
        var (code, _, _) = await h.RunAsync("config", "set", "listen-url", "http://0.0.0.0:7070");
        Assert.Equal(0, code);
        var text = File.ReadAllText(h.Paths.MachineFile);
        Assert.Contains("future", text);
        Assert.Equal("http://keep:1", h.Machine!.ServerUrl);
        Assert.Equal("http://0.0.0.0:7070", h.Machine.ListenUrl);
    }

    [Fact]
    public async Task Set_moves_a_malformed_file_aside_instead_of_overwriting_it()
    {
        using var h = new CliHarness();
        Directory.CreateDirectory(Path.GetDirectoryName(h.Paths.MachineFile)!);
        File.WriteAllText(h.Paths.MachineFile, "{ nope");
        var (code, _, error) = await h.RunAsync("config", "set", "server-url", "http://nas:8080");
        Assert.Equal(0, code);
        Assert.Equal("http://nas:8080", h.Machine!.ServerUrl);
        Assert.True(File.Exists(h.Paths.MachineFile + ".corrupt"));
        Assert.Equal("{ nope", File.ReadAllText(h.Paths.MachineFile + ".corrupt"));
        Assert.Contains(".corrupt", error);
    }
}
