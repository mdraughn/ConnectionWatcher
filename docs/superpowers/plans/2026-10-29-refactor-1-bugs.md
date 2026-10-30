# Refactor 1: drifted copies that are bugs — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix survey items B1–B9 by giving each duplicated piece one home, so the copies can no longer drift: the tree-scope rule, unique target names, the measurement-table list, the glitch wire shape, MQTT client options, the NIC walk, the client fetch helper, the config import catches, and the config-file read-modify-write.

**Architecture:** Ten tasks on branch `refactor-1-bugs`. Each one starts with a test that fails on today's code, then moves the copies onto one shared helper and fixes the bug. Every name this plan creates is listed in "Names later plans use", because R2–R5 build on them.

**Tech Stack:** .NET 10, ASP.NET Core minimal APIs + Razor Pages, EF Core 10.0.13 on SQLite, MQTTnet 5.2, ModelContextProtocol.AspNetCore, xUnit, vitest + happy-dom (ClientApp tests also load plain `wwwroot/lib` scripts).

**Spec:** `docs/superpowers/reviews/2026-10-22-refactor-survey.md`: section B, the parts of D6, D7, D10, D11, D13 and C7 named below, and Rulings 4 (`IsTree`).

## Global Constraints

- Never push. Commit only on branch `refactor-1-bugs`, cut from `master` at `1e0c820` or later.
- Conventional commit prefixes (`fix:`, `refactor:`, `test:`). No attribution or co-author lines. One commit per task.
- After every task, all of these hold:
  - `dotnet build ConnectionWatcher.sln -c Debug` adds no warnings. The pre-existing test-project warnings are CA1416 in `WindowsFolderLockdownTests`, CA2022 ×2 and CS8631 ×1.
  - `dotnet test ConnectionWatcher.sln -c Debug` reports 0 failed.
  - From `Programs/ConWatch/ClientApp`, `npx vitest run` passes.
- No new NuGet or npm packages and no new projects. No EF model change and no migration.
- Wire shapes change only where "Wire changes" says so, and a test that fails first pins each of those changes.
- UTC end to end:
  - Every timestamp is tagged UTC explicitly.
  - Never call `new DateTimeOffset(dt)` unless `dt.Kind` is known to be `Utc`.
  - Don't use `DateTime.Now`.
- Tests wait for conditions (`ConfigApiTests.Eventually`) and never sleep-then-assert. The CI runners have 2 cores and run Linux.
- Line numbers in the survey have drifted, so find code by symbol name. Run commands from the repo root in PowerShell.

## Review Focus

1. **A glitch row whose `StartUtc` has `DateTimeKind.Unspecified`** must serialize as the same UTC instant from REST and from MCP, with no shift by the server's zone. Test: Task 4, `From_reads_an_unspecified_kind_as_utc`.
2. **A save that fails because the server is unreachable** (the service is restarting) must show an error, and no button may be left stuck busy. Tests: Task 7, `apiFetch turns a network failure into a failed result` and `withBusy clears aria-busy when the call throws`.
3. **A wanted name that is taken in a different case** (`"router"` exists, `"Router"` wanted) must yield `"Router 2"`, never a name the NOCASE unique index rejects. Test: Task 2, `Taken_names_compare_case_insensitively`.
4. **A malformed config file** must never be lost silently. That includes when the requested change itself is rejected: a bad `--url` must leave the file untouched and not moved aside. Test: Task 10, `Update_whose_change_throws_touches_nothing`.
5. **A VPN adapter that reports `0.0.0.0` as its gateway** must not be offered as the router, and must not make its DNS servers count as system resolvers. Test: Task 6, `Usable_keeps_only_real_ipv4_gateways`.

---

## Decisions made at plan time

| Item | Survey said | Decided |
|---|---|---|
| B7 suffix | Three styles disagree | `"X 2"`, case-insensitive, at most 64 characters with the suffix included (the base is truncated so the suffix fits). Two of the three sites already use `"X 2"`. Resolver targets already named `"… (2)"` keep their names, because the reconciler matches managed targets by address, not name. |
| B7 limit | Only Discoveries truncates | Every site caps at `TargetNames.MaxLength` = 64. No validator enforces a length today; the others' names are short. |
| D6 scope | Measurement list ×6 incl. snapshot import | The snapshot importer is left alone. It deletes exactly the three tables a `Snapshot` carries (transitions, rollups, glitches). Clearing model warnings there would be a behavior change nobody asked for. |
| D6 shape | One helper | `MeasurementTables` owns three things: the clear-all list, the "transitions before X, keeping each subject's latest" delete, and the "ended model warnings before X" delete. `RetentionPass` and `DataPruner` share the last two. That sharing is how B2 is fixed. |
| B3 shape | One glitch wire shape | `GlitchDto` is the REST shape plus nothing more. MCP gains `id` and `targetId`, and its times become `DateTimeOffset` (`+00:00`). REST does not change. |
| Glitch class magic | `Class == 1` ×6 | Queries use `(int)GlitchClass.Internet` (ConPersistence already references ConEngine). Display text comes from `GlitchDto.ClassName(int)`. |
| B5 tunnels | Gateway walk doesn't skip tunnels | One rule for all three walks: Up, not Loopback, not Tunnel; gateways IPv4 and not `0.0.0.0`. System-resolver discovery now also skips Tunnel interfaces. |
| B6 | Network failure throws | `apiFetch` never throws for a network failure. It resolves `{ ok: false, status: 0, body: { errors: [...] } }`, so every existing `showErrors(list, result)` call shows a message. `site-dns.js`'s try/catch around it becomes dead and is removed. |
| B8 take-over | Catch too narrow | Found worse: the existing `when (ex.InnerException is InvalidOperationException)` catch also fires after the config committed (a duplicate history row throws IOE from EF tracking). It then answers 400 "this node is unchanged", which is false. Take-over now mirrors clone: `!ConfigCommitted` gives 500 `import-error`, and `ConfigCommitted` gives 500 `import-incomplete`. |
| B9 API | `ConfigFile.Update` | `Update(path, Func<ConWatchConfig?, ConWatchConfig> change, out string? warning)`. The change receives `null` when there was no usable file, because service install runs legacy detection only then. The order is: read, change, move a malformed file aside, write. A change that throws therefore touches nothing. |
| B9 tray | `TraySettings` has its own copy | Left for R3 (D11). It decides "corrupt" at load time and caches it, which is a different shape. |

## Wire changes (release-note lines)

- MCP `get_glitches`: rows gain `id` and `targetId`. `startUtc`/`endUtc` are written as `…+00:00` instead of `…Z`. (Task 4)
- `POST /api/v1/admin/prune`: the response gains `modelWarnings` (count deleted). Ended model warnings before the cutoff are now deleted, as the nightly pass and Clear History already do. (Task 3)
- `GET /api/v1/admin/stats`: `tables` gains `glitches` and `discoveries`. (Task 3)
- `POST /api/v1/config/import` (and clone/take-over import): config with names that differ only in case is a 400 validation error, not a 500. (Task 8)
- `POST /api/v1/setup/takeover`: a history-phase import failure is 500 `import-incomplete`, and a config-phase failure that is not a validation error is 500 `import-error`. Both say the source has been disabled. (Task 9)
- New system-resolver targets that collide on name are suffixed `"X 2"`, not `"X (2)"`. (Task 2)
- `conwatch service install` and `conwatch tray install --url`: a config file that cannot be opened now fails with ConfigInvalid instead of being overwritten, and a malformed one is kept as `.corrupt` with a warning (Task 10).
- The Settings page's prune result also reports the model warnings removed (final review).

## Names later plans use

| Name | File | Task |
|---|---|---|
| `TargetScopeExtensions.IsTree(this TargetScope)` | `Core/ConCore/Targets/TargetScope.cs` | 1 |
| `TargetNames.Unique(string, IEnumerable<string>)`, `TargetNames.MaxLength` | `Core/ConCore/Targets/TargetNames.cs` | 2 |
| `MeasurementTables` | `Libraries/ConPersistence/Retention/MeasurementTables.cs` | 3 |
| `ScopeTags`, `GlitchDto` | `Libraries/ConPersistence/Queries/` | 4 |
| `MqttConnection` | `Libraries/ConNotify/MqttConnection.cs` | 5 |
| `HostInterfaces`, `HostInterface`, `HostAddress` | `Libraries/ConInfrastructure/Net/HostInterfaces.cs` | 6 |
| `apiFetch` (never throws), `withBusy` | `Programs/ConWatch/wwwroot/lib/site-config.js` | 7 |
| `ConfigFile.Update` | `Libraries/ConConfig/ConfigFile.cs` | 10 |

---

### Task 1: B1 — the Pools editor offers only tree targets (`IsTree`)

**Files:**
- Modify: `Core/ConCore/Targets/TargetScope.cs` (add the extension class below the enum)
- Modify: `Programs/ConWatch/Pages/Config/Pools.cshtml` (the three `t.Scope != TargetScope.Local` filters: parent select, member list, `allTargets` JSON)
- Test: `Tests/ConEngine.Tests/TargetScopeTests.cs` (new; TreeWalk's tests live in this project too)
- Test: `Tests/ConWatch.Tests/PoolsPageTests.cs` (new)

**Interfaces:**
- Produces: `public static class TargetScopeExtensions { public static bool IsTree(this TargetScope scope); }`. It is true only for `Verdict`, and is documented as "a member of the reachability tree: may have a parent, be a parent, be a pool parent or member". R2 and R4 use it.

- [ ] **Step 1: Write the failing tests**

```csharp
// TargetScopeTests
[Theory]
[InlineData(TargetScope.Verdict, true)]
[InlineData(TargetScope.Local, false)]
[InlineData(TargetScope.Resolver, false)]
public void Only_verdict_targets_are_tree_members(TargetScope scope, bool expected) =>
    Assert.Equal(expected, scope.IsTree());

// PoolsPageTests (pattern: DnsPageTests.SeedWithResolverAsync)
[Fact]
public async Task The_pools_editor_offers_only_tree_targets()
{
    await ConfigApiTests.SeedHomeAsync(_client);
    _factory.Resolvers.Set("10.0.0.53");
    await _factory.Services.GetRequiredService<SystemResolverService>().ReconcileNowAsync(CancellationToken.None);
    var html = await _client.GetStringAsync("/Config/Pools");
    Assert.Contains("Router", html);                         // a tree target is offered
    Assert.DoesNotContain("Your resolver 10.0.0.53", html);  // a Resolver is not (the validator rejects it)
}
```

- [ ] **Step 2: Run them and see the failures**

Run: `dotnet test Tests/ConEngine.Tests --filter TargetScopeTests` (expected: a compile error, `IsTree` is not defined). Then run `dotnet test Tests/ConWatch.Tests --filter PoolsPageTests` (expected: FAIL, the resolver name is in the page).

- [ ] **Step 3: Add `IsTree` and use it at the three Pools.cshtml filters** (`Where(t => t.Scope.IsTree())`)

- [ ] **Step 4: Run both filters again** (expected: PASS)

- [ ] **Step 5: Commit**: `fix(config): the pools editor offers only tree targets (a Resolver pool member failed validation on save)`

---

### Task 2: B7 — one unique-name rule

**Files:**
- Create: `Core/ConCore/Targets/TargetNames.cs`
- Modify:
  - `Programs/ConWatch/SetupService.cs`: delete `Unique(string, Func<string,bool>)`, and pass the doc's target names.
  - `Programs/ConWatch/Pages/Config/Discoveries.cshtml.cs`: `UniqueName(string)` becomes a one-line call over `_targetNames`.
  - `Programs/ConWatch/Dns/SystemResolverReconciler.cs`: delete `UniqueName(string, List<TargetDto>)`.
- Test: `Tests/ConEngine.Tests/TargetNamesTests.cs` (new). Existing tests: `SetupApiTests`, `SystemResolverReconcilerTests`, `DiscoveriesPageTests`.

**Interfaces:**
- Produces:
  - `public static class TargetNames`.
  - `public const int MaxLength = 64`.
  - `public static string Unique(string wanted, IEnumerable<string> taken)`. It trims `wanted`. It returns `wanted` if no taken name equals it case-insensitively. Otherwise it returns the first free `"{base} {n}"` for n = 2, 3, …. The base is truncated so the whole result is at most `MaxLength`. `wanted` itself is also truncated to `MaxLength`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void A_free_name_is_kept_trimmed() =>
    Assert.Equal("Router", TargetNames.Unique("  Router ", ["Modem"]));
[Fact] public void Taken_names_compare_case_insensitively() =>
    Assert.Equal("Router 2", TargetNames.Unique("Router", ["router"]));
[Fact] public void The_first_free_number_wins() =>
    Assert.Equal("Router 4", TargetNames.Unique("Router", ["Router", "Router 2", "ROUTER 3"]));
[Fact] public void The_result_never_exceeds_the_limit_even_with_a_suffix()
{
    var longName = new string('a', 70);
    Assert.Equal(new string('a', 64), TargetNames.Unique(longName, []));
    var suffixed = TargetNames.Unique(longName, [new string('a', 64)]);
    Assert.Equal(new string('a', 62) + " 2", suffixed);
}
```

Also add to `SystemResolverReconcilerTests` one test pinning the new suffix:

```csharp
[Fact] public void A_resolver_whose_name_is_taken_gets_a_numeric_suffix()
// existing target named "Your resolver 10.0.0.53" (not system-managed, different address);
// the reconciler adds the managed one as "Your resolver 10.0.0.53 2"
```

(Build it with the file's existing plan-builder helpers.)

- [ ] **Step 2: Run them** (expected: a compile error, then the reconciler test FAILS with `"… (2)"`)

- [ ] **Step 3: Implement `TargetNames` and replace the three private copies**

- [ ] **Step 4: Run `dotnet test Tests/ConEngine.Tests` and `dotnet test Tests/ConWatch.Tests`** (expected: PASS)

- [ ] **Step 5: Commit**: `fix(targets): one unique-name rule (case-insensitive, "X 2", 64 max) for setup, discoveries and system resolvers`

---

### Task 3: B2 + D6 — `MeasurementTables`

**Files:**
- Create: `Libraries/ConPersistence/Retention/MeasurementTables.cs`
- Modify:
  - `Libraries/ConPersistence/Retention/DataPruner.cs`: use the two deletes, and add `ModelWarnings` to `PruneResult`.
  - `Libraries/ConPersistence/Retention/RetentionPass.cs`: the transition and model-warning prune use the same two deletes, and the log lines stay.
  - `Programs/ConWatch/Api/AdminEndpoints.cs`: clear-history and factory-reset call `ClearAllAsync` inside their existing transactions. Stats gain `Glitches` and `Discoveries`. The prune log line adds the warnings count.
- Test: `Tests/ConPersistence.Tests/DataPrunerTests.cs`, `Tests/ConWatch.Tests/AdminApiTests.cs`

**Interfaces:**
- Produces:
  - `public static class MeasurementTables`, with:
    - `static Task ClearAllAsync(ConWatchDbContext db, CancellationToken ct = default)`: transitions, rollups, model warnings, glitches. The caller owns the transaction.
    - `static Task<int> DeleteTransitionsBeforeAsync(ConWatchDbContext db, DateTime beforeUtc, CancellationToken ct = default)`: keeps each subject's latest (`HistoryQueries.LatestTransitionIdsPerSubject`).
    - `static Task<int> DeleteEndedModelWarningsBeforeAsync(ConWatchDbContext db, DateTime beforeUtc, CancellationToken ct = default)`: `EndUtc != null && EndUtc < beforeUtc`.
  - `PruneResult(int Transitions, int Rollups, int Glitches, int ModelWarnings, long FileSizeBefore, long FileSizeAfter)`: the new field goes before the sizes.

- [ ] **Step 1: Write the failing tests**

```csharp
// DataPrunerTests
[Fact]
public async Task Prunes_ended_model_warnings_before_the_date_and_keeps_open_and_later_ones()
// seed: one warning ended before the cutoff, one ended after, one open (EndUtc null) that started before
// result.ModelWarnings == 1; the two others remain

// AdminApiTests
[Fact]
public async Task Stats_count_glitches_and_discoveries()
// seed one GlitchRow and one DiscoveryRow through the db factory;
// stats.tables.glitches == 1 and stats.tables.discoveries == 1
```

- [ ] **Step 2: Run them** (expected: a compile error on `result.ModelWarnings`, and the stats test FAILS with a missing property)

- [ ] **Step 3: Implement `MeasurementTables` and move the five sites onto it**

- [ ] **Step 4: Run `dotnet test Tests/ConPersistence.Tests` and `dotnet test Tests/ConWatch.Tests --filter "AdminApiTests|AdminEraseTests"`** (expected: PASS, including the existing retention, clear and reset tests)

- [ ] **Step 5: Commit**: `fix(persistence): one measurement-table list; prune-before-date also removes ended model warnings, stats count glitches and discoveries`

---

### Task 4: B3 + D7 — `ScopeTags` and `GlitchDto`

**Files:**
- Create: `Libraries/ConPersistence/Queries/ScopeTags.cs`, `Libraries/ConPersistence/Queries/GlitchDto.cs`
- Modify:
  - `Libraries/ConPersistence/Queries/HistoryQueries.cs`: delete `LocalTargetIdsAsync`, `ResolverTargetIdsAsync`, `KnownTargetIdsAsync` and `ScopeOf`. Their only callers are the four sites below. In the glitch scope query, `g.Class == 1` becomes `(int)GlitchClass.Internet`.
  - `Libraries/ConPersistence/Queries/TransitionDto.cs`: `From(TransitionRow, ISet<Guid>, ISet<Guid>, ISet<Guid>)` becomes `From(TransitionRow r, ScopeTags tags)`.
  - `Programs/ConWatch/Api/HistoryEndpoints.cs`, `Programs/ConWatch/Api/GlitchEndpoints.cs`, and `Programs/ConWatch/Mcp/ConWatchMcpTools.cs` (`get_transitions`, `get_glitches`).
  - `Libraries/ConPersistence/Export/CsvExport.cs` and `Libraries/ConPersistence/Report/IspReportBuilder.cs`: the remaining `Class == 1` sites.
- Test: `Tests/ConPersistence.Tests/GlitchDtoTests.cs` (new), `Tests/ConWatch.Tests/McpEndpointTests.cs`. Existing: `GlitchApiTests`, and the history scope tests.

**Interfaces:**
- Produces:
  - `public sealed class ScopeTags`, with:
    - `static Task<ScopeTags> LoadAsync(ConWatchDbContext db)`: one query, `Targets.Select(t => new { t.Id, t.Scope })`.
    - `string Of(Guid id, string subjectKind)`: today's `ScopeOf` rule, unchanged: Local, then Resolver, then (`subjectKind != "Target"` or a known id) gives `"Verdict"`, else `"Unknown"`.
  - `public sealed record GlitchDto(long Id, Guid TargetId, string TargetName, string Class, DateTimeOffset StartUtc, DateTimeOffset EndUtc, int LostCount, int SampleCount, string? Detail, string Scope)`, with:
    - `static GlitchDto From(GlitchRow g, ScopeTags tags)`. The times are built with `DateTime.SpecifyKind(x, DateTimeKind.Utc)`, the same way `TransitionDto.From` tags its timestamp.
    - `static string ClassName(int cls)`: `"Internet"` for `(int)GlitchClass.Internet`, otherwise `"Target"`. The scope kind passed to `Of` is `"Verdict"` for Internet and `"Target"` otherwise, as today.

- [ ] **Step 1: Write the failing tests**

```csharp
// GlitchDtoTests
[Fact]
public void From_reads_an_unspecified_kind_as_utc()
{
    var start = new DateTime(2026, 9, 4, 3, 34, 0, DateTimeKind.Unspecified);
    var dto = GlitchDto.From(new GlitchRow { Id = 7, TargetId = Guid.NewGuid(), TargetName = "Google",
        Class = 0, StartUtc = start, EndUtc = start.AddSeconds(9) }, ScopeTagsFor());
    Assert.Equal(TimeSpan.Zero, dto.StartUtc.Offset);
    Assert.Equal(start.Ticks, dto.StartUtc.UtcTicks);
}
```

(`ScopeTagsFor` is a small helper in the test that builds tags through `LoadAsync` over the project's in-memory test database, with nothing configured.)

```csharp
// McpEndpointTests
[Fact]
public async Task get_glitches_rows_have_the_REST_shape()
// seed one GlitchRow (as GlitchApiTests.SeedAsync does); call the tool and GET /api/v1/glitches;
// the MCP row's property names equal the REST item's property names (as sets),
// and its startUtc string equals the REST one (both end in "+00:00")
```

- [ ] **Step 2: Run them** (expected: a compile error, then the MCP test FAILS on missing `id`/`targetId` and a `Z` suffix)

- [ ] **Step 3: Implement `ScopeTags` and `GlitchDto`, and move the four sites and the `Class == 1` sites onto them**

- [ ] **Step 4: Run `dotnet test Tests/ConPersistence.Tests` and `dotnet test Tests/ConWatch.Tests --filter "Glitch|Mcp|History|Export|Report"`** (expected: PASS)

- [ ] **Step 5: Commit**: `fix(api): one glitch wire shape for REST and MCP; scope tags loaded in one place`

---

### Task 5: B4 — one MQTT client options builder

**Files:**
- Create: `Libraries/ConNotify/MqttConnection.cs`
- Modify:
  - `Libraries/ConNotify/MqttTester.cs`: use the builder. Its outer 7 s cancel stays.
  - `Libraries/ConNotify/MqttNotifier.cs` (`RunConnectedSessionAsync`): the builder, then the three `WithWill*` calls.
- Test: `Tests/ConNotify.Tests/MqttConnectionTests.cs` (new)

**Interfaces:**
- Produces:
  - `public static class MqttConnection`, with:
    - `public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5)`.
    - `public static MqttClientOptionsBuilder Options(string host, int port, string? username, string? password)`: `WithTcpServer`, `WithTimeout(ConnectTimeout)`, and `WithCredentials(username, password ?? "")` only when the username is non-empty.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public void Options_carry_the_connect_timeout_and_credentials_only_with_a_user()
{
    var withUser = MqttConnection.Options("broker", 1883, "u", "p").Build();
    Assert.Equal(TimeSpan.FromSeconds(5), withUser.Timeout);
    Assert.NotNull(withUser.Credentials);
    Assert.Null(MqttConnection.Options("broker", 1883, null, null).Build().Credentials);
}
```

- [ ] **Step 2: Run it** (`dotnet test Tests/ConNotify.Tests --filter MqttConnectionTests`, expected: a compile error)

- [ ] **Step 3: Implement it, and use it in both places**

- [ ] **Step 4: Run `dotnet test Tests/ConNotify.Tests`** (expected: PASS)

- [ ] **Step 5: Commit**: `fix(mqtt): the notifier connects with the same 5 s timeout as the test button`

---

### Task 6: B5 + D10 (NIC walk) — `HostInterfaces`

**Files:**
- Create: `Libraries/ConInfrastructure/Net/HostInterfaces.cs` (namespace `ConInfrastructure.Net`; this starts the `Net/` folder that R2 extends)
- Modify:
  - `Libraries/ConInfrastructure/Discovery/GatewayDiscovery.cs`: `DefaultRouteGateways()` becomes `HostInterfaces.Read().SelectMany(i => i.Gateways)`.
  - `Libraries/ConInfrastructure/Discovery/LanScanner.cs`: `SystemLocalNetworks.Read()` maps `Read()` to `LocalNetwork`.
  - `Libraries/ConInfrastructure/Dns/SystemResolverSource.cs`: `WindowsResolvers()` becomes `Read().Where(i => i.Gateways.Count > 0).SelectMany(i => i.DnsServers)`, keeping its comment about VPN adapters.
- Test: `Tests/ConEngine.Tests/HostInterfacesTests.cs` (new; ConInfrastructure already grants `InternalsVisibleTo("ConEngine.Tests")`)

**Interfaces:**
- Produces:
  - `public sealed record HostAddress(IPAddress Address, int PrefixLength)`.
  - `public sealed record HostInterface(string Name, NetworkInterfaceType Type, OperationalStatus Status, IReadOnlyList<HostAddress> UnicastV4, IReadOnlyList<IPAddress> Gateways, IReadOnlyList<IPAddress> DnsServers)`.
  - `public static class HostInterfaces`, with:
    - `public static IReadOnlyList<HostInterface> Read()`: maps `NetworkInterface.GetAllNetworkInterfaces()` and applies `Usable`. It returns empty on `NetworkInformationException`.
    - `internal static IReadOnlyList<HostInterface> Usable(IEnumerable<HostInterface> all)`: keeps Up, non-Loopback, non-Tunnel interfaces. In each kept one, `Gateways` is filtered to IPv4 addresses that are not `IPAddress.Any`. An interface left with no gateway is still kept, because the LAN scan needs it.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public void Usable_drops_down_loopback_and_tunnel_interfaces()
// four interfaces: Up Ethernet, Down Ethernet, Up Loopback, Up Tunnel → only the first remains

[Fact]
public void Usable_keeps_only_real_ipv4_gateways()
// one Up Ethernet with gateways [0.0.0.0, fe80::1, 192.168.1.1] → Gateways == [192.168.1.1];
// one Up Ethernet with only [0.0.0.0] → kept, Gateways empty
```

- [ ] **Step 2: Run them** (`dotnet test Tests/ConEngine.Tests --filter HostInterfacesTests`, expected: a compile error)

- [ ] **Step 3: Implement `HostInterfaces`, and move the three walks onto it**

- [ ] **Step 4: Run `dotnet test Tests/ConEngine.Tests --filter "HostInterfaces|GatewayDiscovery|LanScanner|SystemResolverSource"`** (expected: PASS)

- [ ] **Step 5: Commit**: `fix(net): one NIC walk; the router search no longer offers a 0.0.0.0 gateway or a tunnel, and survives a NIC query failure`

---

### Task 7: B6 + C7 (fetch guard) — `apiFetch` never throws, `withBusy`

**Files:**
- Modify:
  - `Programs/ConWatch/wwwroot/lib/site-config.js`: `apiFetch` catches the `fetch` rejection. Add `withBusy`.
  - `Programs/ConWatch/wwwroot/lib/site-settings.js`: the prune, engine-toggle and take-over buttons use `withBusy`.
  - `Programs/ConWatch/wwwroot/lib/site-setup.js`: the WAN-probe and apply buttons use `withBusy`.
  - `Programs/ConWatch/wwwroot/lib/site-dns.js`: remove the now-dead try/catch around `apiFetch`, but keep the `finally` that re-enables the switch.
- Test: `Programs/ConWatch/ClientApp/src/lib/siteConfig.test.ts` (new; it loads the real file as `siteShell.test.ts` does)

**Interfaces:**
- Produces:
  - `apiFetch(method, url, body)`: on a network failure it resolves `{ ok: false, status: 0, body: { errors: ["Could not reach the server (" + error.message + ")."] } }`. Otherwise it is unchanged.
  - `async function withBusy(el, fn)`: sets `aria-busy="true"`, awaits `fn()`, removes the attribute in `finally`, and returns `fn`'s result.

- [ ] **Step 1: Write the failing tests**

```ts
const source = readFileSync(resolve(__dirname, "../../../wwwroot/lib/site-config.js"), "utf-8");
const load = () => new Function(`${source}\nreturn { apiFetch, withBusy };`)();

it("apiFetch turns a network failure into a failed result", async () => {
  vi.stubGlobal("fetch", vi.fn(async () => { throw new TypeError("Failed to fetch"); }));
  const result = await load().apiFetch("PUT", "/api/v1/settings", {});
  expect(result.ok).toBe(false);
  expect(result.status).toBe(0);
  expect(result.body.errors[0]).toContain("Could not reach the server");
});

it("withBusy clears aria-busy when the call throws", async () => {
  const button = document.createElement("button");
  await expect(load().withBusy(button, async () => { throw new Error("boom"); })).rejects.toThrow("boom");
  expect(button.hasAttribute("aria-busy")).toBe(false);
});
```

- [ ] **Step 2: Run them** (from `Programs/ConWatch/ClientApp`: `npx vitest run src/lib/siteConfig.test.ts`, expected: the first test FAILS by rejecting, and the second fails with `withBusy is not defined`)

- [ ] **Step 3: Implement both, and move the five busy pairs and `site-dns.js` onto them**

- [ ] **Step 4: Run `npx vitest run`** (expected: PASS). Run `dotnet test Tests/ConWatch.Tests --filter PageSmokeTests` (expected: PASS; the pages still render the scripts).

- [ ] **Step 5: Commit**: `fix(web): a request that cannot reach the server shows an error instead of leaving a button busy`

---

### Task 8: B8 + D13 (ConfigService) — import catches the uniqueness violation

**Files:**
- Modify: `Libraries/ConPersistence/Config/ConfigService.cs`
- Test: `Tests/ConPersistence.Tests/ConfigServiceTests.cs`

**Interfaces:**
- Produces:
  - `private ConfigResult UniquenessFailure(DbUpdateException ex, string what)`: logs at Debug, then returns `ConfigResult.Fail(ConfigErrorKind.Validation, "Change violates a uniqueness constraint (duplicate name or address).")`. `what` goes only into the log line.
  - `ImportAsync`, `EditDocumentAsync` and `MutateAsync` all catch `DbUpdateException when SqliteErrors.IsUniqueViolation(ex)` and return it.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public async Task Import_with_names_differing_only_in_case_is_a_validation_failure()
// a valid exported document plus a second profile whose Name differs only in case from the first;
// result.Success == false, result.Kind == ConfigErrorKind.Validation; the stored config is unchanged
```

- [ ] **Step 2: Run it** (`dotnet test Tests/ConPersistence.Tests --filter ConfigServiceTests`, expected: FAIL with an unhandled `DbUpdateException`). If the validator already rejects case-colliding profile names, use two pools whose names differ only in case instead, and record which one was used in the commit message.

- [ ] **Step 3: Add the helper and the catch in `ImportAsync`, and route the two existing catches through the helper**

- [ ] **Step 4: Run `dotnet test Tests/ConPersistence.Tests`** (expected: PASS)

- [ ] **Step 5: Commit**: `fix(config): an import whose names collide by case is a 400, not a 500`

---

### Task 9: B8 — take-over import failures say what state the node is in

**Files:**
- Modify: `Programs/ConWatch/Clone/CloneService.cs` (the import `try` in the take-over method)
- Test: `Tests/ConWatch.Tests/CloneTests.cs`

**Interfaces:**
- Consumes: `SnapshotImportException.ConfigCommitted`, and `CloneOutcome.Fail(int, string, string)`.
- Produces: the take-over catch order:
  1. `InvalidOperationException` → 400 `import-failed` (unchanged).
  2. `SnapshotImportException when (!ex.ConfigCommitted && ex.InnerException is InvalidOperationException inner)` → 400 `import-failed` with `inner.Message`. This is unchanged except for the added `!ConfigCommitted`.
  3. `SnapshotImportException when (!ex.ConfigCommitted)` → 500 `import-error`, `ex.Message + " The source has been disabled; this node is unchanged."`, logged at Error.
  4. `SnapshotImportException` → 500 `import-incomplete`, `ex.Message + " The source has been disabled. This node holds the source's config but is left disabled; take over again, or re-enable the source from its Settings page."`, logged at Error.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public async Task Takeover_whose_history_cannot_be_written_says_the_source_is_disabled()
// target built with MakeTarget(s => s.AddHttpClient(CloneService.HttpClientName)
//   .ConfigurePrimaryHttpMessageHandler(() => new FixedSnapshotHandler(json))),
// where json is a valid snapshot (built as FailedCloneAsync builds its own) carrying
// two transitions with the same Id; POST /api/v1/setup/takeover
// → 500, body.code == "import-incomplete", errors[0] contains "source has been disabled"
```

- [ ] **Step 2: Run it** (`dotnet test Tests/ConWatch.Tests --filter Takeover_whose_history`, expected: FAIL with 400 `import-failed` "this node is unchanged")

- [ ] **Step 3: Rewrite the catch block as above**

- [ ] **Step 4: Run `dotnet test Tests/ConWatch.Tests --filter "CloneTests|CloneWithout"`** (expected: PASS)

- [ ] **Step 5: Commit**: `fix(clone): a take-over whose history import fails no longer claims the node is unchanged`

---

### Task 10: B9 + D11 (config RMW) — `ConfigFile.Update`

**Files:**
- Modify: `Libraries/ConConfig/ConfigFile.cs`
- Modify:
  - `Programs/ConWatchCli/Commands/ConfigCommands.cs` (`Apply`): its read/move/warn block becomes the CLI helper.
  - `Programs/ConWatchCli/Commands/ServiceCommands.cs` (install): the read and the compute go into the change function.
  - `Programs/ConWatchCli/Commands/TrayCommands.cs` (`install --url`).
- Create: `Programs/ConWatchCli/Services/CliConfig.cs`
- Test: `Tests/ConConfig.Tests/ConfigFileTests.cs`, `Tests/ConWatchCli.Tests/ServiceCommandTests.cs`, `Tests/ConWatchCli.Tests/TrayCommandTests.cs`

**Interfaces:**
- Produces:
  - `public static ConWatchConfig Update(string path, Func<ConWatchConfig?, ConWatchConfig> change, out string? warning)` on `ConfigFile`. It works in this order:
    1. `TryRead(path, out error, out unreadable)`.
    2. If the file is unreadable, it throws `IOException($"{path} could not be read: {error}")`.
    3. It calls `change(read)`. `read` is null when the file is missing or malformed.
    4. If the file was malformed, it moves the file to `path + ".corrupt"` (overwrite) and sets `warning = $"{path} was not valid JSON; moved to {path}.corrupt and replaced"`.
    5. It runs `WriteAtomic`, then returns what it wrote.
  - `internal static class CliConfig { public static ConWatchConfig Update(CliServices s, string path, Func<ConWatchConfig?, ConWatchConfig> change); }`. It turns the `IOException` into `ConfigException`, and writes `warning: {warning}` to `s.Err`.

- [ ] **Step 1: Write the failing tests**

```csharp
// ConfigFileTests
[Fact] public void Update_on_a_missing_file_passes_null_and_writes()
[Fact] public void Update_moves_a_malformed_file_aside_and_writes_the_change()
// "{ not json" → change gets null; .corrupt holds "{ not json"; warning mentions ".corrupt"; the file holds the change
[Fact] public void Update_whose_change_throws_touches_nothing()
// malformed file; change throws → the same exception escapes; the file still holds "{ not json"; no .corrupt
[Fact] public void Update_on_an_unreadable_file_throws_and_writes_nothing()
// hold the file open with FileShare.None (enforced on Linux by .NET's advisory lock) → IOException

// ServiceCommandTests
[Fact] public async Task Install_over_a_malformed_machine_file_keeps_it_as_corrupt()
// File.WriteAllText(h.Paths.MachineFile, "{ not json"); run "service", "install";
// Code 0; h.Paths.MachineFile + ".corrupt" holds "{ not json"; Err contains "moved to"

// TrayCommandTests
[Fact] public async Task Tray_install_url_over_a_malformed_user_file_keeps_it_as_corrupt()
```

- [ ] **Step 2: Run them** (expected: a compile error, then the two CLI tests FAIL with no `.corrupt`)

- [ ] **Step 3: Implement `ConfigFile.Update` and `CliConfig.Update`, and move the three CLI sites onto them**

- [ ] **Step 4: Run `dotnet test Tests/ConConfig.Tests` and `dotnet test Tests/ConWatchCli.Tests`** (expected: PASS, including `ConfigCommandTests` unchanged)

- [ ] **Step 5: Commit**: `fix(cli): service install and tray install --url keep a malformed config as .corrupt instead of overwriting it`

---

## Finish

- [ ] Run the full gate: build, `dotnet test ConnectionWatcher.sln -c Debug`, and `npx vitest run`.
- [ ] Do a browser check of Task 7 through the built-in browser pane, with the app run from `.claude/launch.json`. On the Settings → Data page, stop the server, press Prune, and confirm the error shows and the button is not busy. The pane never fires dialog `close`, so judge dialog flows by DOM state after a wait.
- [ ] Hand off with superpowers:finishing-a-development-branch. Merge to `master` only on Mark's word, and never push.
