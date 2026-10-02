# Local Scope Backend Implementation Plan (Plan 1A of 4)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Targets gain a `Scope` (`Verdict` | `Local`) and a built-in `Local` profile; Local targets are probed and recorded but never touch the verdict, suspicion, webhook/MQTT notifications or the ISP report, and the status/metrics/MCP surfaces report their scope.

**Architecture:** One new enum (`TargetScope`) flows through the four target shapes (`TargetRow`, `TargetDto`, `TargetConfig`, engine `Target`) via the single mapping file, with a string-stored EF column defaulting to `Verdict`. Validation rules live in `WatcherOptionsValidator`. Consumers that must ignore Local targets look the scope up from the engine graph (`WatcherEngine.IsLocalSubject`) or the target row, because transitions carry only a target id.

**Tech Stack:** .NET 10, EF Core + SQLite, xunit 2.9.3, ASP.NET minimal APIs, System.Text.Json.

**Spec:** `docs/superpowers/specs/2026-10-02-discoveries-and-local-scope-design.md` (§3 and the Local-targets parts of §4/§6).

**Series:** 1A (this plan: backend) → 1B (UI: target dialog scope select, "How's my LAN" dashboard section, History scope filter, clone "Include local devices", server-rendered fallback) → 2 (Discoveries inbox, endpoints, Config page, router/edge search, MCP `list_discoveries`) → 3 (LAN scan, tray scouting, host-network docs). 1B/2/3 are written after 1A lands so they are checked against the real code. After 1A, Local targets can be created through the REST API only; until 1B they appear in the dashboard tree like any root target.

## Global Constraints

- File-scoped namespaces; `Nullable` and `ImplicitUsings` are enabled; no `TreatWarningsAsErrors`.
- New tests use `example.com` hostnames and RFC 5737 addresses (`203.0.113.x`, `198.51.100.x`).
- Tests wait on conditions (`Eventually`), never sleep-then-assert (CI runners have 2 cores).
- A new `TargetScope` JSON value must serialize as a **string** (`"Local"`), via `[JsonConverter(typeof(JsonStringEnumConverter<TargetScope>))]` on the enum — there is no global enum converter.
- A new target column is written in `ConfigMapping.cs` (the four mappings) and nowhere else.
- Migration command: `dotnet ef migrations add AddTargetScope --project Libraries/ConPersistence` (project-only; `--startup-project` fails). Run `dotnet tool restore` first.
- Test commands (per project; never run the whole solution on Linux): `dotnet test Tests/ConEngine.Tests -c Debug`, `Tests/ConPersistence.Tests`, `Tests/ConWatch.Tests`, `Tests/ConNotify.Tests`. Filter: `--filter "FullyQualifiedName~<ClassName>"`.
- Commit after every task. **Never push** (project rule). Commit messages carry no attribution lines.
- Work on a feature branch (e.g. `local-scope-backend`), not directly on `master`.

## Review Focus

Failure modes the spec implies that no single task's main tests would catch; each has a test in the owning task:

1. A snapshot/clone from an **older source with no `scope` key** must import as `Verdict`, not fail (Task 1).
2. A `scope` value the server doesn't know (`"Bogus"`) must be a 400, not a 500 or a silently-`Verdict` target (Task 1).
3. Editing an **existing target that has children** (or pool membership) to `Local` must be rejected with a clear message, not orphan the tree (Task 3).
4. A target **switched from Verdict to Local** keeps its old transitions, rollups and glitches in the database; the ISP report must exclude them anyway (Task 8).
5. A Local target's transitions must not be queued behind a verdict transition: delivery order is FIFO, so a leak would show up as a webhook naming the Local target (Task 5).

## File Structure

| File | Responsibility | Task |
|---|---|---|
| `Core/ConCore/Targets/TargetScope.cs` (new) | The enum | 1 |
| `Core/ConCore/Targets/Target.cs`, `Core/ConEngine/Configuration/WatcherOptions.cs`, `Core/ConEngine/Topology/TargetGraph.cs` | Carry scope into the engine | 1 |
| `Libraries/ConPersistence/Entities/ConfigRows.cs`, `Config/ConfigDtos.cs`, `Config/ConfigMapping.cs`, `ConWatchDbContext.cs`, `Migrations/*AddTargetScope*` | Persist and map scope | 1 |
| `Core/ConCore/Targets/PingProfile.cs`, `Libraries/ConPersistence/Config/BuiltInProfiles.cs` | The `Local` built-in profile | 2 |
| `Core/ConEngine/Configuration/WatcherOptionsValidator.cs` | Local rules | 3 |
| `Core/ConEngine/WatcherEngine.cs` | `IsLocalSubject`, skip suspicion for Local | 4 |
| `Libraries/ConNotify/WebhookNotifier.cs`, `MqttNotifier.cs` | No notifications for Local | 5 |
| `Programs/ConWatch/StatusSnapshotBuilder.cs`, `Programs/ConWatchTray/TrayStatus.cs` | Report scope; tray ignores Local | 6 |
| `Programs/ConWatch/Api/MetricsEndpoint.cs` | `scope="local"` label | 7 |
| `Libraries/ConPersistence/Report/IspReportBuilder.cs` | Report excludes Local | 8 |

New test files: `Tests/ConPersistence.Tests/TargetScopeTests.cs`, `Tests/ConEngine.Tests/LocalScopeValidationTests.cs`, `Tests/ConEngine.Tests/LocalScopeEngineTests.cs`, `Tests/ConWatch.Tests/LocalScopeApiTests.cs`. Existing tests extended where noted.

---

### Task 1: `TargetScope` through the model, mappings, database and JSON

**Files:**
- Create: `Core/ConCore/Targets/TargetScope.cs`
- Modify: `Core/ConCore/Targets/Target.cs`, `Core/ConEngine/Configuration/WatcherOptions.cs` (`TargetConfig`), `Core/ConEngine/Topology/TargetGraph.cs` (`FromOptions`), `Libraries/ConPersistence/Entities/ConfigRows.cs` (`TargetRow`), `Libraries/ConPersistence/Config/ConfigDtos.cs` (`TargetDto`), `Libraries/ConPersistence/Config/ConfigMapping.cs`, `Libraries/ConPersistence/ConWatchDbContext.cs`
- Generate: `Libraries/ConPersistence/Migrations/<timestamp>_AddTargetScope*.cs` and the model snapshot update
- Test: create `Tests/ConPersistence.Tests/TargetScopeTests.cs`; modify `Tests/ConPersistence.Tests/SnapshotShapeTests.cs`; create `Tests/ConWatch.Tests/LocalScopeApiTests.cs`

**Interfaces:**
- Produces: `ConCore.Targets.TargetScope { Verdict = 0, Local = 1 }`; `Target.Scope`, `TargetConfig.Scope`, `TargetDto.Scope`, `TargetRow.Scope` (all default `Verdict`); DB column `Targets.Scope` TEXT NOT NULL default `'Verdict'`.

- [ ] **Step 1: Write the failing persistence tests**

Create `Tests/ConPersistence.Tests/TargetScopeTests.cs`:

```csharp
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
```

- [ ] **Step 2: Run to verify it fails (does not compile)**

Run: `dotnet test Tests/ConPersistence.Tests -c Debug --filter "FullyQualifiedName~TargetScopeTests"`
Expected: build FAIL — `TargetScope` / `Scope` not defined.

- [ ] **Step 3: Add the enum**

Create `Core/ConCore/Targets/TargetScope.cs`:

```csharp
using System.Text.Json.Serialization;

namespace ConCore.Targets;

/// <summary>
/// What a target is for. <see cref="Verdict"/> targets (the default) form the
/// reachability tree and feed the internet verdict. <see cref="Local"/>
/// targets are LAN devices: probed with history, shown in the LAN section,
/// and excluded from the verdict, suspicion, notifications and the ISP report.
/// Serialized as a string everywhere ("Verdict" / "Local").
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<TargetScope>))]
public enum TargetScope
{
    Verdict = 0,
    Local = 1,
}
```

- [ ] **Step 4: Add the property to the four target shapes and the graph**

`Core/ConCore/Targets/Target.cs` — after the `Probe` line add:

```csharp
    /// <summary>Whether this target feeds the verdict (default) or is a LAN device tracked on the side.</summary>
    public TargetScope Scope { get; init; } = TargetScope.Verdict;
```

`Core/ConEngine/Configuration/WatcherOptions.cs` — in `TargetConfig`, after `Probe`:

```csharp
    /// <summary>Verdict (default) or Local; Local targets never affect the verdict.</summary>
    public TargetScope Scope { get; set; } = TargetScope.Verdict;
```

`Core/ConEngine/Topology/TargetGraph.cs` — in `FromOptions`, in the `new Target { ... }` initializer after `Probe = t.Probe,` add `Scope = t.Scope,`.

`Libraries/ConPersistence/Entities/ConfigRows.cs` — add `using ConCore.Targets;` at the top and in `TargetRow` after `Probe`:

```csharp
    /// <summary>Verdict (default) or Local; stored as text.</summary>
    public TargetScope Scope { get; set; } = TargetScope.Verdict;
```

`Libraries/ConPersistence/Config/ConfigDtos.cs` — add `using ConCore.Targets;` and in `TargetDto` after `Probe`:

```csharp
    /// <summary>Verdict (default) or Local; a missing value (older clients/snapshots) means Verdict.</summary>
    public TargetScope Scope { get; init; } = TargetScope.Verdict;
```

- [ ] **Step 5: Map it in `ConfigMapping.cs` (four places)**

```csharp
    public static TargetDto ToDto(this TargetConfig t) => new()
    {
        Id = t.Id, Name = t.Name, Address = t.Address,
        Parent = t.Parent, Profile = t.Profile, Enabled = t.Enabled,
        Operator = t.Operator, Probe = t.Probe, Scope = t.Scope,
    };

    public static TargetConfig ToConfig(this TargetDto t) => new()
    {
        Id = t.Id, Name = t.Name, Address = NormalizeAddress(t.Address),
        Parent = t.Parent, Profile = t.Profile, Enabled = t.Enabled,
        Operator = t.Operator, Probe = NormalizeProbe(t.Probe), Scope = t.Scope,
    };

    public static TargetConfig ToConfig(this TargetRow t, string? parentName, string profileName) => new()
    {
        Id = t.Id, Name = t.Name, Address = t.Address,
        Parent = parentName, Profile = profileName, Enabled = t.Enabled,
        Operator = t.Operator, Probe = t.Probe, Scope = t.Scope,
    };
```

and in `CopyFrom(this TargetRow row, TargetDto dto, ...)` after `row.Probe = NormalizeProbe(dto.Probe);` add `row.Scope = dto.Scope;`. (`using ConCore.Targets;` is already imported there.)

- [ ] **Step 6: Configure the column and generate the migration**

`Libraries/ConPersistence/ConWatchDbContext.cs` — add `using ConCore.Targets;` if absent, and inside `modelBuilder.Entity<TargetRow>(e => { ... })` add:

```csharp
            e.Property(t => t.Scope).HasConversion<string>().HasMaxLength(16)
                .HasDefaultValue(TargetScope.Verdict);
```

Run:

```bash
dotnet tool restore
dotnet ef migrations add AddTargetScope --project Libraries/ConPersistence
```

Open the generated `*_AddTargetScope.cs` and confirm `Up` is an `AddColumn<string>` named `Scope` on table `Targets` with `nullable: false` and `defaultValue: "Verdict"`, and `Down` drops it. If the default came out different, edit it to `"Verdict"` (existing rows must become Verdict). The migration is `partial` with `#nullable disable` like its neighbours; do not hand-edit the Designer or model snapshot.

- [ ] **Step 7: Update the shape test and run**

`Tests/ConPersistence.Tests/SnapshotShapeTests.cs` — the targets key list becomes:

```csharp
        AssertKeys(json.GetProperty("config").GetProperty("targets")[0],
            "id", "name", "address", "parent", "profile", "enabled", "operator", "probe", "scope");
```

Run: `dotnet test Tests/ConPersistence.Tests -c Debug`
Expected: all PASS, including `TargetScopeTests`, `SnapshotShapeTests` and `MigrationsTests` (which fails on a model change without a migration — it is the proof the migration is complete).

- [ ] **Step 8: Write the API tests (Review Focus 2)**

Create `Tests/ConWatch.Tests/LocalScopeApiTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace ConWatch.Tests;

public class LocalScopeApiTests : IDisposable
{
    private readonly ConWatchFactory _factory = new();
    private readonly HttpClient _client;

    public LocalScopeApiTests() => _client = _factory.CreateClient();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task A_Local_target_is_created_and_listed_with_its_scope()
    {
        var create = await _client.PostAsJsonAsync("/api/v1/targets", new
        {
            name = "NAS", address = "203.0.113.9", profile = "Internal", scope = "Local",
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        var list = await _client.GetStringAsync("/api/v1/targets");
        var nas = JsonSerializer.Deserialize<JsonElement>(list).EnumerateArray()
            .Single(t => t.GetProperty("name").GetString() == "NAS");
        Assert.Equal("Local", nas.GetProperty("scope").GetString());
    }

    [Fact]
    public async Task An_unknown_scope_is_a_client_error()
    {
        var response = await _client.PostAsync("/api/v1/targets", new StringContent(
            "{\"name\":\"NAS\",\"address\":\"203.0.113.9\",\"profile\":\"Internal\",\"scope\":\"Bogus\"}",
            Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
```

Run: `dotnet test Tests/ConWatch.Tests -c Debug --filter "FullyQualifiedName~LocalScopeApiTests"`
Expected: PASS. If `GET /api/v1/targets` is not the list route, use the route `ConfigEndpoints.cs` maps (`targets.MapGet("/")`), and if the unknown-scope response is not 400, fix the endpoint binding (the framework's bad-request mapping) rather than loosening the test.

- [ ] **Step 9: Commit**

```bash
git add Core Libraries Tests
git commit -m "feat(config): target Scope (Verdict|Local) through model, mappings, database and JSON"
```

---

### Task 2: The built-in `Local` profile

**Files:**
- Modify: `Core/ConCore/Targets/PingProfile.cs`, `Libraries/ConPersistence/Config/BuiltInProfiles.cs`
- Test: modify `Tests/ConPersistence.Tests/ConfigStoreTests.cs`, `Tests/ConPersistence.Tests/ConfigServiceTests.cs` (pinned built-in lists); create a test in `Tests/ConPersistence.Tests/TargetScopeTests.cs`

**Interfaces:**
- Consumes: `PingProfile`, `BuiltInProfiles.All`, `ConfigStore.EnsureBuiltInsAsync` (runs at start and at the end of every import).
- Produces: `PingProfile.Local`; built-in name `"Local"` (so `PingProfile.IsBuiltIn("Local")` is true and the validator accepts it).

Design note: `Local` keeps `NormalInterval` at **10 s**, equal to `Public`, on purpose. `WatcherEngine` computes `StallAfter` from the maximum `NormalInterval` among enabled targets' profiles; a slower Local profile would stretch the stall detector for the whole engine. Tolerance for sleepy IoT devices comes from a smaller window and lower thresholds instead.

- [ ] **Step 1: Write the failing test**

Append to `TargetScopeTests` (needs `using ConCore.Targets;`, already present):

```csharp
    [Fact]
    public async Task The_Local_builtin_profile_is_seeded_and_valid()
    {
        await _store.SeedAsync(null);
        var options = await _store.LoadWatcherOptionsAsync();

        Assert.True(PingProfile.IsBuiltIn("Local"));
        Assert.True(options.Profiles.ContainsKey("Local"));
        var local = options.Profiles["Local"];
        Assert.Equal(10, local.NormalIntervalSeconds);
        Assert.True(local.FalteringThreshold < local.RespondingThreshold);
        Assert.True(local.MinUsableSample <= local.HistorySize);
        Assert.True(new WatcherOptionsValidator().Validate(null, options).Succeeded);
    }
```

(add `using ConEngine.Configuration;` — already imported for `WatcherOptions`).

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test Tests/ConPersistence.Tests -c Debug --filter "FullyQualifiedName~The_Local_builtin"`
Expected: FAIL — `IsBuiltIn("Local")` is false.

- [ ] **Step 3: Add the profile**

`PingProfile.cs` — after `Public`:

```csharp
    /// <summary>Preset for LAN devices tracked beside the verdict (cameras, IoT, NAS):
    /// a smaller, more forgiving window so a sleepy device is not "down" for one missed ping.
    /// Same interval as <see cref="Public"/> so the engine's stall window is unchanged.</summary>
    public static PingProfile Local { get; } = new()
    {
        Name = "Local",
        NormalInterval = TimeSpan.FromSeconds(10),
        HistorySize = 30,
        MinUsableSample = 6,
        RespondingThreshold = 0.80,
        FalteringThreshold = 0.50,
    };
```

and change `BuiltIns` to `[Internal, Path, Public, Local]`.

`BuiltInProfiles.cs` — add to `All` after `Public`:

```csharp
        new(PingProfile.Local, "LAN devices tracked on the side — cameras, IoT, NAS. Gentle and " +
                               "forgiving; never affects your internet verdict."),
```

- [ ] **Step 4: Run, then fix the pinned lists**

Run: `dotnet test Tests/ConPersistence.Tests -c Debug`
Expected: the new test PASSES; existing tests that pin the profile set FAIL (notably `Import_of_an_old_export_naming_External_restores_the_builtins` in `ConfigServiceTests.cs` which asserts `["External","Internal","Path","Public"]`, and any count/list assertions in `ConfigStoreTests.cs`). Update each expectation to include `"Local"` in the position the test's own ordering dictates (the visible failure message shows the actual list). Do not change any other assertion.

Run: `dotnet test Tests/ConPersistence.Tests -c Debug && dotnet test Tests/ConEngine.Tests -c Debug && dotnet test Tests/ConWatch.Tests -c Debug`
Expected: all PASS (fix any other test that enumerated built-ins the same way).

- [ ] **Step 5: Commit**

```bash
git add Core Libraries Tests
git commit -m "feat(config): built-in Local profile for LAN devices"
```

---

### Task 3: Validation rules for Local targets

**Files:**
- Modify: `Core/ConEngine/Configuration/WatcherOptionsValidator.cs`
- Test: create `Tests/ConEngine.Tests/LocalScopeValidationTests.cs`; extend `Tests/ConPersistence.Tests/ConfigServiceTests.cs`

**Interfaces:**
- Consumes: `TargetConfig.Scope`, `WatcherOptionsValidator.Validate(string?, WatcherOptions)`, `ConfigService.UpsertTargetAsync(TargetDto)`.
- Produces: rules — a Local target has no parent; no target (or pool) has a Local target as parent; a Local target cannot be a pool member (which also keeps it out of the anchor pool, since the anchor pool is a pool). Messages below are the contract tests assert on.

- [ ] **Step 1: Write the failing validator tests**

Create `Tests/ConEngine.Tests/LocalScopeValidationTests.cs`:

```csharp
using ConCore.Targets;
using ConEngine.Configuration;
using Microsoft.Extensions.Options;

namespace ConEngine.Tests;

public class LocalScopeValidationTests
{
    private static ValidateOptionsResult Validate(WatcherOptions options) =>
        new WatcherOptionsValidator().Validate(null, options);

    private static TargetConfig Router => new() { Name = "Router", Address = "203.0.113.1", Profile = "Internal" };
    private static TargetConfig Nas => new() { Name = "NAS", Address = "203.0.113.9", Profile = "Local", Scope = TargetScope.Local };

    [Fact]
    public void A_Local_root_target_is_valid()
    {
        var result = Validate(new WatcherOptions { Targets = [Router, Nas] });
        Assert.True(result.Succeeded, string.Join("; ", result.Failures ?? []));
    }

    [Fact]
    public void A_Local_target_cannot_have_a_parent()
    {
        var nas = Nas;
        nas.Parent = "Router";
        var result = Validate(new WatcherOptions { Targets = [Router, nas] });

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("Target 'NAS' is Local and cannot have a parent."));
    }

    [Fact]
    public void A_target_cannot_have_a_Local_parent()
    {
        var tv = new TargetConfig { Name = "TV", Address = "203.0.113.10", Profile = "Internal", Parent = "NAS" };
        var result = Validate(new WatcherOptions { Targets = [Nas, tv] });

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f =>
            f.Contains("Target 'TV' has Local target 'NAS' as parent; Local targets cannot have children."));
    }

    [Fact]
    public void A_Local_target_cannot_be_a_pool_member()
    {
        var result = Validate(new WatcherOptions
        {
            Targets = [Router, Nas],
            Pools = [new PoolConfig { Name = "Internet", Members = ["Router", "NAS"] }],
        });

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f =>
            f.Contains("Pool 'Internet' member 'NAS' is a Local target; Local targets cannot be pool members."));
    }

    [Fact]
    public void A_pool_cannot_hang_under_a_Local_target()
    {
        var result = Validate(new WatcherOptions
        {
            Targets = [Router, Nas],
            Pools = [new PoolConfig { Name = "Internet", Members = ["Router"], Parent = "NAS" }],
        });

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f =>
            f.Contains("Pool 'Internet' has Local target 'NAS' as parent; Local targets cannot have children."));
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test Tests/ConEngine.Tests -c Debug --filter "FullyQualifiedName~LocalScopeValidationTests"`
Expected: the four rejection tests FAIL (no rules yet); `A_Local_root_target_is_valid` PASSES.

- [ ] **Step 3: Implement the rules**

In `WatcherOptionsValidator.cs` add `using ConCore.Targets;` and:

1. Beside `targetNames`/`addressProbes` declare `var localNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);`.
2. At the end of the first `foreach (var t in options.Targets)` loop body (after the address checks) add:

```csharp
            if (t.Scope == TargetScope.Local)
            {
                if (!string.IsNullOrWhiteSpace(t.Name))
                    localNames.Add(t.Name);
                if (t.Parent is { Length: > 0 })
                    failures.Add($"Target '{t.Name}' is Local and cannot have a parent.");
            }
```

3. In the pool loop's member `foreach (var m in p.Members)`, extend the body so a known Local member is reported:

```csharp
                if (!targetNames.Contains(m))
                    failures.Add(options.Pools.Any(x => x.Name.Equals(m, StringComparison.OrdinalIgnoreCase))
                        ? $"Pool '{p.Name}' member '{m}' is a pool; pools cannot contain pools."
                        : $"Pool '{p.Name}' references unknown member '{m}'.");
                else if (localNames.Contains(m))
                    failures.Add($"Pool '{p.Name}' member '{m}' is a Local target; Local targets cannot be pool members.");
```

4. Replace the pool-parent line with:

```csharp
            if (p.Parent is { Length: > 0 } && !targetNames.Contains(p.Parent))
                failures.Add($"Pool '{p.Name}' references unknown parent target '{p.Parent}'.");
            else if (p.Parent is { Length: > 0 } && localNames.Contains(p.Parent))
                failures.Add($"Pool '{p.Name}' has Local target '{p.Parent}' as parent; Local targets cannot have children.");
```

5. In the target-parent loop, make the final branch:

```csharp
            else if (!targetNames.Contains(t.Parent))
                failures.Add($"Target '{t.Name}' references unknown parent '{t.Parent}'.");
            else if (localNames.Contains(t.Parent))
                failures.Add($"Target '{t.Name}' has Local target '{t.Parent}' as parent; Local targets cannot have children.");
```

- [ ] **Step 4: Run to verify the validator tests pass**

Run: `dotnet test Tests/ConEngine.Tests -c Debug`
Expected: all PASS.

- [ ] **Step 5: Add the service-level tests (Review Focus 3)**

Append to `Tests/ConPersistence.Tests/ConfigServiceTests.cs` (it already seeds `ValidHome`: Router→Modem→{Google,Cloudflare}, pool Internet = Google+Cloudflare under Modem):

```csharp
    [Fact]
    public async Task Creating_a_Local_root_target_succeeds()
    {
        var result = await _service.UpsertTargetAsync(new TargetDto
        {
            Name = "NAS", Address = "203.0.113.9", Profile = "Local", Scope = ConCore.Targets.TargetScope.Local,
        });
        Assert.True(result.Success);
        var options = await _store.LoadWatcherOptionsAsync();
        Assert.Equal(ConCore.Targets.TargetScope.Local, options.Targets.Single(t => t.Name == "NAS").Scope);
    }

    [Fact]
    public async Task Switching_a_target_that_has_children_to_Local_is_rejected_and_rolled_back()
    {
        var routerId = await IdOf("Router"); // parent of Modem
        var result = await _service.UpsertTargetAsync(new TargetDto
        {
            Id = routerId, Name = "Router", Address = "192.168.1.1", Profile = "Internal",
            Scope = ConCore.Targets.TargetScope.Local,
        });
        Assert.False(result.Success);
        Assert.Equal(ConfigErrorKind.Validation, result.Kind);
        Assert.Contains(result.Errors!, e => e.Contains("Local targets cannot have children"));
        Assert.Equal(0, _changeSignals);
        var options = await _store.LoadWatcherOptionsAsync();
        Assert.Equal(ConCore.Targets.TargetScope.Verdict, options.Targets.Single(t => t.Name == "Router").Scope);
    }

    [Fact]
    public async Task Switching_a_pool_member_to_Local_is_rejected()
    {
        var googleId = await IdOf("Google"); // member of Internet
        var result = await _service.UpsertTargetAsync(new TargetDto
        {
            Id = googleId, Name = "Google", Address = "8.8.8.8", Parent = "Modem", Profile = "Public",
            Scope = ConCore.Targets.TargetScope.Local,
        });
        Assert.False(result.Success);
        Assert.Contains(result.Errors!, e => e.Contains("Local targets cannot be pool members")
            || e.Contains("cannot have a parent"));
    }

    [Fact]
    public async Task A_target_cannot_be_parented_to_a_Local_target()
    {
        Assert.True((await _service.UpsertTargetAsync(new TargetDto
        {
            Name = "NAS", Address = "203.0.113.9", Profile = "Local", Scope = ConCore.Targets.TargetScope.Local,
        })).Success);
        var result = await _service.UpsertTargetAsync(new TargetDto
        {
            Name = "Backup", Address = "203.0.113.10", Parent = "NAS", Profile = "Internal",
        });
        Assert.False(result.Success);
        Assert.Contains(result.Errors!, e => e.Contains("Local targets cannot have children"));
    }
```

Run: `dotnet test Tests/ConPersistence.Tests -c Debug --filter "FullyQualifiedName~ConfigServiceTests"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add Core Tests
git commit -m "feat(config): validation rules for Local targets"
```

---

### Task 4: Engine — `IsLocalSubject` and no suspicion for Local targets

**Files:**
- Modify: `Core/ConEngine/WatcherEngine.cs`
- Test: create `Tests/ConEngine.Tests/LocalScopeEngineTests.cs`

**Interfaces:**
- Consumes: `Target.Scope` (Task 1), `WatcherEngine.Graph`.
- Produces: `public bool WatcherEngine.IsLocalSubject(Guid subjectId)` — true only when the id is a graph target whose scope is Local; false for pools, the verdict subject, unknown ids and before the engine has started. Tasks 5 uses it.

- [ ] **Step 1: Write the failing tests**

Create `Tests/ConEngine.Tests/LocalScopeEngineTests.cs`:

```csharp
using System.Net;
using ConCore;
using ConCore.Targets;
using ConEngine.Configuration;
using ConEngine.Scheduling;
using ConEngine.Tests.Fakes;
using ConEngine.Topology;
using ConEngine.Tracking;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace ConEngine.Tests;

public class LocalScopeEngineTests
{
    private static readonly PingProfile Fast = new() { Name = "Fast", HistorySize = 20, MinUsableSample = 5 };

    private static WatcherOptions Options() => new()
    {
        Targets =
        [
            new TargetConfig { Name = "Router", Address = "203.0.113.1", Profile = "Internal" },
            new TargetConfig { Name = "Google", Address = "198.51.100.8", Parent = "Router" },
            new TargetConfig { Name = "NAS", Address = "203.0.113.9", Profile = "Local", Scope = TargetScope.Local },
        ],
        Pools = [new PoolConfig { Name = "Internet", Members = ["Google"] }],
    };

    [Fact]
    public async Task IsLocalSubject_is_true_only_for_Local_targets()
    {
        var engine = new WatcherEngine(TimeProvider.System, new FakePingTransceiverFactory(),
            new ConInfrastructure.DomainNameResolverFactory(), NullLoggerFactory.Instance,
            Microsoft.Extensions.Options.Options.Create(Options()));
        Assert.False(engine.IsLocalSubject(Guid.NewGuid())); // before start: no graph, no throw

        using var cts = new CancellationTokenSource();
        await engine.StartAsync(cts.Token);
        try
        {
            Assert.True(engine.IsLocalSubject(engine.Graph.GetByName("NAS").Id));
            Assert.False(engine.IsLocalSubject(engine.Graph.GetByName("Router").Id));
            Assert.False(engine.IsLocalSubject(engine.Graph.GetPoolByName("Internet").Id));
            Assert.False(engine.IsLocalSubject(VerdictCoordinator.SubjectId));
            Assert.False(engine.IsLocalSubject(Guid.NewGuid()));
        }
        finally
        {
            cts.Cancel();
            await engine.StopAsync();
        }
    }

    [Fact]
    public void A_Local_target_going_down_does_not_change_the_verdict()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-02T00:00:00Z"));
        var graph = TargetGraph.FromOptions(new WatcherOptions
        {
            Targets =
            [
                new TargetConfig { Name = "Router", Address = "192.0.2.1", Profile = "Internal" },
                new TargetConfig { Name = "Google", Address = "192.0.2.8", Parent = "Router" },
                new TargetConfig { Name = "Cloudflare", Address = "192.0.2.9", Parent = "Router" },
                new TargetConfig { Name = "NAS", Address = "192.0.2.50", Profile = "Local", Scope = TargetScope.Local },
            ],
            Pools = [new PoolConfig { Name = "Internet", Members = ["Google", "Cloudflare"] }],
        });
        var monitors = new Dictionary<Guid, TargetMonitor>();
        foreach (var target in graph.Targets.Values)
        {
            var monitor = new TargetMonitor(target, Fast, new PingDiscipline(Fast, time), time, NullLoggerFactory.Instance);
            monitor.StartTracking(IPAddress.Parse(target.Address));
            monitors[target.Id] = monitor;
        }
        new TopologyCoordinator(graph, monitors, NullLogger<TopologyCoordinator>.Instance).Start();
        var verdict = new VerdictCoordinator(graph, graph.GetPoolByName("Internet"), monitors,
            weatherThreshold: 2, time, NullLogger<VerdictCoordinator>.Instance);
        var verdictTransitions = new List<ConEngine.Events.TargetTransition>();
        verdict.TransitionSource.Subscribe(verdictTransitions.Add);
        verdict.Start();

        void Feed(string name, int count, bool success)
        {
            var monitor = monitors[graph.GetByName(name).Id];
            for (int i = 0; i < count; i++)
            {
                time.Advance(TimeSpan.FromSeconds(1));
                monitor.RecordPingResult(new PingResult(monitor.ResolvedAddress!, success,
                    success ? 5 : 0, timestamp: time.GetUtcNow()));
            }
        }

        foreach (var name in new[] { "Router", "Google", "Cloudflare", "NAS" })
            Feed(name, 5, true);
        Assert.Equal(VerdictState.Healthy, verdict.CurrentVerdict);
        var before = verdictTransitions.Count;

        Feed("NAS", 30, false);

        Assert.Equal(HostState.Down, monitors[graph.GetByName("NAS").Id].CurrentState);
        Assert.Equal(VerdictState.Healthy, verdict.CurrentVerdict);
        Assert.Equal(before, verdictTransitions.Count);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test Tests/ConEngine.Tests -c Debug --filter "FullyQualifiedName~LocalScopeEngineTests"`
Expected: build FAIL — `IsLocalSubject` not defined. (The second test pins the spec's assumption that a root, non-anchor target cannot move the verdict; if it FAILS after the build is fixed, stop and report: the spec's premise is wrong and the design needs revisiting.)

- [ ] **Step 3: Implement**

In `Core/ConEngine/WatcherEngine.cs` add `using ConCore.Targets;` if absent. Directly after the `Graph` property (`public TargetGraph Graph => ...`) add:

```csharp
    /// <summary>
    /// True when <paramref name="subjectId"/> is a Local-scope target. Transitions
    /// carry only an id, so the notifiers use this to leave Local targets out.
    /// False for pools, the verdict subject, unknown ids and before the engine starts.
    /// </summary>
    public bool IsLocalSubject(Guid subjectId) =>
        _graph is { } graph && graph.Targets.TryGetValue(subjectId, out var target)
        && target.Scope == TargetScope.Local;
```

In the ping-result subscription, replace `Suspicion?.OnPingSample(monitor.Target.Id, result);` with:

```csharp
                    // Local targets are tracked on the side: no suspicion bursts,
                    // corroboration sweeps or glitch episodes (spec §3 exclusions).
                    if (monitor.Target.Scope != TargetScope.Local)
                        Suspicion?.OnPingSample(monitor.Target.Id, result);
```

- [ ] **Step 4: Run, then commit**

Run: `dotnet test Tests/ConEngine.Tests -c Debug`
Expected: all PASS. (There is no automated test for the suspicion skip itself — it is a one-line guard; the user-visible consequences are covered by the verdict test above and Task 8's glitch exclusion.)

```bash
git add Core Tests
git commit -m "feat(engine): IsLocalSubject; Local targets feed no suspicion"
```

---

### Task 5: No webhook or MQTT notifications for Local targets

**Files:**
- Modify: `Libraries/ConNotify/WebhookNotifier.cs`, `Libraries/ConNotify/MqttNotifier.cs`
- Test: modify `Tests/ConWatch.Tests/NotifierIntegrationTests.cs`

**Interfaces:**
- Consumes: `WatcherEngine.IsLocalSubject` (Task 4), `TargetMonitor.Target.Scope`.
- Produces: no Local transition is queued to webhooks or MQTT; no Home Assistant discovery entities exist for Local targets.

- [ ] **Step 1: Write the failing integration test (Review Focus 5)**

Add to `NotifierIntegrationTests` (it already has the `Hook` listener, `ConWatchFactory` and `ConfigApiTests` helpers):

```csharp
    [Fact]
    public async Task Local_target_transitions_are_not_delivered_to_a_webhook()
    {
        using var hook = new Hook();
        using var factory = new ConWatchFactory();
        var client = factory.CreateClient();
        await client.PutAsJsonAsync("/api/v1/settings", new Dictionary<string, string?>
        {
            ["Webhook.Enabled"] = "true",
            ["Webhook.Urls"] = hook.Url("/hook"),
        });

        await ConfigApiTests.SeedHomeAsync(client);
        (await client.PostAsJsonAsync("/api/v1/targets", new
        {
            name = "LocalNas", address = "203.0.113.9", profile = "Fast", scope = "Local",
        })).EnsureSuccessStatusCode();
        // A verdict-scope target added AFTER the Local one is a marker: webhooks are
        // delivered FIFO, so once it arrives any Local delivery would already have.
        (await client.PostAsJsonAsync("/api/v1/targets", new
        {
            name = "MarkerHost", address = "203.0.113.20", parent = "Router", profile = "Fast",
        })).EnsureSuccessStatusCode();

        await ConfigApiTests.Eventually(() =>
            Task.FromResult(hook.Received.Any(r => r.Body.Contains("MarkerHost"))));

        Assert.DoesNotContain(hook.Received, r => r.Body.Contains("LocalNas"));
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test Tests/ConWatch.Tests -c Debug --filter "FullyQualifiedName~Local_target_transitions_are_not_delivered"`
Expected: FAIL on the final assertion (the Local target's first transition is delivered). If it instead times out waiting for `MarkerHost`, the fake pinger's first-transition behaviour differs — keep `Fast` profile as used by `SeedHomeAsync` and re-check that the target is created under `Router`.

- [ ] **Step 3: Implement the webhook filter**

`WebhookNotifier.Attach`:

```csharp
        _subscription = engine.TransitionSource.Subscribe(
            t =>
            {
                // Local (LAN-device) targets never notify; verdict/pool subjects pass.
                if (!engine.IsLocalSubject(t.TargetId))
                    _queue.Writer.TryWrite(t);
            },
            ex => log.LogError(ex, "Transition stream error in webhook notifier"));
```

- [ ] **Step 4: Implement the MQTT filter**

`MqttNotifier.Attach`:

```csharp
            _subscription = engine.TransitionSource.Subscribe(
                t =>
                {
                    if (!engine.IsLocalSubject(t.TargetId))
                        _inbox.Post(t);
                },
                ex => log.LogError(ex, "Transition stream error in MQTT notifier"));
```

`MqttNotifier.RefreshAllAsync` — add `using ConCore.Targets;` if absent and change the monitor list so Local targets get no discovery entities or retained state:

```csharp
        var monitors = engine.Monitors.Values
            .Where(m => m.Target.Scope != TargetScope.Local)
            .OrderBy(m => m.Target.Name).ToList();
```

(If `_inbox.Post` expects `object` rather than `TargetTransition`, the lambda still compiles.)

- [ ] **Step 5: Run, then commit**

Run: `dotnet test Tests/ConWatch.Tests -c Debug --filter "FullyQualifiedName~NotifierIntegrationTests"` then `dotnet test Tests/ConNotify.Tests -c Debug`
Expected: PASS (MQTT/Docker tests skip without Docker). The MQTT exclusions have no automated test (they need a Mosquitto container); they are verified by the manual smoke test in Task 9.

```bash
git add Libraries Tests
git commit -m "feat(notify): Local targets send no webhook or MQTT notifications"
```

---

### Task 6: Status snapshot carries scope; tray ignores Local

**Files:**
- Modify: `Programs/ConWatch/StatusSnapshotBuilder.cs`, `Programs/ConWatchTray/TrayStatus.cs`
- Test: modify `Tests/ConWatch.Tests/StatusShapeTests.cs`; extend `Tests/ConWatch.Tests/LocalScopeApiTests.cs`

**Interfaces:**
- Consumes: `Target.Scope`.
- Produces: `StatusTarget` gains a last positional parameter `TargetScope Scope` serialized as `"scope": "Verdict" | "Local"`. This shape feeds `/api/v1/status`, the SignalR `statusChanged` push, the dashboard's initial data and MCP `get_current_status`. Plan 1B reads it.

- [ ] **Step 1: Update the shape test (failing first)**

`StatusShapeTests.cs` — the target keys become (alphabetical, as `Keys()` sorts):

```csharp
        Assert.Equal(new[] { "address", "assumed", "avgRtt", "id", "name", "operator", "pingCount",
                "probe", "sampleSize", "scope", "state", "successRate" },
            Keys(status.GetProperty("targets")[0]));
```

Append to `LocalScopeApiTests`:

```csharp
    [Fact]
    public async Task Status_reports_each_targets_scope()
    {
        await ConfigApiTests.SeedHomeAsync(_client);
        (await _client.PostAsJsonAsync("/api/v1/targets", new
        {
            name = "NAS", address = "203.0.113.9", profile = "Fast", scope = "Local",
        })).EnsureSuccessStatusCode();

        JsonElement status = default;
        await ConfigApiTests.Eventually(async () =>
        {
            status = JsonSerializer.Deserialize<JsonElement>(await _client.GetStringAsync("/api/v1/status"));
            return status.GetProperty("targets").EnumerateArray()
                .Any(t => t.GetProperty("name").GetString() == "NAS");
        });

        var scopes = status.GetProperty("targets").EnumerateArray()
            .ToDictionary(t => t.GetProperty("name").GetString()!, t => t.GetProperty("scope").GetString());
        Assert.Equal("Local", scopes["NAS"]);
        Assert.Equal("Verdict", scopes["Router"]);
    }
```

Run: `dotnet test Tests/ConWatch.Tests -c Debug --filter "FullyQualifiedName~StatusShapeTests|FullyQualifiedName~LocalScopeApiTests"`
Expected: FAIL (no `scope` key).

- [ ] **Step 2: Implement**

`StatusSnapshotBuilder.cs` — add `using ConCore.Targets;`, change the record and the projection:

```csharp
public sealed record StatusTarget(Guid Id, string Name, string Address, string? Probe, string State,
    bool Assumed, double SuccessRate, double AvgRtt, int SampleSize, long PingCount, string? Operator,
    TargetScope Scope);
```

and in `Build` add `m.Target.Scope` after `m.Target.Operator` in the `new StatusTarget(...)` call.

`TrayStatus.cs` — in the no-verdict fallback, replace the `var states = ...` pipeline start with a filter so LAN devices never colour the tray:

```csharp
            var states = targets.EnumerateArray()
                .Where(t => !(t.TryGetProperty("scope", out var scope) && scope.GetString() == "Local"))
                .Select(t => (Name: t.GetProperty("name").GetString() ?? "",
                              State: t.GetProperty("state").GetString() ?? ""))
                .ToList();
```

(An all-Local list becomes empty; the existing `states.All(...)` check then returns Measuring, which is correct for "no verdict targets yet". The tray project has no test project and is Windows-only; build it with `dotnet build Programs/ConWatchTray -c Debug` on the Windows dev machine.)

- [ ] **Step 3: Run, then commit**

Run: `dotnet test Tests/ConWatch.Tests -c Debug` then `dotnet build Programs/ConWatchTray -c Debug`
Expected: PASS / build succeeds. Fix any other test that constructs or pins `StatusTarget` JSON (the report found only `StatusShapeTests`).

```bash
git add Programs Tests
git commit -m "feat(status): status targets report their scope; tray ignores Local targets"
```

---

### Task 7: Prometheus `scope="local"` label

**Files:**
- Modify: `Programs/ConWatch/Api/MetricsEndpoint.cs`
- Test: modify `Tests/ConWatch.Tests/MetricsEndpointTests.cs`

**Interfaces:**
- Consumes: `engine.Monitors`, `Target.Scope`.
- Produces: series for Local targets carry an extra `scope="local"` label; Verdict-scope series are byte-for-byte unchanged (adding a label to existing series would create new series identities and break users' dashboards).

- [ ] **Step 1: Write the failing test**

Add to `MetricsEndpointTests`:

```csharp
    [Fact]
    public async Task Local_targets_carry_a_scope_label_and_verdict_targets_do_not()
    {
        await ConfigApiTests.SeedHomeAsync(_client);
        (await _client.PostAsJsonAsync("/api/v1/targets", new
        {
            name = "NAS", address = "203.0.113.9", profile = "Fast", scope = "Local",
        })).EnsureSuccessStatusCode();

        var body = "";
        await ConfigApiTests.Eventually(async () =>
        {
            body = await ScrapeAsync();
            return body.Contains("conwatch_target_up{name=\"NAS\",scope=\"local\"}")
                && body.Contains("conwatch_target_up{name=\"Router\"}");
        });

        Assert.Contains("conwatch_target_sample_size{name=\"NAS\",scope=\"local\"}", body);
        Assert.Contains("conwatch_transitions_total{name=\"NAS\",scope=\"local\"}", body);
        Assert.DoesNotContain("name=\"Router\",scope=", body);
    }
```

(add `using System.Net.Http.Json;` if the file lacks it.)

Run: `dotnet test Tests/ConWatch.Tests -c Debug --filter "FullyQualifiedName~Local_targets_carry_a_scope_label"`
Expected: FAIL.

- [ ] **Step 2: Implement**

In `MetricsEndpoint.cs` add `using ConCore.Targets;`. In `Render`, right after `var sb = new StringBuilder();` add:

```csharp
        var localNames = engine is null
            ? new HashSet<string>()
            : engine.Monitors.Values.Where(m => m.Target.Scope == TargetScope.Local)
                .Select(m => m.Target.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
```

Change `Series` to take the flag:

```csharp
    private static void Series(StringBuilder sb, string family, string name, double value, bool local = false) =>
        sb.Append(family).Append("{name=\"").Append(Escape(name)).Append('"')
          .Append(local ? ",scope=\"local\"" : "")
          .Append("} ")
          .Append(value.ToString("0.######", CultureInfo.InvariantCulture)).Append('\n');
```

and pass `localNames.Contains(m.Target.Name)` as the fifth argument in the four `foreach (var m in targets)` series calls (`conwatch_target_up`, `_loss_ratio`, `_rtt_seconds`, `_sample_size`), and `localNames.Contains(name)` in the `conwatch_transitions_total` loop. The pool series stay as they are.

- [ ] **Step 3: Run, then commit**

Run: `dotnet test Tests/ConWatch.Tests -c Debug --filter "FullyQualifiedName~MetricsEndpointTests"`
Expected: PASS, including the pre-existing exact-format assertions (proof Verdict series are unchanged).

```bash
git add Programs Tests
git commit -m "feat(metrics): Local targets carry scope=local; verdict series unchanged"
```

---

### Task 8: The ISP report ignores Local targets

**Files:**
- Modify: `Libraries/ConPersistence/Report/IspReportBuilder.cs`
- Test: modify `Tests/ConPersistence.Tests/IspReportBuilderTests.cs`

**Interfaces:**
- Consumes: `TargetRow.Scope`, `GlitchRow.TargetId`.
- Produces: per-target report rows and the glitch summary exclude Local targets, including their pre-existing history (Review Focus 4).

- [ ] **Step 1: Write the failing test**

Add to `IspReportBuilderTests` (it already has `_store`, `_factory`, `_builder`, `AddTransition`, `AddRollup`; add `using ConCore.Targets;`):

```csharp
    [Fact]
    public async Task Local_targets_and_their_history_stay_out_of_the_report()
    {
        var service = new ConfigService(_factory, _store);
        Assert.True((await service.UpsertTargetAsync(new TargetDto
        {
            Name = "NAS", Address = "203.0.113.9", Profile = "Local", Scope = TargetScope.Local,
        })).Success);
        var nasId = (await _store.LoadWatcherOptionsAsync()).Targets.Single(t => t.Name == "NAS").Id!.Value;

        await AddTransition(nasId, "NAS", From.AddHours(2), "Down");
        await AddRollup(nasId, From.AddHours(1), 100, 50);
        await using (var db = _factory.CreateDbContext())
        {
            // Three class-0 glitches in distinct hours late in the period would make NAS "chronic".
            for (int i = 0; i < 3; i++)
                db.Glitches.Add(new GlitchRow { TargetId = nasId, TargetName = "NAS", Class = 0,
                    StartUtc = To.AddHours(-1 - i), EndUtc = To.AddHours(-1 - i), LostCount = 2, SampleCount = 8 });
            await db.SaveChangesAsync();
        }

        var report = await _builder.BuildAsync(From, To);

        Assert.DoesNotContain(report.Targets, r => r.Name == "NAS");
        Assert.Equal(0, report.Glitches.TotalCount);
        Assert.DoesNotContain("NAS", report.Glitches.ChronicTargets);
        Assert.Contains(report.Targets, r => r.Name == "Google"); // verdict targets still reported
    }
```

Run: `dotnet test Tests/ConPersistence.Tests -c Debug --filter "FullyQualifiedName~Local_targets_and_their_history"`
Expected: FAIL.

- [ ] **Step 2: Implement**

`IspReportBuilder.cs` — add `using ConCore.Targets;`.

`BuildTargetRowsAsync`: replace the first line with

```csharp
        var targets = await db.Targets.AsNoTracking()
            .Where(t => t.Scope == TargetScope.Verdict)
            .OrderBy(t => t.Name).ToListAsync();
```

`BuildGlitchSummaryAsync`: replace the query with

```csharp
        var localIds = await db.Targets.AsNoTracking()
            .Where(t => t.Scope == TargetScope.Local).Select(t => t.Id).ToListAsync();
        var glitches = await db.Glitches.AsNoTracking()
            .Where(g => g.StartUtc >= fromUtc && g.StartUtc <= toUtc && !localIds.Contains(g.TargetId))
            .ToListAsync();
```

(Internet-class glitches carry the verdict subject id, which is not in `Targets`, so they are kept.)

- [ ] **Step 3: Run, then commit**

Run: `dotnet test Tests/ConPersistence.Tests -c Debug`
Expected: all PASS, including the existing glitch-summary test (its expectations are unchanged because no Local targets exist there).

```bash
git add Libraries Tests
git commit -m "feat(report): ISP report excludes Local targets and their history"
```

---

### Task 9: Whole-branch verification and smoke test

**Files:** none (verification only; fix anything it finds in the owning task's files).

- [ ] **Step 1: Run every test project**

```bash
dotnet test Tests/ConEngine.Tests -c Debug
dotnet test Tests/ConPersistence.Tests -c Debug
dotnet test Tests/ConWatch.Tests -c Debug
dotnet test Tests/ConConfig.Tests -c Debug
dotnet test Tests/ConWatchCli.Tests -c Debug
dotnet test Tests/ConNotify.Tests -c Debug
```

Expected: all PASS (Docker-dependent MQTT cases skip). Fix regressions in the file that caused them; do not weaken assertions.

- [ ] **Step 2: Build the tray and the frontend (unchanged, must still build)**

```bash
dotnet build Programs/ConWatchTray -c Debug
cd Programs/ConWatch/ClientApp && npx tsc --noEmit && npx vitest run && cd ../../..
```

Expected: success. (`TargetStatus` in `ClientApp/src/lib/types.ts` has no `scope` yet — extra JSON fields are ignored; plan 1B adds it.)

- [ ] **Step 3: Manual smoke (covers the MQTT exclusion and a real engine)**

Start the app with the project's launch configuration (`preview_start` / `dotnet run --project Programs/ConWatch`) against a scratch data directory. With the REST API (admin password unset): create a verdict target `Router`, an anchor pool, and a Local target (`POST /api/v1/targets` with `"scope":"Local","profile":"Local"`). Confirm: `GET /api/v1/status` lists the Local target with `"scope":"Local"`; the verdict stays Healthy/Unknown as before; `GET /metrics` shows `scope="local"` on its series; `GET /api/v1/export/snapshot` includes `"scope":"Local"` on that target. If a Mosquitto broker is at hand, confirm no `homeassistant/.../<local-target-slug>` discovery topic is published.

- [ ] **Step 4: Final commit if anything was fixed**

```bash
git status --short
git add -A -- Core Libraries Programs Tests
git commit -m "fix: address verification findings for Local scope backend"
```

(Skip if the tree is clean. Do not push. Hand the branch back for review before merging to `master`.)
