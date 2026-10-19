using System.Net.Http.Json;
using System.Text.Json;
using ConCore.Discovery;
using ConCore.Targets;
using ConEngine.Dns;
using ConEngine.Events;
using ConEngine.Topology;
using ConEngine.Tracking;
using ConPersistence.Config;

namespace ConWatch.Tests;

/// <summary>
/// Freezes the enum member names that leave the process: they are persisted in DB rows,
/// compared as string literals in several files and the React client, sent on the wire and
/// over MQTT, read by the tray and used as Prometheus label values. Tests/contracts/vocabulary.json
/// is the shared fixture (Vitest and later tasks read it). Regenerate only with UPDATE_CONTRACTS=1.
/// </summary>
public class VocabularyTests
{
    private const string File = "vocabulary.json";

    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    private static Dictionary<string, string[]> Actual() => new()
    {
        [nameof(HostState)] = Enum.GetNames<HostState>(),
        [nameof(TransitionTrigger)] = Enum.GetNames<TransitionTrigger>(),
        [nameof(TransitionSubjectKind)] = Enum.GetNames<TransitionSubjectKind>(),
        [nameof(VerdictState)] = Enum.GetNames<VerdictState>(),
        [nameof(ModelWarningKind)] = Enum.GetNames<ModelWarningKind>(),
        [nameof(TargetScope)] = Enum.GetNames<TargetScope>(),
        [nameof(DiscoveryKind)] = Enum.GetNames<DiscoveryKind>(),
        [nameof(DiscoveryMethod)] = Enum.GetNames<DiscoveryMethod>(),
        [nameof(DiscoveryStatus)] = Enum.GetNames<DiscoveryStatus>(),
        [nameof(DnsBlameKind)] = Enum.GetNames<DnsBlameKind>(),
        [nameof(DnsSummaryState)] = Enum.GetNames<DnsSummaryState>(),
    };

    private static string Render()
    {
        var doc = new Dictionary<string, object>();
        foreach (var (name, members) in Actual()) doc[name] = members;
        doc["Markers"] = new[] { "HistoryReset" };
        doc["GlitchClass"] = new Dictionary<string, int>
        {
            [nameof(GlitchClass.Target)] = (int)GlitchClass.Target,
            [nameof(GlitchClass.Internet)] = (int)GlitchClass.Internet,
        };
        return JsonSerializer.Serialize(doc, Pretty);
    }

    private static JsonElement Frozen() =>
        JsonDocument.Parse(System.IO.File.ReadAllText(ContractFiles.PathOf(File))).RootElement;

    [Fact]
    public void Persisted_and_wire_names_are_frozen()
    {
        if (Environment.GetEnvironmentVariable("UPDATE_CONTRACTS") == "1")
            ContractFiles.AssertMatches(File, Render());

        var frozen = Frozen();
        foreach (var (name, members) in Actual())
        {
            var expected = frozen.GetProperty(name).EnumerateArray().Select(e => e.GetString()!).ToArray();
            Assert.True(expected.SequenceEqual(members),
                $"{name}: persisted/wire name changed — add a data migration and update every consumer, or keep the name.\n" +
                $"frozen: {string.Join(", ", expected)}\nactual: {string.Join(", ", members)}");
        }

        Assert.Equal(0, (int)GlitchClass.Target);
        Assert.Equal(1, (int)GlitchClass.Internet);
        var glitch = frozen.GetProperty("GlitchClass");
        Assert.Equal(0, glitch.GetProperty("Target").GetInt32());
        Assert.Equal(1, glitch.GetProperty("Internet").GetInt32());
        Assert.Equal(2, glitch.EnumerateObject().Count());
        Assert.Equal(["HistoryReset"], frozen.GetProperty("Markers").EnumerateArray().Select(e => e.GetString()!).ToArray());
    }

    [Fact]
    public void The_fixture_holds_exactly_the_agreed_keys()
    {
        var keys = Frozen().EnumerateObject().Select(p => p.Name).Order().ToArray();
        var expected = Actual().Keys.Concat(["Markers", "GlitchClass"]).Order().ToArray();
        Assert.Equal(expected, keys);
    }

    [Fact]
    public async Task The_HistoryReset_literal_ConfigService_writes_equals_the_frozen_marker()
    {
        var marker = Frozen().GetProperty("Markers")[0].GetString();
        using var factory = new ConWatchFactory();
        var client = factory.CreateClient();
        await ConfigApiTests.SeedHomeAsync(client);
        var google = (await client.GetFromJsonAsync<List<TargetDto>>("/api/v1/targets"))!.Single(t => t.Name == "Google");

        var put = await client.PutAsJsonAsync($"/api/v1/targets/{google.Id}?addressChange=correction",
            google with { Address = "8.8.4.4" });
        Assert.Equal(System.Net.HttpStatusCode.NoContent, put.StatusCode);

        var log = await client.GetFromJsonAsync<JsonElement>($"/api/v1/transitions?subjectId={google.Id}&limit=50");
        Assert.Contains(log.GetProperty("items").EnumerateArray(),
            i => i.GetProperty("trigger").GetString() == marker);
    }
}
