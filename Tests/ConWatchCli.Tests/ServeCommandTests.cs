using ConConfig;

namespace ConWatchCli.Tests;

public sealed class ServeCommandTests
{
    [Fact]
    public async Task Serve_passes_defaults_as_host_arguments()
    {
        using var h = new CliHarness();
        var (code, _, _) = await h.RunAsync("serve");
        Assert.Equal(0, code);
        Assert.Equal(["--urls", ConfigResolver.DefaultListenUrl, "--Persistence:DbPath", Path.Combine(h.Paths.DefaultDataDir, "conwatch.db")], h.ServeArgs);
    }

    [Fact]
    public async Task Serve_reads_the_machine_file_and_flags_win()
    {
        using var h = new CliHarness();
        h.WriteMachine(new ConWatchConfig(ListenUrl: "http://0.0.0.0:9000", DataDir: Path.Combine(h.Root, "d")));
        await h.RunAsync("serve");
        Assert.Equal("http://0.0.0.0:9000", h.ServeArgs![1]);
        Assert.Equal(Path.Combine(h.Root, "d", "conwatch.db"), h.ServeArgs[3]);

        await h.RunAsync("serve", "--url", "http://0.0.0.0:9001");
        Assert.Equal("http://0.0.0.0:9001", h.ServeArgs![1]);
    }

    [Fact]
    public async Task Serve_environment_beats_the_machine_file()
    {
        using var h = new CliHarness();
        h.WriteMachine(new ConWatchConfig(ListenUrl: "http://0.0.0.0:9000"));
        h.Env[ConfigResolver.EnvListenUrl] = "http://0.0.0.0:9002";
        await h.RunAsync("serve");
        Assert.Equal("http://0.0.0.0:9002", h.ServeArgs![1]);
    }

    [Fact]
    public async Task Serve_never_elevates()
    {
        using var h = new CliHarness(elevated: false);
        await h.RunAsync("serve");
        Assert.Null(h.Elevation.RelaunchArgs);
        Assert.NotNull(h.ServeArgs);
    }
}
