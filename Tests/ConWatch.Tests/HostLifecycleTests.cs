using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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
        // Debounce is 0 s in tests, but the first rebuild still needs real time to land.
        await ConfigApiTests.Eventually(() => Task.FromResult(host.Engine is not null));

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

    /// <summary>Faltering must stay below Responding; this inverts them behind the validating API's back.</summary>
    private static async Task InvertFastProfileAsync(IServiceProvider services)
    {
        var dbFactory = services.GetRequiredService<IDbContextFactory<ConWatchDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();
        var profile = await db.Profiles.SingleAsync(p => p.Name == "Fast");
        profile.RespondingThreshold = 0.5;
        profile.FalteringThreshold = 0.75;
        await db.SaveChangesAsync();
    }

    private static async Task<(HttpStatusCode Status, InvalidHealthDto Body)> HealthAsync(HttpClient client)
    {
        var health = await client.GetAsync("/api/v1/health");
        return (health.StatusCode, (await health.Content.ReadFromJsonAsync<InvalidHealthDto>())!);
    }

    /// <summary>
    /// ConfigInvalid is something to fix, not wait out: once a valid write
    /// lands, the next rebuild must start the engine and the health report
    /// must drop both the status and the stored complaint.
    /// </summary>
    [Fact]
    public async Task ConfigInvalid_clears_after_a_good_write()
    {
        using var factory = new ConWatchFactory();
        var client = factory.CreateClient();
        await ConfigApiTests.SeedHomeAsync(client);
        var host = factory.Services.GetRequiredService<WatcherHostService>();
        await HostTestWait.Eventually(() => Task.FromResult(host.EngineRunning));
        await HostTestWait.SettledRebuildCountAsync(host);

        await InvertFastProfileAsync(factory.Services);
        Assert.False(await host.RebuildAsync());
        var broken = await HealthAsync(client);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, broken.Status);
        Assert.Equal("ConfigInvalid", broken.Body.Status);

        // The good write: the profile back to valid thresholds, through the API.
        var profiles = await client.GetFromJsonAsync<List<ConPersistence.Config.ProfileDto>>("/api/v1/profiles");
        var fast = profiles!.Single(p => p.Name == "Fast");
        (await client.PutAsJsonAsync($"/api/v1/profiles/{fast.Id}",
            fast with { RespondingThreshold = 0.9, FalteringThreshold = 0.5 })).EnsureSuccessStatusCode();

        await HostTestWait.Eventually(() => Task.FromResult(host.EngineRunning));
        var healed = await HealthAsync(client);
        Assert.Equal(HttpStatusCode.OK, healed.Status);
        Assert.Equal("Healthy", healed.Body.Status);
        Assert.Null(healed.Body.StartFailure);
    }

    /// <summary>
    /// A node that boots on a database the validator refuses (an upgrade that
    /// tightened the rules, a raw edit) must still come up: serving the API
    /// and saying ConfigInvalid, so the operator can fix it from the UI.
    /// </summary>
    [Fact]
    public async Task Booting_on_an_invalid_db_reports_ConfigInvalid_and_serves_the_api()
    {
        var dir = Directory.CreateTempSubdirectory("conwatch-invalid-boot").FullName;
        var dbPath = Path.Combine(dir, "conwatch.db");
        try
        {
            using (var first = new ConWatchFactory(dbPath))
            {
                var seeded = first.CreateClient();
                await ConfigApiTests.SeedHomeAsync(seeded);
                var firstHost = first.Services.GetRequiredService<WatcherHostService>();
                await HostTestWait.Eventually(() => Task.FromResult(firstHost.EngineRunning));
                await HostTestWait.SettledRebuildCountAsync(firstHost);
                await InvertFastProfileAsync(first.Services);
            }

            using var second = new ConWatchFactory(dbPath);
            var client = second.CreateClient();
            var host = second.Services.GetRequiredService<WatcherHostService>();

            Assert.False(host.EngineRunning);
            var health = await HealthAsync(client);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, health.Status);
            Assert.Equal("ConfigInvalid", health.Body.Status);
            Assert.False(string.IsNullOrWhiteSpace(health.Body.StartFailure));

            // The API still answers, config included, so the fix is possible.
            var targets = await client.GetAsync("/api/v1/targets");
            Assert.Equal(HttpStatusCode.OK, targets.StatusCode);
            Assert.Equal(3, (await targets.Content.ReadFromJsonAsync<List<ConPersistence.Config.TargetDto>>())!.Count);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/profiles")).StatusCode);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (IOException) { /* best-effort, handles may linger on Windows */ }
        }
    }

    /// <summary>
    /// Writes arriving inside the debounce window collapse into one rebuild
    /// that reflects all of them (the engine is stopped and rebuilt, so a
    /// rebuild per write would also re-measure the world three times).
    /// </summary>
    [Fact]
    public async Task Rapid_writes_coalesce_into_one_rebuild()
    {
        using var factory = new ConWatchFactory();
        var client = factory.CreateClient();
        await ConfigApiTests.SeedHomeAsync(client);
        var host = factory.Services.GetRequiredService<WatcherHostService>();
        await HostTestWait.Eventually(() => Task.FromResult(host.EngineRunning));
        var before = await HostTestWait.SettledRebuildCountAsync(host);

        // Seeding used the factory's instant debounce; the burst gets a real one.
        HostTestWait.SetRebuildDebounce(factory.Services, 1);
        string[] names = ["Alpha", "Bravo", "Charlie"];
        for (var i = 0; i < names.Length; i++)
            (await client.PostAsJsonAsync("/api/v1/targets", new ConPersistence.Config.TargetDto
            {
                Name = names[i], Address = $"192.0.2.{10 + i}", Parent = "Router", Profile = "Fast",
            })).EnsureSuccessStatusCode();

        await HostTestWait.Eventually(() => Task.FromResult(host.RebuildCount > before));
        Assert.Equal(before + 1, await HostTestWait.SettledRebuildCountAsync(host));

        var status = await client.GetFromJsonAsync<JsonElement>("/api/v1/status");
        var listed = status.GetProperty("targets").EnumerateArray()
            .Select(t => t.GetProperty("name").GetString()).ToHashSet();
        Assert.Superset(new HashSet<string?>(names), listed);
    }

    private sealed record InvalidHealthDto(string Status, bool EngineRunning, string? StartFailure);
}
