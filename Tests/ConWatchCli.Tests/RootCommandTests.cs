namespace ConWatchCli.Tests;

public sealed class RootCommandTests
{
    [Fact]
    public async Task No_arguments_prints_help_and_returns_zero()
    {
        using var h = new CliHarness();
        var (code, output, _) = await h.RunAsync();
        Assert.Equal(0, code);
        Assert.Contains("serve", output);
        Assert.Contains("version", output);
        Assert.Contains("conwatch.json", output);
        Assert.Contains("service", output);
        Assert.Contains("config", output);
        Assert.Contains("tray", output);
    }

    [Fact]
    public async Task Unknown_command_is_a_usage_error()
    {
        using var h = new CliHarness();
        var (code, _, error) = await h.RunAsync("bogus");
        Assert.Equal(ExitCodes.Usage, code);
        Assert.NotEmpty(error);
    }

    [Fact]
    public async Task Version_prints_the_informational_version()
    {
        using var h = new CliHarness();
        var (code, output, _) = await h.RunAsync("version");
        Assert.Equal(0, code);
        Assert.Contains("9.9.9-test+abc", output);
    }
}
