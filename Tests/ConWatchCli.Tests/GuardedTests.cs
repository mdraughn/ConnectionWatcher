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

    [Fact]
    public async Task Ctrl_c_during_serve_startup_is_a_cancel_not_a_failure()
    {
        // The host's StartAsync throws when Ctrl+C lands before it is up; that
        // used to print "The operation was canceled." and exit 4.
        using var h = new CliHarness();
        using var cts = new CancellationTokenSource();
        h.ServeBody = ct =>
        {
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(0);
        };
        var (code, _, error) = await h.RunAsync(cts.Token, "serve");
        Assert.Equal(130, code);
        Assert.Equal("Cancelled.", error.Trim());
    }

    [Fact]
    public async Task A_cancellation_the_user_did_not_ask_for_is_still_a_failure()
    {
        using var h = new CliHarness();
        h.ServeBody = _ => throw new TaskCanceledException("timed out somewhere inside");
        var (code, _, error) = await h.RunAsync("serve");
        Assert.Equal(ExitCodes.ServiceControlFailed, code);
        Assert.Contains("timed out somewhere inside", error);
    }

    [Fact]
    public async Task Platform_not_supported_maps_to_service_control_failed()
    {
        using var h = new CliHarness();
        h.Services.Status = new ServiceStatus(ServiceState.Stopped, "\"x\" serve", true);
        h.Services.Throw = new PlatformNotSupportedException("no services here");
        var (code, _, error) = await h.RunAsync("service", "start");
        Assert.Equal(ExitCodes.ServiceControlFailed, code);
        Assert.Contains("no services here", error);
    }
}
