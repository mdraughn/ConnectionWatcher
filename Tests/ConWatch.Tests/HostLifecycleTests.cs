using System.Net.Http.Json;
using ConWatch;
using Microsoft.Extensions.DependencyInjection;

namespace ConWatch.Tests;

public class HostLifecycleTests
{
    [Fact]
    public async Task Rebuild_after_stop_does_not_start_a_new_engine()
    {
        using var factory = new ConWatchFactory();
        var client = factory.CreateClient();
        var apply = await client.PostAsJsonAsync("/api/v1/setup/apply",
            new SetupApplyRequest(new SetupTarget("Router", "192.168.1.1"), null, ["8.8.8.8", "1.1.1.1"]));
        apply.EnsureSuccessStatusCode();
        var host = factory.Services.GetRequiredService<WatcherHostService>();
        await Task.Delay(300); // debounce is 0 s in tests; let the first rebuild land
        Assert.NotNull(host.Engine);

        // A rebuild that was queued behind StopAsync on the rebuild lock runs
        // after it releases; it must find the host stopped and do nothing.
        await host.StopAsync(CancellationToken.None);
        await host.RebuildAsync();

        Assert.Null(host.Engine);
    }
}
