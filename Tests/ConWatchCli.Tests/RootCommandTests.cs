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

    [Fact]
    public void Strip_removes_the_pair_anywhere()
    {
        string[][] inputs =
        [
            ["service", CliApp.OutputOption, "x.log", "start"],
            [CliApp.OutputOption, "x.log", "service", "start"],
            ["service", "start", CliApp.OutputOption, "x.log"],
        ];
        foreach (var input in inputs)
        {
            var (args, file) = CliApp.StripOutputOption(input);
            Assert.Equal(["service", "start"], args);
            Assert.Equal("x.log", file);
        }
        var (plain, none) = CliApp.StripOutputOption(["service", "start"]);
        Assert.Equal(["service", "start"], plain);
        Assert.Null(none);
    }

    [Fact]
    public void A_dangling_output_flag_is_left_in_place()
    {
        var (args, file) = CliApp.StripOutputOption(["service", "start", CliApp.OutputOption]);
        Assert.Equal(["service", "start", CliApp.OutputOption], args);
        Assert.Null(file);
    }

    [Fact]
    public void The_output_option_spelling_is_pinned()
    {
        // WindowsElevation appends this constant and Program strips it; a rename on
        // either side would break every elevated command run from an unelevated prompt.
        Assert.Equal("--__output", CliApp.OutputOption);
    }

    [Fact]
    public async Task A_stripped_elevated_command_parses()
    {
        using var h = new CliHarness();
        h.Services.Status = new ConWatchCli.Services.ServiceStatus(ConWatchCli.Services.ServiceState.Stopped, "\"x\" serve", true);
        var (args, file) = CliApp.StripOutputOption(["service", "start", CliApp.OutputOption, "x"]);
        Assert.Equal("x", file);
        var (code, _, error) = await h.RunAsync(args);
        Assert.Equal(ExitCodes.Ok, code);
        Assert.Empty(error);
        Assert.Contains("start", h.Services.Calls);
    }

    // Spec 2026-09-19-conwatch-cli-design.md section 4: "Exit codes: 0 success, 1 usage error,
    // 2 config invalid, 3 service or tray exe not installed, 4 service control failed,
    // 5 elevation declined, 130 cancelled with Ctrl+C." Scripts and the installer rely on the numbers.
    [Theory]
    [InlineData(nameof(ExitCodes.Ok), ExitCodes.Ok, 0)]
    [InlineData(nameof(ExitCodes.Usage), ExitCodes.Usage, 1)]
    [InlineData(nameof(ExitCodes.ConfigInvalid), ExitCodes.ConfigInvalid, 2)]
    [InlineData(nameof(ExitCodes.NotInstalled), ExitCodes.NotInstalled, 3)]
    [InlineData(nameof(ExitCodes.ServiceControlFailed), ExitCodes.ServiceControlFailed, 4)]
    [InlineData(nameof(ExitCodes.ElevationDeclined), ExitCodes.ElevationDeclined, 5)]
    [InlineData(nameof(ExitCodes.Cancelled), ExitCodes.Cancelled, 130)]
    public void Exit_codes_match_the_documented_numbers(string name, int actual, int documented)
    {
        Assert.True(documented == actual, $"ExitCodes.{name} is {actual}; the spec documents {documented}");
    }
}
