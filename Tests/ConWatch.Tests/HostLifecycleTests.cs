using System.Net;
using System.Net.Http.Json;
using ConPersistence;
using ConWatch;
using Microsoft.EntityFrameworkCore;
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

    /// <summary>
    /// Config that the validator rejects can only reach the database around
    /// the API (a raw write, or an upgrade that tightened the rules). The
    /// rebuild must then report failure and /health must say so, rather than
    /// reporting success and leaving the node silently engine-less.
    /// </summary>
    [Fact]
    public async Task Invalid_stored_config_makes_rebuild_report_failure_and_health_ConfigInvalid()
    {
        using var factory = new ConWatchFactory();
        var client = factory.CreateClient();
        await ConfigApiTests.SeedHomeAsync(client);
        var host = factory.Services.GetRequiredService<WatcherHostService>();
        await ConfigApiTests.Eventually(async () =>
        {
            var response = await client.GetAsync("/api/v1/health");
            return host.Engine is not null && response.IsSuccessStatusCode;
        });

        // Straight to the database, bypassing the validating API: Faltering
        // must stay below Responding, and this row inverts them.
        var dbFactory = factory.Services.GetRequiredService<IDbContextFactory<ConWatchDbContext>>();
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            var profile = await db.Profiles.SingleAsync(p => p.Name == "Fast");
            profile.RespondingThreshold = 0.5;
            profile.FalteringThreshold = 0.75;
            await db.SaveChangesAsync();
        }

        Assert.False(await host.RebuildAsync());
        Assert.Null(host.Engine);

        var health = await client.GetAsync("/api/v1/health");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, health.StatusCode);
        var body = await health.Content.ReadFromJsonAsync<InvalidHealthDto>();
        Assert.NotNull(body);
        Assert.Equal("ConfigInvalid", body.Status);
        Assert.False(string.IsNullOrWhiteSpace(body.StartFailure));
    }

    private sealed record InvalidHealthDto(string Status, bool EngineRunning, string? StartFailure);
}
