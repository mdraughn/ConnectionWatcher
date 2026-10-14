using System.Text.Json;
using ConCore.Targets;
using ConEngine.Configuration;
using ConPersistence.Config;
using Microsoft.EntityFrameworkCore;

namespace ConPersistence.Tests;

/// <summary>Spec 2026-10-10 §4.1: the SystemManaged flag, default false everywhere.</summary>
public class TargetSystemManagedTests : IDisposable
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly TestDbFactory _factory = new();
    private readonly ConfigStore _store;

    public TargetSystemManagedTests() => _store = new ConfigStore(_factory);

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task SystemManaged_round_trips_through_the_store_and_defaults_to_false()
    {
        await _store.SeedAsync(new WatcherOptions
        {
            Targets =
            [
                new TargetConfig { Name = "Router", Address = "203.0.113.1", Profile = "Internal" },
                new TargetConfig
                {
                    Name = "Your resolver 203.0.113.1", Address = "203.0.113.1", Profile = "Resolver",
                    Probe = "dns", Scope = TargetScope.Resolver, SystemManaged = true,
                },
            ],
        });

        var options = await _store.LoadWatcherOptionsAsync();
        Assert.True(options.Targets.Single(t => t.Name.StartsWith("Your resolver")).SystemManaged);
        Assert.False(options.Targets.Single(t => t.Name == "Router").SystemManaged);

        var exported = await _store.ExportAsync();
        Assert.True(exported.Targets.Single(t => t.Name.StartsWith("Your resolver")).SystemManaged);

        await using var db = _factory.CreateDbContext();
        Assert.True((await db.Targets.SingleAsync(t => t.Name.StartsWith("Your resolver"))).SystemManaged);
    }

    [Fact]
    public void SystemManaged_serializes_as_a_boolean()
    {
        var json = JsonSerializer.Serialize(new TargetDto
        {
            Name = "R", Address = "203.0.113.1", Scope = TargetScope.Resolver, Probe = "dns", SystemManaged = true,
        }, Web);
        Assert.Contains("\"systemManaged\":true", json);
        Assert.Contains("\"scope\":\"Resolver\"", json);
    }

    [Fact]
    public void A_target_with_no_systemManaged_key_is_not_managed()
    {
        var dto = JsonSerializer.Deserialize<TargetDto>("{\"name\":\"T\",\"address\":\"203.0.113.5\"}", Web)!;
        Assert.False(dto.SystemManaged);
    }

    [Fact]
    public void SystemManaged_requires_the_Resolver_scope()
    {
        var result = new WatcherOptionsValidator().Validate(null, new WatcherOptions
        {
            Targets = [new TargetConfig { Name = "T", Address = "203.0.113.5", SystemManaged = true }],
        });
        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("system-managed but not a Resolver"));
    }
}
