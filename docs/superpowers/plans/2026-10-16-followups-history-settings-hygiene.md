# Follow-ups: history scope, settings, hygiene, polish minors — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close four groups of open follow-ups in one branch: (2) a deleted Local target's history no longer leaks onto the internet side and the README documents the `scope="local"` metrics label; (3) the Traceroute ASN lookup becomes a switch and a target in two pools gets a deterministic stagger slot; (4) the September hygiene items — the one remaining sleep-then-assert test, the hardcoded `"Minute"` rollup resolution, the `dotnet ef` note, and throttled bad-subscriber / dropped-event logging; (6) the deferred minors from the DNS polish branch.

**Architecture:** History rows do not store a scope, so an unknown subject (a target deleted since) is tagged **`Unknown`** on the way out and the Internet and Local filters both exclude it — it stays reachable under `all`; the ISP glitch summary switches from "not Local" to an allow-list of current Verdict-scope targets. The settings form helper learns checkboxes, so a switch posts `true`/`false` like the select did. The engine picks a shared target's pool by name, so its stagger is reproducible. A small pure `LogThrottle` in ConCore decides when a repeating error line is logged; the engine's subscriber-threw path and the channel recorder's drop callback use it. The MQTT skip decision and the registry integer read become pure helpers with tests.

**Tech Stack:** .NET 10, EF Core + SQLite, xUnit; Razor + vanilla JS for the Settings page.

**Spec:** no single spec; the binding sources are `docs/superpowers/specs/2026-10-02-discoveries-and-local-scope-design.md` (scope semantics), `docs/superpowers/specs/2026-10-01-config-refinements-design.md` (the ASN lookup checkbox), `docs/superpowers/plans/2026-09-18-review-fixes-followups.md` (the hygiene list), and the DNS polish review's deferred minors (recorded in `docs/superpowers/plans/2026-10-16-dns-watch-polish.md`'s ledger summary in this plan's Task 5). Master is at c8efc67.

## Global Constraints

- **History scope** (discoveries spec §3, "scope"): `/api/v1/transitions` and `/api/v1/glitches` items carry `scope`; values are now `"Verdict"`, `"Local"`, `"Resolver"` or **`"Unknown"`** (a Target-kind subject that is no longer configured). `scope=local` returns only current Local targets; `scope=internet` returns verdict/pool subjects and current non-Local targets — **never** an Unknown target; `scope=all` returns everything. The ISP report's glitch summary counts only glitches of **current Verdict-scope targets plus Internet-class (verdict-subject) glitches**. The wire type of `scope` stays a string; the React `TransitionEvent.scope` union gains `"Unknown"`.
- **README metrics** (discoveries follow-up): the Grafana/Prometheus section states that per-target series of Local targets carry `scope="local"`, so dashboards can filter LAN devices out or in.
- **Settings** (config-refinements spec: the ASN lookup is a checkbox): the Traceroute form's control is `<input type="checkbox" role="switch" name="Traceroute.AsnLookup">`; `wireSettingsForm` posts a checkbox as `"true"`/`"false"`; selects and text fields post as before.
- **Stagger**: a target that belongs to more than one pool takes its phase from the pool that sorts first by name (ordinal, case-insensitive); the choice no longer depends on dictionary order.
- **Hygiene**: `RollupResolution.Minute/Hour/Day` constants in ConPersistence replace every `"Minute"`/`"Hour"`/`"Day"` literal in persistence code (DataRows default, HistoryQueries, WarmStartQueries, RollupCollector, RollupMerge); `MasterEnableTests.M2M_engine_toggle_rebuilds_exactly_once` waits for the rebuild and then for the count to settle instead of sleeping 500 ms; `LogThrottle` (ConCore): the first occurrence logs, then at most one line per **60 s** carrying the number suppressed since; it is applied to the engine's "ping sample subscriber threw" error and the channel recorder's "channel full; dropped" warning; the README's Development section gains the project-only `dotnet ef` form.
- **DNS polish minors**: `MqttNotifier.ShouldPublishDns(DnsPublication? last, DnsPublication next, bool onlyIfChanged)` pure and tested; `WindowsDohDetector.AsInt(object?)` accepts `int` and `long` (REG_DWORD/REG_QWORD) and reads anything else as null; the ticker captures `tickCts.Token` once before the loop; README: the spliced sentence is re-wrapped and "Publishes happen only on state changes and reconnects" gains ", plus a once-a-minute re-check of the name-resolution sensor that publishes only when it changed".
- Repo rules: tests wait for conditions in deadline loops, never sleep-then-assert; no personal addresses (documentation ranges); commit per task; never push; no attribution lines; output pristine apart from NU1903 and the pre-existing CS8631/CA2022/CA1416 in untouched test files.
- Test commands: `dotnet test Tests/ConWatch.Tests --filter "FullyQualifiedName~<Class>"`, `dotnet test Tests/ConPersistence.Tests`, `dotnet test Tests/ConEngine.Tests --filter "FullyQualifiedName~<Class>"`, `dotnet test Tests/ConNotify.Tests`, `npm --prefix Programs/ConWatch/ClientApp run build`, `dotnet test ConnectionWatcher.sln`.

## Review Focus

1. **A deleted Local target's old transitions and glitches** must appear under `scope=all` tagged `Unknown`, and under neither `scope=local` nor `scope=internet`; the ISP report's glitch count must not include them. Task 1 tests transitions and glitches through the API and the summary through the report builder.
2. **A deleted Verdict target** is also `Unknown` and leaves the internet side (a reasonable person deleting an old anchor expects its rows to stop counting as internet evidence; they remain under `all`). Task 1's test covers it with the same assertions.
3. **The ASN switch unchecked** must post `"false"` (not omit the key), so turning the lookup off actually saves. Task 2 tests `wireSettingsForm`'s checkbox serialisation through a vitest-free DOM-less check? No harness exists for `site-config.js`; the task verifies through the host test that the page renders the switch with the stored state, and the controller verifies the post in the browser.
4. **Two pools that both contain a target** must give it one phase regardless of insertion order. Task 3 tests with the pools added in both orders.
5. **A subscriber that throws on every sample** must log once, then once a minute with a count — never once per sample. Task 4 tests `LogThrottle` with a fake clock and the engine path with a capturing logger.

## File Structure

**Modify**
- `Libraries/ConPersistence/Queries/HistoryQueries.cs` — Internet/Local filters exclude unknown targets; `KnownTargetIdsAsync`.
- `Libraries/ConPersistence/Queries/TransitionDto.cs` — `Scope` becomes `string?` with `"Unknown"`.
- `Libraries/ConPersistence/Report/IspReportBuilder.cs` — glitch allow-list.
- `Programs/ConWatch/Api/HistoryEndpoints.cs`, `Programs/ConWatch/Api/GlitchEndpoints.cs`, `Programs/ConWatch/Mcp/ConWatchMcpTools.cs` — pass the known-id set; tag `Unknown`.
- `Programs/ConWatch/ClientApp/src/lib/types.ts` — scope union.
- `README.md` — metrics line; `dotnet ef` note; MQTT wording and wrap.
- `Programs/ConWatch/wwwroot/lib/site-config.js`, `Programs/ConWatch/Pages/Settings.cshtml` — checkbox.
- `Core/ConEngine/WatcherEngine.cs` — pool order; throttled subscriber error.
- `Core/ConCore/Logging/LogThrottle.cs` (new), `Libraries/ConPersistence/Recording/ChannelRecorder.cs`.
- `Libraries/ConPersistence/Entities/RollupResolution.cs` (new) + the five literal sites.
- `Tests/ConWatch.Tests/MasterEnableTests.cs`.
- `Libraries/ConNotify/MqttNotifier.cs`, `Libraries/ConInfrastructure/Dns/DohConfiguration.cs`.
- Tests: `Tests/ConWatch.Tests/HistoryScopeApiTests.cs`, `Tests/ConPersistence.Tests/IspReportBuilderTests.cs` (or the existing report test class — find it with `grep -rl IspReportBuilder Tests`), `Tests/ConWatch.Tests/SettingsPageTests.cs` (find the existing Settings page test or add to `PageSmokeTests`), `Tests/ConEngine.Tests/PoolStaggerTests.cs` (new), `Tests/ConEngine.Tests/LogThrottleTests.cs` (new), `Tests/ConEngine.Tests/PingPumpResilienceTests.cs`, `Tests/ConPersistence.Tests/RecorderCollectorTests.cs`, `Tests/ConNotify.Tests/BuildersTests.cs`, `Tests/ConEngine.Tests/DohDetectorTests.cs`.

---

### Task 1: A deleted target's history is `Unknown`, off both scope filters, and out of the ISP glitch count

**Files:**
- Modify: `Libraries/ConPersistence/Queries/HistoryQueries.cs`, `Libraries/ConPersistence/Queries/TransitionDto.cs`, `Libraries/ConPersistence/Report/IspReportBuilder.cs`, `Programs/ConWatch/Api/HistoryEndpoints.cs`, `Programs/ConWatch/Api/GlitchEndpoints.cs`, `Programs/ConWatch/Mcp/ConWatchMcpTools.cs`, `Programs/ConWatch/ClientApp/src/lib/types.ts`, `README.md`
- Test: `Tests/ConWatch.Tests/HistoryScopeApiTests.cs`, the ISP report builder's test class (`grep -rl "IspReportBuilder" Tests` — add there)

**Interfaces:**
- Produces: `HistoryQueries.KnownTargetIdsAsync(db)` → `HashSet<Guid>` of every configured target id; `TransitionDto.Scope` is `string?` (`"Verdict"`, `"Local"`, `"Resolver"`, `"Unknown"`; null and omitted on live messages); `TransitionDto.From(row, localIds, resolverIds, knownIds)`; `HistoryQueries.ScopeOf(Guid id, string subjectKind, ISet<Guid> localIds, ISet<Guid> resolverIds, ISet<Guid> knownIds)` → string (shared by transitions, glitches and MCP). Filters: `Internet` = `SubjectKind != "Target" || (knownIds.Contains(id) && !localIds.Contains(id))`; `Local` = `localIds.Contains(id)` (unchanged); glitches have no kind column and are always targets, so `Internet` = `knownIds ∩ ¬localIds`.

- [ ] **Step 1: Write the failing tests**

Append to `HistoryScopeApiTests`:

```csharp
    [Fact]
    public async Task A_deleted_target_is_Unknown_under_all_and_off_both_filters() // Review Focus 1, 2
    {
        await SeedWithLocalNasAsync();
        var targets = await GetJson("/api/v1/targets");
        var nasId = targets.EnumerateArray().Single(t => t.GetProperty("name").GetString() == "NAS").GetProperty("id").GetString();
        (await _client.DeleteAsync($"/api/v1/targets/{nasId}")).EnsureSuccessStatusCode();

        var all = (await GetJson("/api/v1/transitions?limit=200")).GetProperty("items").EnumerateArray().ToList();
        var nas = all.Where(i => i.GetProperty("subjectName").GetString() == "NAS").ToList();
        Assert.NotEmpty(nas);
        Assert.All(nas, i => Assert.Equal("Unknown", i.GetProperty("scope").GetString()));
        var local = (await GetJson("/api/v1/transitions?scope=local&limit=200")).GetProperty("items").EnumerateArray();
        Assert.DoesNotContain(local, i => i.GetProperty("subjectName").GetString() == "NAS");
        var internet = (await GetJson("/api/v1/transitions?scope=internet&limit=200")).GetProperty("items").EnumerateArray().ToList();
        Assert.DoesNotContain(internet, i => i.GetProperty("subjectName").GetString() == "NAS");
        Assert.All(internet, i => Assert.NotEqual("Unknown", i.GetProperty("scope").GetString()));
        Assert.Contains(internet, i => i.GetProperty("subjectName").GetString() == "Router"); // live targets unaffected
    }
```

Create `Tests/ConPersistence.Tests/IspReportBuilderTests.cs` (there is no report test class yet; `IspReportBuilder(IDbContextFactory<ConWatchDbContext> dbFactory, SettingsStore settings)` with `BuildAsync(DateTime fromUtc, DateTime toUtc)` returns an `IspReport` whose `Glitches` is `GlitchSummary(TotalCount, InternetCount, …)`; use `TestDbFactory` and `new SettingsStore(factory)` as `ConfigHealthTests` does, and seed a Verdict-scope target through `ConfigService.EditDocumentAsync` the way that class seeds targets). The test inserts three `GlitchRow`s inside the range (`Class = 0, TargetId = the live Verdict target`; `Class = 0, TargetId = Guid.NewGuid()` for a target that no longer exists; `Class = 1, TargetId = Guid.NewGuid()` for an Internet-class glitch — those carry the verdict subject id, which is never a target) with `TargetName`, `StartUtc`/`EndUtc`, `LostCount`/`SampleCount` filled, then asserts `report.Glitches.TotalCount == 2` and `InternetCount == 1`.

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test Tests/ConWatch.Tests --filter "FullyQualifiedName~HistoryScopeApiTests"` and the report test class.
Expected: FAIL (`scope` is `"Verdict"`, NAS rows present under `internet`; the unknown glitch is counted).

- [ ] **Step 3: Implement**

`HistoryQueries.cs`:

```csharp
    /// <summary>Every configured target id, for telling a deleted subject ("Unknown") from a live one.</summary>
    public static async Task<HashSet<Guid>> KnownTargetIdsAsync(ConWatchDbContext db) =>
        (await db.Targets.AsNoTracking().Select(t => t.Id).ToListAsync()).ToHashSet();

    /// <summary>The scope tag history rows carry: a verdict/pool subject or a live non-Local
    /// target is "Verdict"; a live Local or Resolver target its own; a Target subject that is no
    /// longer configured is "Unknown" — it stays under scope=all and is off both filters.</summary>
    public static string ScopeOf(Guid id, string subjectKind, ISet<Guid> localIds, ISet<Guid> resolverIds, ISet<Guid> knownIds) =>
        localIds.Contains(id) ? "Local"
        : resolverIds.Contains(id) ? "Resolver"
        : subjectKind != "Target" || knownIds.Contains(id) ? "Verdict"
        : "Unknown";
```

In `TransitionsAsync`, replace the Internet branch: `query.Where(t => t.SubjectKind != "Target" || (knownIds.Contains(t.SubjectId) && !localIds.Contains(t.SubjectId)))` where `var knownIds = db.Targets.Select(t => t.Id);` (an `IQueryable`, translated like `localIds`). In `GlitchesAsync`, the Internet branch becomes `query.Where(g => knownIds.Contains(g.TargetId) && !localIds.Contains(g.TargetId))`.

`TransitionDto.cs`: `public string? Scope { get; init; }` (keep `JsonIgnore WhenWritingNull`); replace the two `From(row, …)` overloads with one `From(TransitionRow r, ISet<Guid> localIds, ISet<Guid> resolverIds, ISet<Guid> knownIds) => From(r) with { Scope = HistoryQueries.ScopeOf(r.SubjectId, r.SubjectKind, localIds, resolverIds, knownIds) }` (keep a two-set overload only if a caller still needs it — grep `TransitionDto.From(` and update every caller to pass the known set). The doc comment lists the four values.

`IspReportBuilder.BuildGlitchSummaryAsync`: replace the `localIds` exclusion with an allow-list:

```csharp
        // Only glitches of targets that are internet evidence today count: current Verdict-scope
        // targets, plus Internet-class glitches (verdict subject). A deleted or Local target's
        // old glitches are not the ISP's.
        var verdictIds = await db.Targets.AsNoTracking()
            .Where(t => t.Scope == TargetScope.Verdict).Select(t => t.Id).ToListAsync();
        var glitches = await db.Glitches.AsNoTracking()
            .Where(g => g.StartUtc >= fromUtc && g.StartUtc <= toUtc && (g.Class == 1 || verdictIds.Contains(g.TargetId)))
            .ToListAsync();
```

(Check how Internet-class glitches are keyed — if `Class == 1` rows carry the verdict subject id, the clause above keeps them; read the existing comment at that site and `GlitchRow` to confirm, and adjust the predicate if Internet-class glitches carry a target id instead.)

`HistoryEndpoints.cs`, `GlitchEndpoints.cs`, `ConWatchMcpTools.cs`: fetch `knownIds` alongside `localIds`/`resolverIds` and tag through `ScopeOf` (glitches: `subjectKind: "Target"`). `types.ts`: both `scope?:` unions gain `| "Unknown"`. README (Grafana section, after the per-target series list): "Per-target series of Local-scope targets carry `scope=\"local\"`, so a dashboard can leave LAN devices out (`conwatch_target_up{scope!=\"local\"}`) or chart them alone."

- [ ] **Step 4: Run the tests**

Run: `dotnet test Tests/ConWatch.Tests --filter "FullyQualifiedName~HistoryScopeApiTests|FullyQualifiedName~GlitchApiTests|FullyQualifiedName~HistoryApiTests|FullyQualifiedName~Mcp"`, the report test class, and `npm --prefix Programs/ConWatch/ClientApp run build`.
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Libraries/ConPersistence/Queries Libraries/ConPersistence/Report/IspReportBuilder.cs Programs/ConWatch/Api/HistoryEndpoints.cs Programs/ConWatch/Api/GlitchEndpoints.cs Programs/ConWatch/Mcp/ConWatchMcpTools.cs Programs/ConWatch/ClientApp/src/lib/types.ts README.md Tests
git commit -m "fix(history): a deleted target's rows are Unknown — off both scope filters and out of the ISP glitch count; README notes the scope=local metrics label"
```

---

### Task 2: The Traceroute ASN lookup is a switch

**Files:**
- Modify: `Programs/ConWatch/wwwroot/lib/site-config.js` (`wireSettingsForm`), `Programs/ConWatch/Pages/Settings.cshtml` (~lines 63-70)
- Test: `Tests/ConWatch.Tests/SettingsPageTests.cs` (new; `ConWatchFactory` + `CreateClient()` like `HistoryScopeApiTests`; `PageSmokeTests` already proves `/Settings` renders "Look up the operator of each hop" — that string must survive as the switch's label)

**Interfaces:**
- `wireSettingsForm` serialises `[name]` fields as: checkbox → `"true"`/`"false"`; otherwise `value === "" ? null : value`.

- [ ] **Step 1: Write the failing test**

In the Settings page test class:

```csharp
    [Fact]
    public async Task The_ASN_lookup_is_a_switch_that_reflects_the_stored_setting()
    {
        await ConfigApiTests.SeedHomeAsync(_client);
        var html = await _client.GetStringAsync("/Settings");
        Assert.Contains("type=\"checkbox\" role=\"switch\" name=\"Traceroute.AsnLookup\"", html);
        Assert.DoesNotContain("<select name=\"Traceroute.AsnLookup\"", html);
        (await _client.PutAsJsonAsync("/api/v1/settings", new Dictionary<string, string?> { ["Traceroute.AsnLookup"] = "true" })).EnsureSuccessStatusCode();
        html = await _client.GetStringAsync("/Settings");
        Assert.Matches("name=\"Traceroute.AsnLookup\"[^>]*checked", html);
    }
```

(If the admin lock applies to `/Settings` in the test host, follow how the class's other tests reach the page.)

- [ ] **Step 2: Run to verify it fails**

Expected: FAIL (the select is still there).

- [ ] **Step 3: Implement**

`site-config.js`, in `wireSettingsForm`: `values[field.name] = field.type === "checkbox" ? (field.checked ? "true" : "false") : field.value === "" ? null : field.value;`. `Settings.cshtml`: replace the select with

```cshtml
    <label>
        <input type="checkbox" role="switch" name="@SettingKeys.TracerouteAsnLookup" @(Model.Setting(SettingKeys.TracerouteAsnLookup) == "true" ? "checked" : "") />
        Look up the operator of each hop
    </label>
```

keeping the explanatory paragraph and the Save button.

- [ ] **Step 4: Run the tests**

Run: the Settings page test class and `dotnet test Tests/ConWatch.Tests --filter "FullyQualifiedName~PageSmokeTests"`.
Expected: PASS. The controller then checks in the browser that unticking and saving stores `"false"` (Review Focus 3).

- [ ] **Step 5: Commit**

```bash
git add Programs/ConWatch/wwwroot/lib/site-config.js Programs/ConWatch/Pages/Settings.cshtml Tests/ConWatch.Tests
git commit -m "feat(settings): the ASN lookup is a switch; settings forms post checkboxes as true/false"
```

---

### Task 3: A target in two pools takes its stagger from the first pool by name

**Files:**
- Modify: `Core/ConEngine/WatcherEngine.cs` (~lines 177-181)
- Test: `Tests/ConEngine.Tests/PoolStaggerTests.cs` (new)

**Interfaces:**
- Produces: `public static Dictionary<Guid, (int Index, int Count)> WatcherEngine.PoolPositions(IEnumerable<PoolDefinition> pools)` — pools ordered by `Name` (OrdinalIgnoreCase); the first pool naming a target wins.

- [ ] **Step 1: Write the failing test**

```csharp
using ConCore.Targets;

namespace ConEngine.Tests;

public class PoolStaggerTests
{
    [Fact]
    public void A_target_in_two_pools_takes_its_slot_from_the_first_pool_by_name_whatever_the_order() // Review Focus 4
    {
        var shared = Guid.NewGuid();
        var alpha = new PoolDefinition(Guid.NewGuid(), "Alpha", [Guid.NewGuid(), shared, Guid.NewGuid()], null);
        var beta = new PoolDefinition(Guid.NewGuid(), "beta", [shared, Guid.NewGuid()], null);
        var ab = WatcherEngine.PoolPositions([alpha, beta]);
        var ba = WatcherEngine.PoolPositions([beta, alpha]);
        Assert.Equal((1, 3), ab[shared]);
        Assert.Equal(ab[shared], ba[shared]);
    }
}
```

(`PoolDefinition` is a record in `Core/ConCore/Targets/PoolDefinition.cs`; read it and construct the two pools the way it is declared — positional or object initializer — keeping `Name` and `MemberTargetIds` as above.)

- [ ] **Step 2: Run to verify it fails**

Expected: FAIL (compile: `PoolPositions` missing).

- [ ] **Step 3: Implement**

Extract the loop at ~177-181 into:

```csharp
    /// <summary>Pool membership index for phase staggering: targetId → (memberIndex, memberCount).
    /// A target in several pools takes the first pool by name, so its slot does not depend on
    /// the order pools were loaded in.</summary>
    public static Dictionary<Guid, (int Index, int Count)> PoolPositions(IEnumerable<PoolDefinition> pools)
    {
        var positions = new Dictionary<Guid, (int, int)>();
        foreach (var pool in pools.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
            for (int i = 0; i < pool.MemberTargetIds.Count; i++)
                positions.TryAdd(pool.MemberTargetIds[i], (i, pool.MemberTargetIds.Count));
        return positions;
    }
```

and call `var poolPosition = PoolPositions(_graph.Pools.Values);` (adjust the tuple element names where `pos.index`/`pos.count` are read).

- [ ] **Step 4: Run the tests**

Run: `dotnet test Tests/ConEngine.Tests --filter "FullyQualifiedName~PoolStaggerTests|FullyQualifiedName~Pool"`.
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Core/ConEngine/WatcherEngine.cs Tests/ConEngine.Tests/PoolStaggerTests.cs
git commit -m "fix(engine): a target in two pools takes its stagger slot from the first pool by name"
```

---

### Task 4: Hygiene — rollup resolution constants, the last sleep-then-assert, throttled repeat logging, the `dotnet ef` note

**Files:**
- Create: `Core/ConCore/Logging/LogThrottle.cs`, `Libraries/ConPersistence/Entities/RollupResolution.cs`, `Tests/ConEngine.Tests/LogThrottleTests.cs`
- Modify: `Libraries/ConPersistence/Entities/DataRows.cs:46`, `Libraries/ConPersistence/Queries/HistoryQueries.cs` (`ResolveResolution`), `Libraries/ConPersistence/Queries/WarmStartQueries.cs:22`, `Libraries/ConPersistence/Recording/RollupCollector.cs:122`, `Libraries/ConPersistence/Recording/RollupMerge.cs:31`, `Libraries/ConPersistence/Recording/ChannelRecorder.cs:26`, `Core/ConEngine/WatcherEngine.cs` (~line 310), `Tests/ConWatch.Tests/MasterEnableTests.cs:161`, `README.md` (Development section)
- Test: `Tests/ConEngine.Tests/LogThrottleTests.cs`, `Tests/ConEngine.Tests/PingPumpResilienceTests.cs`, `Tests/ConPersistence.Tests/RecorderCollectorTests.cs`

**Interfaces:**
- Produces (`ConCore.Logging`): `public sealed class LogThrottle(TimeSpan window) { public bool ShouldLog(DateTimeOffset now, out int suppressedSince); }` — thread-safe (a lock); the first call returns true with 0; later calls within `window` of the last logged instant return false and count; the first call at or after the window returns true with the count, then resets. `public static class RollupResolution { public const string Minute = "Minute", Hour = "Hour", Day = "Day"; }`.

- [ ] **Step 1: Write the failing tests**

`LogThrottleTests.cs`:

```csharp
using ConCore.Logging;

namespace ConEngine.Tests;

public class LogThrottleTests
{
    [Fact]
    public void Logs_the_first_then_once_per_window_with_the_suppressed_count() // Review Focus 5
    {
        var t0 = DateTimeOffset.Parse("2026-10-16T00:00:00Z");
        var throttle = new LogThrottle(TimeSpan.FromSeconds(60));
        Assert.True(throttle.ShouldLog(t0, out var n)); Assert.Equal(0, n);
        Assert.False(throttle.ShouldLog(t0.AddSeconds(1), out _));
        Assert.False(throttle.ShouldLog(t0.AddSeconds(59), out _));
        Assert.True(throttle.ShouldLog(t0.AddSeconds(60), out n)); Assert.Equal(2, n);
        Assert.False(throttle.ShouldLog(t0.AddSeconds(61), out _));
        Assert.True(throttle.ShouldLog(t0.AddSeconds(200), out n)); Assert.Equal(1, n);
    }
}
```

In `PingPumpResilienceTests.A_throwing_sample_subscriber_does_not_stop_the_pump`, replace `NullLoggerFactory.Instance` with a capturing logger factory (write a small `CapturingLoggerFactory` in `Tests/ConEngine.Tests/Fakes/` modelled on `RecorderCollectorTests.CapturingLogger`) and, after waiting for `delivered >= afterFirstThrow + 5`, assert that the number of captured Error entries containing "subscriber threw" is exactly 1 (several samples threw, one line logged).

In `RecorderCollectorTests`, find the test that fills the channel (the `dropped` path; `grep -n "channel full\|dropped" Tests/ConPersistence.Tests/RecorderCollectorTests.cs`) and assert the captured warnings containing "channel full" number exactly 1 after many drops; if no such test exists, add one that posts `capacity + 20` events to a recorder whose write is blocked (the `FailingOnceDbFactory`/blocked-db pattern in that file) and asserts one warning.

`MasterEnableTests.M2M_engine_toggle_rebuilds_exactly_once`: replace `await Task.Delay(500);` with `await ConfigApiTests.Eventually(() => Task.FromResult(host.RebuildCount >= before + 1)); Assert.Equal(before + 1, await SettledRebuildCountAsync(host));` and drop the separate `Assert.Equal(before + 1, host.RebuildCount)` (the settled helper already asserts stability for 500 ms without a bare sleep).

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test Tests/ConEngine.Tests --filter "FullyQualifiedName~LogThrottleTests|FullyQualifiedName~PingPumpResilienceTests"` and `dotnet test Tests/ConPersistence.Tests --filter "FullyQualifiedName~RecorderCollectorTests"`.
Expected: FAIL (compile on `LogThrottle`; the pump test sees many error lines).

- [ ] **Step 3: Implement**

`LogThrottle.cs`:

```csharp
namespace ConCore.Logging;

/// <summary>Keeps a repeating error line from flooding the log: the first occurrence is
/// logged, then at most one line per window, carrying how many were suppressed since.</summary>
public sealed class LogThrottle(TimeSpan window)
{
    private readonly Lock _lock = new();
    private DateTimeOffset? _lastLogged;
    private int _suppressed;

    public bool ShouldLog(DateTimeOffset now, out int suppressedSince)
    {
        lock (_lock)
        {
            if (_lastLogged is { } last && now - last < window)
            {
                _suppressed++;
                suppressedSince = 0;
                return false;
            }
            suppressedSince = _suppressed;
            _suppressed = 0;
            _lastLogged = now;
            return true;
        }
    }
}
```

`WatcherEngine`: a field `private readonly LogThrottle _subscriberErrors = new(TimeSpan.FromMinutes(1));` and the catch becomes `if (_subscriberErrors.ShouldLog(_timeProvider.GetUtcNow(), out var suppressed)) _log.LogError(ex, "A ping sample subscriber threw for {Target}; sample still processed ({Suppressed} more since the last line)", monitor.Target.Name, suppressed);`. `ChannelRecorder`: a static-per-type throttle is not possible in the field initializer (no `this`), so make the drop callback call a static helper `DropLogger.Log(log, typeof(TEvent).Name, dropped)` backed by a `static readonly LogThrottle` per closed generic type (`private static readonly LogThrottle DropThrottle = new(TimeSpan.FromMinutes(1));` — a static field in a generic class is per closed type, which is what we want), using `TimeProvider.System.GetUtcNow()`; message: "{Kind} channel full; dropped {Event} ({Suppressed} more drops since the last line)".

`RollupResolution.cs` with the three constants; replace the literals at the five sites (`ResolveResolution`'s switch returns the constants; `DataRows` default; `WarmStartQueries`; `RollupCollector`; `RollupMerge`'s default parameter).

README Development section, after the `dotnet run` block: "Migrations: `dotnet ef migrations add <Name> --project Libraries/ConPersistence` — the library carries its own design-time factory, so no startup project is needed (passing `--startup-project Programs/ConWatch` fails because the design package is private to the library)."

- [ ] **Step 4: Run the tests**

Run: the three test classes above, `dotnet test Tests/ConPersistence.Tests`, and `dotnet test Tests/ConWatch.Tests --filter "FullyQualifiedName~MasterEnableTests"` three times (the rewritten test must pass every time).
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Core/ConCore/Logging/LogThrottle.cs Core/ConEngine/WatcherEngine.cs Libraries/ConPersistence Tests/ConEngine.Tests Tests/ConPersistence.Tests Tests/ConWatch.Tests/MasterEnableTests.cs README.md
git commit -m "chore: rollup resolution constants, throttled repeat logging, the last sleep-then-assert, and the dotnet ef note"
```

---

### Task 5: The DNS polish minors

**Files:**
- Modify: `Libraries/ConNotify/MqttNotifier.cs`, `Libraries/ConInfrastructure/Dns/DohConfiguration.cs`, `README.md`
- Test: `Tests/ConNotify.Tests/BuildersTests.cs` (`DnsPublicationTests`), `Tests/ConEngine.Tests/DohDetectorTests.cs`

**Interfaces:**
- Produces: `public static bool MqttNotifier.ShouldPublishDns(DnsPublication? last, DnsPublication next, bool onlyIfChanged)`; `public static int? WindowsDohDetector.AsInt(object? value)`.

- [ ] **Step 1: Write the failing tests**

```csharp
    // DnsPublicationTests
    [Fact]
    public void The_tick_skips_only_an_unchanged_publication_and_a_full_publish_never_skips()
    {
        var a = new DnsPublication("online", "ON", "{\"state\":\"Ok\"}");
        Assert.True(MqttNotifier.ShouldPublishDns(null, a, onlyIfChanged: true));   // first of a session
        Assert.False(MqttNotifier.ShouldPublishDns(a, a with { }, onlyIfChanged: true));
        Assert.True(MqttNotifier.ShouldPublishDns(a, a with { State = "OFF" }, onlyIfChanged: true));
        Assert.True(MqttNotifier.ShouldPublishDns(a, a with { }, onlyIfChanged: false));
    }

    // DohDetectorTests
    [Fact]
    public void Registry_integers_are_read_from_DWORD_and_QWORD_and_nothing_else()
    {
        Assert.Equal(2, WindowsDohDetector.AsInt(2));
        Assert.Equal(3, WindowsDohDetector.AsInt(3L));
        Assert.Null(WindowsDohDetector.AsInt("2"));
        Assert.Null(WindowsDohDetector.AsInt(null));
    }
```

- [ ] **Step 2: Run to verify they fail**

Expected: FAIL (compile).

- [ ] **Step 3: Implement**

`MqttNotifier`: `public static bool ShouldPublishDns(DnsPublication? last, DnsPublication next, bool onlyIfChanged) => !onlyIfChanged || next != last;` used by `PublishDnsAsync`. Ticker: `var tickToken = tickCts.Token;` captured before `Task.Run` and used in the loop. `DohConfiguration`: `public static int? AsInt(object? value) => value switch { int i => i, long l => (int)l, _ => null };` used for both reads. README: re-wrap the spliced MQTT sentence to the paragraph's width and extend "Publishes happen only on state changes and reconnects" as the Global Constraints say.

- [ ] **Step 4: Run the tests**

Run: `dotnet test Tests/ConNotify.Tests` and `dotnet test Tests/ConEngine.Tests --filter "FullyQualifiedName~DohDetectorTests"`.
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Libraries/ConNotify/MqttNotifier.cs Libraries/ConInfrastructure/Dns/DohConfiguration.cs Tests/ConNotify.Tests/BuildersTests.cs Tests/ConEngine.Tests/DohDetectorTests.cs README.md
git commit -m "chore(dns): the tick's skip rule and the registry integer read are pure and tested; ticker token captured; README wording"
```

---

### Task 6: Whole-branch gate

- [ ] Run `dotnet test ConnectionWatcher.sln` (allow 10 minutes; re-run the known flake alone if it is the only failure), `npm --prefix Programs/ConWatch/ClientApp test -- --run`, `npm --prefix Programs/ConWatch/ClientApp run build`, and `dotnet build Programs/ConWatchTray`. Expected: 0 failures, no new warnings. Nothing to commit.

---

## Self-review notes

- **Coverage:** item 2 → Task 1; item 3 → Tasks 2, 3; item 4 → Task 4 (fixed-delay test, Minute constants, EF note, throttled logging); item 6 → Task 5. The `PingPumpResilienceTests` 200 ms item from the September list was already fixed (the test waits); the ledger will say so.
- **Type consistency:** `TransitionDto.Scope` becomes `string?` — every `with { Scope = TargetScope.X }` caller must change to the string (grep `Scope = TargetScope` under Programs and Libraries); `ScopeOf` is the only place the four strings are spelled; `KnownTargetIdsAsync` is used by three endpoints; `LogThrottle` is used by the engine and the recorder; `RollupResolution` by five sites.
- **Review Focus:** 1, 2 → Task 1; 3 → Task 2 (host render + browser check); 4 → Task 3; 5 → Task 4.
- **Deliberate choices:** `Unknown` is off both filters rather than kept on the internet side (a deleted anchor's rows stop counting as internet evidence; they remain under `all`); the glitch summary's allow-list keys on current Verdict targets plus Internet-class rows; the throttle window is one minute for both sites.
