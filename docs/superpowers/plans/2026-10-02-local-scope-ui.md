# Local Scope UI and History Implementation Plan (Plan 1B of 4)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make Local-scope targets visible and manageable: a "How's my LAN" dashboard section, a scope filter on history (REST, MCP and the History page), a scope select on the Targets dialog, and an "Include local devices" option on clone.

**Architecture:** History rows get their scope from a join against the Targets table (transition and glitch rows carry no scope column); the REST/MCP history surfaces take an optional `scope` filter and report a `scope` field. The dashboard island separates Local targets from the verdict tree and tracks their last-change times in the status store. Config pages stay server-rendered Razor with inline vanilla JS; the dashboard and History pages are React islands built by Vite.

**Tech Stack:** .NET 10 / ASP.NET Razor Pages / EF Core + SQLite; React 18 + TypeScript + Vite + Vitest; xunit 2.9.3.

**Spec:** `docs/superpowers/specs/2026-10-02-discoveries-and-local-scope-design.md` (§3 dashboard and history, §4 clone and MCP, §6 testing). Plan 1A (`docs/superpowers/plans/2026-10-02-local-scope-backend.md`) is merged on `master`: `TargetScope` (`Verdict` | `Local`, serialized as the strings `"Verdict"`/`"Local"`), the built-in `Local` profile, validator rules (a Local target has no parent/children, is not a pool member), and `StatusTarget.Scope` already exist.

**Series:** 1A (backend, done) → **1B (this plan)** → 2 (Discoveries inbox, endpoints, Config page, router/edge search, MCP `list_discoveries`) → 3 (LAN scan, tray scouting, host-network docs).

## Global Constraints

- File-scoped namespaces; `Nullable` and `ImplicitUsings` enabled; no `TreatWarningsAsErrors`.
- New tests use `example.com` hostnames and RFC 5737 addresses (`203.0.113.x`, `198.51.100.x`). Tests wait on conditions (`ConfigApiTests.Eventually`), never sleep-then-assert (CI runners have 2 cores).
- `TargetScope` JSON is a string. History row `scope` values are exactly `"Verdict"` or `"Local"`; the REST/MCP filter values are exactly `all` (default), `internet`, `local` (case-insensitive); anything else is a 400 (REST).
- Existing status/history clients must keep working: `scope` is an additive field; a missing `scope` on a target or transition means `Verdict`.
- Config pages (`Programs/ConWatch/Pages/Config/*`, `Setup.cshtml`) use inline vanilla JS and `wwwroot/lib/site-config.js` helpers — no Vite, no new dependencies. The dashboard and History pages are React islands: after editing `Programs/ConWatch/ClientApp`, run `npm run build` (it type-checks with `tsc --noEmit`, then `vite build`); `wwwroot/js/*.js` is git-ignored build output.
- Frontend commands (from `Programs/ConWatch/ClientApp`): `npx vitest run`, `npx tsc --noEmit`, `npm run build`. .NET commands per project: `dotnet test Tests/ConPersistence.Tests -c Debug`, `Tests/ConEngine.Tests`, `Tests/ConWatch.Tests` (and `ConNotify.Tests`, `ConConfig.Tests`, `ConWatchCli.Tests` in the final task); filter with `--filter "FullyQualifiedName~<Class>"`. Never run the whole solution. `ConWatch.Tests` has one known cold-start flake in `ProbeFieldTests` (passes on rerun).
- Commit after every task. **Never push** (project rule). Commit messages carry no attribution lines. Work on a feature branch in a git worktree (e.g. `local-scope-ui`), not on `master`.
- Do not edit `README.md` — it has uncommitted edits in the main checkout (a rewrite in progress). Documentation changes in this plan go in `SECURITY.md` only.

## Review Focus

Failure modes the spec implies that no task's main tests would catch; each has a test in the owning task:

1. A **live** SignalR transition for a Local target must not become the dashboard's "latest event" or appear in the recent-events panel — it would read as internet trouble (Task 2, store test).
2. A Local target with **no recorded transition yet** (just added) must show "—" for "since", never `NaN`, `Invalid Date` or a crash (Task 2, `lan.ts` test).
3. A config with **only Local targets** (no verdict-scope subjects) must still let the user reach the LAN view on History — the scope toggle must not be hidden by the "no subjects" early return (Task 3).
4. `scope=bogus` on REST must be a 400, and on MCP a clear tool error, not a 500 or a silent "all" (Task 1).
5. Cloning **without** local devices must leave no orphan transitions, rollups or glitches for the skipped targets, and must not touch pools or the verdict (Task 5, test counts rows).

## File Structure

| File | Responsibility | Task |
|---|---|---|
| `Libraries/ConPersistence/Queries/ScopeFilter.cs` (new) | `ScopeFilter` enum + `ScopeFilters.TryParse` | 1 |
| `Libraries/ConPersistence/Queries/HistoryQueries.cs`, `TransitionDto.cs` | Scope filter and scope tagging for history rows | 1 |
| `Programs/ConWatch/Api/HistoryEndpoints.cs`, `GlitchEndpoints.cs`, `Mcp/ConWatchMcpTools.cs` | `scope` param and `scope` field on REST/MCP | 1 |
| `Programs/ConWatch/ClientApp/src/lib/types.ts`, `lan.ts` (new), `statusStore.ts`, `story.ts` | Client types, LAN helpers, store | 2 |
| `Programs/ConWatch/ClientApp/src/components/LanSection.tsx` (new), `GlitchSection.tsx`, `dashboard.tsx` | LAN section and Internet-only story | 2 |
| `Programs/ConWatch/Pages/Index.cshtml`, `Index.cshtml.cs` | Server-rendered dashboard fallback | 2 |
| `Programs/ConWatch/Pages/History.cshtml.cs`, `ClientApp/src/history.tsx`, `lib/historyData.ts` | History scope toggle | 3 |
| `Programs/ConWatch/Pages/Config/Targets.cshtml`, `Targets.cshtml.cs` | Scope column, scope select, prefill | 4 |
| `Programs/ConWatch/Clone/CloneService.cs`, `Api/SetupEndpoints.cs`, `Pages/Setup.cshtml`, `wwwroot/lib/site-setup.js` | "Include local devices" | 5 |
| `SECURITY.md` | Open-export exposure of adopted devices | 6 |

New test files: `Tests/ConPersistence.Tests/HistoryScopeTests.cs`, `Tests/ConWatch.Tests/HistoryScopeApiTests.cs`, `Tests/ConWatch.Tests/LocalScopeUiTests.cs`, `ClientApp/src/lib/lan.test.ts`. Existing tests extended where named.

---

### Task 1: Scope on history rows — filter and field (REST, MCP)

**Files:**
- Create: `Libraries/ConPersistence/Queries/ScopeFilter.cs`
- Modify: `Libraries/ConPersistence/Queries/HistoryQueries.cs`, `Libraries/ConPersistence/Queries/TransitionDto.cs`, `Programs/ConWatch/Api/HistoryEndpoints.cs`, `Programs/ConWatch/Api/GlitchEndpoints.cs`, `Programs/ConWatch/Mcp/ConWatchMcpTools.cs`
- Test: create `Tests/ConPersistence.Tests/HistoryScopeTests.cs`, `Tests/ConWatch.Tests/HistoryScopeApiTests.cs`; modify `Tests/ConWatch.Tests/HistoryApiTests.cs` (the pinned transition JSON), `Tests/ConWatch.Tests/McpEndpointTests.cs`, `Tests/ConWatch.Tests/GlitchApiTests.cs` if it pins item keys

**Interfaces:**
- Produces: `enum ScopeFilter { All, Internet, Local }`; `ScopeFilters.TryParse(string? text, out ScopeFilter filter)` (null/empty → `All`; false for unknown text); `HistoryQueries.TransitionsAsync(..., string? subjectName = null, ScopeFilter scope = ScopeFilter.All)`; `HistoryQueries.GlitchesAsync(..., int limit, ScopeFilter scope = ScopeFilter.All)`; `HistoryQueries.LocalTargetIdsAsync(db)` → `HashSet<Guid>`; `TransitionDto.Scope` (`TargetScope?`, last property, omitted when null) and `TransitionDto.From(TransitionRow r, ISet<Guid> localIds)`. REST: `GET /api/v1/transitions?scope=…`, `GET /api/v1/glitches?scope=…`, each item gains `"scope"`.
- Scope rules: a row is `Local` iff its subject/target id is a *current* Local target; pool rows, verdict rows, Internet-class glitches and rows of since-deleted targets are `Verdict` (history is evidence; a deleted target's scope is unknowable).

- [ ] **Step 1: Write the failing persistence tests**

Create `Tests/ConPersistence.Tests/HistoryScopeTests.cs`:

```csharp
using ConCore.Targets;
using ConEngine.Configuration;
using ConEngine.Topology;
using ConPersistence.Config;
using ConPersistence.Entities;
using ConPersistence.Queries;

namespace ConPersistence.Tests;

public class HistoryScopeTests : IAsyncLifetime, IDisposable
{
    private readonly TestDbFactory _factory = new();
    private Guid _routerId;
    private Guid _nasId;

    public async Task InitializeAsync()
    {
        var store = new ConfigStore(_factory);
        await store.SeedAsync(new WatcherOptions
        {
            Targets =
            [
                new TargetConfig { Name = "Router", Address = "203.0.113.1", Profile = "Internal" },
                new TargetConfig { Name = "NAS", Address = "203.0.113.9", Profile = "Local", Scope = TargetScope.Local },
            ],
        });
        var options = await store.LoadWatcherOptionsAsync();
        _routerId = options.Targets.Single(t => t.Name == "Router").Id!.Value;
        _nasId = options.Targets.Single(t => t.Name == "NAS").Id!.Value;

        var at = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        await using var db = _factory.CreateDbContext();
        db.Transitions.AddRange(
            Row(_routerId, "Router", "Target", at),
            Row(_nasId, "NAS", "Target", at.AddMinutes(1)),
            Row(VerdictCoordinator.SubjectId, "Internet", "Verdict", at.AddMinutes(2)));
        db.Glitches.AddRange(
            new GlitchRow { TargetId = _routerId, TargetName = "Router", Class = 0, StartUtc = at, EndUtc = at },
            new GlitchRow { TargetId = _nasId, TargetName = "NAS", Class = 0, StartUtc = at, EndUtc = at },
            new GlitchRow { TargetId = VerdictCoordinator.SubjectId, TargetName = "Internet", Class = 1, StartUtc = at, EndUtc = at });
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;
    public void Dispose() => _factory.Dispose();

    private static TransitionRow Row(Guid id, string name, string kind, DateTime at) => new()
    {
        TimestampUtc = at, SubjectId = id, SubjectName = name, SubjectKind = kind,
        FromState = "Responding", ToState = "Down", Trigger = "ThresholdCrossed",
    };

    private async Task<List<string>> Names(ScopeFilter scope, string? subjectName = null)
    {
        await using var db = _factory.CreateDbContext();
        var rows = await HistoryQueries.TransitionsAsync(db, null, null, null, 100, null, subjectName, scope);
        return rows.Select(r => r.SubjectName).OrderBy(n => n).ToList();
    }

    [Fact]
    public async Task All_returns_every_subject()
        => Assert.Equal(["Internet", "NAS", "Router"], await Names(ScopeFilter.All));

    [Fact]
    public async Task Internet_excludes_Local_targets_but_keeps_pools_and_the_verdict()
        => Assert.Equal(["Internet", "Router"], await Names(ScopeFilter.Internet));

    [Fact]
    public async Task Local_returns_only_Local_targets()
        => Assert.Equal(["NAS"], await Names(ScopeFilter.Local));

    [Fact]
    public async Task Scope_combines_with_a_subject_name_filter()
    {
        Assert.Equal(["NAS"], await Names(ScopeFilter.Local, "NAS"));
        Assert.Empty(await Names(ScopeFilter.Internet, "NAS"));
    }

    [Fact]
    public async Task Glitches_filter_by_scope_and_keep_internet_class_glitches_in_Internet()
    {
        await using var db = _factory.CreateDbContext();
        var internet = await HistoryQueries.GlitchesAsync(db, null, null, null, 100, ScopeFilter.Internet);
        var local = await HistoryQueries.GlitchesAsync(db, null, null, null, 100, ScopeFilter.Local);

        Assert.Equal(["Internet", "Router"], internet.Select(g => g.TargetName).OrderBy(n => n));
        Assert.Equal(["NAS"], local.Select(g => g.TargetName));
    }

    [Fact]
    public async Task LocalTargetIds_lists_only_current_Local_targets()
    {
        await using var db = _factory.CreateDbContext();
        var ids = await HistoryQueries.LocalTargetIdsAsync(db);
        Assert.Equal([_nasId], ids);
    }

    [Theory]
    [InlineData(null, ScopeFilter.All)]
    [InlineData("", ScopeFilter.All)]
    [InlineData("all", ScopeFilter.All)]
    [InlineData("Internet", ScopeFilter.Internet)]
    [InlineData("LOCAL", ScopeFilter.Local)]
    public void TryParse_accepts_the_three_values_case_insensitively(string? text, ScopeFilter expected)
    {
        Assert.True(ScopeFilters.TryParse(text, out var filter));
        Assert.Equal(expected, filter);
    }

    [Fact]
    public void TryParse_rejects_anything_else()
        => Assert.False(ScopeFilters.TryParse("bogus", out _));
}
```

- [ ] **Step 2: Run to verify it fails (does not compile)**

Run: `dotnet test Tests/ConPersistence.Tests -c Debug --filter "FullyQualifiedName~HistoryScopeTests"`
Expected: build FAIL — `ScopeFilter`, `ScopeFilters`, the new parameters and `LocalTargetIdsAsync` are not defined.

- [ ] **Step 3: Add the filter type**

Create `Libraries/ConPersistence/Queries/ScopeFilter.cs`:

```csharp
namespace ConPersistence.Queries;

/// <summary>Which targets a history query covers: everything, the internet-facing
/// subjects (verdict-scope targets, pools, the verdict), or the LAN devices.</summary>
public enum ScopeFilter
{
    All,
    Internet,
    Local,
}

public static class ScopeFilters
{
    /// <summary>Null or empty means <see cref="ScopeFilter.All"/>; false for unknown text.</summary>
    public static bool TryParse(string? text, out ScopeFilter filter)
    {
        filter = ScopeFilter.All;
        if (string.IsNullOrWhiteSpace(text))
            return true;
        switch (text.Trim().ToLowerInvariant())
        {
            case "all": return true;
            case "internet": filter = ScopeFilter.Internet; return true;
            case "local": filter = ScopeFilter.Local; return true;
            default: return false;
        }
    }
}
```

- [ ] **Step 4: Implement the query side**

In `HistoryQueries.cs` add `using ConCore.Targets;`. Change `TransitionsAsync` to:

```csharp
    public static async Task<List<TransitionRow>> TransitionsAsync(ConWatchDbContext db,
        Guid? subjectId, DateTime? fromUtc, DateTime? toUtc, int limit, long? before,
        string? subjectName = null, ScopeFilter scope = ScopeFilter.All)
    {
        limit = Math.Clamp(limit, 1, 1000);
        IQueryable<TransitionRow> query = db.Transitions.AsNoTracking();
        if (subjectId is { } sid)
            query = query.Where(t => t.SubjectId == sid);
        if (subjectName is { Length: > 0 })
            query = query.Where(t => t.SubjectName == subjectName);
        if (fromUtc is { } from)
            query = query.Where(t => t.TimestampUtc >= from);
        if (toUtc is { } to)
            query = query.Where(t => t.TimestampUtc <= to);
        if (before is { } cursor)
            query = query.Where(t => t.Id < cursor);
        if (scope != ScopeFilter.All)
        {
            var localIds = db.Targets.Where(t => t.Scope == TargetScope.Local).Select(t => t.Id);
            query = scope == ScopeFilter.Local
                ? query.Where(t => localIds.Contains(t.SubjectId))
                : query.Where(t => !localIds.Contains(t.SubjectId));
        }
        return await query.OrderByDescending(t => t.Id).Take(limit).ToListAsync();
    }
```

Change `GlitchesAsync` to:

```csharp
    public static Task<List<GlitchRow>> GlitchesAsync(ConWatchDbContext db,
        Guid? targetId, DateTime? fromUtc, DateTime? toUtc, int limit,
        ScopeFilter scope = ScopeFilter.All)
    {
        limit = Math.Clamp(limit, 1, 1000);
        IQueryable<GlitchRow> query = db.Glitches.AsNoTracking();
        if (targetId is { } tid)
            query = query.Where(g => g.TargetId == tid);
        if (fromUtc is { } from)
            query = query.Where(g => g.StartUtc >= from);
        if (toUtc is { } to)
            query = query.Where(g => g.StartUtc <= to);
        if (scope != ScopeFilter.All)
        {
            var localIds = db.Targets.Where(t => t.Scope == TargetScope.Local).Select(t => t.Id);
            query = scope == ScopeFilter.Local
                ? query.Where(g => localIds.Contains(g.TargetId))
                : query.Where(g => !localIds.Contains(g.TargetId));
        }
        return query.OrderByDescending(g => g.StartUtc).ThenByDescending(g => g.Id)
            .Take(limit).ToListAsync();
    }
```

and add beside them:

```csharp
    /// <summary>The ids of the current Local-scope targets, for tagging history rows with a scope.</summary>
    public static async Task<HashSet<Guid>> LocalTargetIdsAsync(ConWatchDbContext db) =>
        (await db.Targets.AsNoTracking().Where(t => t.Scope == TargetScope.Local)
            .Select(t => t.Id).ToListAsync()).ToHashSet();
```

In `TransitionDto.cs` add `using ConCore.Targets;`, add as the LAST property (after `AvgRtt`):

```csharp
    /// <summary>"Verdict" or "Local" on history rows (REST /transitions, MCP get_transitions);
    /// omitted on live messages, whose subscribers know their targets.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TargetScope? Scope { get; init; }
```

and add beside the `From(TransitionRow r)` overload:

```csharp
    /// <summary>A history row tagged with its subject's current scope; pools, the verdict
    /// and since-deleted targets read as <see cref="TargetScope.Verdict"/>.</summary>
    public static TransitionDto From(TransitionRow r, ISet<Guid> localIds) => From(r) with
    {
        Scope = localIds.Contains(r.SubjectId) ? TargetScope.Local : TargetScope.Verdict,
    };
```

- [ ] **Step 5: Run the persistence tests**

Run: `dotnet test Tests/ConPersistence.Tests -c Debug`
Expected: all PASS (the new `HistoryScopeTests` included).

- [ ] **Step 6: Write the failing API tests**

Create `Tests/ConWatch.Tests/HistoryScopeApiTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace ConWatch.Tests;

public class HistoryScopeApiTests : IDisposable
{
    private readonly ConWatchFactory _factory = new();
    private readonly HttpClient _client;

    public HistoryScopeApiTests() => _client = _factory.CreateClient();

    public void Dispose() => _factory.Dispose();

    private async Task<JsonElement> GetJson(string url)
    {
        var response = await _client.GetAsync(url);
        response.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
    }

    private async Task SeedWithLocalNasAsync()
    {
        await ConfigApiTests.SeedHomeAsync(_client);
        (await _client.PostAsJsonAsync("/api/v1/targets", new
        {
            name = "NAS", address = "203.0.113.9", profile = "Fast", scope = "Local",
        })).EnsureSuccessStatusCode();
        // Both a verdict-scope and the Local target must have recorded a transition.
        await ConfigApiTests.Eventually(async () =>
        {
            var items = (await GetJson("/api/v1/transitions?limit=200")).GetProperty("items");
            var names = items.EnumerateArray().Select(i => i.GetProperty("subjectName").GetString()).ToHashSet();
            return names.Contains("NAS") && names.Contains("Router");
        });
    }

    [Fact]
    public async Task Scope_filters_transitions_and_each_item_reports_its_scope()
    {
        await SeedWithLocalNasAsync();

        var all = (await GetJson("/api/v1/transitions?limit=200")).GetProperty("items").EnumerateArray().ToList();
        var local = (await GetJson("/api/v1/transitions?scope=local&limit=200")).GetProperty("items").EnumerateArray().ToList();
        var internet = (await GetJson("/api/v1/transitions?scope=internet&limit=200")).GetProperty("items").EnumerateArray().ToList();

        Assert.Contains(all, i => i.GetProperty("subjectName").GetString() == "NAS");
        Assert.NotEmpty(local);
        Assert.All(local, i =>
        {
            Assert.Equal("NAS", i.GetProperty("subjectName").GetString());
            Assert.Equal("Local", i.GetProperty("scope").GetString());
        });
        Assert.NotEmpty(internet);
        Assert.DoesNotContain(internet, i => i.GetProperty("subjectName").GetString() == "NAS");
        Assert.All(internet, i => Assert.Equal("Verdict", i.GetProperty("scope").GetString()));
    }

    [Fact]
    public async Task An_unknown_scope_is_a_400_on_transitions_and_glitches()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.GetAsync("/api/v1/transitions?scope=bogus")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.GetAsync("/api/v1/glitches?scope=bogus")).StatusCode);
    }

    [Fact]
    public async Task Glitch_items_carry_a_scope_field()
    {
        await ConfigApiTests.SeedHomeAsync(_client);
        var body = await GetJson("/api/v1/glitches?scope=internet");
        Assert.Equal(JsonValueKind.Array, body.GetProperty("items").ValueKind);
        // No glitches exist yet; the filter itself must be accepted (200) for every legal value.
        foreach (var scope in new[] { "all", "internet", "local" })
            Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync($"/api/v1/glitches?scope={scope}")).StatusCode);
    }
}
```

Update the pinned JSON in `Tests/ConWatch.Tests/HistoryApiTests.cs` (the test ending at line ~162, `Assert.Equal($$"""{"id":…,"avgRtt":12.5}""", …)`): the REST row now ends with `"avgRtt":12.5,"scope":"Verdict"}` (the pinned row is a Router target that is not Local). Change only the end of that expected string.

Run: `dotnet test Tests/ConWatch.Tests -c Debug --filter "FullyQualifiedName~HistoryScopeApiTests|FullyQualifiedName~HistoryApiTests"`
Expected: FAIL (no `scope` param/field yet; the pinned row test fails on the missing `scope`).

- [ ] **Step 7: Implement the endpoints**

`HistoryEndpoints.cs` — replace the `/transitions` handler:

```csharp
        api.MapGet("/transitions", async (IDbContextFactory<ConWatchDbContext> dbFactory,
            Guid? subjectId, string? subjectName, string? from, string? to, string? scope,
            int limit = 100, long? before = null) =>
        {
            if (!ScopeFilters.TryParse(scope, out var scopeFilter))
                return ApiResults.Error(StatusCodes.Status400BadRequest, null,
                    "'scope' must be all, internet or local.");
            if (!TimeRange.TryParseRange(from, to, out var fromUtc, out var toUtc, out var problem))
                return problem!;
            await using var db = await dbFactory.CreateDbContextAsync();
            var rows = await HistoryQueries.TransitionsAsync(db, subjectId,
                fromUtc, toUtc, limit, before, subjectName, scopeFilter);
            var localIds = await HistoryQueries.LocalTargetIdsAsync(db);
            var items = rows.Select(r => TransitionDto.From(r, localIds)).ToList();
            long? nextBefore = rows.Count == Math.Clamp(limit, 1, 1000) ? rows.Last().Id : null;
            return Results.Ok(new { Items = items, NextBefore = nextBefore });
        });
```

`GlitchEndpoints.cs` — replace the `/glitches` handler:

```csharp
        api.MapGet("/glitches", async (IDbContextFactory<ConWatchDbContext> dbFactory,
            Guid? targetId, string? from, string? to, string? scope, int limit = 100) =>
        {
            if (!ScopeFilters.TryParse(scope, out var scopeFilter))
                return ApiResults.Error(StatusCodes.Status400BadRequest, null,
                    "'scope' must be all, internet or local.");
            if (!TimeRange.TryParseRange(from, to, out var fromUtc, out var toUtc, out var problem))
                return problem!;
            await using var db = await dbFactory.CreateDbContextAsync();
            var rows = await HistoryQueries.GlitchesAsync(db, targetId, fromUtc, toUtc, limit, scopeFilter);
            var localIds = await HistoryQueries.LocalTargetIdsAsync(db);

            return Results.Ok(new
            {
                Items = rows.Select(g => new
                {
                    g.Id, g.TargetId, g.TargetName,
                    Class = g.Class == 1 ? "Internet" : "Target",
                    StartUtc = new DateTimeOffset(g.StartUtc),
                    EndUtc = new DateTimeOffset(g.EndUtc),
                    g.LostCount, g.SampleCount, g.Detail,
                    Scope = localIds.Contains(g.TargetId) ? "Local" : "Verdict",
                }).ToList(),
            });
        });
```

(Both files need `using ConPersistence.Queries;` — `HistoryEndpoints.cs` already has it; add to `GlitchEndpoints.cs` if absent.)

- [ ] **Step 8: Implement the MCP tools**

In `ConWatchMcpTools.cs`, update `get_transitions`:

```csharp
    [McpServerTool(Name = "get_transitions", ReadOnly = true)]
    [Description("The raw state-transition log (newest first): every recorded state " +
        "change for targets, pools, and the internet verdict, with trigger and evidence. " +
        "Use for detailed digging after get_connection_report. " +
        "Rows have the REST /api/v1/transitions shape; times are ISO-8601 UTC (+00:00). " +
        "Each row has a scope: 'Local' rows are LAN devices (cameras, NAS, IoT) tracked on " +
        "the side — they are NOT evidence about the internet connection and never affect " +
        "the verdict; 'Verdict' rows are the path to the internet, its anchors and the verdict.")]
    public async Task<string> GetTransitions(
        [Description("Only transitions at/after this ISO-8601 UTC time (optional)")] DateTime? fromUtc = null,
        [Description("Only transitions at/before this ISO-8601 UTC time (optional)")] DateTime? toUtc = null,
        [Description("Filter to one subject by exact name, e.g. 'Router' or 'Internet' (optional)")] string? subjectName = null,
        [Description("Max rows, 1-500 (default 100)")] int limit = 100,
        [Description("'all' (default), 'internet' (path, anchors, pools, verdict) or 'local' (LAN devices)")] string? scope = null)
    {
        if (!ScopeFilters.TryParse(scope, out var scopeFilter))
            throw new ArgumentException("scope must be 'all', 'internet' or 'local'.", nameof(scope));
        await using var db = await dbFactory.CreateDbContextAsync();
        var rows = await HistoryQueries.TransitionsAsync(db, subjectId: null,
            fromUtc is { } f ? TimeRange.AsUtc(f) : null,
            toUtc is { } u ? TimeRange.AsUtc(u) : null,
            Math.Clamp(limit, 1, 500), before: null, subjectName, scopeFilter);
        var localIds = await HistoryQueries.LocalTargetIdsAsync(db);
        return JsonSerializer.Serialize(rows.Select(r => TransitionDto.From(r, localIds)), Json);
    }
```

and `get_glitches` (add the parameter, filter and a `scope` field; keep its description and append "Each row has a scope ('Local' = a LAN device, not internet evidence)."):

```csharp
        [Description("Max rows, 1-500 (default 100)")] int limit = 100,
        [Description("'all' (default), 'internet' or 'local' (LAN devices)")] string? scope = null)
    {
        if (!ScopeFilters.TryParse(scope, out var scopeFilter))
            throw new ArgumentException("scope must be 'all', 'internet' or 'local'.", nameof(scope));
        await using var db = await dbFactory.CreateDbContextAsync();
        var rows = await HistoryQueries.GlitchesAsync(db, targetId: null,
            fromUtc is { } f ? TimeRange.AsUtc(f) : null,
            toUtc is { } u ? TimeRange.AsUtc(u) : null,
            Math.Clamp(limit, 1, 500), scopeFilter);
        var localIds = await HistoryQueries.LocalTargetIdsAsync(db);
        return JsonSerializer.Serialize(rows.Select(g => new
        {
            StartUtc = g.StartUtc,
            EndUtc = g.EndUtc,
            g.TargetName,
            @class = g.Class == 1 ? "Internet" : "Target",
            g.LostCount, g.SampleCount, g.Detail,
            Scope = localIds.Contains(g.TargetId) ? "Local" : "Verdict",
        }), Json);
    }
```

Add an MCP test to `Tests/ConWatch.Tests/McpEndpointTests.cs` following the style of its existing `get_transitions` test (copy its JSON-RPC helper): after seeding, `get_transitions` with `{"scope":"local"}` returns only rows with `"scope":"Local"`, and with `{"scope":"bogus"}` returns a tool error (an `isError: true` result or JSON-RPC error — assert whichever the existing tests show for a failing tool call, not a 500).

- [ ] **Step 9: Run and commit**

Run: `dotnet test Tests/ConWatch.Tests -c Debug && dotnet test Tests/ConPersistence.Tests -c Debug`
Expected: all PASS. Fix any other test that pins the history JSON shape (`GlitchApiTests`, MCP tests) by adding the new `scope` key only.

```bash
git add Libraries Programs Tests
git commit -m "feat(history): scope filter and scope field on transitions and glitches (REST, MCP)"
```

---

### Task 2: Dashboard — "How's my LAN" section and an Internet-only story

**Files:**
- Create: `Programs/ConWatch/ClientApp/src/lib/lan.ts`, `Programs/ConWatch/ClientApp/src/lib/lan.test.ts`, `Programs/ConWatch/ClientApp/src/components/LanSection.tsx`, `Tests/ConWatch.Tests/LocalScopeUiTests.cs`
- Modify: `ClientApp/src/lib/types.ts`, `ClientApp/src/lib/statusStore.ts`, `ClientApp/src/lib/statusStore.test.ts`, `ClientApp/src/lib/story.ts`, `ClientApp/src/components/GlitchSection.tsx`, `ClientApp/src/dashboard.tsx`, `Programs/ConWatch/Pages/Index.cshtml`, `Programs/ConWatch/Pages/Index.cshtml.cs`

**Interfaces:**
- Consumes: status `targets[].scope` (`"Verdict"|"Local"`, plan 1A) and `GET /api/v1/transitions?scope=internet|local` (Task 1).
- Produces: `isLocal`, `summarizeLan`, `sinceLabel` (in `lan.ts`); `StoreState.localChanges: Record<string,string>` and `seedLocalChanges(items)` on the store; `<LanSection targets changes />`. After this task the verdict tree, event strip, ledger, punchcard and recent-glitches never show Local targets.

- [ ] **Step 1: Write the failing TypeScript tests**

Create `ClientApp/src/lib/lan.test.ts`:

```ts
import { describe, expect, it } from "vitest";
import { isLocal, sinceLabel, summarizeLan } from "./lan";
import type { TargetStatus } from "./types";

const t = (over: Partial<TargetStatus>): TargetStatus => ({
  id: "x", name: "X", address: "203.0.113.9", state: "Responding",
  successRate: 1, avgRtt: 1, sampleSize: 10, ...over,
});

describe("isLocal", () => {
  it("is true only for scope Local; a missing scope is Verdict", () => {
    expect(isLocal(t({ scope: "Local" }))).toBe(true);
    expect(isLocal(t({ scope: "Verdict" }))).toBe(false);
    expect(isLocal(t({}))).toBe(false);
  });
});

describe("summarizeLan", () => {
  it("counts only Local targets", () => {
    const s = summarizeLan([
      t({ id: "a", scope: "Local", state: "Responding" }),
      t({ id: "b", scope: "Local", state: "Down" }),
      t({ id: "c", scope: "Local", state: "Unreachable" }),
      t({ id: "d", scope: "Local", state: "Starting" }),
      t({ id: "e", scope: "Verdict", state: "Down" }),
    ]);
    expect(s).toEqual({ total: 4, up: 1, down: 2 });
  });
  it("is all zeros with no Local targets", () => {
    expect(summarizeLan([t({})])).toEqual({ total: 0, up: 0, down: 0 });
  });
});

describe("sinceLabel", () => {
  const now = new Date("2026-10-02T12:00:00Z");
  it("is an em dash when there is no recorded change (a just-added device)", () => {
    expect(sinceLabel(undefined, now)).toBe("—");
    expect(sinceLabel(null, now)).toBe("—");
    expect(sinceLabel("", now)).toBe("—");
  });
  it("is an em dash for an unparseable timestamp, never NaN", () => {
    expect(sinceLabel("not a date", now)).toBe("—");
  });
  it("formats elapsed time", () => {
    expect(sinceLabel("2026-10-02T11:59:50Z", now)).toBe("just now");
    expect(sinceLabel("2026-10-02T11:55:00Z", now)).toBe("5 min");
    expect(sinceLabel("2026-10-02T09:00:00Z", now)).toBe("3 h");
    expect(sinceLabel("2026-09-29T12:00:00Z", now)).toBe("3 d");
  });
  it("treats a timestamp slightly in the future as just now", () => {
    expect(sinceLabel("2026-10-02T12:00:30Z", now)).toBe("just now");
  });
});
```

Append to `ClientApp/src/lib/statusStore.test.ts` (inside the existing `describe("statusStore", …)`, using its `snapshot`/`transition` helpers; add a Local target by building a new snapshot):

```ts
  const lanSnapshot: Snapshot = {
    ...snapshot,
    targets: [
      ...snapshot.targets,
      { id: "t9", name: "NAS", address: "203.0.113.9", state: "Responding", successRate: 1, avgRtt: 1, sampleSize: 10, scope: "Local" },
    ],
  };

  it("keeps a live Local transition out of the latest/recent events (it is not internet news)", () => {
    const store = createStatusStore();
    store.applySnapshot(lanSnapshot);
    store.applyTransition(transition({ subjectId: "t9", subjectName: "NAS", to: "Down", timestamp: "2026-10-02T10:00:00Z" }));
    expect(store.getState().latestTransition).toBeNull();
    expect(store.getState().recentTransitions).toHaveLength(0);
    expect(store.getState().localChanges["t9"]).toBe("2026-10-02T10:00:00Z");
  });

  it("still records verdict-scope transitions as before", () => {
    const store = createStatusStore();
    store.applySnapshot(lanSnapshot);
    store.applyTransition(transition({}));
    expect(store.getState().latestTransition?.subjectName).toBe("Google");
    expect(store.getState().localChanges).toEqual({});
  });

  it("seeds last-change times from history, newest wins, live is never overwritten by older", () => {
    const store = createStatusStore();
    store.applySnapshot(lanSnapshot);
    store.applyTransition(transition({ subjectId: "t9", timestamp: "2026-10-02T11:00:00Z" }));
    store.seedLocalChanges([ // newest first, as the API returns them
      transition({ subjectId: "t9", timestamp: "2026-10-02T09:00:00Z" }),
      transition({ subjectId: "t8", timestamp: "2026-10-02T08:00:00Z" }),
      transition({ subjectId: "t8", timestamp: "2026-10-01T08:00:00Z" }),
    ]);
    expect(store.getState().localChanges["t9"]).toBe("2026-10-02T11:00:00Z");
    expect(store.getState().localChanges["t8"]).toBe("2026-10-02T08:00:00Z");
  });
```

Run (from `Programs/ConWatch/ClientApp`): `npx vitest run src/lib/lan.test.ts src/lib/statusStore.test.ts`
Expected: FAIL (`./lan` not found; `localChanges`/`seedLocalChanges` undefined).

- [ ] **Step 2: Types and helpers**

`ClientApp/src/lib/types.ts` — add to `TargetStatus` (after `pingCount`) and to `TransitionEvent` (after `burstLossRate`):

```ts
  /** "Verdict" (default when absent) or "Local": a LAN device tracked beside the verdict. */
  scope?: "Verdict" | "Local";
```

Create `ClientApp/src/lib/lan.ts`:

```ts
// Pure helpers for the "How's my LAN" section (unit-tested).
import type { TargetStatus } from "./types";

/** Local targets are LAN devices; a missing scope (older server) means Verdict. */
export const isLocal = (t: { scope?: string }): boolean => t.scope === "Local";

export interface LanSummary {
  total: number;
  up: number;
  down: number;
}

export function summarizeLan(targets: TargetStatus[]): LanSummary {
  const lan = targets.filter(isLocal);
  return {
    total: lan.length,
    up: lan.filter((t) => t.state === "Responding").length,
    down: lan.filter((t) => t.state === "Down" || t.state === "Unreachable").length,
  };
}

/** Time since an ISO timestamp: "just now", "5 min", "3 h", "3 d"; "—" when unknown. */
export function sinceLabel(iso: string | null | undefined, now: Date): string {
  if (!iso) return "—";
  const ms = now.getTime() - Date.parse(iso);
  if (!Number.isFinite(ms)) return "—";
  const seconds = Math.max(0, Math.floor(ms / 1000));
  if (seconds < 60) return "just now";
  const minutes = Math.floor(seconds / 60);
  if (minutes < 60) return `${minutes} min`;
  const hours = Math.floor(minutes / 60);
  if (hours < 48) return `${hours} h`;
  return `${Math.floor(hours / 24)} d`;
}
```

- [ ] **Step 3: Store**

`ClientApp/src/lib/statusStore.ts` — add `localChanges: Record<string, string>;` to `StoreState`, `localChanges: {},` to the initial state, and:

```ts
    applyTransition(t: TransitionEvent): void {
      // A LAN device's blip is not internet news: it never becomes the status
      // line's latest event or lands in the recent-events panel.
      if (state.targets.some((x) => x.id === t.subjectId && x.scope === "Local")) {
        state.localChanges = laterOf(state.localChanges, t.subjectId, t.timestamp);
        notify();
        return;
      }
      state.latestTransition = t;
      state.recentTransitions = [t, ...state.recentTransitions].slice(0, MAX_RECENT);
      notify();
    },
    /** History for Local targets (newest first): the last-change time per device. */
    seedLocalChanges(transitions: TransitionEvent[]): void {
      let next = state.localChanges;
      for (const t of transitions) next = laterOf(next, t.subjectId, t.timestamp);
      state.localChanges = next;
      notify();
    },
```

and above `createStatusStore` add:

```ts
function laterOf(map: Record<string, string>, id: string, timestamp: string): Record<string, string> {
  const existing = map[id];
  return existing && Date.parse(existing) >= Date.parse(timestamp) ? map : { ...map, [id]: timestamp };
}
```

- [ ] **Step 4: Internet-only story fetches**

`ClientApp/src/lib/story.ts` — both fetches gain the scope:

```ts
    fetch(`/api/v1/transitions?from=${from.toISOString()}&limit=1000&scope=internet`),
    fetch(`/api/v1/glitches?from=${from.toISOString()}&limit=1000&scope=internet`),
```

`ClientApp/src/components/GlitchSection.tsx` — the URL becomes
``const url = `/api/v1/glitches?from=${from.toISOString()}&to=${to.toISOString()}&limit=1000&scope=internet`;``

- [ ] **Step 5: The LAN component**

Create `ClientApp/src/components/LanSection.tsx`:

```tsx
// "How's my LAN": the LAN devices tracked beside the verdict. Never colours
// the verdict, the tree or the tray; its own summary and rows only.
import { isLocal, sinceLabel, summarizeLan } from "../lib/lan";
import type { TargetStatus } from "../lib/types";

export function LanSection({ targets, changes }: {
  targets: TargetStatus[];
  /** Last-change ISO time per target id. */
  changes: Record<string, string>;
}) {
  const lan = targets.filter(isLocal);
  if (lan.length === 0) return null;
  const { up, total } = summarizeLan(targets);
  const now = new Date();
  return (
    <div id="lan-section" style={{ marginTop: "1.5rem" }}>
      <h4>How&apos;s my LAN <small>({up} of {total} up)</small></h4>
      <ul className="cw-tree">
        {lan.map((t) => (
          <li key={t.id}>
            <div className="cw-row">
              <span className={`dot ${t.state.toLowerCase()}`}></span>
              <span>{t.name}</span>
              <span className="addr">
                {t.address}{t.probe ? ` · ${t.probe.replace(":", "/")}` : ""}
              </span>
              <span className="stats">
                <span>{t.state}</span>
                <span className="sep">·</span>
                <span title="since the last state change">{sinceLabel(changes[t.id], now)}</span>
              </span>
            </div>
          </li>
        ))}
      </ul>
    </div>
  );
}
```

- [ ] **Step 6: Wire the dashboard**

`ClientApp/src/dashboard.tsx`:

1. Imports: `import { LanSection } from "./components/LanSection";` and `import { isLocal } from "./lib/lan";`.
2. In the live (non-archive) return, the tree gets only verdict-scope targets and the LAN section follows it:

```tsx
      <Tree topology={topology} targets={state.targets.filter((t) => !isLocal(t))} pools={state.pools}
        trunkNames={initial.trunkNames} anchorNames={initial.anchorNames} />
      <LanSection targets={state.targets} changes={state.localChanges} />
      <RecentEvents initial={initial} />
```

3. The initial seeding fetch becomes Internet-only, and a second fetch seeds the LAN last-change times:

```tsx
  fetch("/api/v1/transitions?limit=50&scope=internet")
    .then((r) => (r.ok ? r.json() : null))
    .then((body) => {
      if (body?.items) store.seedRecent(body.items as TransitionEvent[]);
    })
    .catch(() => undefined);
  fetch("/api/v1/transitions?limit=200&scope=local")
    .then((r) => (r.ok ? r.json() : null))
    .then((body) => {
      if (body?.items) store.seedLocalChanges(body.items as TransitionEvent[]);
    })
    .catch(() => undefined);
```

(The verdict-trail fetch `subjectName=Internet` is unchanged.)

- [ ] **Step 7: Run the frontend tests and build**

Run (from `Programs/ConWatch/ClientApp`): `npx vitest run && npx tsc --noEmit && npm run build`
Expected: all PASS; build succeeds. Fix any type error in existing tests that construct `StoreState` by hand (add `localChanges: {}`).

- [ ] **Step 8: Server-rendered fallback — failing test, then implement**

Create `Tests/ConWatch.Tests/LocalScopeUiTests.cs`:

```csharp
using System.Net.Http.Json;

namespace ConWatch.Tests;

public class LocalScopeUiTests : IDisposable
{
    private readonly ConWatchFactory _factory = new();
    private readonly HttpClient _client;

    public LocalScopeUiTests() => _client = _factory.CreateClient();

    public void Dispose() => _factory.Dispose();

    private async Task SeedWithLocalNasAsync()
    {
        await ConfigApiTests.SeedHomeAsync(_client);
        (await _client.PostAsJsonAsync("/api/v1/targets", new
        {
            name = "NasBox", address = "203.0.113.9", profile = "Fast", scope = "Local",
        })).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task The_fallback_dashboard_lists_Local_targets_under_the_LAN_heading_not_in_the_tree()
    {
        await SeedWithLocalNasAsync();
        string html = "";
        await ConfigApiTests.Eventually(async () =>
        {
            html = await _client.GetStringAsync("/");
            return html.Contains("How's my LAN") && html.Contains("NasBox");
        });

        var lan = html.IndexOf("How's my LAN", StringComparison.Ordinal);
        var tree = html.IndexOf("<ul class=\"cw-tree\">", StringComparison.Ordinal);
        Assert.True(tree >= 0 && tree < lan, "the verdict tree comes first");
        // The fallback region before the LAN heading never names the Local target
        // (the initial-data JSON island is above #dashboard-root and is excluded).
        var fallback = html[html.IndexOf("id=\"dashboard-root\"", StringComparison.Ordinal)..];
        Assert.True(fallback.IndexOf("NasBox", StringComparison.Ordinal) > fallback.IndexOf("How's my LAN", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Without_Local_targets_the_fallback_has_no_LAN_heading()
    {
        await ConfigApiTests.SeedHomeAsync(_client);
        string html = "";
        await ConfigApiTests.Eventually(async () =>
        {
            html = await _client.GetStringAsync("/");
            return html.Contains("Router");
        });
        Assert.DoesNotContain("How's my LAN", html);
    }
}
```

Run: `dotnet test Tests/ConWatch.Tests -c Debug --filter "FullyQualifiedName~LocalScopeUiTests"` — Expected: the first test FAILS (no LAN heading).

`Programs/ConWatch/Pages/Index.cshtml.cs` — add `using ConCore.Targets;`; add beside `Roots`/`Children`:

```csharp
    /// <summary>LAN devices for the fallback's "How's my LAN" list (never in the tree).</summary>
    public List<TargetMonitor> LocalMonitors { get; } = new();
```

and at the top of the `foreach (var monitor in engine.Monitors.Values.OrderBy(...))` body add:

```csharp
                if (monitor.Target.Scope == TargetScope.Local)
                {
                    LocalMonitors.Add(monitor);
                    continue;
                }
```

`Programs/ConWatch/Pages/Index.cshtml` — inside `#dashboard-root`, directly after the `<ul class="cw-tree">…</ul>` block and before `@if (Model.EngineDisabled)`, add:

```cshtml
    @if (Model.LocalMonitors.Count > 0)
    {
        <h4>How's my LAN <small>(@Model.LocalMonitors.Count(m => m.CurrentState == HostState.Responding) of @Model.LocalMonitors.Count up)</small></h4>
        <ul class="cw-tree">
            @foreach (var monitor in Model.LocalMonitors)
            {
                <li>
                    <div class="cw-row">
                        <span class="dot @monitor.CurrentState.ToString().ToLowerInvariant()"></span>
                        <span>@monitor.Target.Name</span>
                        <span class="stats">@monitor.CurrentState</span>
                    </div>
                </li>
            }
        </ul>
    }
```

and change the empty-state condition `else if (Model.Roots.Count == 0)` to `else if (Model.Roots.Count == 0 && Model.LocalMonitors.Count == 0)`. (`HostState` is in `ConEngine.Tracking`, which `Index.cshtml` already imports with `@using ConEngine.Tracking`.)

- [ ] **Step 9: Run and commit**

Run: `dotnet test Tests/ConWatch.Tests -c Debug` then (from `ClientApp`) `npx vitest run && npm run build`
Expected: all PASS.

```bash
git add Programs Tests
git commit -m "feat(dashboard): How's my LAN section; Local targets stay out of the tree, strip, ledger and recent events"
```

---

### Task 3: History page — Internet / LAN scope toggle

**Files:**
- Modify: `Programs/ConWatch/Pages/History.cshtml.cs`, `ClientApp/src/history.tsx`, `ClientApp/src/lib/historyData.ts`, `ClientApp/src/lib/historyData.test.ts`, `Tests/ConWatch.Tests/HistoryPageTests.cs`

**Interfaces:**
- Consumes: Task 1 is not required here (the History island already queries per `subjectId`, which is unambiguous); the page subjects JSON gains `scope`.
- Produces: `subjectsForScope(subjects, scope)` and `type HistoryScope = "internet" | "local"` in `historyData.ts`; each History subject has `scope: "Verdict" | "Local"` (pools and the verdict are always `"Verdict"`). The recurrence punchcard, the cause-aligned board and the ledger stay Internet-only; the LAN view shows per-device stats, timeline, latency and loss.

- [ ] **Step 1: Write the failing tests**

Append to `ClientApp/src/lib/historyData.test.ts` (top-level; add `subjectsForScope` to the import from `./historyData`):

```ts
describe("subjectsForScope", () => {
  const subjects = [
    { id: "r", name: "Router", scope: "Verdict" },
    { id: "n", name: "NAS", scope: "Local" },
    { id: "p", name: "Internet" }, // no scope: an older page payload means Verdict
  ];
  it("internet keeps everything that is not Local", () => {
    expect(subjectsForScope(subjects, "internet").map((s) => s.id)).toEqual(["r", "p"]);
  });
  it("local keeps only Local targets", () => {
    expect(subjectsForScope(subjects, "local").map((s) => s.id)).toEqual(["n"]);
  });
  it("is empty rather than throwing when nothing matches", () => {
    expect(subjectsForScope([], "local")).toEqual([]);
  });
});
```

Append to `Tests/ConWatch.Tests/HistoryPageTests.cs` a test (inside the class; add `using System.Net.Http.Json;` already present):

```csharp
    [Fact]
    public async Task Subjects_carry_their_scope_so_the_page_can_split_Internet_from_LAN()
    {
        var client = _factory.CreateClient();
        await ConfigApiTests.SeedHomeAsync(client);
        (await client.PostAsJsonAsync("/api/v1/targets", new
        {
            name = "NasBox", address = "203.0.113.9", profile = "Fast", scope = "Local",
        })).EnsureSuccessStatusCode();

        var data = Island(await client.GetStringAsync("/History"), "history-data");

        Assert.Contains("\"name\":\"NasBox\"", data);
        Assert.Matches("\"name\":\"NasBox\"[^}]*\"scope\":\"Local\"", data);
        Assert.Matches("\"name\":\"Router\"[^}]*\"scope\":\"Verdict\"", data);
        Assert.Matches("\"name\":\"Internet \\(verdict\\)\"[^}]*\"scope\":\"Verdict\"", data);
    }
```

Run: `npx vitest run src/lib/historyData.test.ts` (ClientApp) and `dotnet test Tests/ConWatch.Tests -c Debug --filter "FullyQualifiedName~HistoryPageTests"`
Expected: FAIL (no `subjectsForScope`; no `scope` in the subjects JSON).

- [ ] **Step 2: Page model**

`Programs/ConWatch/Pages/History.cshtml.cs` — in the target subjects projection add after `Role = …`:

```csharp
                Scope = t.Scope.ToString(),
```

and add `Scope = "Verdict",` to the pool subject object and to the verdict subject object (after their `Role = "other"` lines). Object property order matters for the regexes above only in that `scope` follows `name` within the same JSON object — it does.

- [ ] **Step 3: Pure helper**

Append to `ClientApp/src/lib/historyData.ts`:

```ts
export type HistoryScope = "internet" | "local";

/** The History subjects for one scope: LAN devices, or everything else (targets, pools, the verdict). */
export function subjectsForScope<T extends { scope?: string }>(subjects: T[], scope: HistoryScope): T[] {
  return subjects.filter((s) => (s.scope === "Local") === (scope === "local"));
}
```

- [ ] **Step 4: The toggle**

`ClientApp/src/history.tsx`:

1. Import `subjectsForScope, type HistoryScope` from `./lib/historyData` (extend the existing import line), and add `scope?: "Verdict" | "Local";` to `interface Subject`.
2. Replace the first lines of `App` (state + `subject`) with:

```tsx
function App({ subjects }: { subjects: Subject[] }) {
  const hasLan = subjects.some((s) => s.scope === "Local");
  const [scope, setScope] = useState<HistoryScope>("internet");
  const visible = subjectsForScope(subjects, scope);
  const [subjectId, setSubjectId] = useState(visible[0]?.id ?? "");
  const [rangeKey, setRangeKey] = useState("24h");
  const [rollups, setRollups] = useState<RollupPoint[]>([]);
  const [transitions, setTransitions] = useState<TransitionEvent[]>([]);
  const [loading, setLoading] = useState(false);

  const subject = visible.find((s) => s.id === subjectId);
```

3. After the `useEffect` and before the `if (!subject)` early return, define the toggle (so it also renders when one scope has no subjects — a config with only LAN devices must still reach them) and change the early return:

```tsx
  const scopeToggle = hasLan && (
    <div className="cw-segmented" style={{ alignSelf: "center" }}>
      {([["internet", "Internet"], ["local", "LAN"]] as const).map(([key, label]) => (
        <button key={key} type="button" className={scope === key ? "active" : ""}
          onClick={() => {
            setScope(key);
            setSubjectId(subjectsForScope(subjects, key)[0]?.id ?? "");
          }}>{label}</button>
      ))}
    </div>
  );

  if (!subject) return (
    <div>
      <div style={{ display: "flex", gap: "1rem", flexWrap: "wrap" }}>{scopeToggle}</div>
      <p>{hasLan ? "No subjects in this view." : "No subjects yet — add targets on the Targets page."}</p>
    </div>
  );
```

4. In the controls row, render `{scopeToggle}` first, and list `visible` in the select:

```tsx
      <div style={{ display: "flex", gap: "1rem", flexWrap: "wrap" }}>
        {scopeToggle}
        <select value={subjectId} onChange={(e) => setSubjectId(e.target.value)} style={{ maxWidth: "18rem" }}>
          {visible.map((s) => (
```

5. The Internet-only overview sections render only in the Internet view, and the board never lists Local targets:

```tsx
      {scope === "internet" && (
        <>
          <RecurrenceSection to={to} />

          <h4>All targets, cause-aligned <small>(last {OVERVIEW_DAYS} days)</small></h4>
          <RibbonBoard rangeStart={new Date(to.getTime() - OVERVIEW_DAYS * 24 * 3_600_000)} rangeEnd={to}
            subjects={subjects.filter((s): s is Subject & { kind: "target" } => s.kind === "target" && s.scope !== "Local")
              .map((s): BoardSubject => ({
                id: s.id, name: s.name, kind: s.kind,
                parent: s.parent ?? null, role: s.role ?? "other",
              }))} />

          <LedgerSection />
        </>
      )}
```

(The `All targets, cause-aligned` heading and board move inside the fragment; keep their existing props.)

- [ ] **Step 5: Run and commit**

Run (ClientApp): `npx vitest run && npx tsc --noEmit && npm run build`, then `dotnet test Tests/ConWatch.Tests -c Debug --filter "FullyQualifiedName~HistoryPageTests"`
Expected: all PASS, including the existing live-vs-archive island equality test.

```bash
git add Programs Tests
git commit -m "feat(history): Internet / LAN scope toggle; LAN devices get their own per-device view"
```

---

### Task 4: Targets page — scope column, scope select, prefill; switching scope

**Files:**
- Modify: `Programs/ConWatch/Pages/Config/Targets.cshtml`, `Programs/ConWatch/Pages/Config/Targets.cshtml.cs`, `Tests/ConWatch.Tests/PageSmokeTests.cs`, `Tests/ConWatch.Tests/LocalScopeApiTests.cs`

**Interfaces:**
- Consumes: `TargetDto.Scope` (the edit button's `data-json` already carries it); the built-in `Local` profile.
- Produces: the dialog's `scope` select (`name="scope"`, values `Verdict`/`Local`); `?scope=Local` prefill (plan 2's Discoveries "Adopt" opens this dialog pre-filled); the Parent dropdown lists only non-Local targets; choosing Local disables Parent and defaults the profile to `Local`.

- [ ] **Step 1: Write the failing tests**

Append to `Tests/ConWatch.Tests/PageSmokeTests.cs` (inside the class; it already has `_factory`; copy the helper style of the neighbouring tests that seed via `ConfigApiTests.SeedHomeAsync(client)`):

```csharp
    [Fact]
    public async Task Targets_dialog_has_a_scope_select_and_the_table_a_scope_column()
    {
        var html = await _factory.CreateClient().GetStringAsync("/Config/Targets");
        Assert.Contains("name=\"scope\"", html);
        Assert.Contains("<th>Scope</th>", html);
        Assert.Contains("<option value=\"Verdict\"", html);
        Assert.Contains("<option value=\"Local\"", html);
    }

    [Fact]
    public async Task Scope_prefill_selects_Local_and_defaults_the_profile_to_Local()
    {
        var html = await _factory.CreateClient()
            .GetStringAsync("/Config/Targets?address=203.0.113.9&name=Cam&scope=Local");
        Assert.Contains("<option value=\"Local\" selected=\"selected\"", html);
        Assert.Contains("<option selected=\"selected\" title=\"LAN devices tracked on the side", html);
    }

    [Fact]
    public async Task The_parent_dropdown_never_offers_a_Local_target()
    {
        var client = _factory.CreateClient();
        await ConfigApiTests.SeedHomeAsync(client);
        (await client.PostAsJsonAsync("/api/v1/targets", new
        {
            name = "NasBox", address = "203.0.113.9", profile = "Fast", scope = "Local",
        })).EnsureSuccessStatusCode();

        var html = await client.GetStringAsync("/Config/Targets");
        var parent = System.Text.RegularExpressions.Regex.Match(html,
            "<select name=\"parent\">(.*?)</select>", System.Text.RegularExpressions.RegexOptions.Singleline);
        Assert.True(parent.Success);
        Assert.Contains("Router", parent.Groups[1].Value);
        Assert.DoesNotContain("NasBox", parent.Groups[1].Value);
    }
```

(add `using System.Net.Http.Json;` if the file lacks it.)

Append to `Tests/ConWatch.Tests/LocalScopeApiTests.cs` (the positive scope-switch test deferred from plan 1A's review):

```csharp
    [Fact]
    public async Task A_target_can_be_switched_Verdict_to_Local_and_back()
    {
        await ConfigApiTests.SeedHomeAsync(_client);
        (await _client.PostAsJsonAsync("/api/v1/targets", new
        {
            name = "Cam", address = "203.0.113.20", profile = "Fast",
        })).EnsureSuccessStatusCode();

        async Task<(string Id, JsonElement Dto)> Cam()
        {
            var list = JsonSerializer.Deserialize<JsonElement>(await _client.GetStringAsync("/api/v1/targets"));
            var dto = list.EnumerateArray().Single(t => t.GetProperty("name").GetString() == "Cam");
            return (dto.GetProperty("id").GetString()!, dto);
        }

        var (id, _) = await Cam();
        var toLocal = await _client.PutAsJsonAsync($"/api/v1/targets/{id}", new
        {
            id, name = "Cam", address = "203.0.113.20", profile = "Fast", scope = "Local",
        });
        Assert.True(toLocal.IsSuccessStatusCode, await toLocal.Content.ReadAsStringAsync());
        Assert.Equal("Local", (await Cam()).Dto.GetProperty("scope").GetString());

        var back = await _client.PutAsJsonAsync($"/api/v1/targets/{id}", new
        {
            id, name = "Cam", address = "203.0.113.20", profile = "Fast", scope = "Verdict",
        });
        Assert.True(back.IsSuccessStatusCode, await back.Content.ReadAsStringAsync());
        Assert.Equal("Verdict", (await Cam()).Dto.GetProperty("scope").GetString());
    }
```

Run: `dotnet test Tests/ConWatch.Tests -c Debug --filter "FullyQualifiedName~PageSmokeTests|FullyQualifiedName~LocalScopeApiTests"`
Expected: the three page tests FAIL; the switch test PASSES (API already supports it — it pins the behaviour).

- [ ] **Step 2: Page model**

`Targets.cshtml.cs` — add beside the other prefill properties:

```csharp
    public string? PrefillScope => Request.Query["scope"];
```

and change `DefaultProfile` to:

```csharp
    /// <summary>The profile the dialog selects: the prefilled one when it exists, else Local for a
    /// Local-scope prefill, else Public.</summary>
    public string DefaultProfile =>
        Config.Profiles.Any(p => p.Name.Equals(PrefillProfile, StringComparison.OrdinalIgnoreCase))
            ? PrefillProfile!
            : string.Equals(PrefillScope, "Local", StringComparison.OrdinalIgnoreCase) ? "Local" : "Public";
```

- [ ] **Step 3: View**

`Targets.cshtml`:

1. Add `@using ConCore.Targets` below `@using System.Text.Json`.
2. Table header: `<th>Profile</th><th>Scope</th><th>Enabled</th>`; body cell after the profile cell: `<td>@(t.Scope == TargetScope.Local ? "Local" : "Verdict")</td>`.
3. Parent options skip Local targets:

```cshtml
                        @foreach (var t in Model.Config.Targets.Where(t => t.Scope != TargetScope.Local).OrderBy(t => t.Name))
```

4. In the dialog, after the `Profile`/`Enabled` grid, add a Scope row:

```cshtml
            <label>Scope
                <select name="scope" id="scope-select">
                    <option value="Verdict" selected="@(!string.Equals(Model.PrefillScope, "Local", StringComparison.OrdinalIgnoreCase))">Internet path — counts toward the verdict</option>
                    <option value="Local" selected="@string.Equals(Model.PrefillScope, "Local", StringComparison.OrdinalIgnoreCase)">LAN device — tracked on the side, never affects the verdict</option>
                </select>
            </label>
```

5. In the script, after `const form = …; const dialog = …;` add:

```js
        const scopeSelect = document.getElementById("scope-select");
        const parentSelect = form.elements.parent;
        const pageDefaultProfile = @Html.Raw(json(Model.DefaultProfile));
        function applyScope() {
            const local = scopeSelect.value === "Local";
            parentSelect.disabled = local;   // a LAN device is always a root
            if (local) parentSelect.value = "";
        }
        scopeSelect.addEventListener("change", () => {
            applyScope();
            const profile = form.elements.profile;
            const hasLocalProfile = [...profile.options].some((o) => o.value === "Local");
            // Follow the scope with the profile unless the user already picked another one.
            if (scopeSelect.value === "Local" && hasLocalProfile && profile.value === pageDefaultProfile)
                profile.value = "Local";
            else if (scopeSelect.value === "Verdict" && profile.value === "Local")
                profile.value = pageDefaultProfile === "Local" ? "Public" : pageDefaultProfile;
        });
        applyScope();
```

6. `buildDto` gains `scope: scopeSelect.value,` and its `parent` line becomes `parent: scopeSelect.value === "Local" ? null : (form.elements.parent.value || null),`.
7. `fillForm` gains, before `setProbe(...)`: `scopeSelect.value = dto.scope ?? "Verdict"; applyScope();` and its `parent` assignment stays above (a Local target's `dto.parent` is null).
8. `resetForm` gains `applyScope();` as its first line (`form.reset()` has already restored the prefilled scope).

- [ ] **Step 4: Run and commit**

Run: `dotnet test Tests/ConWatch.Tests -c Debug`
Expected: all PASS (existing `PageSmokeTests` for the Targets page keep passing; the page smoke list includes `ISP / operator`, the Public default title, etc.). The dialog's runtime behaviour is exercised in Task 7's browser check.

```bash
git add Programs Tests
git commit -m "feat(config): scope column and select on the Targets page; Local prefill; no Local parents"
```

---

### Task 5: Clone — "Include local devices"

**Files:**
- Modify: `Programs/ConWatch/Clone/CloneService.cs`, `Programs/ConWatch/Api/SetupEndpoints.cs`, `Programs/ConWatch/Pages/Setup.cshtml`, `Programs/ConWatch/wwwroot/lib/site-setup.js`, `Tests/ConWatch.Tests/CloneTests.cs`

**Interfaces:**
- Consumes: `Snapshot` (`Config.Targets[].Scope`, `Transitions[].SubjectId`, `Rollups[].TargetId`, `Glitches[].TargetId`), `SnapshotImporter.ImportAsync`.
- Produces: `CloneRequest.IncludeLocalDevices` (default `true`); `CloneService.CloneAsync(…, CancellationToken ct, bool includeLocalDevices = true)`; the clone summary gains `LocalDevicesSkipped` (count). Take-over always keeps Local targets (it is the same site's final sync).

- [ ] **Step 1: Write the failing tests**

Add to `Tests/ConWatch.Tests/CloneTests.cs` (inside the class; it already has `MakeTarget()`, `GetJson`, `SeedSourceAsync()`, `_sourceClient`). The request body below is the one the existing clone tests use (`sourceUrl = "http://source.test/"` is routed at the source app by `MakeTarget()`):

```csharp
    private async Task SeedSourceWithLocalNasAsync()
    {
        await SeedSourceAsync();
        (await _sourceClient.PostAsJsonAsync("/api/v1/targets", new
        {
            name = "NasBox", address = "203.0.113.9", profile = "Fast", scope = "Local",
        })).EnsureSuccessStatusCode();
        // The Local target must have recorded history to be carried (or skipped).
        await ConfigApiTests.Eventually(async () =>
            (await GetJson(_sourceClient, "/api/v1/transitions?scope=local&limit=10"))
                .GetProperty("items").GetArrayLength() > 0);
    }

    [Fact]
    public async Task Clone_copies_Local_devices_and_their_history_by_default()
    {
        await SeedSourceWithLocalNasAsync();
        var target = MakeTarget();
        var client = target.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/setup/clone", new
        {
            sourceUrl = "http://source.test/", includeSecrets = true, allowInsecure = true,
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var targets = await GetJson(client, "/api/v1/targets");
        var nas = targets.EnumerateArray().Single(t => t.GetProperty("name").GetString() == "NasBox");
        Assert.Equal("Local", nas.GetProperty("scope").GetString());
        Assert.True((await GetJson(client, "/api/v1/transitions?scope=local&limit=10"))
            .GetProperty("items").GetArrayLength() > 0);
    }

    [Fact]
    public async Task Clone_without_Local_devices_leaves_no_trace_of_them_and_keeps_everything_else()
    {
        await SeedSourceWithLocalNasAsync();
        var target = MakeTarget();
        var client = target.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/setup/clone", new
        {
            sourceUrl = "http://source.test/", includeSecrets = true, allowInsecure = true,
            includeLocalDevices = false,
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var summary = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        Assert.Equal(1, summary.GetProperty("localDevicesSkipped").GetInt32());

        var names = (await GetJson(client, "/api/v1/targets")).EnumerateArray()
            .Select(t => t.GetProperty("name").GetString()).ToList();
        Assert.DoesNotContain("NasBox", names);
        Assert.Contains("Router", names);                       // verdict-scope targets still cloned
        var pools = await GetJson(client, "/api/v1/pools");
        Assert.Equal(1, pools.GetArrayLength());                 // pools untouched
        // No orphan history for the skipped device: nothing in the log names it.
        var all = (await GetJson(client, "/api/v1/transitions?limit=1000")).GetProperty("items").EnumerateArray();
        Assert.DoesNotContain(all, i => i.GetProperty("subjectName").GetString() == "NasBox");
        var glitches = (await GetJson(client, "/api/v1/glitches?limit=1000")).GetProperty("items").EnumerateArray();
        Assert.DoesNotContain(glitches, g => g.GetProperty("targetName").GetString() == "NasBox");
    }
```

Run: `dotnet test Tests/ConWatch.Tests -c Debug --filter "FullyQualifiedName~CloneTests"`
Expected: the second test FAILS (NasBox is cloned; no `localDevicesSkipped`); the first PASSES.

- [ ] **Step 2: Implement the service change**

`CloneService.cs` — add `using ConCore.Targets;`. Change the signature and body:

```csharp
    public async Task<CloneOutcome> CloneAsync(string? sourceUrl, string? sourcePassword,
        bool includeSecrets, bool allowInsecure, CancellationToken ct, bool includeLocalDevices = true)
```

After `var snapshot = fetched.Snapshot!;` replace that line with:

```csharp
        var snapshot = fetched.Snapshot!;
        var localSkipped = 0;
        if (!includeLocalDevices)
        {
            localSkipped = snapshot.Config.Targets.Count(t => t.Scope == TargetScope.Local);
            snapshot = WithoutLocalDevices(snapshot);
        }
```

and change the success return to `return CloneOutcome.Ok(Summary(snapshot, includeSecrets, localSkipped));` (the take-over call passes `localSkipped` default 0). Add:

```csharp
    /// <summary>The snapshot minus every Local-scope target and the transitions, rollups and
    /// glitches recorded for them. Pools and the verdict cannot reference Local targets
    /// (validator rule), so nothing else needs rewriting.</summary>
    internal static Snapshot WithoutLocalDevices(Snapshot snapshot)
    {
        var localIds = snapshot.Config.Targets
            .Where(t => t.Scope == TargetScope.Local && t.Id is not null)
            .Select(t => t.Id!.Value).ToHashSet();
        return snapshot with
        {
            Config = snapshot.Config with
            {
                Targets = snapshot.Config.Targets.Where(t => t.Scope != TargetScope.Local).ToList(),
            },
            Transitions = snapshot.Transitions.Where(t => !localIds.Contains(t.SubjectId)).ToList(),
            Rollups = snapshot.Rollups.Where(r => !localIds.Contains(r.TargetId)).ToList(),
            Glitches = snapshot.Glitches.Where(g => !localIds.Contains(g.TargetId)).ToList(),
        };
    }
```

and extend `Summary`:

```csharp
    private static object Summary(Snapshot snapshot, bool includeSecrets, int localDevicesSkipped = 0) => new
    {
        Targets = snapshot.Config.Targets.Count,
        Pools = snapshot.Config.Pools.Count,
        Profiles = snapshot.Config.Profiles.Count,
        Transitions = snapshot.Transitions.Count,
        Rollups = snapshot.Rollups.Count,
        Glitches = snapshot.Glitches.Count,
        SecretsTransferred = includeSecrets,
        LocalDevicesSkipped = localDevicesSkipped,
        RedactedSettings = snapshot.RedactedSettings,
    };
```

(If `Snapshot`/`ConfigDocument` expose `Targets`/`Transitions` with `{ get; init; }` the `with` expressions compile as written; if a collection property is get-only, change it to `init` in the record rather than mutating.)

`SetupEndpoints.cs`:

```csharp
    public record CloneRequest(string? SourceUrl, string? SourcePassword,
        bool IncludeSecrets = true, bool AllowInsecure = false, bool IncludeLocalDevices = true);
```

and the `/setup/clone` handler:

```csharp
        api.MapPost("/setup/clone", async (CloneRequest request, CloneService clone,
            CancellationToken ct) => (await clone.CloneAsync(request.SourceUrl,
                request.SourcePassword, request.IncludeSecrets, request.AllowInsecure, ct,
                request.IncludeLocalDevices)).ToHttp());
```

- [ ] **Step 3: Setup page**

`Setup.cshtml` — in `#clone-section`, update the explanatory paragraph's first sentence to "Clones the source node's configuration (including any local LAN devices), full history, and …" and add, directly above the `<p><button id="clone-btn" …>` line:

```cshtml
    <p><label><input id="clone-local" type="checkbox" checked />
        Also copy my local devices <small>(LAN devices tracked on the side — uncheck when this node is on a different network)</small></label></p>
```

`wwwroot/lib/site-setup.js` — in `runClone`, add one property to the POST body:

```js
        includeSecrets,
        allowInsecure,
        includeLocalDevices: document.getElementById("clone-local").checked,
```

- [ ] **Step 4: Run and commit**

Run: `dotnet test Tests/ConWatch.Tests -c Debug`
Expected: all PASS (existing clone/take-over tests unchanged: take-over still imports everything).

```bash
git add Programs Tests
git commit -m "feat(clone): 'Include local devices' option; skipped devices leave no history behind"
```

---

### Task 6: SECURITY.md — adopted devices are visible on the open surface

**Files:**
- Modify: `SECURITY.md`

**Interfaces:** none (documentation).

- [ ] **Step 1: Edit**

In `SECURITY.md`, in the first bullet of "Security posture" (the one beginning "**The watching surface is open by design.**"), extend the last sentence so the bullet ends:

```markdown
  they expose what your dashboard shows. Anyone who can reach the port can
  see your connection history and target names. That includes any LAN
  devices you track as Local targets (names, addresses and up/down
  history appear in `/api/v1/status`, the sanitized snapshot export and
  the MCP tools) — treat that list like a network inventory when deciding
  who can reach the port.
```

- [ ] **Step 2: Commit**

```bash
git add SECURITY.md
git commit -m "docs(security): Local targets are visible on the open watching surface"
```

---

### Task 7: Whole-branch verification and browser check

**Files:** none (verification only; fix any defect in the owning task's files).

- [ ] **Step 1: Run every automated check**

```bash
dotnet test Tests/ConEngine.Tests -c Debug
dotnet test Tests/ConPersistence.Tests -c Debug
dotnet test Tests/ConWatch.Tests -c Debug
dotnet test Tests/ConConfig.Tests -c Debug
dotnet test Tests/ConWatchCli.Tests -c Debug
dotnet test Tests/ConNotify.Tests -c Debug
cd Programs/ConWatch/ClientApp && npx tsc --noEmit && npx vitest run && npm run build
```

Expected: all PASS (ConWatch known flake aside).

- [ ] **Step 2: Browser check against a scratch instance**

Build the client first (Step 1 does). Start the app from the worktree against a SCRATCH data directory (`Persistence__DbPath` under a temp directory) on a free loopback port, with admin password unset; use only RFC 5737 addresses. Seed through the REST API: a profile `Fast` (0.5 s interval is fine), a router, an anchor pool of two documentation addresses, and two Local targets (one reachable address such as `127.0.0.1`, one unreachable `203.0.113.99`). Then, with the built-in browser (`mcp__Claude_Browser__*`; start with `preview_start` `{url}` or `navigate`), verify and capture screenshots/text:

1. **Dashboard** `/`: the verdict tree lists the router/anchors only; a "How's my LAN (N of M up)" section lists the two Local devices with state and a since-time (an em dash until the first change); no Local device appears in the status bar line or "show all events" panel; no console errors.
2. **History** `/History`: an Internet/LAN toggle is present; LAN lists only the Local devices with their timeline/latency/loss; Internet view shows the recurrence punchcard, board and ledger without the Local devices.
3. **Targets** `/Config/Targets`: the table has a Scope column; "Add target" shows the Scope select; choosing "LAN device" disables Parent, clears it, and switches the profile to `Local`; switching back restores it; saving creates a Local target; the Parent dropdown never lists a Local target; `/Config/Targets?address=203.0.113.50&name=Cam&scope=Local` opens the dialog with Scope=LAN device and profile Local.
4. **Setup** `/Setup` (open the clone section): the "Also copy my local devices" checkbox is present and checked.

Stop the app and delete the scratch data. Never touch the user's real ConnectionWatcher service, tray or data directory. Record what was observed; anything that does not match is a defect for the owning task.

- [ ] **Step 3: Commit only if something was fixed**

```bash
git status --short
git add -A -- Programs Libraries Tests SECURITY.md
git commit -m "fix: address verification findings for Local scope UI"
```

(Skip if clean. Do not push; hand the branch back for review before merging to `master`.)
