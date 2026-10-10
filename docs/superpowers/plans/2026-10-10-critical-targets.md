# Critical Targets — Implementation Plan (Plan C of 4)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A target can be marked **critical**: "notify when this is down, even if the internet is fine". The setup wizard marks the router critical, and adopting a Gateway discovery pre-checks it. Existing installs are unchanged. A critical target's own failure and its recovery reach the tray (as a toast), webhooks (even with "verdict only" on) and MQTT (as an attribute), and the dashboard marks the row. Critical targets never change the verdict.

**Architecture:**
- One new `bool Critical` flows through the target pipeline exactly like `Operator`/`Scope` did:
  - `Target` → `TargetConfig` → `TargetGraph`
  - `TargetRow` (EF migration `AddTargetCritical`) → `TargetDto` → `ConfigMapping`
  - Snapshots and clone carry `TargetDto` whole, so they need no code.
- `WatcherEngine.IsCriticalSubject(id)` mirrors `IsLocalSubject`. The notifiers use it.
- The status snapshot gains `critical` and `explainedBy` per target (spec §5). The tray reads them from `/api/v1/status` and keeps its own "already announced" set, so each failure toasts once and each recovery once.
- The engine's state machine and the verdict are untouched.

**Tech Stack:** .NET 10, C#, EF Core + SQLite, xunit 2.9.3; React 18 + vitest 2; WinForms tray (pure logic in `ConInfrastructure`, tested in `ConWatch.Tests`).

**Spec:** `docs/superpowers/specs/2026-10-09-evidence-based-verdict-design.md`. This plan implements §4.7 (critical targets), D6, the §5 rows for `Target`/persistence/DTOs, status and MCP `critical` + `explainedBy`, setup wizard, Discoveries adopt, tray critical toasts, `ConNotify` "critical passes verdict only" and MQTT attributes `critical`, and the §7 test bullets for them. That is rollout step 4 of §8.

## Global Constraints

- File-scoped namespaces; `Nullable` and `ImplicitUsings` enabled; match the surrounding comment density and naming.
- **D6:** `Critical` defaults to `false` everywhere: the C# property, the DB column for existing rows, a JSON body without the key, and old snapshots. **Existing installs are unchanged.**
- Dialog label text, verbatim (spec §4.7): **"Critical — notify when this is down, even if the internet is fine"**.
- Wire name: `critical` (camelCase JSON) on `TargetDto` and the status target. `explainedBy` (a GUID or null) on the status target.
- **Critical never changes the verdict.** No engine coordinator reads `Target.Critical`; only the host and notifiers do.
- **A critical event is a critical target's own failure or recovery:**
  - Failure: entering `Down` or `Unreachable` from a state that is neither.
  - Recovery: leaving `Down`/`Unreachable` for an answering state (`Responding`, `Recovering`, `Faltering`), or a released target's first answering transition out of `Starting` that names its former cause (`Reason.AncestorId` set).
  - A `Down`↔`Unreachable` reclassification is **not** a new event.
- Model warnings (Plan B) stay admin-only and never toast. Only the critical toast may *mention* that traffic is passing the device (spec §4.7: "the toast text says so").
- **Never sleep-then-assert in tests:** wait on conditions. CI runners have 2 cores.
- Test commands, per project (never the whole solution): `dotnet test Tests/ConEngine.Tests -c Debug`, `Tests/ConPersistence.Tests`, `Tests/ConNotify.Tests`, `Tests/ConWatch.Tests`; filter with `--filter "FullyQualifiedName~<Class>"`. UI: `cd Programs/ConWatch/ClientApp && npx vitest run`, plus `npm run build` for the type check. `ConWatch.Tests` has known cold-start flakes (`ProbeFieldTests`, the `SettingsApiTests` constructor, `NotifierIntegrationTests.VerdictOnly_…`). Rerun once before calling a failure real.
- EF migrations: `dotnet tool restore`, then `dotnet ef migrations add <Name> --project Libraries/ConPersistence` (project-only form; run `dotnet restore Libraries/ConPersistence` first in a fresh worktree).
- Commit after every task. **Never push.** Commit messages carry no attribution lines. Work on a feature branch `critical-targets` in a git worktree (`.worktrees/critical-targets`). Do not edit `README.md` (the user has an uncommitted rewrite). Before merging, check `git status` in the MAIN checkout for stray edits.

## Decisions this plan makes (flag to Mark at review)

These are not in the spec.

1. **Critical events bypass both webhook filters,** "verdict only" and "skip explained transitions" (default on). Spec §4.7 says a critical target's own `Down` (or `Unreachable`) and its recovery are reportable, and "critical means tell me" (§9.1). So a critical NAS behind a dead switch (Unreachable, explained) is still delivered. Non-event transitions of a critical target (for example `Responding → Faltering`) follow the normal filters.
2. **Critical LAN (Local-scope) targets notify through webhooks and the tray,** which today drop Local targets entirely. **MQTT keeps excluding Local targets** (no entity is created for them). The spec only says the existing per-target sensor gains an attribute.
3. **No tray critical toast while the internet itself is down.** When the tray state is `Down` (LinkDown), the "Internet DOWN" toast already names the blamed hop, and a second toast for the router would be crying wolf (D7). If the critical device is still down when the internet comes back, it toasts then. Its recovery toasts only if its failure did.
4. **The wizard marks the router critical only when it creates it.** An existing target at the router's address is reused untouched (D6).
5. **Webhook and MQTT transition payloads do not gain a `critical` field.** A consumer that cares can read `/api/v1/status`. That avoids changing the shared `TransitionDto`.
6. **The status target also gains `explainedBy`** (spec §5 lists it next to `critical`). It is the monitor's current root cause (`TargetMonitor.ExplainedBy`) and is shown nowhere new yet.

## Review Focus

The failure modes most likely to bite, each pinned by a test in the named task:

1. **An existing install upgrades.** Every existing target reads `critical: false` after the migration, and a JSON body or old snapshot without the key imports as `false` (Task 1).
2. **A critical router dies while the anchors still answer,** as in the 2026-10-08 shape with the router critical. Expect one webhook with "verdict only" on, one tray toast saying traffic is still passing it (the HopBypassed note exists), and one recovery toast. No second toast when it is reclassified (Tasks 4 and 5).
3. **A tray poll that fails** (service restarting) between a critical failure and its recovery. Expect no false "answering again" toast when the service is unreachable, and no duplicate failure toast when it returns still down (Task 5).
4. **A critical target is deleted or un-marked while down.** Expect no "answering again" toast; the announcement is dropped silently (Task 5).
5. **A released critical target** (Unreachable → Starting → Responding naming its former cause) gets exactly one recovery delivery, and `Unreachable → Starting` itself is not delivered (Task 4).

---

## File Structure

| File | Responsibility | Task |
|---|---|---|
| `Core/ConCore/Targets/Target.cs`, `Core/ConEngine/Configuration/WatcherOptions.cs`, `Core/ConEngine/Topology/TargetGraph.cs` | `Critical` on the model | 1 |
| `Core/ConEngine/WatcherEngine.cs` | `IsCriticalSubject` | 1 |
| `Libraries/ConPersistence/Entities/ConfigRows.cs`, `Config/ConfigDtos.cs`, `Config/ConfigMapping.cs`, `Migrations/*_AddTargetCritical.cs` | Column, DTO, the four mapping lines | 1 |
| `Programs/ConWatch/StatusSnapshotBuilder.cs`, `Mcp/ConWatchMcpTools.cs` | `critical` + `explainedBy` on status/MCP | 2 |
| `Programs/ConWatch/Pages/Config/Targets.cshtml(.cs)`, `Pages/Config/Discoveries.cshtml.cs`, `SetupService.cs` | Dialog checkbox + table marker, Gateway pre-check, wizard router | 3 |
| `Libraries/ConNotify/WebhookFilter.cs`, `WebhookNotifier.cs`, `Builders.cs`, `MqttNotifier.cs` | Critical bypass; MQTT target attributes | 4 |
| `Libraries/ConInfrastructure/Tray/TrayStatus.cs`, `Tray/CriticalWatch.cs` (new), `Programs/ConWatchTray/Program.cs` | Parse, announce once, toast, tooltip | 5 |
| `Programs/ConWatch/ClientApp/src/lib/types.ts`, `src/components/TargetRow.tsx`, `wwwroot/css/site.css` | Row marker; chip always on for critical | 6 |

Tests: `Tests/ConPersistence.Tests/{TargetCriticalTests (new), SnapshotShapeTests}.cs`; `Tests/ConEngine.Tests/CriticalSubjectTests.cs` (new); `Tests/ConWatch.Tests/{CriticalFieldTests (new), StatusShapeTests, McpEndpointTests, PageSmokeTests, DiscoveriesPageTests, SetupApiTests, NotifierIntegrationTests, TrayStatusTests, CriticalWatchTests (new)}.cs`; `Tests/ConNotify.Tests/{WebhookFilterTests, BuildersTests}.cs`; `ClientApp/src/components/TargetRow.test.tsx`.

---

### Task 1: Model, persistence and `IsCriticalSubject`

**Files:**
- Modify: `Core/ConCore/Targets/Target.cs`, `Core/ConEngine/Configuration/WatcherOptions.cs` (`TargetConfig`), `Core/ConEngine/Topology/TargetGraph.cs` (`FromOptions` initializer), `Core/ConEngine/WatcherEngine.cs`, `Libraries/ConPersistence/Entities/ConfigRows.cs` (`TargetRow`), `Libraries/ConPersistence/Config/ConfigDtos.cs` (`TargetDto`), `Libraries/ConPersistence/Config/ConfigMapping.cs`
- Create: migration `AddTargetCritical` (generated)
- Test: `Tests/ConPersistence.Tests/TargetCriticalTests.cs` (new), `Tests/ConPersistence.Tests/SnapshotShapeTests.cs`, `Tests/ConEngine.Tests/CriticalSubjectTests.cs` (new), `Tests/ConWatch.Tests/CriticalFieldTests.cs` (new)

**Interfaces:**
- Produces:
  - `Target.Critical`, `TargetConfig.Critical`, `TargetRow.Critical`, `TargetDto.Critical`, all `bool` and default `false`
  - JSON key `critical`
  - `WatcherEngine.IsCriticalSubject(Guid subjectId)`: true only for an enabled-or-not target in the graph with `Critical`; false for pools, the verdict subject, unknown ids and before start

- [ ] **Step 1: Write the failing persistence tests**

Create `Tests/ConPersistence.Tests/TargetCriticalTests.cs`:

```csharp
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

    [Fact]
    public void Every_mapping_carries_critical()
    {
        var config = new TargetConfig { Name = "R", Address = "203.0.113.1", Profile = "Internal", Critical = true };
        Assert.True(config.ToDto().Critical);
        Assert.True(config.ToDto().ToConfig().Critical);
        var row = new Entities.TargetRow { Id = Guid.NewGuid(), Name = "R", Address = "203.0.113.1", Critical = true };
        Assert.True(row.ToConfig(null, "Internal").Critical);
    }
}
```

In `SnapshotShapeTests.cs` (L51–52), add `"critical"` to the pinned target keys:

```csharp
        AssertKeys(json.GetProperty("config").GetProperty("targets")[0],
            "id", "name", "address", "parent", "profile", "enabled", "operator", "probe", "scope", "critical");
```

`TargetRow` may require more properties than the test sets (for example `ProfileId`). If the object initializer does not compile, set them to `Guid.Empty`/defaults; the mapping only reads the fields listed.

- [ ] **Step 2: Write the failing engine test**

Create `Tests/ConEngine.Tests/CriticalSubjectTests.cs`:

```csharp
using ConEngine.Configuration;
using ConEngine.Tests.Fakes;
using ConEngine.Topology;
using Microsoft.Extensions.Logging.Abstractions;

namespace ConEngine.Tests;

public class CriticalSubjectTests
{
    private static WatcherOptions Options() => new()
    {
        Targets =
        [
            new TargetConfig { Name = "Router", Address = "192.168.1.1", Profile = "Internal", Critical = true },
            new TargetConfig { Name = "Google", Address = "8.8.8.8", Parent = "Router" },
            new TargetConfig { Name = "NAS", Address = "192.168.1.20", Profile = "Internal",
                Scope = ConCore.Targets.TargetScope.Local, Critical = true },
        ],
        Pools = [new PoolConfig { Name = "Internet", Members = ["Google"] }],
    };

    [Fact]
    public async Task IsCriticalSubject_is_true_only_for_critical_targets()
    {
        var engine = new WatcherEngine(TimeProvider.System, new FakePingTransceiverFactory(),
            new ConInfrastructure.DomainNameResolverFactory(), NullLoggerFactory.Instance,
            Microsoft.Extensions.Options.Options.Create(Options()));
        Assert.False(engine.IsCriticalSubject(Guid.NewGuid())); // before start: no graph, no throw

        using var cts = new CancellationTokenSource();
        await engine.StartAsync(cts.Token);
        try
        {
            Assert.True(engine.IsCriticalSubject(engine.Graph.GetByName("Router").Id));
            Assert.True(engine.IsCriticalSubject(engine.Graph.GetByName("NAS").Id)); // Local can be critical
            Assert.False(engine.IsCriticalSubject(engine.Graph.GetByName("Google").Id));
            Assert.False(engine.IsCriticalSubject(engine.Graph.GetPoolByName("Internet").Id));
            Assert.False(engine.IsCriticalSubject(VerdictCoordinator.SubjectId));
            Assert.True(engine.Graph.GetByName("Router").Critical); // TargetGraph carries it
        }
        finally
        {
            cts.Cancel();
            await engine.StopAsync();
        }
    }
}
```

- [ ] **Step 3: Write the failing end-to-end round-trip test**

Create `Tests/ConWatch.Tests/CriticalFieldTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ConPersistence.Config;

namespace ConWatch.Tests;

/// <summary>The Critical flag through the REST API, config export/import and snapshots.</summary>
public class CriticalFieldTests : IDisposable
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly ConWatchFactory _factory = new();
    private readonly HttpClient _client;

    public CriticalFieldTests() => _client = _factory.CreateClient();

    public void Dispose() => _factory.Dispose();

    private async Task<ConfigDocument> ExportAsync() =>
        JsonSerializer.Deserialize<ConfigDocument>(
            await _client.GetStringAsync("/api/v1/config/export"), Web)!;

    [Fact]
    public async Task Critical_round_trips_through_create_update_export_and_import()
    {
        var create = await _client.PostAsJsonAsync("/api/v1/targets", new
        {
            name = "Router", address = "192.168.1.1", profile = "Internal", critical = true,
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        (await _client.PostAsJsonAsync("/api/v1/targets", new
        {
            name = "Google", address = "8.8.8.8", parent = "Router", profile = "Public",
        })).EnsureSuccessStatusCode();

        var doc = await ExportAsync();
        var router = doc.Targets.Single(t => t.Name == "Router");
        Assert.True(router.Critical);
        Assert.False(doc.Targets.Single(t => t.Name == "Google").Critical); // no key = false

        var update = await _client.PutAsJsonAsync($"/api/v1/targets/{router.Id}",
            router with { Critical = false });
        Assert.Equal(HttpStatusCode.NoContent, update.StatusCode);
        Assert.False((await ExportAsync()).Targets.Single(t => t.Name == "Router").Critical);

        // Import carries it back.
        var import = await _client.PostAsJsonAsync("/api/v1/config/import", doc);
        Assert.True(import.IsSuccessStatusCode, await import.Content.ReadAsStringAsync());
        Assert.True((await ExportAsync()).Targets.Single(t => t.Name == "Router").Critical);

        // And a snapshot carries it.
        var snapshot = await _client.GetStringAsync("/api/v1/export/snapshot");
        Assert.Contains("\"critical\":true", snapshot);
    }
}
```

If `/api/v1/config/import` expects a different body shape than `ConfigDocument`, use whatever `OperatorFieldTests.Operator_survives_config_import_and_snapshot_round_trip` posts there.

- [ ] **Step 4: Run them to verify they fail**

Run:
```bash
dotnet test Tests/ConPersistence.Tests -c Debug --filter "FullyQualifiedName~TargetCriticalTests|FullyQualifiedName~SnapshotShapeTests"
dotnet test Tests/ConEngine.Tests -c Debug --filter "FullyQualifiedName~CriticalSubjectTests"
```
Expected: build FAILS (`'TargetConfig' does not contain a definition for 'Critical'`). Capture the output.

- [ ] **Step 5: Implement the model**

`Target.cs`, after `Scope`:

```csharp
    /// <summary>Report this target's own failure and recovery even while the
    /// internet is fine (spec 2026-10-09 §4.7). Never affects the verdict.</summary>
    public bool Critical { get; init; }
```

`TargetConfig` in `WatcherOptions.cs`, after `Scope`:

```csharp
    /// <summary>Notify when this is down, even if the internet is fine.</summary>
    public bool Critical { get; set; }
```

`TargetGraph.FromOptions`: add `Critical = t.Critical,` after `Scope = t.Scope,`.

`WatcherEngine`, directly after `IsLocalSubject`:

```csharp
    /// <summary>
    /// True when <paramref name="subjectId"/> is a target marked critical: the
    /// notifiers report its own failure and recovery even while the internet
    /// is fine. False for pools, the verdict subject, unknown ids and before
    /// the engine starts.
    /// </summary>
    public bool IsCriticalSubject(Guid subjectId) =>
        _graph is { } graph && graph.Targets.TryGetValue(subjectId, out var target) && target.Critical;
```

- [ ] **Step 6: Implement persistence**

- `TargetRow` (`ConfigRows.cs`), after `Scope`: `public bool Critical { get; set; }`.
- `TargetDto` (`ConfigDtos.cs`), after `Scope`:

```csharp
    /// <summary>Notify when this is down, even if the internet is fine. A
    /// missing value (older clients/snapshots) means false.</summary>
    public bool Critical { get; init; }
```

- `ConfigMapping.cs`: in each of `ToDto(this TargetConfig t)`, `ToConfig(this TargetDto t)` and `ToConfig(this TargetRow t, …)`, add `Critical = t.Critical,` after `Scope = t.Scope`. In `CopyFrom`, after `row.Scope = dto.Scope;`, add `row.Critical = dto.Critical;`.
- Do **not** add `HasDefaultValue` for the column in `ConWatchDbContext`. A bool default of `false` is EF's sentinel and would warn. The migration's own `defaultValue: false` fills existing rows.

Generate the migration:
```bash
dotnet tool restore
dotnet ef migrations add AddTargetCritical --project Libraries/ConPersistence
```

Open it. `Up` must be exactly one `AddColumn<bool>(name: "Critical", table: "Targets", type: "INTEGER", nullable: false, defaultValue: false)`, and `Down` a `DropColumn`. Nothing else.

- [ ] **Step 7: Run the tests to verify they pass**

Run:
```bash
dotnet test Tests/ConPersistence.Tests -c Debug
dotnet test Tests/ConEngine.Tests -c Debug --filter "FullyQualifiedName~CriticalSubjectTests"
dotnet test Tests/ConWatch.Tests -c Debug --filter "FullyQualifiedName~CriticalFieldTests|FullyQualifiedName~CloneTests|FullyQualifiedName~ExportApiTests"
```
Expected: all PASS, including `MigrationsTests`.

- [ ] **Step 8: Commit**

```bash
git add Core Libraries/ConPersistence Tests
git commit -m "feat(config): Critical target flag through model, mappings, database and JSON (default false)"
```

---

### Task 2: Status and MCP carry `critical` and `explainedBy`

**Files:**
- Modify: `Programs/ConWatch/StatusSnapshotBuilder.cs` (`StatusTarget` + builder), `Programs/ConWatch/Mcp/ConWatchMcpTools.cs` (`[Description]` only)
- Test: `Tests/ConWatch.Tests/StatusShapeTests.cs`, `Tests/ConWatch.Tests/McpEndpointTests.cs`

**Interfaces:**
- Consumes: `Target.Critical` (Task 1); `TargetMonitor.ExplainedBy` (`Guid?`, existing).
- Produces: `StatusTarget(..., TargetScope Scope, bool Critical, Guid? ExplainedBy)`. JSON keys `critical` and `explainedBy` on every `targets[]` entry of `/api/v1/status`, the SignalR push, the dashboard's initial data and MCP `get_current_status`.

- [ ] **Step 1: Write the failing tests**

In `StatusShapeTests.Live_status_shape_is_pinned`, replace the pinned target keys (L42–44):

```csharp
        Assert.Equal(new[] { "address", "assumed", "avgRtt", "critical", "explainedBy", "id", "name",
                "operator", "pingCount", "probe", "sampleSize", "scope", "state", "successRate" },
            Keys(status.GetProperty("targets")[0]));
```

Add a fact to `StatusShapeTests`:

```csharp
    [Fact]
    public async Task Status_reports_which_targets_are_critical()
    {
        await ConfigApiTests.SeedHomeAsync(_client);
        var targets = (await _client.GetFromJsonAsync<List<ConPersistence.Config.TargetDto>>("/api/v1/targets"))!;
        var router = targets.Single(t => t.Name == "Router");
        (await _client.PutAsJsonAsync($"/api/v1/targets/{router.Id}", router with { Critical = true }))
            .EnsureSuccessStatusCode();

        await ConfigApiTests.Eventually(async () =>
        {
            var response = await _client.GetAsync("/api/v1/status");
            if (!response.IsSuccessStatusCode)
                return false;
            var status = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
            var rows = status.GetProperty("targets").EnumerateArray().ToList();
            return rows.Count == 3
                && rows.Single(t => t.GetProperty("name").GetString() == "Router").GetProperty("critical").GetBoolean()
                && !rows.Single(t => t.GetProperty("name").GetString() == "Google").GetProperty("critical").GetBoolean();
        });
    }
```

In `McpEndpointTests.Status_tool_reports_the_status_snapshot`, where the router target's keys are checked, add an assertion that the router element has a boolean `critical` property:

```csharp
        Assert.Equal(JsonValueKind.False, router.GetProperty("critical").ValueKind);
```

Use the test's own variable for the router element; if it compares a pinned key list, add `"critical"` and `"explainedBy"` there instead.

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test Tests/ConWatch.Tests -c Debug --filter "FullyQualifiedName~StatusShapeTests|FullyQualifiedName~McpEndpointTests"`
Expected: FAIL (key sets differ; `critical` not found).

- [ ] **Step 3: Implement**

In `StatusSnapshotBuilder.cs`:

```csharp
/// <param name="Operator">The target's operator/ISP tag, or null.</param>
/// <param name="Critical">Notify when this is down, even if the internet is fine (spec §4.7).</param>
/// <param name="ExplainedBy">The root cause (target id) explaining this target's
/// Unreachable state, or null.</param>
public sealed record StatusTarget(Guid Id, string Name, string Address, string? Probe, string State,
    bool Assumed, double SuccessRate, double AvgRtt, int SampleSize, long PingCount, string? Operator,
    TargetScope Scope, bool Critical, Guid? ExplainedBy);
```

and in `Build`, after `m.Target.Scope`:

```csharp
                m.Target.Scope,
                m.Target.Critical,
                m.ExplainedBy))
```

In `ConWatchMcpTools.GetCurrentStatus`'s `[Description]`, append: `" Each target carries critical (the user asked to be told when it is down, even if the internet is fine) and explainedBy (the id of the upstream target whose failure explains this one, or null)."`

- [ ] **Step 4: Run the tests**

Run: `dotnet test Tests/ConWatch.Tests -c Debug --filter "FullyQualifiedName~StatusShapeTests|FullyQualifiedName~McpEndpointTests|FullyQualifiedName~LocalScopeApiTests|FullyQualifiedName~SignalRTests"`
Expected: all PASS.

- [ ] **Step 5: Commit**

```bash
git add Programs/ConWatch/StatusSnapshotBuilder.cs Programs/ConWatch/Mcp/ConWatchMcpTools.cs Tests/ConWatch.Tests
git commit -m "feat(api): status and MCP report critical and explainedBy per target"
```

---

### Task 3: Config UI, setup wizard and Gateway adopt

**Files:**
- Modify: `Programs/ConWatch/Pages/Config/Targets.cshtml`, `Programs/ConWatch/Pages/Config/Targets.cshtml.cs`, `Programs/ConWatch/Pages/Config/Discoveries.cshtml.cs` (`AdoptUrl`), `Programs/ConWatch/SetupService.cs` (router creation, L52–57), `Programs/ConWatch/wwwroot/css/site.css`
- Test: `Tests/ConWatch.Tests/PageSmokeTests.cs`, `Tests/ConWatch.Tests/DiscoveriesPageTests.cs` (L150), `Tests/ConWatch.Tests/SetupApiTests.cs`

**Interfaces:**
- Consumes: `TargetDto.Critical` (Task 1).
- Produces:
  - Targets page query param `critical=true` (pre-checks the dialog checkbox)
  - `TargetsModel.PrefillCritical` (bool)
  - CSS class `cw-critical` (the small "critical" marker), which Task 6 reuses

- [ ] **Step 1: Write the failing tests**

In `PageSmokeTests.cs`, add:

```csharp
    [Fact]
    public async Task Targets_dialog_has_a_critical_checkbox_unchecked_by_default()
    {
        var html = await _client.GetStringAsync("/Config/Targets");
        Assert.Contains("Critical — notify when this is down, even if the internet is fine", html);
        Assert.Matches("<input[^>]*name=\"critical\"", html);
        Assert.DoesNotMatch("<input[^>]*name=\"critical\"[^>]*checked", html);
    }

    [Fact]
    public async Task Critical_prefill_checks_the_box()
    {
        var html = await _client.GetStringAsync("/Config/Targets?address=192.0.2.1&name=Router&critical=true");
        Assert.Matches("<input[^>]*name=\"critical\"[^>]*checked", html);
    }
```

Use the class's existing client/factory field names. If `PageSmokeTests` needs a seeded config to render `/Config/Targets`, follow `Targets_page_prefills_profile_and_operator_from_the_query` (L191–198).

In `DiscoveriesPageTests.cs` L150, the Gateway adopt URL expectation becomes:

```
/Config/Targets?address=192.0.2.1&name=Router&profile=Internal&scope=Verdict&critical=true&discoveryId=
```

The NetworkEdge and LocalDevice expectations (L151–152, L174, L188) stay unchanged: neither kind is pre-checked.

In `SetupApiTests.cs`, extend `Apply_builds_router_anchors_pool_and_settings` (after its target assertions):

```csharp
        Assert.True(targets.Single(t => t.Name == "Router").Critical);
        Assert.All(targets.Where(t => t.Name != "Router"), t => Assert.False(t.Critical));
```

and add a fact (use the file's existing helpers for posting the apply request and reading targets, with the `Router` helper at L52):

```csharp
    [Fact]
    public async Task Apply_does_not_mark_an_existing_router_target_critical()
    {
        // D6: existing installs are unchanged; the wizard reuses a target at the router's address untouched.
        (await _client.PostAsJsonAsync("/api/v1/targets", new
        {
            name = "My Router", address = "192.168.1.1", profile = "Internal",
        })).EnsureSuccessStatusCode();

        // … apply the wizard with Router (192.168.1.1) exactly as Apply_builds_router_anchors_pool_and_settings does …

        var targets = (await _client.GetFromJsonAsync<List<ConPersistence.Config.TargetDto>>("/api/v1/targets"))!;
        Assert.False(targets.Single(t => t.Address == "192.168.1.1").Critical);
    }
```

The elided line is the apply call copied from the neighbouring test, not a placeholder for new logic.

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test Tests/ConWatch.Tests -c Debug --filter "FullyQualifiedName~PageSmokeTests|FullyQualifiedName~DiscoveriesPageTests|FullyQualifiedName~SetupApiTests"`
Expected: the new and changed assertions FAIL.

- [ ] **Step 3: Targets page**

`Targets.cshtml.cs`, next to the other prefills:

```csharp
    /// <summary>Adopting a Gateway discovery pre-checks Critical (spec §4.7).</summary>
    public bool PrefillCritical =>
        string.Equals(Request.Query["critical"], "true", StringComparison.OrdinalIgnoreCase);
```

`Targets.cshtml`, directly after the Scope `</label>` (after L129):

```html
            <label>
                <input type="checkbox" name="critical" checked="@Model.PrefillCritical" />
                Critical — notify when this is down, even if the internet is fine
            </label>
```

Razor renders `checked="checked"` when true and omits the attribute when false.

In the table, the Name cell (L73, `<td>@t.Name</td>`) becomes:

```cshtml
            <td>@t.Name@if (t.Critical) {<small class="cw-critical" title="Notified when this is down, even if the internet is fine">critical</small>}</td>
```

In the inline script:
- `buildDto` (L297–306): add `critical: form.elements.critical.checked,` after `enabled`.
- `fillForm` (L307–317): add `form.elements.critical.checked = dto.critical ?? false;` after the `enabled` line.

`site.css`, near the `.cw-notes` rules:

```css
.cw-critical { margin-left: 0.45rem; padding: 0 0.35rem; border: 1px solid var(--border-strong);
    border-radius: 4px; color: var(--muted); font-size: 0.7rem; font-weight: 600;
    text-transform: uppercase; letter-spacing: 0.03em; vertical-align: 1px; }
```

- [ ] **Step 4: Gateway adopt and the wizard**

`Discoveries.cshtml.cs` `AdoptUrl`, after `.Add("scope", scope);`:

```csharp
        // A router is the device you most want to hear about: adopting the
        // gateway pre-checks Critical (spec §4.7). The admin can uncheck it.
        if (d.Kind == DiscoveryKind.Gateway)
            query = query.Add("critical", "true");
```

`SetupService.cs`, the router `TargetDto` (L52–57) gains `Critical = true,`. Add a one-line comment: "A router the wizard creates is critical (spec §4.7); a reused target is left as it is (D6)."

- [ ] **Step 5: Run the tests**

Run: `dotnet test Tests/ConWatch.Tests -c Debug --filter "FullyQualifiedName~PageSmokeTests|FullyQualifiedName~DiscoveriesPageTests|FullyQualifiedName~SetupApiTests|FullyQualifiedName~DiscoveryApiTests"`
Expected: all PASS.

- [ ] **Step 6: Commit**

```bash
git add Programs/ConWatch Tests/ConWatch.Tests
git commit -m "feat(config): Critical checkbox; the wizard's router and adopted gateways start critical"
```

---

### Task 4: Notifiers — critical events pass the webhook filters; MQTT attribute

**Files:**
- Modify: `Libraries/ConNotify/WebhookFilter.cs`, `Libraries/ConNotify/WebhookNotifier.cs`, `Libraries/ConNotify/Builders.cs` (`MqttTopics`, `HaDiscovery`), `Libraries/ConNotify/MqttNotifier.cs` (`RefreshAllAsync`)
- Test: `Tests/ConNotify.Tests/WebhookFilterTests.cs`, `Tests/ConNotify.Tests/BuildersTests.cs`, `Tests/ConWatch.Tests/NotifierIntegrationTests.cs`

**Interfaces:**
- Consumes: `WatcherEngine.IsCriticalSubject` (Task 1).
- Produces:
  - `WebhookFilter.ShouldDeliver(TargetTransition t, bool verdictOnly, bool skipExplained, bool critical = false)`
  - `WebhookFilter.IsCriticalEvent(TargetTransition t)`
  - `MqttTopics.TargetAttributes(string slug)` → `{Base}/target/{slug}/attributes`
  - `HaDiscovery.TargetConfig(...)` gains `json_attributes_topic`
  - `HaDiscovery.TargetAttributes(bool critical)` → `{"critical":true|false}`

- [ ] **Step 1: Write the failing filter tests**

Append to `WebhookFilterTests.cs`:

```csharp
    [Fact]
    public void A_critical_targets_failure_and_recovery_pass_verdict_only_and_skip_explained()
    {
        Assert.True(WebhookFilter.ShouldDeliver(T(HostState.Responding, HostState.Down), true, true, critical: true));
        Assert.True(WebhookFilter.ShouldDeliver(T(HostState.Faltering, HostState.Unreachable, trigger: TransitionTrigger.ParentDown), true, true, critical: true));
        Assert.True(WebhookFilter.ShouldDeliver(T(HostState.Down, HostState.Recovering), true, true, critical: true));
        Assert.True(WebhookFilter.ShouldDeliver(T(HostState.Unreachable, HostState.Responding), true, true, critical: true));
    }

    [Fact]
    public void A_released_critical_targets_recovery_is_delivered_once_and_the_release_itself_is_not()
    {
        var cause = Guid.NewGuid();
        var release = T(HostState.Unreachable, HostState.Starting, trigger: TransitionTrigger.ParentRecovered);
        var back = T(HostState.Starting, HostState.Responding) with
        {
            Reason = new TransitionReason { Trigger = TransitionTrigger.ThresholdCrossed, AncestorId = cause },
        };
        Assert.False(WebhookFilter.ShouldDeliver(release, true, true, critical: true));
        Assert.True(WebhookFilter.ShouldDeliver(back, true, true, critical: true));
    }

    [Fact]
    public void Reclassifying_a_critical_target_between_down_and_unreachable_is_not_a_new_event()
    {
        Assert.False(WebhookFilter.IsCriticalEvent(T(HostState.Down, HostState.Unreachable, trigger: TransitionTrigger.ParentDown)));
        Assert.False(WebhookFilter.IsCriticalEvent(T(HostState.Unreachable, HostState.Down)));
        Assert.False(WebhookFilter.ShouldDeliver(T(HostState.Down, HostState.Unreachable, trigger: TransitionTrigger.ParentDown), true, true, critical: true));
    }

    [Fact]
    public void Other_transitions_of_a_critical_target_follow_the_normal_filters()
    {
        Assert.False(WebhookFilter.ShouldDeliver(T(HostState.Responding, HostState.Faltering), true, true, critical: true));
        Assert.True(WebhookFilter.ShouldDeliver(T(HostState.Responding, HostState.Faltering), false, true, critical: true));
    }

    [Fact]
    public void A_non_critical_target_is_unchanged_by_the_new_parameter()
    {
        Assert.False(WebhookFilter.ShouldDeliver(T(HostState.Responding, HostState.Down), true, true));
        Assert.True(WebhookFilter.ShouldDeliver(T(HostState.Responding, HostState.Down), false, true));
    }
```

- [ ] **Step 2: Write the failing MQTT builder tests**

In `BuildersTests.cs` `HaDiscoveryTests.Target_discovery_document_shape`, add:

```csharp
        Assert.Equal("conwatch/target/google_dns/attributes", json.GetProperty("json_attributes_topic").GetString());
```

and add:

```csharp
    [Fact]
    public void Target_attributes_carry_critical()
    {
        Assert.Equal("conwatch/target/router/attributes", Topics.TargetAttributes("router"));
        Assert.Equal("{\"critical\":true}", HaDiscovery.TargetAttributes(critical: true));
        Assert.Equal("{\"critical\":false}", HaDiscovery.TargetAttributes(critical: false));
    }
```

- [ ] **Step 3: Write the failing integration tests**

Add to `NotifierIntegrationTests.cs` (same pattern as `VerdictOnly_suppresses_target_transitions` and `Local_target_transitions_are_not_delivered_to_a_webhook`):

```csharp
    [Fact]
    public async Task A_critical_targets_failure_reaches_a_verdict_only_webhook()
    {
        using var hook = new Hook();
        using var factory = new ConWatchFactory();
        var client = factory.CreateClient();
        await client.PutAsJsonAsync("/api/v1/settings", new Dictionary<string, string?>
        {
            ["Webhook.Enabled"] = "true",
            ["Webhook.VerdictOnly"] = "true",
            ["Webhook.Urls"] = hook.Url("/hook"),
        });
        await ConfigApiTests.SeedHomeAsync(client);
        // A critical LAN device (Local scope, never a verdict subject) that is down from the start.
        factory.Pings.SetUp(System.Net.IPAddress.Parse("203.0.113.9"), false);
        (await client.PostAsJsonAsync("/api/v1/targets", new
        {
            name = "CriticalNas", address = "203.0.113.9", profile = "Fast", scope = "Local", critical = true,
        })).EnsureSuccessStatusCode();

        await ConfigApiTests.Eventually(() => Task.FromResult(hook.Received.Any(r =>
        {
            var body = JsonSerializer.Deserialize<JsonElement>(r.Body);
            return body.GetProperty("subjectName").GetString() == "CriticalNas"
                && body.GetProperty("to").GetString() == "Down";
        })));
        // Only its failure got through verdict-only: no Starting/Initializing noise for it.
        Assert.All(hook.Received.Where(r => r.Body.Contains("CriticalNas")), r =>
            Assert.Equal("Down", JsonSerializer.Deserialize<JsonElement>(r.Body).GetProperty("to").GetString()));
    }
```

Adjust the JSON property names (`subjectName`, `to`) to `TransitionDto`'s Web-cased names if they differ. Check `BuildersTests.WebhookPayloadTests`.

- [ ] **Step 4: Run them to verify they fail**

Run:
```bash
dotnet test Tests/ConNotify.Tests -c Debug
dotnet test Tests/ConWatch.Tests -c Debug --filter "FullyQualifiedName~NotifierIntegrationTests"
```
Expected: build FAILS on the `critical:` argument, `IsCriticalEvent`, `TargetAttributes`.

- [ ] **Step 5: Implement the filter**

In `WebhookFilter.cs`, add `critical` and the event rule. Keep the existing body:

```csharp
    /// <param name="critical">The subject is a target marked critical: its own
    /// failure and recovery are always delivered, past "verdict only" and
    /// "skip explained" (spec 2026-10-09 §4.7: critical means tell me).</param>
    public static bool ShouldDeliver(TargetTransition t, bool verdictOnly, bool skipExplained,
        bool critical = false)
    {
        if (critical && IsCriticalEvent(t))
            return true;
        if (verdictOnly && t.Kind != TransitionSubjectKind.Verdict)
            return false;
        … existing skipExplained block unchanged …
        return true;
    }

    /// <summary>A target's own failure (entering Down/Unreachable from neither)
    /// or recovery (leaving them for an answering state, or a released
    /// target's first answer naming its former cause). Down↔Unreachable is a
    /// reclassification, not a new event.</summary>
    public static bool IsCriticalEvent(TargetTransition t)
    {
        if (t.Kind != TransitionSubjectKind.Target)
            return false;
        static bool Failed(HostState s) => s is HostState.Down or HostState.Unreachable;
        static bool Answering(HostState s) =>
            s is HostState.Responding or HostState.Recovering or HostState.Faltering;
        if (Failed(t.To))
            return !Failed(t.From);
        if (Failed(t.From))
            return Answering(t.To);
        return t.From == HostState.Starting && t.Reason.AncestorId is not null && Answering(t.To);
    }
```

- [ ] **Step 6: Implement the webhook notifier**

In `WebhookNotifier.cs`:
- The channel carries `(TargetTransition Transition, bool Critical)`.
- `Attach` decides criticality when the transition arrives, because the engine that knew it may be gone by delivery:

```csharp
    private readonly Channel<(TargetTransition Transition, bool Critical)> _queue =
        Channel.CreateBounded<(TargetTransition, bool)>(
            new BoundedChannelOptions(500) { FullMode = BoundedChannelFullMode.DropOldest });

    public void Attach(WatcherEngine engine)
    {
        _subscription?.Dispose();
        _subscription = engine.TransitionSource.Subscribe(
            t =>
            {
                // Local (LAN-device) targets never notify, unless marked critical
                // (spec §4.7); verdict/pool subjects pass.
                var critical = engine.IsCriticalSubject(t.TargetId);
                if (critical || !engine.IsLocalSubject(t.TargetId))
                    _queue.Writer.TryWrite((t, critical));
            },
            ex => log.LogError(ex, "Transition stream error in webhook notifier"));
    }
```

`ExecuteAsync` iterates `(transition, critical)` and calls `DeliverAsync(transition, critical, stoppingToken)`. `DeliverAsync` passes `critical` to `WebhookFilter.ShouldDeliver(transition, verdictOnly, skipExplained, critical)`. The log line in the catch keeps using `transition.TargetId`.

- [ ] **Step 7: Implement MQTT target attributes**

`Builders.cs`:
- `MqttTopics`: add `public string TargetAttributes(string slug) => $"{Base}/target/{slug}/attributes";` after `TargetState`.
- `HaDiscovery.TargetConfig`: add `json_attributes_topic = topics.TargetAttributes(slug),` after `state_topic`.
- Add:

```csharp
    /// <summary>A target sensor's attributes: whether it is critical (spec §4.7).</summary>
    public static string TargetAttributes(bool critical) =>
        JsonSerializer.Serialize(new { critical }, Options);
```

`MqttNotifier.RefreshAllAsync`:
- In the stale-slug loop, also clear `_topics.TargetAttributes(stale)` (empty, retained).
- In the per-monitor loop, after publishing `TargetState`:

```csharp
            await PublishAsync(client, _topics.TargetAttributes(slug),
                HaDiscovery.TargetAttributes(monitor.Target.Critical), retain: true, ct);
```

Local targets stay excluded from MQTT (decision 2). Changing `Critical` rebuilds the engine, and the new engine's attach triggers this refresh, so the attribute follows edits.

- [ ] **Step 8: Run the tests**

Run:
```bash
dotnet test Tests/ConNotify.Tests -c Debug
dotnet test Tests/ConWatch.Tests -c Debug --filter "FullyQualifiedName~NotifierIntegrationTests"
```
Expected: all PASS. The Docker-broker MQTT tests are `[SkippableFact]` and may skip.

- [ ] **Step 9: Commit**

```bash
git add Libraries/ConNotify Tests/ConNotify.Tests Tests/ConWatch.Tests/NotifierIntegrationTests.cs
git commit -m "feat(notify): a critical target's failure and recovery pass every webhook filter; MQTT attribute critical"
```

---

### Task 5: Tray — critical toasts, announced once

**Files:**
- Modify: `Libraries/ConInfrastructure/Tray/TrayStatus.cs`, `Programs/ConWatchTray/Program.cs`
- Create: `Libraries/ConInfrastructure/Tray/CriticalWatch.cs`
- Test: `Tests/ConWatch.Tests/TrayStatusTests.cs`, `Tests/ConWatch.Tests/CriticalWatchTests.cs` (new)

**Interfaces:**
- Consumes: `critical`, `id`, `name`, `state` on status `targets[]` (Task 2); `warnings[]` entries `{kind:"HopBypassed", targetId}` (Plan B).
- Produces (namespace `ConInfrastructure.Tray`):
  - `record TrayCritical(string Id, string Name, string State, bool Bypassed)` with `bool IsDown`
  - `TrayInfo.Critical` (`IReadOnlyList<TrayCritical>`, init property, default empty; positional parameters unchanged)
  - `record TrayToast(string Title, string Body)`
  - `class CriticalWatch` with `IReadOnlyList<TrayToast> Update(TrayInfo next, bool announce)`
  - `TrayStatus.Tooltip` lists critical targets that are down

- [ ] **Step 1: Write the failing parser and tooltip tests**

Append to `TrayStatusTests.cs`:

```csharp
    private const string RouterId = "11111111-1111-1111-1111-111111111111";

    private static string Status(string routerState, bool critical = true, string verdict = "Healthy",
        string warnings = "[]") =>
        $$"""
        {"enabled":true,"verdict":{"state":"{{verdict}}","detail":"d","operator":null},
         "targets":[{"id":"{{RouterId}}","name":"Router","state":"{{routerState}}","scope":"Verdict","critical":{{(critical ? "true" : "false")}}},
                    {"id":"22222222-2222-2222-2222-222222222222","name":"Google","state":"Responding","scope":"Verdict","critical":false}],
         "pools":[],"warnings":{{warnings}}}
        """;

    [Fact]
    public void Critical_targets_are_parsed_with_whether_traffic_passes_them()
    {
        var info = TrayStatus.FromStatusJson(Status("Down", warnings:
            $$"""[{"key":"hop-bypassed:{{RouterId}}","kind":"HopBypassed","targetId":"{{RouterId}}","targetName":"Router","message":"m","since":"x","acknowledged":true}]"""));
        var router = Assert.Single(info.Critical);
        Assert.Equal("Router", router.Name);
        Assert.True(router.IsDown);
        Assert.True(router.Bypassed); // acknowledged notes still say traffic passes it
        Assert.Equal(TrayState.Healthy, info.State); // critical never changes the state
    }

    [Fact]
    public void Non_critical_targets_and_old_servers_yield_no_critical_entries()
    {
        Assert.Empty(TrayStatus.FromStatusJson(Status("Down", critical: false)).Critical);
        Assert.Empty(TrayStatus.FromStatusJson(Healthy + "}").Critical); // no targets[] critical keys
        Assert.Empty(TrayStatus.FromStatusJson("not json").Critical);
    }

    [Fact]
    public void Tooltip_lists_down_critical_targets_before_notes()
    {
        var info = new TrayInfo(TrayState.Healthy, "d", ["Router isn't answering (traffic passes it)"])
        {
            Critical = [new TrayCritical(RouterId, "Router", "Down", Bypassed: true)],
        };
        Assert.Equal("Internet: healthy\nd\n⚠ Router is Down\n🔧 Router isn't answering (traffic passes it)",
            TrayStatus.Tooltip(info));
    }
```

- [ ] **Step 2: Write the failing announcement tests**

Create `Tests/ConWatch.Tests/CriticalWatchTests.cs`:

```csharp
using ConInfrastructure.Tray;

namespace ConWatch.Tests;

public class CriticalWatchTests
{
    private static TrayInfo Info(TrayState state, params TrayCritical[] critical) =>
        new(state, null, []) { Critical = critical };

    private static TrayCritical Router(string state, bool bypassed = false) =>
        new("r", "Router", state, bypassed);

    [Fact]
    public void A_critical_failure_toasts_once_and_its_recovery_once()
    {
        var watch = new CriticalWatch();
        Assert.Empty(watch.Update(Info(TrayState.Healthy, Router("Responding")), announce: true));

        var down = Assert.Single(watch.Update(Info(TrayState.Healthy, Router("Down")), announce: true));
        Assert.Equal("Router is not answering", down.Title);
        Assert.Equal("Your internet is still up.", down.Body);
        Assert.Empty(watch.Update(Info(TrayState.Healthy, Router("Down")), announce: true));
        Assert.Empty(watch.Update(Info(TrayState.Healthy, Router("Unreachable")), announce: true)); // reclassified

        var back = Assert.Single(watch.Update(Info(TrayState.Healthy, Router("Responding")), announce: true));
        Assert.Equal("Router is answering again", back.Title);
    }

    [Fact]
    public void A_bypassed_critical_hop_says_traffic_is_still_passing_it()
    {
        var watch = new CriticalWatch();
        var toast = Assert.Single(watch.Update(Info(TrayState.Healthy, Router("Down", bypassed: true)), announce: true));
        Assert.Equal("Router is not answering",
            toast.Title);
        Assert.Equal("Traffic is still passing it, so your internet is up — see ConnectionWatcher notes.", toast.Body);
    }

    [Fact]
    public void A_released_target_recovers_through_starting_with_one_toast()
    {
        var watch = new CriticalWatch();
        watch.Update(Info(TrayState.Healthy, Router("Down")), announce: true);
        Assert.Empty(watch.Update(Info(TrayState.Healthy, Router("Starting")), announce: true));
        Assert.Single(watch.Update(Info(TrayState.Healthy, Router("Responding")), announce: true));
    }

    [Fact]
    public void An_unreachable_service_is_no_news_either_way()
    {
        var watch = new CriticalWatch();
        watch.Update(Info(TrayState.Healthy, Router("Down")), announce: true);
        Assert.Empty(watch.Update(new TrayInfo(TrayState.Unreachable, null, []), announce: true));
        Assert.Empty(watch.Update(Info(TrayState.Healthy, Router("Down")), announce: true)); // still announced
    }

    [Fact]
    public void A_target_deleted_or_unmarked_while_down_is_dropped_silently()
    {
        var watch = new CriticalWatch();
        watch.Update(Info(TrayState.Healthy, Router("Down")), announce: true);
        Assert.Empty(watch.Update(Info(TrayState.Healthy), announce: true));
        Assert.Empty(watch.Update(Info(TrayState.Healthy, Router("Responding")), announce: true));
    }

    [Fact]
    public void While_the_internet_is_down_the_internet_toast_speaks_and_the_device_waits()
    {
        var watch = new CriticalWatch();
        Assert.Empty(watch.Update(Info(TrayState.Down, Router("Down")), announce: true));
        // Internet back, router still down: now it is news.
        Assert.Single(watch.Update(Info(TrayState.Healthy, Router("Down")), announce: true));
    }

    [Fact]
    public void A_recovery_never_toasts_unless_its_failure_did()
    {
        var watch = new CriticalWatch();
        watch.Update(Info(TrayState.Down, Router("Down")), announce: true); // suppressed
        Assert.Empty(watch.Update(Info(TrayState.Healthy, Router("Responding")), announce: true));
    }

    [Fact]
    public void A_silent_update_records_state_without_toasting()
    {
        // First poll after the tray starts: no toasts, but no later "again" for what was already down.
        var watch = new CriticalWatch();
        Assert.Empty(watch.Update(Info(TrayState.Healthy, Router("Down")), announce: false));
        Assert.Empty(watch.Update(Info(TrayState.Healthy, Router("Down")), announce: true));
        Assert.Single(watch.Update(Info(TrayState.Healthy, Router("Responding")), announce: true));
    }

    [Fact]
    public void While_degraded_the_body_does_not_claim_the_internet_is_fine()
    {
        var watch = new CriticalWatch();
        var toast = Assert.Single(watch.Update(Info(TrayState.Degraded, Router("Down")), announce: true));
        Assert.Equal("Marked critical in ConnectionWatcher.", toast.Body);
    }
}
```

- [ ] **Step 3: Run them to verify they fail**

Run: `dotnet test Tests/ConWatch.Tests -c Debug --filter "FullyQualifiedName~TrayStatusTests|FullyQualifiedName~CriticalWatchTests"`
Expected: build FAILS (`TrayCritical`, `CriticalWatch` missing).

- [ ] **Step 4: Implement the parser and tooltip**

In `TrayStatus.cs`:

```csharp
/// <summary>A target marked critical, as the status reports it.</summary>
/// <param name="Bypassed">A HopBypassed note exists for it: traffic is passing it.</param>
public record TrayCritical(string Id, string Name, string State, bool Bypassed)
{
    public bool IsDown => State is "Down" or "Unreachable";
}

/// <summary>State, the human-readable evidence line, and the short labels of
/// unacknowledged admin notes (never part of the state: spec 2026-10-09 D3).</summary>
public record TrayInfo(TrayState State, string? Detail, IReadOnlyList<string> Notes)
{
    /// <summary>Targets marked critical (spec §4.7), any state.</summary>
    public IReadOnlyList<TrayCritical> Critical { get; init; } = [];
}
```

In `FromStatusJson`, compute `critical` once, right after `notes`, and return every successful `TrayInfo` with `{ Critical = critical }` (the `catch` stays empty):

```csharp
            var bypassed = status.TryGetProperty("warnings", out var ws) && ws.ValueKind == JsonValueKind.Array
                ? ws.EnumerateArray()
                    .Where(w => w.ValueKind == JsonValueKind.Object
                                && w.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String
                                && k.GetString() == "HopBypassed"
                                && w.TryGetProperty("targetId", out var id) && id.ValueKind == JsonValueKind.String)
                    .Select(w => w.GetProperty("targetId").GetString()!)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>();
            var critical = status.TryGetProperty("targets", out var ts) && ts.ValueKind == JsonValueKind.Array
                ? ts.EnumerateArray()
                    .Where(t => t.TryGetProperty("critical", out var c) && c.ValueKind == JsonValueKind.True)
                    .Select(t =>
                    {
                        var id = t.GetProperty("id").GetString() ?? "";
                        return new TrayCritical(id, t.GetProperty("name").GetString() ?? "",
                            t.GetProperty("state").GetString() ?? "", bypassed.Contains(id));
                    })
                    .ToList()
                : new List<TrayCritical>();
```

`Tooltip`: after the `Detail` line and before the notes loop:

```csharp
        foreach (var c in info.Critical.Where(c => c.IsDown))
            text += $"\n⚠ {c.Name} is {c.State}";
```

- [ ] **Step 5: Implement `CriticalWatch`**

Create `Libraries/ConInfrastructure/Tray/CriticalWatch.cs`:

```csharp
namespace ConInfrastructure.Tray;

/// <summary>A toast the tray should show.</summary>
public record TrayToast(string Title, string Body);

/// <summary>
/// Decides the critical-target toasts (spec 2026-10-09 §4.7) from successive
/// status polls: each failure is announced once and its recovery once, by
/// remembering what was announced rather than comparing to the last poll
/// (a released target recovers through Starting; a failed poll is no news).
/// Not thread-safe: the tray calls it from its UI timer only.
/// </summary>
public class CriticalWatch
{
    private readonly HashSet<string> _announced = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="announce">False for the first poll after the tray starts:
    /// record what is already down without toasting it.</param>
    public IReadOnlyList<TrayToast> Update(TrayInfo next, bool announce)
    {
        if (next.State == TrayState.Unreachable)
            return []; // no data: neither a failure nor a recovery

        var toasts = new List<TrayToast>();
        var present = next.Critical.Select(c => c.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _announced.RemoveWhere(id => !present.Contains(id)); // deleted or un-marked: drop silently

        foreach (var c in next.Critical)
        {
            if (c.IsDown && !_announced.Contains(c.Id))
            {
                // While the internet itself is down its own toast names the
                // blamed hop; a second toast for the device would cry wolf (D7).
                if (next.State == TrayState.Down && announce)
                    continue;
                _announced.Add(c.Id);
                if (announce)
                    toasts.Add(new TrayToast($"{c.Name} is not answering", FailureBody(c, next.State)));
            }
            else if (c.State is "Responding" or "Recovering" or "Faltering" && _announced.Remove(c.Id) && announce)
            {
                toasts.Add(new TrayToast($"{c.Name} is answering again", "Marked critical in ConnectionWatcher."));
            }
        }
        return toasts;
    }

    private static string FailureBody(TrayCritical c, TrayState internet) =>
        c.Bypassed ? "Traffic is still passing it, so your internet is up — see ConnectionWatcher notes."
        : internet == TrayState.Healthy ? "Your internet is still up."
        : "Marked critical in ConnectionWatcher.";
}
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test Tests/ConWatch.Tests -c Debug --filter "FullyQualifiedName~TrayStatusTests|FullyQualifiedName~CriticalWatchTests"`
Expected: all PASS.

- [ ] **Step 7: Wire it into the tray**

In `Programs/ConWatchTray/Program.cs`:
- Add the field `private readonly CriticalWatch _critical = new();`.
- In `RefreshAsync`, make the tooltip refresh also when the critical states change. Track the last list in a field `private IReadOnlyList<TrayCritical> _criticalShown = [];` and include `!next.Critical.SequenceEqual(_criticalShown)` in the change condition, updating `_criticalShown` inside the block.
- After that block and before `_firstPoll = false;`:

```csharp
        // Critical devices (spec §4.7): their own failure and recovery toast,
        // even while the internet is fine. Notes never toast (D3).
        var critical = _critical.Update(next, announce: !_firstPoll && _settings.NotificationsEnabled);
        foreach (var toast in critical)
            Balloon(toast.Title, Truncate(toast.Body, 250),
                toast.Title.EndsWith("again", StringComparison.Ordinal) ? ToolTipIcon.Info : ToolTipIcon.Warning);
```

`Balloon` takes `(title, text, icon)` and an optional open path. Pass `"/#notes"` as the open path when the body mentions the notes. Match `Balloon`'s actual signature (`Program.cs`, around L268).

Run: `dotnet build Programs/ConWatchTray -c Debug`
Expected: 0 warnings, 0 errors.

- [ ] **Step 8: Commit**

```bash
git add Libraries/ConInfrastructure/Tray Programs/ConWatchTray Tests/ConWatch.Tests/TrayStatusTests.cs Tests/ConWatch.Tests/CriticalWatchTests.cs
git commit -m "feat(tray): toast a critical device's failure and recovery once each, even when the internet is fine"
```

---

### Task 6: Dashboard — critical marker

**Files:**
- Modify: `Programs/ConWatch/ClientApp/src/lib/types.ts`, `Programs/ConWatch/ClientApp/src/components/TargetRow.tsx`
- Test: `Programs/ConWatch/ClientApp/src/components/TargetRow.test.tsx`

**Interfaces:**
- Consumes: `critical` and `explainedBy` on status targets (Task 2); CSS class `cw-critical` (Task 3, `site.css`, loaded on every page).
- Produces: `TargetStatus.critical?: boolean`, `TargetStatus.explainedBy?: string | null`.

- [ ] **Step 1: Write the failing tests**

Append to `TargetRow.test.tsx` inside its `describe`:

```tsx
  it("marks a critical target and always shows its state chip", () => {
    const html = renderToStaticMarkup(
      <TargetRow target={t({ critical: true })} show={{ stateChip: false, rtt: false }} />);
    expect(html).toContain("cw-critical");
    expect(html).toContain(">critical<");
    expect(html).toContain("statechip"); // the LAN section hides chips, but not for critical
  });
  it("leaves non-critical rows unmarked", () => {
    expect(renderToStaticMarkup(<TargetRow target={t()} />)).not.toContain("cw-critical");
  });
```

The file's helper `t(over)` defaults `state: "Down"`, so the chip has a colour.

- [ ] **Step 2: Run them to verify they fail**

Run: `cd Programs/ConWatch/ClientApp && npx vitest run src/components/TargetRow.test.tsx`
Expected: FAIL (no `cw-critical`; the type error on `critical` shows in `npm run build`).

- [ ] **Step 3: Implement**

`types.ts`, in `TargetStatus` after `scope`:

```ts
  /** Notify when this is down, even if the internet is fine (spec §4.7). */
  critical?: boolean;
  /** Id of the upstream target whose failure explains this one's Unreachable state. */
  explainedBy?: string | null;
```

`TargetRow.tsx`:
- Change the chip line to `const chip = (stateChip || target.critical) ? CHIP_COLOR[target.state] : undefined;` with the comment `// a critical row always shows its state (spec §4.7: prominently)`.
- In the name span, after the assumed suffix:

```tsx
        {target.critical && <span className="cw-critical"
          title="Notified when this is down, even if the internet is fine">critical</span>}
```

- [ ] **Step 4: Run the tests and the build**

Run: `cd Programs/ConWatch/ClientApp && npx vitest run && npm run build`
Expected: all PASS; the build succeeds. Build output under `wwwroot/js` is untracked; don't commit it.

- [ ] **Step 5: Commit**

```bash
git add Programs/ConWatch/ClientApp/src
git commit -m "feat(ui): critical targets are marked and always show their state"
```

---

### Task 7: Verification

- [ ] **Step 1: All suites**

Run each, one at a time:
```bash
dotnet test Tests/ConEngine.Tests -c Debug
dotnet test Tests/ConPersistence.Tests -c Debug
dotnet test Tests/ConNotify.Tests -c Debug
dotnet test Tests/ConConfig.Tests -c Debug
dotnet test Tests/ConWatch.Tests -c Debug
dotnet test Tests/ConWatchCli.Tests -c Debug
```
and `cd Programs/ConWatch/ClientApp && npx vitest run`.
Expected: all green (rerun `ConWatch.Tests` once for the known flakes before calling a failure real).

- [ ] **Step 2: The verdict is untouched**

Run: `grep -rn "\.Critical\b" Core/ConEngine`
Expected: only `TargetGraph.FromOptions` (the mapping) and `WatcherEngine.IsCriticalSubject`. No coordinator reads it.

- [ ] **Step 3: Browser check**

Run the worktree build locally, with a router target at an unreachable test address (`192.0.2.1`) and real anchors under it:
1. Mark the router critical in the Targets dialog. The checkbox shows the spec label, and the table shows the "critical" marker.
2. The dashboard row shows the marker and its Down chip, and the verdict stays Up.
3. `/api/v1/status` shows `"critical":true` for the router.
4. The Discoveries page's Gateway "Adopt" link carries `critical=true`, and the dialog it opens has the box checked.

Take a screenshot of the dialog and the dashboard row.

- [ ] **Step 4: Tray (Mark, manual)**

Against the same server, with notifications on and a critical router whose probe fails while the anchors answer:
- One toast says "Router is not answering", with "Traffic is still passing it…".
- The tooltip shows "⚠ Router is Down".
- No second toast arrives on later polls.
- When the probe answers again, there is one "Router is answering again" toast.
- Restarting the tray while the router is down produces no toast.

- [ ] **Step 5: Hand back**

Report the branch, commit range and test results. Merging is Mark's decision. Never push.
