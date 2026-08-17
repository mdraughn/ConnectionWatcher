using System.Net.Http.Json;
using ConPersistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ConWatch.Tests;

public class HostSmokeTests : IDisposable
{
    private readonly ConWatchFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task App_boots_migrates_seeds_and_reports_health()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/v1/health");
        response.EnsureSuccessStatusCode();

        var health = await response.Content.ReadFromJsonAsync<HealthDto>();
        Assert.NotNull(health);
        Assert.True(health.EngineRunning);
        Assert.Equal(0, health.TargetCount);

        // Migration ran and builtin profiles were seeded.
        Assert.True(File.Exists(_factory.DbPath));
        using var scope = _factory.Services.CreateScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ConWatchDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();
        Assert.Equal(2, await db.Profiles.CountAsync());
    }

    private sealed record HealthDto(string Status, bool EngineRunning, int TargetCount);
}
