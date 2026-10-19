using System.Collections;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using System.Text.Json;
using ConCore.Targets;
using ConEngine.Configuration;
using ConEngine.Topology;
using ConPersistence.Config;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Sdk;

namespace ConWatch.Tests;

/// <summary>
/// A target's configuration passes through five near-identical mappers: DTO, database
/// row, export/import document, engine options, target graph. These tests fill every
/// public field with a non-default value and follow it through all of them, so the next
/// field added and forgotten by one mapper fails here instead of silently reverting.
/// </summary>
public class ConfigRoundTripTests : IDisposable
{
    private readonly ConWatchFactory _factory = new();
    private readonly HttpClient _client;

    public ConfigRoundTripTests() => _client = _factory.CreateClient();

    public void Dispose() => _factory.Dispose();

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    // ---- reflection helpers (public properties only) ----

    private static IEnumerable<PropertyInfo> Settable(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.SetMethod is { IsPublic: true });

    /// <summary>A value that differs from the type's default for every settable public property, unless overridden.</summary>
    private static T Filled<T>(Dictionary<string, object?> overrides) where T : notnull
    {
        var instance = Activator.CreateInstance<T>();
        Assert.Empty(overrides.Keys.Except(Settable(typeof(T)).Select(p => p.Name)));
        foreach (var p in Settable(typeof(T)))
        {
            var value = overrides.TryGetValue(p.Name, out var o) ? o : Different(p, p.GetValue(instance));
            p.SetValue(instance, value);
        }
        return instance;
    }

    private static object? Different(PropertyInfo p, object? current)
    {
        var type = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
        if (type == typeof(bool)) return !(current is true);
        if (type == typeof(string)) return $"{p.Name}-unique";
        if (type == typeof(Guid)) return Guid.NewGuid();
        if (type == typeof(int)) return (int)(current ?? 0) + 7;
        if (type == typeof(double)) return (double)(current ?? 0.0) + 0.5;
        if (type.IsEnum)
            return Enum.GetValues(type).Cast<object>().First(v => !v.Equals(current));
        throw new NotSupportedException(
            $"No non-default value rule for {p.DeclaringType!.Name}.{p.Name} ({p.PropertyType.Name}); add an override or a rule.");
    }

    private static bool SameValue(object? a, object? b) =>
        a is IEnumerable ea and not string && b is IEnumerable eb and not string
            ? ea.Cast<object?>().SequenceEqual(eb.Cast<object?>())
            : Equals(a, b);

    /// <summary>Every public property of <paramref name="expected"/> not in <paramref name="exceptions"/> comes back equal.</summary>
    private static void AssertSame(object expected, object actual, string stage, Dictionary<string, string> exceptions)
    {
        foreach (var p in expected.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (exceptions.ContainsKey(p.Name)) continue;
            var found = actual.GetType().GetProperty(p.Name, BindingFlags.Public | BindingFlags.Instance);
            if (found is null)
                throw new XunitException($"{stage}: {actual.GetType().Name} has no public property {p.Name}.");
            var want = p.GetValue(expected);
            var got = found.GetValue(actual);
            Assert.True(SameValue(want, got), $"{stage}: {p.Name} was {Show(want)}, came back {Show(got)}.");
        }
    }

    private static string Show(object? v) =>
        v is IEnumerable e and not string ? "[" + string.Join(",", e.Cast<object?>()) + "]" : v?.ToString() ?? "null";

    // The explicit exception lists: every entry says why the field is not compared like the rest.
    private static readonly Dictionary<string, string> TargetStoreExceptions = new()
    {
        ["Id"] = "assigned by the store on create; checked non-null and stable across import instead",
        ["SystemManaged"] = "read-only on the API: ignored on create, so a stored target never carries it",
    };

    private static readonly Dictionary<string, string> TargetOptionsExceptions = new()
    {
        ["Id"] = "assigned by the store on create; checked non-null and stable across import instead",
        ["SystemManaged"] = "read-only on the API: ignored on create, so a stored target never carries it",
    };

    private static readonly Dictionary<string, string> TargetGraphExceptions = new()
    {
        ["Parent"] = "name in the options, id in the graph (ParentId); checked explicitly",
        ["Profile"] = "name in the options, ProfileName in the graph; checked explicitly",
    };

    private static readonly Dictionary<string, string> ProfileStoreExceptions = new()
    {
        ["Id"] = "assigned by the store on create; checked non-null and stable across import instead",
    };

    private static readonly Dictionary<string, string> ProfileEngineExceptions = new()
    {
        ["Id"] = "the engine options key profiles by name",
        ["Name"] = "the engine options key profiles by name; PingProfile.Name checked explicitly",
        ["Description"] = "display-only text; the engine never sees it",
    };

    private static readonly Dictionary<string, string> PoolStoreExceptions = new()
    {
        ["Id"] = "assigned by the store on create; checked non-null and stable across import instead",
    };

    private static readonly Dictionary<string, string> PoolGraphExceptions = new()
    {
        ["Members"] = "names in the options, MemberTargetIds in the graph; checked explicitly, in order",
        ["Parent"] = "name in the options, ParentId in the graph; checked explicitly",
    };

    // ---- CF5 ----

    [Fact]
    public async Task Every_public_target_field_survives_store_export_import_and_graph()
    {
        using var scope = _factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ConfigService>();
        var store = scope.ServiceProvider.GetRequiredService<ConfigStore>();

        var profile = Filled<ProfileDto>(new()
        {
            ["Id"] = null, ["Name"] = "Tuned", ["Description"] = "Tuned for the round trip.",
            ["NormalIntervalSeconds"] = 2.5, ["PingTimeoutSeconds"] = 1.5, ["BurstIntervalSeconds"] = 0.25,
            ["BurstMaxDurationSeconds"] = 30.0, ["HistorySize"] = 40, ["MinUsableSample"] = 8,
            ["RespondingThreshold"] = 0.9, ["FalteringThreshold"] = 0.6, ["DownThreshold"] = 0.05, ["RecoveringThreshold"] = 0.3,
        });
        // The overrides keep the numbers legal; make sure each is still away from its default.
        var profileDefaults = new ProfileDto { Name = "x" };
        foreach (var p in Settable(typeof(ProfileDto)).Where(p => p.Name != "Id"))
            Assert.False(SameValue(p.GetValue(profileDefaults), p.GetValue(profile)), $"profile {p.Name} left at its default");

        var hub = new TargetDto { Name = "Hub", Address = "192.0.2.1" };
        var spare = new TargetDto { Name = "Spare", Address = "192.0.2.79", Parent = "Hub" };
        // Local targets cannot have a parent, so the fields split over two targets: Full has the parent, Side the Local scope.
        var full = Filled<TargetDto>(new()
        {
            ["Id"] = null, ["Name"] = "Full", ["Address"] = "192.0.2.77", ["Parent"] = "Hub", ["Profile"] = "Tuned",
            ["Probe"] = "tcp:443", ["Scope"] = TargetScope.Verdict,
        });
        var side = Filled<TargetDto>(new()
        {
            ["Id"] = null, ["Name"] = "Side", ["Address"] = "192.0.2.78", ["Parent"] = null, ["Profile"] = "Tuned",
            ["Probe"] = "tcp:80", ["Scope"] = TargetScope.Local,
        });
        var pool = Filled<PoolDto>(new()
        {
            ["Id"] = null, ["Name"] = "Mix", ["Members"] = new List<string> { "Spare", "Full" }, ["Parent"] = "Hub",
        });

        // Every settable field is non-default in at least one target (Parent in Full, Scope in Side).
        var targetDefaults = new TargetDto { Name = "x", Address = "x" };
        foreach (var p in Settable(typeof(TargetDto)).Where(p => p.Name != "Id"))
            Assert.True(!SameValue(p.GetValue(targetDefaults), p.GetValue(full)) || !SameValue(p.GetValue(targetDefaults), p.GetValue(side)),
                $"target {p.Name} is at its default in both fixtures");

        Assert.True((await service.UpsertProfileAsync(profile)).Success);
        foreach (var t in new[] { hub, spare, full, side })
        {
            var r = await service.UpsertTargetAsync(t);
            Assert.True(r.Success, string.Join("; ", r.Errors ?? []));
        }
        var poolResult = await service.UpsertPoolAsync(pool);
        Assert.True(poolResult.Success, string.Join("; ", poolResult.Errors ?? []));

        // Stage 1: the store's export. Stage 2: the same document imported and exported again.
        var exported = await store.ExportAsync();
        VerifyDocument(exported, "export", null);
        var imported = await service.ImportAsync(exported);
        Assert.True(imported.Success, string.Join("; ", imported.Errors ?? []));
        var reExported = await store.ExportAsync();
        VerifyDocument(reExported, "import", exported);

        // Stage 3: the engine options, then the graph.
        var options = await store.LoadWatcherOptionsAsync();
        var graph = TargetGraph.FromOptions(options);

        AssertSame(full, options.Targets.Single(t => t.Name == "Full"), "options Full", TargetOptionsExceptions);
        AssertSame(side, options.Targets.Single(t => t.Name == "Side"), "options Side", TargetOptionsExceptions);

        foreach (var config in options.Targets)
        {
            var target = graph.GetByName(config.Name);
            foreach (var p in typeof(TargetConfig).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (TargetGraphExceptions.ContainsKey(p.Name)) continue;
                var found = typeof(Target).GetProperty(p.Name, BindingFlags.Public | BindingFlags.Instance);
                Assert.True(found is not null, $"graph: Target has no property {p.Name} (TargetConfig.{p.Name} is not reaching the engine, or needs an exception with a reason)");
                Assert.True(SameValue(p.GetValue(config), found.GetValue(target)),
                    $"graph {config.Name}: {p.Name} was {Show(p.GetValue(config))}, came back {Show(found.GetValue(target))}.");
            }
            Assert.Equal(config.Profile, target.ProfileName);
            Assert.Equal(config.Parent is null ? null : graph.GetByName(config.Parent).Id, target.ParentId);
        }
        Assert.Equal(graph.GetByName("Hub").Id, graph.GetByName("Full").ParentId);

        var profileConfig = options.Profiles["Tuned"];
        AssertSame(profile, profileConfig, "options profile", ProfileEngineExceptions);
        var engineProfile = graph.ProfileFor(graph.GetByName("Full"));
        Assert.Equal("Tuned", engineProfile.Name);
        foreach (var p in Settable(typeof(PingProfileConfig)))
        {
            var seconds = p.Name.EndsWith("Seconds", StringComparison.Ordinal);
            var engineName = seconds ? p.Name[..^"Seconds".Length] : p.Name;
            var found = typeof(PingProfile).GetProperty(engineName, BindingFlags.Public | BindingFlags.Instance);
            Assert.True(found is not null, $"graph: PingProfile has no property {engineName} for PingProfileConfig.{p.Name}");
            var got = found.GetValue(engineProfile);
            var want = p.GetValue(profileConfig);
            if (seconds) want = TimeSpan.FromSeconds((double)want!);
            Assert.True(SameValue(want, got), $"graph profile: {engineName} was {Show(want)}, came back {Show(got)}.");
        }

        var poolConfig = options.Pools.Single(p => p.Name == "Mix");
        AssertSame(pool, poolConfig, "options pool", PoolStoreExceptions);
        var poolDef = graph.GetPoolByName("Mix");
        foreach (var p in typeof(PoolConfig).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (PoolGraphExceptions.ContainsKey(p.Name)) continue;
            var found = typeof(PoolDefinition).GetProperty(p.Name, BindingFlags.Public | BindingFlags.Instance);
            Assert.True(found is not null, $"graph: PoolDefinition has no property {p.Name}");
            Assert.True(SameValue(p.GetValue(poolConfig), found.GetValue(poolDef)), $"graph pool: {p.Name} differs");
        }
        Assert.Equal(new[] { "Spare", "Full" }.Select(n => graph.GetByName(n).Id), poolDef.MemberTargetIds);
        Assert.Equal(graph.GetByName("Hub").Id, poolDef.ParentId);

        void VerifyDocument(ConfigDocument doc, string stage, ConfigDocument? previous)
        {
            AssertSame(profile, doc.Profiles.Single(p => p.Name == "Tuned"), $"{stage} profile", ProfileStoreExceptions);
            AssertSame(full, doc.Targets.Single(t => t.Name == "Full"), $"{stage} Full", TargetStoreExceptions);
            AssertSame(side, doc.Targets.Single(t => t.Name == "Side"), $"{stage} Side", TargetStoreExceptions);
            AssertSame(pool, doc.Pools.Single(p => p.Name == "Mix"), $"{stage} pool", PoolStoreExceptions);
            Assert.All(doc.Targets, t => Assert.False(t.SystemManaged));
            Assert.NotNull(doc.Profiles.Single(p => p.Name == "Tuned").Id);
            Assert.NotNull(doc.Targets.Single(t => t.Name == "Full").Id);
            Assert.NotNull(doc.Pools.Single(p => p.Name == "Mix").Id);
            if (previous is not null)
            {
                // Import keeps identity.
                Assert.Equal(previous.Profiles.Single(p => p.Name == "Tuned").Id, doc.Profiles.Single(p => p.Name == "Tuned").Id);
                Assert.Equal(previous.Targets.Single(t => t.Name == "Full").Id, doc.Targets.Single(t => t.Name == "Full").Id);
                Assert.Equal(previous.Pools.Single(p => p.Name == "Mix").Id, doc.Pools.Single(p => p.Name == "Mix").Id);
            }
        }
    }

    // ---- CF11 ----

    [Fact]
    public async Task Config_export_key_sets_are_pinned()
    {
        await ConfigApiTests.SeedHomeAsync(_client);
        var response = await _client.GetAsync("/api/v1/config/export");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        static string[] Keys(JsonElement e) => e.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray();
        var shape = new SortedDictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["document"] = Keys(root),
            ["pool"] = Keys(root.GetProperty("pools")[0]),
            ["profile"] = Keys(root.GetProperty("profiles")[0]),
            ["target"] = Keys(root.GetProperty("targets")[0]),
        };

        ContractFiles.AssertMatches("config-export.keys.json",
            JsonSerializer.Serialize(shape, new JsonSerializerOptions { WriteIndented = true }));
    }

    // ---- old exports ----

    [Fact]
    public async Task A_v0_10_export_imports()
    {
        var json = await File.ReadAllTextAsync(ContractFiles.PathOf("old-export-v0.10.json"));
        var response = await _client.PostAsync("/api/v1/config/import", new StringContent(json, Encoding.UTF8, "application/json"));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());

        var doc = await _client.GetFromJsonAsync<ConfigDocument>("/api/v1/config/export", Web);
        Assert.NotNull(doc);
        Assert.Equal(new[] { "Cloudflare", "Google", "Modem", "Router", "Web" }, doc.Targets.Select(t => t.Name).Order());
        Assert.Equal("Router", doc.Targets.Single(t => t.Name == "Modem").Parent);
        Assert.Equal("Comcast", doc.Targets.Single(t => t.Name == "Modem").Operator);
        Assert.Equal("Modem", doc.Targets.Single(t => t.Name == "Google").Parent);
        Assert.Equal("tcp:443", doc.Targets.Single(t => t.Name == "Web").Probe);
        Assert.False(doc.Targets.Single(t => t.Name == "Web").Enabled);
        Assert.All(doc.Targets, t => Assert.Equal(TargetScope.Verdict, t.Scope));
        Assert.All(doc.Targets, t => Assert.False(t.Critical));
        Assert.All(doc.Targets, t => Assert.Contains(doc.Profiles, p => p.Name == t.Profile));
        // AUDIT: the brief expected "External" to become Public on import. Import keeps it as a custom profile
        // named External (a v0.10 export lists it among its profiles); only the database migration renames it.
        // Pinned as it is: the targets keep working because the profile came with the document.
        Assert.Equal("External", doc.Targets.Single(t => t.Name == "Google").Profile);
        Assert.Equal(10.0, doc.Profiles.Single(p => p.Name == "External").NormalIntervalSeconds);

        var pool = Assert.Single(doc.Pools);
        Assert.Equal("Internet", pool.Name);
        Assert.Equal(new[] { "Google", "Cloudflare" }, pool.Members);
        Assert.Equal("Modem", pool.Parent);

        // The built-ins added since v0.10 exist straight away.
        foreach (var builtIn in PingProfile.BuiltIns)
            Assert.Contains(doc.Profiles, p => p.Name == builtIn.Name);
    }

    [Fact]
    public async Task A_minimal_target_post_defaults_to_public_and_enabled()
    {
        var post = await _client.PostAsync("/api/v1/targets",
            new StringContent("{\"name\":\"Minimal\",\"address\":\"192.0.2.9\"}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);

        var targets = await _client.GetFromJsonAsync<List<TargetDto>>("/api/v1/targets", Web);
        var minimal = Assert.Single(targets!, t => t.Name == "Minimal");
        Assert.Equal("Public", minimal.Profile);
        Assert.True(minimal.Enabled);
        Assert.Equal(TargetScope.Verdict, minimal.Scope);
        Assert.False(minimal.Critical);
        Assert.Null(minimal.Parent);
    }
}
