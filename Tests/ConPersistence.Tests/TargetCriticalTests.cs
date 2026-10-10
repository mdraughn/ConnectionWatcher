using System.Text.Json;
using ConEngine.Configuration;
using ConPersistence.Config;
using Microsoft.EntityFrameworkCore;

namespace ConPersistence.Tests;

/// <summary>Spec 2026-10-09 §4.7 / D6: the Critical flag, default false everywhere.</summary>
public class TargetCriticalTests : IDisposable
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly TestDbFactory _factory = new();
    private readonly ConfigStore _store;

    public TargetCriticalTests() => _store = new ConfigStore(_factory);

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Critical_round_trips_through_the_store_and_defaults_to_false()
    {
        await _store.SeedAsync(new WatcherOptions
        {
            Targets =
            [
                new TargetConfig { Name = "Router", Address = "203.0.113.1", Profile = "Internal", Critical = true },
                new TargetConfig { Name = "Google", Address = "8.8.8.8", Parent = "Router" },
            ],
        });

        var options = await _store.LoadWatcherOptionsAsync();
        Assert.True(options.Targets.Single(t => t.Name == "Router").Critical);
        Assert.False(options.Targets.Single(t => t.Name == "Google").Critical);

        var exported = await _store.ExportAsync();
        Assert.True(exported.Targets.Single(t => t.Name == "Router").Critical);

        await using var db = _factory.CreateDbContext();
        Assert.True((await db.Targets.SingleAsync(t => t.Name == "Router")).Critical);
    }

    [Fact]
    public void Critical_serializes_as_a_boolean()
    {
        var json = JsonSerializer.Serialize(
            new TargetDto { Name = "Router", Address = "203.0.113.1", Critical = true }, Web);
        Assert.Contains("\"critical\":true", json);
    }

    [Fact]
    public void A_target_with_no_critical_key_is_not_critical()
    {
        // An older snapshot, export or API client never sends the key.
        var dto = JsonSerializer.Deserialize<TargetDto>(
            "{\"name\":\"T\",\"address\":\"203.0.113.5\"}", Web)!;
        Assert.False(dto.Critical);
    }
}
