using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using ConPersistence;
using ConPersistence.Config;
using ConPersistence.Entities;
using ConPersistence.Recording;
using ConWatch.NameResolution;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Sdk;

namespace ConWatch.Tests;

/// <summary>
/// HA1: the JSON the dashboard reads, as key sets per object path, pinned in
/// Tests/contracts/*.keys.json. The client checks its TypeScript types against
/// the same files (ClientApp/src/lib/contracts.test.ts), so a renamed C#
/// property fails here and the regenerated fixture then fails there.
/// </summary>
public class ContractShapeTests : IDisposable
{
    private static readonly IPAddress RouterIp = IPAddress.Parse("192.168.1.1");
    private const string SystemResolver = "192.0.2.53";
    private const string UpstreamResolver = "198.51.100.53";

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private readonly ConWatchFactory _factory = new();
    private readonly HttpClient _client;

    public ContractShapeTests() => _client = _factory.CreateClient();

    public void Dispose() => _factory.Dispose();

    private static string[] Keys(JsonElement e) =>
        e.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray();

    /// <summary>Records <paramref name="obj"/>'s keys at <paramref name="path"/>, then every nested
    /// object (<c>path.key</c>) and every array of objects (<c>path.key[]</c>, all elements merged).
    /// Arrays of scalars and null values add nothing. Paths in <paramref name="skip"/> are maps
    /// keyed by ids (or checked elsewhere): neither recorded nor walked.</summary>
    private static void Walk(JsonElement obj, string path, SortedDictionary<string, string[]> shape,
        ISet<string>? skip = null)
    {
        Record(shape, path, Keys(obj));
        foreach (var p in obj.EnumerateObject())
        {
            var child = $"{path}.{p.Name}";
            if (skip?.Contains(child) == true)
                continue;
            if (p.Value.ValueKind == JsonValueKind.Object)
                Walk(p.Value, child, shape, skip);
            else if (p.Value.ValueKind == JsonValueKind.Array)
                WalkElements(p.Value, child + "[]", shape, skip);
        }
    }

    private static void WalkElements(JsonElement array, string path, SortedDictionary<string, string[]> shape,
        ISet<string>? skip = null)
    {
        foreach (var e in array.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object))
            Walk(e, path, shape, skip);
    }

    private static void Record(SortedDictionary<string, string[]> shape, string path, string[] keys)
    {
        if (shape.TryGetValue(path, out var seen) && !seen.SequenceEqual(keys))
            throw new XunitException(
                $"Elements at {path} disagree: [{string.Join(", ", seen)}] vs [{string.Join(", ", keys)}].");
        shape[path] = keys;
    }

    private static string Serialize(SortedDictionary<string, string[]> shape) =>
        JsonSerializer.Serialize(shape, Indented);

    private async Task<JsonElement?> GetJsonAsync(string url)
    {
        var response = await _client.GetAsync(url); // 503 while the engine is (re)building
        if (!response.IsSuccessStatusCode)
            return null;
        return JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
    }

    private static int Count(JsonElement e, string name) =>
        e.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array ? a.GetArrayLength() : 0;

    private static bool Has(JsonElement e, string name, JsonValueKind kind, out JsonElement value) =>
        e.TryGetProperty(name, out value) && value.ValueKind == kind;

    /// <summary>The verdict is DnsDown with its blame, and a note is held. Read through
    /// TryGetProperty so a renamed key makes this false, never throws.</summary>
    private static bool DnsDownWithNote(JsonElement status) =>
        Has(status, "verdict", JsonValueKind.Object, out var verdict)
        && verdict.TryGetProperty("state", out var state) && state.GetString() == "DnsDown"
        && Has(status, "dns", JsonValueKind.Object, out var dns)
        && Has(dns, "blame", JsonValueKind.Object, out _)
        && Count(status, "warnings") > 0;

    /// <summary>Every optional block of a snapshot is present: <see cref="DnsDownWithNote"/>, and
    /// at least one row in each list the dashboard reads.</summary>
    private static bool FullyPopulated(JsonElement status) =>
        DnsDownWithNote(status)
        && status.TryGetProperty("dns", out var dns)
        && Count(dns, "systemResolvers") > 0 && Count(dns, "upstreamResolvers") > 0 && Count(dns, "lookupNames") > 0
        && Count(status, "targets") > 0 && Count(status, "pools") > 0;

    /// <summary>Set when <see cref="SettledAsync"/> timed out and fell through to a comparison.</summary>
    private string? _unsettled;

    /// <summary>Waits for <paramref name="ready"/>. On timeout it hands back the last answer
    /// anyway, so a renamed key fails on the contract diff, which names it, rather than on
    /// "condition not met"; the diff then also says the settle timed out. With UPDATE_CONTRACTS=1
    /// it rethrows instead, so a regeneration never writes a partial shape.</summary>
    private async Task<JsonElement> SettledAsync(Func<Task<JsonElement?>> read, Func<JsonElement, bool> ready,
        int timeoutMs = 30000)
    {
        JsonElement? last = null;
        try
        {
            await ConfigApiTests.Eventually(async () => (last = await read() ?? last) is { } v && ready(v), timeoutMs);
        }
        catch (XunitException) when (last is not null && Environment.GetEnvironmentVariable("UPDATE_CONTRACTS") != "1")
        {
            _unsettled = $"settle condition not met within {timeoutMs} ms; this is the last answer the server gave.";
        }
        return last ?? throw new XunitException("No answer to compare.");
    }

    /// <summary>Checks a fixture, adding the settle timeout (if any) to a mismatch message.</summary>
    private void AssertMatches(string name, string actual)
    {
        try
        {
            ContractFiles.AssertMatches(name, actual);
        }
        catch (XunitException e) when (_unsettled is not null)
        {
            throw new XunitException($"{_unsettled}\n{e.Message}");
        }
    }

    /// <summary>Home topology plus an upstream resolver and a system resolver on the fast profile,
    /// settled Healthy; then the system resolver and the Router stop answering while the anchors
    /// still do: DnsDown with its blame, and a held HopBypassed note.</summary>
    private async Task SeedFullyPopulatedAsync()
    {
        await ConfigApiTests.SeedHomeAsync(_client);
        (await _client.PostAsJsonAsync("/api/v1/targets", new
        {
            name = "Upstream", address = UpstreamResolver, profile = "Fast", probe = "dns", scope = "Resolver",
        })).EnsureSuccessStatusCode();

        _factory.Resolvers.Set(SystemResolver);
        await _factory.Services.GetRequiredService<SystemResolverService>().ReconcileNowAsync(CancellationToken.None);
        // The managed row's built-in Resolver profile probes every 30 s; the document edit is the
        // reconciler's path (the API refuses a profile change on a managed row), as DnsStatusTests does.
        await ConfigApiTests.Eventually(async () =>
        {
            var edited = await _factory.Services.GetRequiredService<ConfigService>().EditDocumentAsync(doc =>
            {
                var i = doc.Targets.FindIndex(t => t.SystemManaged);
                if (i < 0) return ConfigResult.Fail(ConfigErrorKind.NotFound, "not yet");
                doc.Targets[i] = doc.Targets[i] with { Profile = "Fast" };
                return ConfigResult.Ok();
            });
            return edited.Success;
        });
        await SettledAsync(() => GetJsonAsync("/api/v1/status"), s =>
            Has(s, "verdict", JsonValueKind.Object, out var v)
            && v.TryGetProperty("state", out var state) && state.GetString() == "Healthy"
            && Has(s, "dns", JsonValueKind.Object, out var d)
            && Has(d, "systemResolvers", JsonValueKind.Array, out var resolvers)
            && resolvers.EnumerateArray().Any(r =>
                r.TryGetProperty("address", out var a) && a.GetString() == SystemResolver
                && r.TryGetProperty("state", out var rs) && rs.GetString() == "Responding"));

        _factory.Pings.SetUp(IPAddress.Parse(SystemResolver), false);
        _factory.Pings.SetUp(RouterIp, false);
        await SettledAsync(() => GetJsonAsync("/api/v1/status"), DnsDownWithNote);

        // A glitch, inserted as GlitchApiTests does (the engine needs a scripted episode to record one).
        var dbFactory = _factory.Services.GetRequiredService<IDbContextFactory<ConWatchDbContext>>();
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            var t0 = DateTime.UtcNow.AddMinutes(-5);
            db.Glitches.Add(new GlitchRow
            {
                TargetId = Guid.NewGuid(), TargetName = "Google", Class = 0,
                StartUtc = t0, EndUtc = t0.AddSeconds(9), LostCount = 2, SampleCount = 8, Detail = "d",
            });
            await db.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task Server_shapes_match_the_contract_fixtures()
    {
        await SeedFullyPopulatedAsync();

        // ---- status ----
        // The seed reached DnsDown with a note; the lists are filled by then, so 10 s is plenty.
        var status = await SettledAsync(() => GetJsonAsync("/api/v1/status"), FullyPopulated, 10000);
        var statusShape = new SortedDictionary<string, string[]>(StringComparer.Ordinal);
        Walk(status, "status", statusShape);
        AssertMatches("status.keys.json", Serialize(statusShape));

        // ---- transitions ----
        var recorder = _factory.Services.GetRequiredService<TransitionRecorder>();
        JsonElement transitions = default;
        await ConfigApiTests.Eventually(async () =>
        {
            await recorder.FlushAsync();
            return await GetJsonAsync("/api/v1/transitions?limit=1") is { } t && Count(transitions = t, "items") == 1;
        });
        var transitionShape = new SortedDictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["transitions"] = Keys(transitions),
        };
        WalkElements(transitions.GetProperty("items"), "transition[]", transitionShape);
        // opening=true prepends marked rows: their own path, so the plain path stays pinned exactly.
        var first = transitions.GetProperty("items")[0];
        var openingFrom = Uri.EscapeDataString(
            first.GetProperty("timestamp").GetDateTimeOffset().AddSeconds(1).ToString("o"));
        var withOpening = (await GetJsonAsync(
            $"/api/v1/transitions?subjectId={first.GetProperty("subjectId").GetGuid()}&from={openingFrom}&opening=true"))!.Value;
        var openingRows = withOpening.GetProperty("items").EnumerateArray()
            .Where(e => e.TryGetProperty("opening", out _)).ToList();
        Assert.NotEmpty(openingRows);
        foreach (var e in openingRows)
            Walk(e, "transition.opening[]", transitionShape);
        AssertMatches("transition.keys.json", Serialize(transitionShape));
        var timestamp = first.GetProperty("timestamp").GetString()!;
        Assert.Matches(@"[+-]\d{2}:\d{2}$", timestamp); // an offset, never a bare local time
        Assert.EndsWith("+00:00", timestamp);

        // ---- glitches ----
        JsonElement glitches = default;
        await ConfigApiTests.Eventually(async () =>
            await GetJsonAsync("/api/v1/glitches") is { } g && Count(glitches = g, "items") > 0);
        Assert.True(Count(glitches, "items") > 0, "The seeded glitch is missing.");
        var glitchShape = new SortedDictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["glitches"] = Keys(glitches),
        };
        WalkElements(glitches.GetProperty("items"), "glitch[]", glitchShape);
        AssertMatches("glitch.keys.json", Serialize(glitchShape));

        // ---- rollups (minute resolution, for an answering anchor) ----
        var google = (await _client.GetFromJsonAsync<List<TargetDto>>("/api/v1/targets"))!.Single(t => t.Name == "Google");
        var rollupCollector = _factory.Services.GetRequiredService<RollupCollector>();
        var from = Uri.EscapeDataString(DateTime.UtcNow.AddHours(-1).ToString("o"));
        var to = Uri.EscapeDataString(DateTime.UtcNow.AddHours(1).ToString("o"));
        JsonElement rollups = default;
        await ConfigApiTests.Eventually(async () =>
        {
            await rollupCollector.FlushAllAsync();
            return await GetJsonAsync($"/api/v1/rollups/{google.Id}?from={from}&to={to}&resolution=minute") is { } r
                && Count(rollups = r, "points") > 0;
        });
        Assert.Equal("Minute", rollups.GetProperty("resolution").GetString());
        var rollupShape = new SortedDictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["rollups"] = Keys(rollups),
        };
        WalkElements(rollups.GetProperty("points"), "rollup[]", rollupShape);
        AssertMatches("rollup.keys.json", Serialize(rollupShape));
        Assert.All(rollups.GetProperty("points").EnumerateArray(),
            p => Assert.EndsWith("+00:00", p.GetProperty("periodStart").GetString()));

        // ---- the Index page's initial-data island ----
        var island = await SettledAsync(async () =>
        {
            var response = await _client.GetAsync("/");
            if (response.StatusCode != HttpStatusCode.OK)
                return null;
            var match = Regex.Match(await response.Content.ReadAsStringAsync(),
                """<script id="initial-data" type="application/json">(.*?)</script>""", RegexOptions.Singleline);
            Assert.True(match.Success, "The Index page has no initial-data island.");
            return JsonSerializer.Deserialize<JsonElement>(match.Groups[1].Value);
        }, i => Has(i, "snapshot", JsonValueKind.Object, out var snapshot) && FullyPopulated(snapshot));
        // parents and poolMembers are maps keyed by ids; the snapshot is the status shape, checked below.
        var islandShape = new SortedDictionary<string, string[]>(StringComparer.Ordinal);
        Walk(island, "index.island", islandShape,
            new HashSet<string> { "index.island.parents", "index.island.poolMembers", "index.island.snapshot" });
        AssertMatches("index-island.keys.json", Serialize(islandShape));
        var snapshotShape = new SortedDictionary<string, string[]>(StringComparer.Ordinal);
        Walk(island.GetProperty("snapshot"), "status", snapshotShape);
        Assert.Equal(Serialize(statusShape), Serialize(snapshotShape));
    }
}
