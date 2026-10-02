using System.Text.Json;
using ConCore.Targets;
using ConEngine.Configuration;
using ConPersistence.Config;

namespace ConPersistence.Tests;

public class TargetScopeTests : IDisposable
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly TestDbFactory _factory = new();
    private readonly ConfigStore _store;

    public TargetScopeTests() => _store = new ConfigStore(_factory);

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Scope_round_trips_through_the_store_and_defaults_to_Verdict()
    {
        await _store.SeedAsync(new WatcherOptions
        {
            Targets =
            [
                new TargetConfig { Name = "Router", Address = "203.0.113.1", Profile = "Internal" },
                new TargetConfig { Name = "NAS", Address = "203.0.113.9", Profile = "Internal", Scope = TargetScope.Local },
            ],
        });

        var options = await _store.LoadWatcherOptionsAsync();

        Assert.Equal(TargetScope.Verdict, options.Targets.Single(t => t.Name == "Router").Scope);
        Assert.Equal(TargetScope.Local, options.Targets.Single(t => t.Name == "NAS").Scope);
    }

    [Fact]
    public void Scope_serializes_as_a_string()
    {
        var json = JsonSerializer.Serialize(
            new TargetDto { Name = "NAS", Address = "203.0.113.9", Scope = TargetScope.Local }, Web);

        Assert.Contains("\"scope\":\"Local\"", json);
    }

    [Fact]
    public void A_target_with_no_scope_key_is_Verdict()
    {
        // What an older source's snapshot or a pre-scope API client sends.
        var dto = JsonSerializer.Deserialize<TargetDto>(
            "{\"name\":\"T\",\"address\":\"203.0.113.5\"}", Web)!;

        Assert.Equal(TargetScope.Verdict, dto.Scope);
    }

    [Fact]
    public void An_unknown_scope_is_rejected_not_defaulted()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<TargetDto>(
            "{\"name\":\"T\",\"address\":\"203.0.113.5\",\"scope\":\"Bogus\"}", Web));
    }
}
