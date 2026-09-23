using ConWatchCli.Services;

namespace ConWatchCli.Tests;

public sealed class GuardedTests
{
    [Fact]
    public async Task An_unmapped_exception_is_caught_and_maps_to_service_control_failed()
    {
        using var h = new CliHarness();
        h.Services.Status = new ServiceStatus(ServiceState.Stopped, "\"x\" serve", true);
        h.Services.Throw = new InvalidOperationException("boom");
        var (code, _, error) = await h.RunAsync("service", "start");
        Assert.Equal(ExitCodes.ServiceControlFailed, code);
        Assert.Contains("boom", error);
    }
}
