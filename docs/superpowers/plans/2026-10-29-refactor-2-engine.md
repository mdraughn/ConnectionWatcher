# Refactor 2: engine and core dedupes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Carry out survey items D1, D2 (with ruling 1), D3, D5 (with ruling 2), D9, the rest of D10 and D12. Each one gets a single home, so the engine, the host and the libraries stop restating the same rule.

**Architecture:** Seven groups, in order.
- A: named state groups (`HostStateExtensions`, `VerdictStateExtensions`).
- B: one tree walk and one trunk rule. `TreeWalk.TrunkOf` and `TargetGraph.AncestorIds/DescendantIds/TrunkIds` replace every hand-rolled walk. Ruling 1 (a nested anchor is never trunk) becomes true everywhere.
- C: one ISP rule (`IspRule`) shared by the verdict and `ConfigHealth`, plus one anchor-pool query.
- D: `OrderedOutbox<T>` for the four publish-outside-the-lock copies. Rx `SerialDisposable`/`CompositeDisposable` for observer and coordinator teardown.
- E: `DnsOutcome` words and one parse of the DNS result line.
- F: `ProbePolicy.IsRfc1918` and `ConInfrastructure/Net/` (`IcmpEcho`, `ReverseDns`, `MacAddress`).
- G: one source for ping-profile defaults and one for target-role defaults.

Most tasks change no behavior and are pinned by tests that already exist. The few that do change behavior have a test that fails first, and each is listed under "Behavior changes".

**Tech Stack:** .NET 10, System.Reactive 6.1.0 (already referenced by ConEngine and flowing to ConNotify, ConPersistence and ConWatch), EF Core 10.0.13 on SQLite, xUnit, Microsoft.Extensions.TimeProvider.Testing.

**Spec:** `docs/superpowers/reviews/2026-10-22-refactor-survey.md`, items D1, D2, D3, D5, D9, D10 and D12, and Rulings 1 and 2. Precise locations are in the reviewer reports in this session's scratchpad (`survey-1.md` for core, `survey-2.md` for persistence, `survey-4.md` for infra). Those reports are not needed to execute this plan.

## Global Constraints

- Never push. Work on branch `refactor-2-engine`, cut from `master` after plan R1 (`2026-10-29-refactor-1-bugs.md`) is merged. Nothing in this plan merges into `master`.
- One commit per task. Use conventional prefixes (`refactor:`, `fix:`, `test:`) and no attribution or co-author lines.
- After every task:
  - `dotnet build ConnectionWatcher.sln -c Debug` adds no warning. The only pre-existing warnings are CA1416 in `WindowsFolderLockdownTests`, CA2022 ×2 and CS8631 ×1, all in test projects.
  - `dotnet test ConnectionWatcher.sln -c Debug` has 0 failed.
  - From `Programs/ConWatch/ClientApp`, `npx vitest run` passes. This plan touches no client code.
- No new NuGet or npm packages and no new projects. No EF model change or migration. No new `InternalsVisibleTo`: ConInfrastructure → ConEngine.Tests and ConWatch → ConWatch.Tests already exist and are enough.
- UTC end to end. Tag every timestamp UTC explicitly. Never call `new DateTimeOffset(dt)` on a value whose `Kind` is not known to be UTC, and never use `DateTime.Now`.
- Tests wait for conditions and never sleep-then-assert (the CI runners have 2 cores). Every wait has a timeout, after which the test fails.
- Run commands from the repo root in PowerShell. Locate code by symbol: line numbers in the survey have drifted.
- Names that R1 produces and this plan may use: `ConCore.Targets.TargetScopeExtensions.IsTree`, `ConCore.Targets.TargetNames.Unique`, `ConInfrastructure.Net.HostInterfaces`, and the `Libraries/ConInfrastructure/Net/` folder.
- If a test fails that the task did not touch, rerun the project once. The same test failing twice is a real failure.

## Review Focus

1. **A disabled anchor's parent must not become trunk.** Suspicion would sweep that parent and the verdict could blame it, even though no monitored anchor sits behind it. `TrunkIds` takes the monitored anchors only. Test: Task 2, `Trunk_comes_only_from_the_anchors_given`.
2. **A parent cycle in a graph that skipped validation must end.** `TargetGraph.FromOptions` does not validate, and every walk used to loop on a cycle. Test: Task 2, `Walks_end_on_a_parent_cycle`.
3. **Operator tags differing only in case or padding are one operator.** `" comcast"` and `"Comcast"` must be the same ISP in the verdict and in `ConfigHealth`. Test: Task 5, `OperatorKey_ignores_case_and_padding`. The existing `ConfigHealthTests.Every_anchor_tagged_as_the_isp_warns_unprovable` already uses `" comcast"`.
4. **A DNS message with a parenthesised group that is not at the end.** For `"WrongAnswer: 10.0.0.1 (x) more"` there is no name, and the detail is the whole text after `": "`. The two old parsers disagreed here. Test: Task 9, `A_group_that_does_not_end_the_message_is_not_a_name`.
5. **The profile dialog under a non-English server culture.** It must offer `0.95`, not `0,95`; the browser rejects `0,95` in a `type=number` field and the field comes up empty. Test: Task 12, `Dialog_defaults_are_invariant_whatever_the_culture`.

---

## Decisions made at plan time

| Item | Survey said | Found / decided |
|---|---|---|
| D1 names | `IsAnswering`/`IsUndetermined`/`IsDownOrUnreachable`/`IsHealthy` (one report), `IsFailed`/`IsAnswering` (another) | The names are `IsAnswering`, `IsUndetermined`, `IsFailed` and `IsHealthy`. They go in a new `Core/ConEngine/Tracking/HostStateExtensions.cs` rather than `HostStatus.cs`, so each type has its own file (see O1). `TopologyCoordinator.Answering`, the verdict's private `Answering`/`Undetermined` and `WebhookFilter`'s local functions are deleted. Groups used only once are left inline: `PingDiscipline`'s "troubled" set, `ModelCheck`'s `Down or Faltering`, `DnsSummary`'s degraded set and the topology burst trigger. |
| D1 verdict groups | `IsReachable()`, `Severity()` | Only `IsReachable()` = `not (LinkDown or Unknown)`, which has two users (the verdict's `Seed`, for `_wasReachable`, and `WatcherEngine`'s model-check lambda). `Map` stays private: it has one user. `Severity()` has no caller and is dropped. The tray (`TrayStatus`) and `StateMapping.InternetPayload` match wire strings. ConInfrastructure does not reference ConEngine, so they stay as they are. |
| D2 graph API | `AncestorIds(id)`, `DescendantIds(id)` | Both take an optional `Func<Guid,bool> include` filter. The walk goes through excluded nodes, the way the old walks went through unmonitored hops. A third method, `TrunkIds(anchorIds, include)`, carries ruling 1. `GetDescendants` is removed; its only other user is a test. All three are cycle-safe through `TreeWalk`, so they are capped at `TreeWalk.MaxDepth` (32 nodes). The old engine walks had no cap. A chain deeper than 31 ancestors now loses its root-most hops; no real network is that deep. |
| D2 verdict hop order | — | The verdict blames the shallowest losing hop. A hop's depth is now `graph.AncestorIds(id).Count`, which equals its index in the old root-first chain. Ties between equal-depth hops keep anchor order. `TrunkIds` lists nodes in first-seen order walking each anchor upward, and `OrderBy` is stable, so ties resolve exactly as before. `VerdictBlameOrderTests` pins this. |
| Ruling 1, `TrunkChains` | "a nested anchor ... still sits in the path chain of the anchor below it" | `TopologyNames.TrunkNames` never lists an anchor. `TrunkChains`, the Targets-page pipeline drawing, keeps a nested anchor where it sits, because that is the path. The doc comment changes from "their union is TrunkNames" to "their union, minus the anchors, is TrunkNames". |
| Ruling 1, pool-member filtering | Survey listed "filter pool members to monitored ones ×3" | Left as it is. Each is one LINQ line over a different collection, and a helper would not make any of them clearer. |
| D3 home | `IspRule.CountedAnchors` + `OperatorTag.Normalize` | One static class, `ConCore.Targets.IspRule`, with `OperatorKey` (trimmed and lower-cased; null when blank) and `CountedAnchors`. `ConfigMapping.NormalizeOperator` (trim only) is storage normalization, not the ISP comparison, so it stays. |
| D3 drift | ConfigHealth counts disabled anchors and hops | `ConfigHealth` now judges what the engine monitors: enabled targets only. That covers the ISP set, the anchors that count, and the anchor count in `anchors-below-threshold` and `anchors-below-recommended`. This is a behavior change, pinned by failing-first tests and listed below. |
| D3 anchor-pool lookup | `ConfigStore.LoadAnchorPoolAsync(db, settings)` | A static query `ConPersistence.Queries.TopologyQueries.AnchorPoolAsync(db, poolName)`. It is AsNoTracking, includes members, and returns null for a blank name. Both `ConfigHealth` and `IspReportBuilder` read the setting themselves (ConfigHealth needs the name for its two "no pool" warnings), so the query does not take the settings store. Pool names are `NOCASE` in SQLite, so `==` already ignores case. The hard-coded threshold fallback `2` becomes `SettingsDefaults.WeatherThreshold`, which is also 2 today. |
| Ruling 2, outbox | `OrderedOutbox<T>` with a mode | `OrderedOutbox<T>(Action<T> publish, OutboxMode mode)` goes in `ConEngine.Events`. It does not own the Subject: `TargetMonitor` queues `Action`s, some of which log and some of which don't, so it uses `OrderedOutbox<Action>(a => a(), OutboxMode.Wait)`. |
| D5 Attach/Detach | `SubscriptionSlot` / `EngineSubscription` helper | No new type. Rx's `SerialDisposable` already swaps and disposes the previous subscription atomically, and it disposes anything assigned after its own `Dispose`. `MetricsCounters` assigns a `CompositeDisposable` of its two subscriptions. Observers keep their own `_lock` where it guards other fields (`_engine`, `_engineStarted`). |
| D5 teardown | `CompositeDisposable` ×4 + engine | This covers the four coordinators (Pool, Topology, ModelCheck, Verdict). `WatcherEngine.StopAsync` is left alone: its list is cleared and refilled by a restart, and `CompositeDisposable.Clear()` would buy nothing. |
| D9 window | `DnsSummary.Judged(recent)` + one constant pair | `DnsSummary.JudgedWindow = 10` (renamed from `FilteredWindow`) is the one window, and `DnsSummary.Judged(recent)` is the one cut. `ModelCheckCoordinator.ResolverWindow` is deleted. `ResolverRaise` (3 wrong answers) and `FilteredRaise` (3 filtered) are different thresholds and both stay. `DesignConstantsTests` asserts `JudgedWindow`. |
| D9 parse | `Parse` also returns the detail; `DnsResult` carries it | `DnsResultLog.Parse` returns `(Outcome, Name, Detail)`. `Parse`'s rule wins: the name is the last ` (…)` group only when the message ends with `)`, and the detail is the text after the first `": "` of what remains, or `""`. `DnsResult` gains an init property `Detail` (default `""`) so the five positional parameters, and every test constructing one, stay the same. `ModelCheckCoordinator.DetailOf` is deleted. |
| D10 RFC 1918 | `AddressRanges` static (core report) vs `ProbePolicy.IsRfc1918` (infra report) | `ProbePolicy.IsRfc1918(IPAddress)` and `ProbePolicy.IsPrivate(IPAddress)` go in `ProbeSpec.cs`. `IsPrivate(string)` and `IsPrivateOrCarrier(IPAddress)` call them, so the address no longer round-trips through a string. `LanScanner.IsRfc1918` is deleted. |
| D10 ICMP / rDNS exception policy | — | `IcmpEcho.TryPingAsync` returns null for no reply and for any exception except cancellation, which propagates. It uses the `WaitAsync` fix from `PingTransceiverIcmp`. `ReverseDns.LookupAsync` returns null on timeout, `SocketException` or `ArgumentException`, propagates cancellation, and never leaves its timeout timer running. This changes three callers; see below. `LanScanner.IcmpPing`/`ReverseDns` remain as one-line adapters, because they are the delegates passed to `LanScanner` in two production wirings. |
| D12 profile defaults | `PingProfileConfig` reads a default `PingProfile` | Yes. `Resolver` also drops the three values it restates: `BurstMaxDuration` 60, `DownThreshold` 0.10 and `RecoveringThreshold` 0.25. `Profiles.cshtml` renders the dialog values from a `PingProfileConfig` instance, formatted invariant. |
| D12 role defaults | `TargetRoleDefaults` | A record, `ConCore.Targets.TargetRole(string Profile, TargetScope Scope, bool Critical = false, string? Probe = null)`, with static roles `Router`, `PathHop`, `Anchor`, `LocalDevice` and `Resolver`. Default *names* ("Router", "ISP edge") stay at their one call site each. |

## Behavior changes (release-note lines)

1. **Ruling 1.** An anchor nested under another anchor is no longer trunk. Its loss is internet-side in the Internet glitch wording ("trunk clean (upstream)" unless a real trunk hop lost too). The dashboard and History `trunkNames` no longer list it, so its glitches show as anchor marks. It still triggers and joins corroboration sweeps. (Task 3)
2. **Config warnings judge what the engine monitors.** A disabled path hop's operator tag no longer makes anchors "ISP-owned", a disabled anchor no longer counts toward `anchors-below-threshold` or `anchors-below-recommended`, and a disabled anchor's tag no longer decides `anchors-all-isp`. (Task 6)
3. **The vendor lookup accepts what the neighbor table accepts.** `OuiTable.Describe` takes un-padded octets (`"a:b:c:d:e:f"`) and returns null for all-zero, broadcast and multicast MACs. It no longer accepts bare 12-hex MACs ("a4bbcc001122"); its only production caller passes normalized neighbor-table MACs. (Task 11)
4. **Cancellation propagates out of one-shot probes.** It now propagates from the router search's ICMP probe, the UPnP test ping and the traceroute's reverse-DNS lookup; these used to return "no answer". The router search's probe now returns "no answer" for any non-cancellation exception, where before such an exception failed the whole search. (Task 11)
5. **One depth cap for every walk.** Parent walks in `ConfigHealth`'s drift check and in `WarmStartSeeder` are capped at `TreeWalk.MaxDepth` (32), up from 20. The engine's walks were uncapped and are now capped at 32. (Tasks 2 and 4)

---

## File structure

| Path | Responsibility after this plan | Tasks |
|---|---|---|
| `Core/ConEngine/Tracking/HostStateExtensions.cs` | `IsAnswering` / `IsUndetermined` / `IsFailed` / `IsHealthy` (new) | 1 |
| `Core/ConEngine/Topology/VerdictStateExtensions.cs` | `IsReachable` (new) | 1 |
| `Core/ConCore/Targets/TreeWalk.cs` | + `TrunkOf` | 2, 3 |
| `Core/ConEngine/Topology/TargetGraph.cs` | + `AncestorIds`, `DescendantIds`, `TrunkIds`; − `GetDescendants` | 2, 3 |
| `Core/ConCore/Targets/IspRule.cs` | Operator key + counted-anchor rule (new) | 5, 6 |
| `Libraries/ConPersistence/Queries/TopologyQueries.cs` | `AnchorPoolAsync` (new) | 6 |
| `Core/ConEngine/Events/OrderedOutbox.cs` | Queue-under-lock / publish-after outbox, Wait or Skip (new) | 7 |
| `Core/ConCore/Dns/DnsOutcome.cs` | DNS outcome words + `IsJudged` (new) | 9 |
| `Libraries/ConInfrastructure/Net/IcmpEcho.cs`, `ReverseDns.cs`, `MacAddress.cs` | One-shot echo, capped rDNS, MAC normalization (new) | 11 |
| `Core/ConCore/Targets/TargetRole.cs` | Profile/scope/critical/probe per role (new) | 13 |
| `Tests/ConEngine.Tests/StateGroupTests.cs`, `IspRuleTests.cs`, `OrderedOutboxTests.cs`, `NetHelpersTests.cs`, `TargetRoleTests.cs` | New tests | 1, 5, 7, 11, 13 |

---

## Group A: named state groups

### Task 1: D1, HostState and VerdictState groups get names

**Files:**
- Create: `Core/ConEngine/Tracking/HostStateExtensions.cs`, `Core/ConEngine/Topology/VerdictStateExtensions.cs`, `Tests/ConEngine.Tests/StateGroupTests.cs`
- Modify (every site below; each keeps its exact semantics):
  - `Core/ConEngine/Dns/DnsSummary.cs`: `Compute` (undetermined, failed, answering).
  - `Core/ConEngine/Scheduling/PingDiscipline.cs`: `_state is not (Responding or Starting)` → `!_state.IsHealthy()`.
  - `Core/ConEngine/Topology/ModelCheckCoordinator.cs`: `Evaluate` (`state is not (Down or Unreachable)`, the witness `Answering`) and `CheckResolvers` (answering, down, upstream down, answering resolvers).
  - `Core/ConEngine/Topology/PoolCoordinator.cs`: `Recalculate` (all undetermined, all failed).
  - `Core/ConEngine/Topology/SuspicionCoordinator.cs`: `priorHealthy`, and the episode hand-off `stateNow is not (Responding or Starting)`.
  - `Core/ConEngine/Topology/TopologyCoordinator.cs`: delete the public static `Answering` and use `IsAnswering()`.
  - `Core/ConEngine/Topology/VerdictCoordinator.cs`: delete the private `Answering`/`Undetermined`. Every `is HostState.Down or HostState.Unreachable` becomes `IsFailed()`. The `Seed` line becomes `_wasReachable = verdict.IsReachable();`.
  - `Core/ConEngine/WatcherEngine.cs`: the model-check `internetReachable` lambda becomes `Verdict is null || Verdict.CurrentVerdict.IsReachable()`.
  - `Libraries/ConNotify/Builders.cs`: `StateMapping.TargetPayload` uses `IsFailed()`.
  - `Libraries/ConNotify/WebhookFilter.cs`: `IsCriticalEvent` drops its local `Failed`/`Answering`.
  - `Programs/ConWatch/StatusSnapshotBuilder.cs`: `TopologyCoordinator.Answering(state)` → `state.IsAnswering()`.
  - `Programs/ConWatch/WarmStartSeeder.cs`: the four `Initializing or Starting` tests → `IsUndetermined()`.

**Interfaces:**
- Produces (namespace `ConEngine.Tracking`):
  ```csharp
  public static class HostStateExtensions
  {
      /// <summary>The state machine saw replies in its window: Responding, Faltering or Recovering.</summary>
      public static bool IsAnswering(this HostState state);
      /// <summary>No verdict on this target yet: Initializing or Starting.</summary>
      public static bool IsUndetermined(this HostState state);
      /// <summary>Down or Unreachable.</summary>
      public static bool IsFailed(this HostState state);
      /// <summary>No trouble seen: Responding, or Starting before any verdict.</summary>
      public static bool IsHealthy(this HostState state);
  }
  ```
- Produces (namespace `ConEngine.Topology`): `public static class VerdictStateExtensions { public static bool IsReachable(this VerdictState v); }`, true unless `LinkDown` or `Unknown`.

- [ ] **Step 1: Write the test that names every group**

```csharp
public class StateGroupTests
{
    [Theory]
    //          state                    answering undetermined failed healthy
    [InlineData(HostState.Initializing, false,    true,        false, false)]
    [InlineData(HostState.Starting,     false,    true,        false, true)]
    [InlineData(HostState.Responding,   true,     false,       false, true)]
    [InlineData(HostState.Faltering,    true,     false,       false, false)]
    [InlineData(HostState.Down,         false,    false,       true,  false)]
    [InlineData(HostState.Recovering,   true,     false,       false, false)]
    [InlineData(HostState.Unreachable,  false,    false,       true,  false)]
    public void Each_host_state_is_in_its_documented_groups(HostState s, bool answering, bool undetermined, bool failed, bool healthy)
    {
        Assert.Equal(answering, s.IsAnswering());
        Assert.Equal(undetermined, s.IsUndetermined());
        Assert.Equal(failed, s.IsFailed());
        Assert.Equal(healthy, s.IsHealthy());
    }

    [Theory]
    [InlineData(VerdictState.Unknown, false)]
    [InlineData(VerdictState.LinkDown, false)]
    [InlineData(VerdictState.Healthy, true)]
    [InlineData(VerdictState.SiteIncident, true)]
    [InlineData(VerdictState.InternetWeather, true)]
    [InlineData(VerdictState.LinkDegraded, true)]
    [InlineData(VerdictState.DnsDown, true)]
    public void Reachable_means_neither_LinkDown_nor_Unknown(VerdictState v, bool reachable) =>
        Assert.Equal(reachable, v.IsReachable());
}
```

- [ ] **Step 2: Run it and see it fail to compile**

Run: `dotnet test Tests/ConEngine.Tests -c Debug --filter "FullyQualifiedName~StateGroupTests"`
Expected: build error, `IsAnswering` not defined.

- [ ] **Step 3: Add the two extension classes, then replace every site in the Files list**

After the change, `Select-String -Path Core,Libraries,Programs -Recurse -Include *.cs -Pattern 'HostState.Down or HostState.Unreachable|HostState.Initializing or HostState.Starting|TopologyCoordinator.Answering'` finds nothing.

- [ ] **Step 4: Run the full suite**

Run: `dotnet test ConnectionWatcher.sln -c Debug`
Expected: 0 failed. The coordinator, notifier and seeder tests pin the behavior.

- [ ] **Step 5: Commit**

```powershell
git add -A; git commit -m "refactor(engine): name the HostState and VerdictState groups instead of restating them (D1)"
```

---

## Group B: one walk, one trunk

### Task 2: D2, ancestor, descendant and trunk walks live on the graph

**Files:**
- Modify: `Core/ConCore/Targets/TreeWalk.cs` (add `TrunkOf`), `Core/ConEngine/Topology/TargetGraph.cs`
- Modify: `TopologyCoordinator` (constructor), `ModelCheckCoordinator` (constructor `_beyond`), `VerdictCoordinator` (constructor: `_ancestors`, `_paths`, `_descendants`)
- Test: `Tests/ConEngine.Tests/TreeWalkTests.cs`, `Tests/ConEngine.Tests/TargetGraphTests.cs` (the `Descendants_are_transitive_and_exclude_self` test moves to `DescendantIds`)

**Interfaces:**
- Produces in `ConCore.Targets.TreeWalk`:
  ```csharp
  /// <summary>The trunk above these anchors: <see cref="AncestorsOf"/> minus the anchors themselves
  /// (ruling 2026-10-28: an anchor nested under another anchor is never trunk). First-seen order.</summary>
  public static List<T> TrunkOf<T>(IReadOnlyCollection<T> anchors, Func<T, T?> parentOf,
      IEqualityComparer<T>? comparer = null) where T : class
  ```
- Produces in `ConEngine.Topology.TargetGraph`:
  ```csharp
  /// <summary>The target's ancestors, root first, itself excluded; only those <paramref name="include"/>
  /// accepts (all when null), walking through the rest. Cycle-safe; capped at TreeWalk.MaxDepth.</summary>
  public IReadOnlyList<Guid> AncestorIds(Guid targetId, Func<Guid, bool>? include = null);
  /// <summary>Transitive descendants, breadth-first (nearest first), itself excluded; filtered like
  /// AncestorIds. Cycle-safe.</summary>
  public IReadOnlyList<Guid> DescendantIds(Guid targetId, Func<Guid, bool>? include = null);
  /// <summary>Every ancestor of any of <paramref name="anchorIds"/>, minus those anchors, first-seen
  /// order walking each anchor upward; filtered like AncestorIds. Callers pass the anchors they monitor.</summary>
  public IReadOnlyList<Guid> TrunkIds(IEnumerable<Guid> anchorIds, Func<Guid, bool>? include = null);
  ```
- Removes: `TargetGraph.GetDescendants`.

The verdict's replacement code, which fixes the hop depth and order (see Decisions):

```csharp
var anchorIds = _anchors.Select(a => a.Target.Id).ToList();
_ancestors = _graph.TrunkIds(anchorIds, monitors.ContainsKey)
    .Select(id => (monitors[id], _graph.AncestorIds(id).Count)).ToList();
foreach (var anchor in _anchors)
    _paths.Add((anchor, _graph.AncestorIds(anchor.Target.Id, monitors.ContainsKey).Select(id => monitors[id]).ToList()));
```

- [ ] **Step 1: Write the tests**

In `TreeWalkTests`:
```csharp
[Fact]
public void TrunkOf_is_every_anchor_ancestor_minus_the_anchors()
{
    // Router <- Google (anchor) <- Google DNS (anchor); Router <- Cloudflare (anchor)
    var map = new Dictionary<string, string> { ["Google"] = "Router", ["Google DNS"] = "Google", ["Cloudflare"] = "Router" };
    var anchors = new[] { new Node("Google"), new Node("Google DNS"), new Node("Cloudflare") };
    var trunk = TreeWalk.TrunkOf(anchors, Parents(map));
    Assert.Equal(["Router"], trunk.Select(n => n.Name));
}
```
(`Node` and `Parents` are the file's existing helpers. `Node` is a record, so value equality removes the anchors.)

In `TargetGraphTests`, using `Home()` (Router ← Modem ← Google/Cloudflare; Router ← TV):
```csharp
[Fact]
public void AncestorIds_are_root_first_and_filtered_walking_through_the_rest()
{
    var g = Home();
    var google = g.GetByName("Google").Id;
    Assert.Equal([g.GetByName("Router").Id, g.GetByName("Modem").Id], g.AncestorIds(google));
    var router = g.GetByName("Router").Id;
    Assert.Equal([router], g.AncestorIds(google, id => id == router)); // Modem skipped, walked through
}

[Fact]
public void DescendantIds_are_breadth_first()
{
    var g = Home();
    var names = g.DescendantIds(g.GetByName("Router").Id).Select(id => g.Targets[id].Name).ToList();
    Assert.Equal(["Modem", "TV"], names.Take(2).Order());   // depth 1 before depth 2
    Assert.Equal(["Cloudflare", "Google"], names.Skip(2).Order());
}

[Fact]
public void Trunk_comes_only_from_the_anchors_given()
{
    var g = Home();
    var trunk = g.TrunkIds([g.GetByName("Google").Id]).Select(id => g.Targets[id].Name);
    Assert.Equal(["Modem", "Router"], trunk);
    Assert.Empty(g.TrunkIds([g.GetByName("Router").Id])); // a root anchor has no trunk
}

[Fact]
public void Walks_end_on_a_parent_cycle()
{
    // FromOptions does not validate, so a cycle can reach the graph.
    var g = TargetGraph.FromOptions(new WatcherOptions
    {
        Targets =
        [
            new TargetConfig { Name = "A", Address = "10.0.0.1", Parent = "B" },
            new TargetConfig { Name = "B", Address = "10.0.0.2", Parent = "A" },
        ],
    });
    var a = g.GetByName("A").Id;
    Assert.Equal([g.GetByName("B").Id], g.AncestorIds(a));
    Assert.Equal([g.GetByName("B").Id], g.DescendantIds(a));
}
```

- [ ] **Step 2: Run them and see them fail to compile**

Run: `dotnet test Tests/ConEngine.Tests -c Debug --filter "FullyQualifiedName~TreeWalkTests|FullyQualifiedName~TargetGraphTests"`
Expected: build error, `TrunkOf` and `AncestorIds` not defined.

- [ ] **Step 3: Implement**

- `TrunkOf`: `AncestorsOf` filtered by a `HashSet<T>` of the anchors, using the same comparer.
- `AncestorIds`: `TreeWalk.ChainToRoot(Targets[id], t => t.ParentId is { } p && Targets.TryGetValue(p, out var parent) ? parent : null)`, minus the target, reversed, filtered.
- `DescendantIds`: a BFS over `_childrenByParent` with a visited set seeded with the start.
- `TrunkIds`: `TreeWalk.TrunkOf` over the anchor `Target`s, then ids, then filtered.
- Replace the walks in the three coordinators: Topology `_ancestors`/`_descendants`, ModelCheck `_beyond`, and the Verdict code above plus `_descendants` from `DescendantIds(hop, monitors.ContainsKey)`.
- Delete `GetDescendants`.
- Update the verdict constructor comment: "the trunk excludes every anchor (T4 M1, ruling 2026-10-28)".

- [ ] **Step 4: Run the engine tests**

Run: `dotnet test Tests/ConEngine.Tests -c Debug`
Expected: 0 failed. `VerdictBlameOrderTests`, `VerdictCoordinatorTests` (including `A_tagged_anchor_nested_under_another_anchor_does_not_make_its_operator_the_isp`), `TopologyCoordinatorTests` and `ModelCheckCoordinatorTests` pin the behavior.

- [ ] **Step 5: Commit**

```powershell
git add -A; git commit -m "refactor(engine): one cycle-safe ancestor, descendant and trunk walk on TargetGraph (D2)"
```

---

### Task 3: Ruling 1, a nested anchor is never trunk in suspicion or the topology names

**Files:**
- Modify: `Core/ConEngine/Topology/SuspicionCoordinator.cs` (constructor `_trunkIds`), `Programs/ConWatch/TopologyNames.cs` (`Compute`, `Names` doc)
- Test: `Tests/ConEngine.Tests/SuspicionCoordinatorTests.cs` (rewrite `A_nested_anchor_counts_as_trunk_for_sweeps`), `Tests/ConWatch.Tests/TopologyNamesTests.cs`

**Interfaces:**
- Consumes: `TargetGraph.TrunkIds`, `TreeWalk.TrunkOf` (Task 2).
- Produces: no new names. After this task, `TopologyNames.Names.TrunkNames` never contains an anchor name.

- [ ] **Step 1: Rewrite the suspicion test to the ruling**

Rename `A_nested_anchor_counts_as_trunk_for_sweeps` to `A_nested_anchor_sweeps_but_is_never_trunk`. Keep its rig and part (a) unchanged. Then:
- Replace its comment with: "Ruling 2026-10-28: the trunk is the anchors' ancestors minus the anchors. The outer anchor still triggers and joins sweeps, but its glitch is internet-side."
- Change the final assertion to `Assert.Contains("trunk clean (upstream)", internet.Detail);`
- Add a part (c), driven like part (b): a loss on `Router` overlapping the two anchors' losses still reads `"link/ISP"`.

- [ ] **Step 2: Add the topology-names test**

```csharp
[Fact]
public void A_nested_anchor_is_an_anchor_only()
{
    var doc = new ConfigDocument
    {
        Targets =
        [
            new() { Name = "Router", Address = "192.168.1.1" },
            new() { Name = "Google", Address = "8.8.8.8", Parent = "Router" },
            new() { Name = "Google DNS", Address = "8.8.4.4", Parent = "Google" },
            new() { Name = "Cloudflare", Address = "1.1.1.1", Parent = "Router" },
        ],
        Pools = [new() { Name = "Internet", Members = ["Google", "Google DNS", "Cloudflare"] }],
    };
    var names = TopologyNames.Compute(doc, "Internet", weatherThreshold: 2);
    Assert.Equal(["Router"], names.TrunkNames);
    Assert.Contains(names.TrunkChains, c => c.SequenceEqual(["Router", "Google"])); // still on the path
}
```

- [ ] **Step 3: Run both and see them fail**

Run: `dotnet test Tests/ConEngine.Tests -c Debug --filter "FullyQualifiedName~A_nested_anchor_sweeps_but_is_never_trunk"`, then `dotnet test Tests/ConWatch.Tests -c Debug --filter "FullyQualifiedName~A_nested_anchor_is_an_anchor_only"`
Expected: FAIL. The detail reads `link/ISP`, and `TrunkNames` is `["Router", "Google"]`.

- [ ] **Step 4: Implement**

- Suspicion: `_trunkIds = graph.TrunkIds(_anchorIds, monitors.ContainsKey).ToHashSet();`
- `TopologyNames.Compute`: `var trunk = TreeWalk.TrunkOf(members, ParentOf).Select(t => t.Name).ToList();`
- Update the `Names` record's doc: TrunkNames is "every ancestor of every anchor, minus the anchors", and TrunkChains' "union, minus the anchors, is TrunkNames".
- Update the class summary: "the trunk is the union of every anchor's ancestor chain, minus the anchors (rulings 2026-09-18 and 2026-10-28)".

- [ ] **Step 5: Run the suites**

Run: `dotnet test Tests/ConEngine.Tests -c Debug; dotnet test Tests/ConWatch.Tests -c Debug`
Expected: 0 failed.

- [ ] **Step 6: Commit**

```powershell
git add -A; git commit -m "fix(topology): a nested anchor is never trunk in suspicion or the dashboard roles (ruling 2026-10-28)"
```

---

### Task 4: D2, the stored-config walks use TreeWalk

**Files:**
- Modify: `Programs/ConWatch/WarmStartSeeder.cs` (`DropUnjustifiedUnreachable`), `Libraries/ConPersistence/Config/ConfigHealth.cs` (`PoolParentDriftAsync` chain building)

**Interfaces:**
- Consumes: `TreeWalk.ChainToRoot`.
- Produces: nothing new.

- [ ] **Step 1: Confirm the pins exist**

These tests must exist and pass before the edit:
- `WarmStartSeederTests`: `Unreachable_without_a_down_ancestor_is_dropped`, `Unreachable_behind_a_down_ancestor_is_kept`, `Unreachable_grandchild_is_kept_when_the_Down_ancestor_is_two_levels_up`.
- `ConfigHealthTests`: `Reparenting_members_without_the_pool_warns_drift`, `Unparented_pool_with_parented_members_warns_drift`.

Run: `dotnet test Tests/ConWatch.Tests -c Debug --filter "FullyQualifiedName~WarmStartSeederTests"; dotnet test Tests/ConPersistence.Tests -c Debug --filter "FullyQualifiedName~ConfigHealthTests"`
Expected: PASS.

- [ ] **Step 2: Replace both hand-rolled loops**

- Seeder: walk `TreeWalk.ChainToRoot(targetsById[id], c => c.Parent is { Length: > 0 } p && byName.TryGetValue(p, out var a) ? a.Config : null).Skip(1)`. The target is justified when any ancestor's seed is `Down`. `byName` maps name → `(Id, Config)`, as today.
- Drift: each member's chain is `TreeWalk.ChainToRoot(target, parentOf).Skip(1).Select(t => t.Id).ToList()` (nearest first, as today).
- Delete both `depth < 20` loops.

- [ ] **Step 3: Run the same tests**

Expected: PASS.

- [ ] **Step 4: Commit**

```powershell
git add -A; git commit -m "refactor: the warm-start and pool-drift walks use TreeWalk (D2)"
```

---

## Group C: one ISP rule

### Task 5: D3, the counted-anchor rule lives in ConCore and the verdict uses it

**Files:**
- Create: `Core/ConCore/Targets/IspRule.cs`, `Tests/ConEngine.Tests/IspRuleTests.cs`
- Modify: `Core/ConEngine/Topology/VerdictCoordinator.cs` (constructor ISP set and `_counted`; delete `Normalize`; `CountOperators` keys on `IspRule.OperatorKey`)

**Interfaces:**
- Produces (namespace `ConCore.Targets`):
  ```csharp
  public static class IspRule
  {
      /// <summary>The comparison key for an operator tag: trimmed, lower-cased; null when blank.</summary>
      public static string? OperatorKey(string? tag);

      /// <summary>The anchors whose answer proves the internet is up: those whose operator is not on the
      /// trunk (the ISP is whoever operates the trunk; untagged anchors are never the ISP's; with no tag on
      /// the trunk every anchor counts). When every anchor is the ISP's, all of them count and
      /// <paramref name="usingIspFallback"/> is true (spec 2026-10-09 §4.1 decision (a)); false with no anchors.</summary>
      public static List<T> CountedAnchors<T>(IReadOnlyList<T> anchors, IEnumerable<T> trunk,
          Func<T, string?> operatorOf, out bool usingIspFallback);
  }
  ```
- Consumes: `TargetGraph.TrunkIds` (Task 2) for the verdict's trunk.

- [ ] **Step 1: Write the tests**

```csharp
public class IspRuleTests
{
    private sealed record N(string Name, string? Op);

    [Theory]
    [InlineData(" Comcast ", "comcast")]
    [InlineData("COMCAST", "comcast")]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void OperatorKey_ignores_case_and_padding(string? tag, string? key) => Assert.Equal(key, IspRule.OperatorKey(tag));

    [Fact]
    public void Anchors_sharing_the_trunk_operator_do_not_count()
    {
        var counted = IspRule.CountedAnchors<N>([new N("A", " comcast"), new N("B", null), new N("C", "Google")],
            [new N("Modem", "Comcast")], n => n.Op, out var fallback);
        Assert.Equal(["B", "C"], counted.Select(n => n.Name));
        Assert.False(fallback);
    }

    [Fact]
    public void Every_anchor_the_isps_counts_them_all_as_a_fallback()
    {
        var counted = IspRule.CountedAnchors<N>([new N("A", "Comcast"), new N("B", "comcast")],
            [new N("Modem", "Comcast")], n => n.Op, out var fallback);
        Assert.Equal(2, counted.Count);
        Assert.True(fallback);
    }

    [Fact]
    public void No_trunk_tag_counts_every_anchor_without_fallback()
    {
        var counted = IspRule.CountedAnchors<N>([new N("A", "Comcast")], [new N("Modem", null)], n => n.Op, out var fallback);
        Assert.Single(counted);
        Assert.False(fallback);
    }

    [Fact]
    public void No_anchors_is_no_fallback()
    {
        Assert.Empty(IspRule.CountedAnchors<N>([], [new N("Modem", "Comcast")], n => n.Op, out var fallback));
        Assert.False(fallback);
    }
}
```

- [ ] **Step 2: Run them and see them fail to compile**

Run: `dotnet test Tests/ConEngine.Tests -c Debug --filter "FullyQualifiedName~IspRuleTests"`
Expected: build error.

- [ ] **Step 3: Implement `IspRule` and use it in the verdict**

In the constructor:
```csharp
_counted = IspRule.CountedAnchors(_anchors, _ancestors.Select(a => a.Monitor), m => m.Target.Operator, out var fallback);
UsingIspFallback = fallback;
```
`CountOperators` becomes `IspRule.OperatorKey(a.Target.Operator) is { } key ? "op:" + key : "id:" + a.Target.Id`. Delete the private `Normalize`.

- [ ] **Step 4: Run the engine suite**

Run: `dotnet test Tests/ConEngine.Tests -c Debug`
Expected: 0 failed. `VerdictCoordinatorTests` pins the ISP and fallback behavior and `ModelCheckCoordinatorTests` pins UnprovableUp.

- [ ] **Step 5: Commit**

```powershell
git add -A; git commit -m "refactor(verdict): the ISP/counted-anchor rule lives in ConCore.IspRule (D3)"
```

---

### Task 6: D3, ConfigHealth judges the engine's rule, and one anchor-pool query

**Files:**
- Create: `Libraries/ConPersistence/Queries/TopologyQueries.cs`
- Modify: `Libraries/ConPersistence/Config/ConfigHealth.cs`:
  - `EvaluateAsync` loads the pool via the query, loads targets once and passes them down.
  - `AllAnchorsIspOwnedAsync` becomes a sync `AllAnchorsIspOwned(pool, targets)` built on `TreeWalk.TrunkOf` + `IspRule`, enabled targets only.
  - The member count covers enabled members only.
  - The threshold fallback is `SettingsDefaults.WeatherThreshold`.
  - `PoolParentDriftAsync` takes the loaded targets.
- Modify: `Libraries/ConPersistence/Report/IspReportBuilder.cs` (`BuildTopologyAsync` uses the query).
- Test: `Tests/ConPersistence.Tests/ConfigHealthTests.cs`

**Interfaces:**
- Produces: `public static class TopologyQueries { public static Task<PoolRow?> AnchorPoolAsync(ConWatchDbContext db, string? poolName, CancellationToken ct = default); }` in namespace `ConPersistence.Queries`. It is AsNoTracking, `Include(p => p.Members)`, and returns null for a null or blank name.
- Consumes: `IspRule.CountedAnchors`, `IspRule.OperatorKey` (Task 5), `TreeWalk.TrunkOf` (Task 2).

- [ ] **Step 1: Write the failing tests**

These use the file's fixture: pool "Internet" = Google, Cloudflare, under Modem ← Router.
```csharp
[Fact]
public async Task A_disabled_isp_hop_does_not_make_the_anchors_isp_owned()
{
    await _service.UpsertTargetAsync((await Existing("Modem")) with { Operator = "Comcast", Enabled = false });
    await _service.UpsertTargetAsync((await Existing("Google")) with { Operator = "Comcast" });
    await _service.UpsertTargetAsync((await Existing("Cloudflare")) with { Operator = "Comcast" });
    Assert.DoesNotContain("anchors-all-isp", await Codes()); // the engine monitors no tagged hop
}

[Fact]
public async Task A_disabled_anchor_does_not_count_toward_the_threshold()
{
    await _service.UpsertTargetAsync((await Existing("Cloudflare")) with { Enabled = false });
    Assert.Contains("anchors-below-threshold", await AnchorCodes()); // one enabled anchor, threshold 2
}
```

- [ ] **Step 2: Run them and see them fail**

Run: `dotnet test Tests/ConPersistence.Tests -c Debug --filter "FullyQualifiedName~ConfigHealthTests"`
Expected: both new tests FAIL. The old code counts disabled targets.

If `UpsertTargetAsync` refuses to disable a target that is a pool member, stop and report: the test needs a different fixture, not a code workaround.

- [ ] **Step 3: Implement**

- The ISP warning is `usingIspFallback` from `IspRule.CountedAnchors(enabledAnchors, TreeWalk.TrunkOf(enabledAnchors, parentOf).Where(t => t.Enabled), t => t.Operator, out var fallback)`.
- `parentOf` walks every stored target, enabled or not, because the engine walks through disabled hops.
- `IspReportBuilder.BuildTopologyAsync` calls `TopologyQueries.AnchorPoolAsync(db, anchorPoolName)`.

- [ ] **Step 4: Run the persistence suite**

Run: `dotnet test Tests/ConPersistence.Tests -c Debug`
Expected: 0 failed. `IspReportBuilderTests` pins the report.

- [ ] **Step 5: Commit**

```powershell
git add -A; git commit -m "fix(health): config warnings judge enabled targets with the engine's ISP rule; one anchor-pool query (D3)"
```

---

## Group D: publish and teardown

### Task 7: D5 + ruling 2, one ordered outbox with Wait and Skip modes

**Files:**
- Create: `Core/ConEngine/Events/OrderedOutbox.cs`, `Tests/ConEngine.Tests/OrderedOutboxTests.cs`
- Modify:
  - `Tracking/TargetMonitor.cs` (`_outbox`, `_publishLock`, `DrainOutbox`), Wait mode.
  - `Topology/PoolCoordinator.cs`, Skip mode.
  - `Topology/VerdictCoordinator.cs`, Skip mode.
  - `Topology/ModelCheckCoordinator.cs`, Skip mode.

**Interfaces:**
- Produces (namespace `ConEngine.Events`):
  ```csharp
  public enum OutboxMode
  {
      /// <summary>Drain blocks until no other thread is publishing, so on return the caller's items are
      /// delivered. TargetMonitor: callers (and tests) rely on a transition being out when the call returns.</summary>
      Wait,
      /// <summary>Drain returns at once when another thread is publishing; that thread re-checks the queue
      /// after it lets go and delivers the item. Coordinators: they never wait inside another delivery.</summary>
      Skip,
  }

  /// <summary>Queue under the owner's lock (queue order is state order), publish after releasing it, one
  /// publisher at a time, never under the owner's lock. If publish throws, the exception propagates out of
  /// Drain and items still queued are delivered by the next Drain.</summary>
  public sealed class OrderedOutbox<T>(Action<T> publish, OutboxMode mode)
  {
      public void Enqueue(T item);
      public void Drain();
  }
  ```
- Each coordinator's publish delegate keeps its log line: `"Pool transition: {Transition}"`, `"Verdict: {Transition}"`, and the model-check `LogDebug`. `TargetMonitor` uses `new OrderedOutbox<Action>(a => a(), OutboxMode.Wait)`.

- [ ] **Step 1: Write the tests**

```csharp
public class OrderedOutboxTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public void Items_are_published_in_enqueue_order()
    {
        var seen = new List<int>();
        var box = new OrderedOutbox<int>(seen.Add, OutboxMode.Skip);
        box.Enqueue(1); box.Enqueue(2); box.Drain();
        Assert.Equal([1, 2], seen);
    }

    [Theory]
    [InlineData(OutboxMode.Wait)]
    [InlineData(OutboxMode.Skip)]
    public async Task An_item_queued_while_another_thread_publishes_is_delivered_after_it(OutboxMode mode)
    {
        using var inFirst = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var seen = new ConcurrentQueue<int>();
        var box = new OrderedOutbox<int>(i => { if (i == 1) { inFirst.Set(); release.Wait(Timeout); } seen.Enqueue(i); }, mode);

        box.Enqueue(1);
        var first = Task.Run(box.Drain);
        Assert.True(inFirst.Wait(Timeout));
        box.Enqueue(2);
        var second = Task.Run(() => { box.Drain(); return seen.Contains(2); });

        if (mode == OutboxMode.Skip)
            Assert.False(await second.WaitAsync(Timeout)); // returned at once; the first drainer owns item 2
        release.Set();
        await first.WaitAsync(Timeout);
        if (mode == OutboxMode.Wait)
            Assert.True(await second.WaitAsync(Timeout)); // returned only after item 2 was out
        Assert.Equal([1, 2], seen);
    }

    [Fact]
    public void A_throwing_publish_leaves_later_items_for_the_next_drain()
    {
        var seen = new List<int>();
        var box = new OrderedOutbox<int>(i => { if (i == 1) throw new InvalidOperationException(); seen.Add(i); }, OutboxMode.Skip);
        box.Enqueue(1); box.Enqueue(2);
        Assert.Throws<InvalidOperationException>(box.Drain);
        box.Drain();
        Assert.Equal([2], seen);
    }
}
```

- [ ] **Step 2: Run them and see them fail to compile**

Run: `dotnet test Tests/ConEngine.Tests -c Debug --filter "FullyQualifiedName~OrderedOutboxTests"`
Expected: build error.

- [ ] **Step 3: Implement and adopt**

The class holds a `ConcurrentQueue<T>` and a `Lock`.
- `Wait` mode is the monitor's current `if empty return; lock { while dequeue publish }`.
- `Skip` mode is the coordinators' current `while (!empty) { if (!TryEnter) return; try { … } finally { Exit } }`.
- Move the doc comments from the four `DrainOutbox` methods into the class.

Then delete the four owners' `_outbox`, `_publishLock` and `DrainOutbox`, and call `_outbox.Enqueue`/`_outbox.Drain()` where they did. In `TargetMonitor`'s class summary, `_publishLock` becomes "the outbox (Wait mode)".

- [ ] **Step 4: Run the engine suite**

Run: `dotnet test Tests/ConEngine.Tests -c Debug`
Expected: 0 failed. `TargetMonitorTests`, the coordinator tests and `ProbeThreadingTests` pin ordering and delivery.

- [ ] **Step 5: Commit**

```powershell
git add -A; git commit -m "refactor(engine): one OrderedOutbox for the four publish-outside-the-lock copies, Wait and Skip modes kept (D5, ruling 2)"
```

---

### Task 8: D5, observer subscriptions and coordinator teardown use Rx disposables

**Files:**
- Modify (Attach/Detach/Dispose, `IDisposable? _subscription` → `readonly SerialDisposable _subscription = new()`):
  - `Libraries/ConNotify/WebhookNotifier.cs`
  - `Libraries/ConNotify/MqttNotifier.cs`
  - `Libraries/ConPersistence/Recording/ChannelRecorder.cs`
  - `Libraries/ConPersistence/Recording/RollupCollector.cs`
  - `Libraries/ConPersistence/Warnings/ModelWarningLedger.cs` (only its `_subscription`; `_resync`/`_probeOnlyResync` stay)
  - `Programs/ConWatch/StatusBroadcaster.cs`
  - `Programs/ConWatch/Metrics/MetricsCounters.cs`: its list becomes one `SerialDisposable` assigned a `CompositeDisposable` of its two subscriptions. This also removes its duplicated dispose loop.
- Modify (teardown, `List<IDisposable> _subscriptions` → `readonly CompositeDisposable _subscriptions = new()`): `PoolCoordinator`, `TopologyCoordinator` (delete `_disposeLock`), `ModelCheckCoordinator`, `VerdictCoordinator`.

**Interfaces:**
- Consumes: `System.Reactive.Disposables.SerialDisposable` and `CompositeDisposable`.
- Produces: nothing new. Attach, Detach and Dispose keep their signatures and contracts:
  - Attach replaces any prior subscription.
  - Detach drops it.
  - Dispose is idempotent.
  - A coordinator's subject completes once, on the first Dispose.

- [ ] **Step 1: Confirm the pins**

These must pass before the edit:
- `ObserverDetachTests`.
- `MqttNotifierInboxTests`.
- The recorder and ledger tests under `Tests/ConPersistence.Tests`.
- The coordinator tests under `Tests/ConEngine.Tests`.
- For each coordinator, a test that disposes twice. Where one is missing, add `Dispose_twice_completes_the_stream_once` to that coordinator's test file:
  ```csharp
  var completions = 0;
  coordinator.TransitionSource.Subscribe(_ => { }, () => completions++); // ChangeSource for ModelCheck
  coordinator.Dispose(); coordinator.Dispose();
  Assert.Equal(1, completions);
  ```

Run: `dotnet test ConnectionWatcher.sln -c Debug`
Expected: PASS.

- [ ] **Step 2: Replace the bodies**

- Attach is `_subscription.Disposable = source.Subscribe(...)`, under the observer's existing lock where it has one.
- Detach is `_subscription.Disposable = null`.
- Dispose is `_subscription.Dispose()`.

In the coordinators:
- Add subscriptions with `_subscriptions.Add(...)`.
- Dispose checks `_subscriptions.IsDisposed` under the existing `_lock` and returns early when set. It then calls `_subscriptions.Dispose()` inside the lock, and `OnCompleted` outside it.
- `VerdictCoordinator.UseProbeOnlyVeto` keeps its `_disposed` check.

- [ ] **Step 3: Run the full suite**

Run: `dotnet test ConnectionWatcher.sln -c Debug`
Expected: 0 failed.

- [ ] **Step 4: Commit**

```powershell
git add -A; git commit -m "refactor: observer and coordinator subscriptions use Rx SerialDisposable/CompositeDisposable (D5)"
```

---

## Group E: DNS words

### Task 9: D9, DNS outcome words, one parse, one judged window

**Files:**
- Create: `Core/ConCore/Dns/DnsOutcome.cs`
- Modify:
  - `Core/ConEngine/Dns/DnsResultLog.cs`: `Parse` returns a triple, and `DnsResult` gains `Detail`.
  - `Core/ConEngine/Dns/DnsSummary.cs`: `JudgedWindow` and `Judged`.
  - `Core/ConEngine/Topology/ModelCheckCoordinator.cs`: delete `DetailOf` and `ResolverWindow`; use `DnsSummary.Judged` and `DnsOutcome`.
  - `Libraries/ConInfrastructure/Dns/DnsQueryTransceiver.cs`: `Classify` uses `DnsOutcome`.
  - `Programs/ConWatch/StatusSnapshotBuilder.cs`: `DnsOutcome.Filtered`.
- Test: `Tests/ConEngine.Tests/DnsResultLogTests.cs`, `ResolverNotesTests.cs` (the `DetailOf_…` theory moves to `Parse`), `DesignConstantsTests.cs`, `Tests/ConWatch.Tests/StatusResolverDetailTests.cs` (deconstruction).

**Interfaces:**
- Produces (namespace `ConCore.Dns`):
  ```csharp
  public static class DnsOutcome
  {
      public const string Verified = "Verified";
      public const string Answered = "Answered";
      public const string Filtered = "Filtered";
      public const string WrongAnswer = "WrongAnswer";
      /// <summary>A seeded-name outcome judged against its expected set (Answered is unjudged).</summary>
      public static bool IsJudged(string outcome);
  }
  ```
- Produces:
  - `DnsResultLog.Parse(string message) -> (string Outcome, string? Name, string Detail)`.
  - `DnsResult` adds `public string Detail { get; init; } = "";`, set by `Record`.
  - `DnsSummary.JudgedWindow = 10` (replaces `FilteredWindow`).
  - `DnsSummary.Judged(IReadOnlyList<DnsResult> recent) -> List<DnsResult>`: the last `JudgedWindow` judged results, oldest first.
- Removes: `ModelCheckCoordinator.DetailOf`, `ModelCheckCoordinator.ResolverWindow`, `DnsSummary.FilteredWindow`.

- [ ] **Step 1: Move and extend the parse tests**

In `DnsResultLogTests`, rewrite `Outcome_and_name_are_parsed_from_the_message` to deconstruct three values and assert the detail as well. Add these:
```csharp
[Theory]
[InlineData("Filtered: NXDOMAIN (dns.google)", "NXDOMAIN")]
[InlineData("WrongAnswer: 10.99.0.1, 10.99.0.2 (dns.google)", "10.99.0.1, 10.99.0.2")]
[InlineData("WrongAnswer: 10.99.0.1", "10.99.0.1")]
[InlineData("Filtered (dns.google)", "")]
[InlineData("WrongAnswer", "")]
public void Detail_is_the_text_between_the_outcome_and_the_name(string message, string detail) =>
    Assert.Equal(detail, DnsResultLog.Parse(message).Detail);

[Fact]
public void A_group_that_does_not_end_the_message_is_not_a_name()
{
    var (outcome, name, detail) = DnsResultLog.Parse("WrongAnswer: 10.0.0.1 (x) more");
    Assert.Equal("WrongAnswer", outcome);
    Assert.Null(name);
    Assert.Equal("10.0.0.1 (x) more", detail);
}
```
Delete `ResolverNotesTests.DetailOf_strips_the_outcome_and_the_name_without_ever_throwing`; its rows are now in the theory above. In `DesignConstantsTests`, replace the `ResolverWindow` line with `Assert.Equal(10, DnsSummary.JudgedWindow);`.

- [ ] **Step 2: Run them and see them fail to compile**

Run: `dotnet test Tests/ConEngine.Tests -c Debug --filter "FullyQualifiedName~DnsResultLogTests|FullyQualifiedName~DesignConstantsTests"`
Expected: build error. `Parse` still returns two values, and `JudgedWindow` is missing.

- [ ] **Step 3: Implement**

- `Parse` keeps its current name rule and adds the detail.
- `IsFiltered` and `CheckResolvers` both work over `DnsSummary.Judged(recent)`, so `judged.Last(r => r.Outcome == DnsOutcome.Filtered)` can no longer throw on a window mismatch.
- The ResolverLying and ResolverFiltered messages use `last.Detail`.
- Replace every outcome literal in the Files list with a `DnsOutcome` constant. Messages the transceiver builds stay byte-identical; `DnsQueryTransceiverTests` pins them.

- [ ] **Step 4: Run the suites**

Run: `dotnet test Tests/ConEngine.Tests -c Debug; dotnet test Tests/ConWatch.Tests -c Debug`
Expected: 0 failed. `DnsSummaryTests`, `ResolverNotesTests` and `DnsQueryTransceiverTests` pin the behavior.

- [ ] **Step 5: Commit**

```powershell
git add -A; git commit -m "refactor(dns): one set of outcome words, one parse with detail, one judged window (D9)"
```

---

## Group F: network helpers

### Task 10: D10, one RFC 1918 test on ProbePolicy

**Files:**
- Modify: `Core/ConCore/ProbeSpec.cs` (`ProbePolicy`), `Libraries/ConInfrastructure/Discovery/LanScanner.cs` (delete `IsRfc1918`; `ScanAsync`'s network filter uses `ProbePolicy.IsRfc1918`)
- Test: `Tests/ConEngine.Tests/ProbeSpecTests.cs` (the row-table test that calls `LanScanner.IsRfc1918` calls `ProbePolicy.IsRfc1918`)

**Interfaces:**
- Produces on `ConCore.ProbePolicy`:
  - `public static bool IsRfc1918(IPAddress ip)`: IPv4 10/8, 172.16/12, 192.168/16.
  - `public static bool IsPrivate(IPAddress ip)`: `IsRfc1918`, loopback, or 169.254/16.
- `IsPrivate(string)` and `IsPrivateOrCarrier(IPAddress)` call the `IPAddress` overloads.

- [ ] **Step 1: Point the existing table test at the new member**

The test that asserts `isPrivate`, `isPrivateOrCarrier` and `isRfc1918` per address is that test. Change only its third assertion.

Run: `dotnet test Tests/ConEngine.Tests -c Debug --filter "FullyQualifiedName~ProbeSpecTests"`
Expected: build error, `ProbePolicy.IsRfc1918` not defined.

- [ ] **Step 2: Implement, then delete `LanScanner.IsRfc1918`**

- [ ] **Step 3: Run the engine suite**

Run: `dotnet test Tests/ConEngine.Tests -c Debug`
Expected: 0 failed.

- [ ] **Step 4: Commit**

```powershell
git add -A; git commit -m "refactor: one RFC 1918 test on ProbePolicy; IsPrivate takes an IPAddress (D10)"
```

---

### Task 11: D10, one-shot ICMP, capped reverse DNS and MAC parsing in ConInfrastructure/Net

**Files:**
- Create in `Libraries/ConInfrastructure/Net/`: `IcmpEcho.cs`, `ReverseDns.cs`, `MacAddress.cs`, plus `Tests/ConEngine.Tests/NetHelpersTests.cs`.
- Modify:
  - `Discovery/GatewayDiscovery.cs`: production ctor probe `(a, ct) => IcmpEcho.TryPingAsync(a, TimeSpan.FromSeconds(1), ct)`; delete `IcmpProbeAsync`.
  - `Discovery/LanScanner.cs`: `IcmpPing`/`ReverseDns` become one-line adapters over the new helpers, with `PingTimeout` and 1 s.
  - `Discovery/UpnpWanAddressProbe.cs`: `TestPingAsync` uses `IcmpEcho` with 2 s.
  - `Traceroute/TracerouteRunner.cs`: delete `ReverseDnsAsync` and use `ReverseDns.LookupAsync(…, 1 s, ct)`.
  - `Discovery/NeighborTable.cs`: `TryNormalizeMac` moves out; callers use `MacAddress.TryNormalize`.
  - `Discovery/OuiTable.cs`: `Describe` normalizes through `MacAddress.TryNormalize`.

**Interfaces:**
- Produces (namespace `ConInfrastructure.Net`):
  ```csharp
  public static class IcmpEcho
  {
      /// <summary>One echo. Round-trip ms on a reply; null on no reply or any failure except cancellation,
      /// which throws promptly on every platform (the PingTransceiverIcmp WaitAsync fix).</summary>
      public static Task<long?> TryPingAsync(IPAddress address, TimeSpan timeout, CancellationToken ct);
  }
  public static class ReverseDns
  {
      /// <summary>The PTR name, or null on timeout, SocketException or ArgumentException. Cancellation throws.
      /// The timeout timer never outlives the call.</summary>
      public static Task<string?> LookupAsync(IPAddress address, TimeSpan timeout, CancellationToken ct);
      internal static Task<string?> LookupAsync(IPAddress address, TimeSpan timeout,
          Func<string, CancellationToken, Task<IPHostEntry>> resolve, CancellationToken ct);
  }
  public static class MacAddress
  {
      /// <summary>"AA-BB-..", "a:b:c:d:e:f" → "aa:bb:cc:dd:ee:ff"; false for malformed, all-zero,
      /// broadcast and multicast MACs. (Moved verbatim from NeighborTable.TryNormalizeMac.)</summary>
      public static bool TryNormalize(string text, out string mac);
  }
  ```
- The public `LookupAsync` calls the internal one with `Dns.GetHostEntryAsync`. The internal one is `resolve(address.ToString(), ct).WaitAsync(timeout, ct)`, with `TimeoutException` mapped to null. ConInfrastructure's existing `InternalsVisibleTo` already gives ConEngine.Tests access.

- [ ] **Step 1: Write the tests**

```csharp
public class NetHelpersTests
{
    [Fact]
    public async Task A_cancelled_echo_throws_rather_than_reporting_no_answer()
    {
        using var cts = new CancellationTokenSource();
        var task = IcmpEcho.TryPingAsync(IPAddress.Parse("192.0.2.1"), TimeSpan.FromSeconds(10), cts.Token);
        cts.CancelAfter(200);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task A_lookup_slower_than_the_cap_is_null()
    {
        var never = new TaskCompletionSource<IPHostEntry>();
        Assert.Null(await ReverseDns.LookupAsync(IPAddress.Loopback, TimeSpan.FromMilliseconds(50), (_, _) => never.Task, CancellationToken.None));
    }

    [Fact]
    public async Task A_failing_lookup_is_null_but_cancellation_throws()
    {
        Assert.Null(await ReverseDns.LookupAsync(IPAddress.Loopback, TimeSpan.FromSeconds(5),
            (_, _) => Task.FromException<IPHostEntry>(new SocketException()), CancellationToken.None));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ReverseDns.LookupAsync(IPAddress.Loopback,
            TimeSpan.FromSeconds(5), (_, ct) => Task.FromCanceled<IPHostEntry>(ct), cts.Token));
    }

    [Theory]
    [InlineData("a:b:c:d:e:f", "0a:0b:0c:0d:0e:0f")]
    [InlineData("A4-BB-CC-DD-EE-FF", "a4:bb:cc:dd:ee:ff")]
    public void Macs_normalize_padded_and_lower_case(string text, string expected)
    {
        Assert.True(MacAddress.TryNormalize(text, out var mac));
        Assert.Equal(expected, mac);
    }
}
```
In `OuiTableTests`, add:
```csharp
[Fact]
public void An_unpadded_mac_finds_its_vendor() => Assert.Equal("Example Networks", Small.Describe("a4:bb:cc:0:1:2"));

[Theory]
[InlineData("00:00:00:00:00:00")]
[InlineData("ff:ff:ff:ff:ff:ff")]
[InlineData("01:00:5e:00:00:01")]
public void Zero_broadcast_and_multicast_macs_have_no_vendor(string mac) => Assert.Null(Small.Describe(mac));
```

- [ ] **Step 2: Run them and see them fail**

Run: `dotnet test Tests/ConEngine.Tests -c Debug --filter "FullyQualifiedName~NetHelpersTests|FullyQualifiedName~OuiTableTests"`
Expected: build error first. After the types exist, the two `OuiTableTests` additions FAIL until `Describe` is switched.

- [ ] **Step 3: Implement the three helpers and switch every caller in the Files list**

- [ ] **Step 4: Run the engine suite**

Run: `dotnet test Tests/ConEngine.Tests -c Debug`
Expected: 0 failed. `GatewayDiscoveryTests`, `LanScannerTests`, `NeighborTableTests`, `TracerouteRunnerTests` and `PingTransceiverIcmpTests` stay green.

- [ ] **Step 5: Commit**

```powershell
git add -A; git commit -m "refactor(infra): one-shot ICMP, capped reverse DNS and MAC parsing live in Net/ (D10)"
```

---

## Group G: defaults

### Task 12: D12, ping-profile defaults have one source

**Files:**
- Modify:
  - `Core/ConEngine/Configuration/WatcherOptions.cs` (`PingProfileConfig` initializers).
  - `Core/ConCore/Targets/PingProfile.cs` (`Resolver` drops `BurstMaxDuration`, `DownThreshold` and `RecoveringThreshold`, which equal the defaults).
  - `Programs/ConWatch/Pages/Config/Profiles.cshtml` + `.cshtml.cs` (dialog `value=`s from `ProfilesModel.DialogDefaults`).
- Test: `Tests/ConWatch.Tests/` (new `ProfilesDialogTests.cs`). `PingProfileTests.PingProfileConfig_defaults_equal_PingProfile_defaults` and `Resolver_preset_queries_every_half_minute_and_bursts_on_a_miss` already pin the rest.

**Interfaces:**
- Produces: `public static IReadOnlyDictionary<string, string> DialogDefaults` on `ProfilesModel`. The keys are the form's field names (`normalIntervalSeconds` … `recoveringThreshold`). The values are a default `PingProfileConfig`'s numbers, formatted with `CultureInfo.InvariantCulture`.
- `PingProfileConfig` initializers read `private static readonly PingProfile Default = new() { Name = "" };`, for example `NormalIntervalSeconds = Default.NormalInterval.TotalSeconds`.

- [ ] **Step 1: Write the culture test**

```csharp
public class ProfilesDialogTests
{
    [Fact]
    public void Dialog_defaults_are_invariant_whatever_the_culture()
    {
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            var d = ConWatch.Pages.Config.ProfilesModel.DialogDefaults;
            Assert.Equal("10", d["normalIntervalSeconds"]);
            Assert.Equal("0.95", d["respondingThreshold"]);
            Assert.Equal("100", d["historySize"]);
        }
        finally { CultureInfo.CurrentCulture = saved; }
    }
}
```
Use the actual namespace of `ProfilesModel` from `Profiles.cshtml.cs`.

- [ ] **Step 2: Run it and see it fail to compile**

Run: `dotnet test Tests/ConWatch.Tests -c Debug --filter "FullyQualifiedName~ProfilesDialogTests"`
Expected: build error.

- [ ] **Step 3: Implement**

The ten `value="…"` attributes in `Profiles.cshtml` become `value="@ProfilesModel.DialogDefaults["…"]"`.

- [ ] **Step 4: Run the suites**

Run: `dotnet test Tests/ConEngine.Tests -c Debug; dotnet test Tests/ConWatch.Tests -c Debug`
Expected: 0 failed. `PageSmokeTests` renders `/Config/Profiles`.

- [ ] **Step 5: Commit**

```powershell
git add -A; git commit -m "refactor(profiles): profile defaults come from PingProfile alone; the dialog renders them (D12)"
```

---

### Task 13: D12, target-role defaults and built-in profile names

**Files:**
- Create: `Core/ConCore/Targets/TargetRole.cs`, `Tests/ConEngine.Tests/TargetRoleTests.cs`
- Modify:
  - `Programs/ConWatch/SetupService.cs`: router, WAN hop, anchors and upstream resolver use roles.
  - `Programs/ConWatch/Pages/Config/Discoveries.cshtml.cs`: `AdoptUrl` gets profile, scope and critical from roles.
  - `Programs/ConWatch/Pages/Config/Targets.cshtml.cs`: `DefaultProfile` uses `TargetRole.LocalDevice.Profile` / `TargetRole.Anchor.Profile`.
  - `Programs/ConWatch/Dns/SystemResolverReconciler.cs`: delete `ProfileName` and use `TargetRole.Resolver.Profile`/`.Probe`; its match on `"dns"` uses `TargetRole.Resolver.Probe`.

**Interfaces:**
- Produces (namespace `ConCore.Targets`):
  ```csharp
  /// <summary>What a target gets for the role it plays when ConnectionWatcher creates or pre-fills it.</summary>
  public sealed record TargetRole(string Profile, TargetScope Scope, bool Critical = false, string? Probe = null)
  {
      public static TargetRole Router { get; }      // Internal, Verdict, Critical
      public static TargetRole PathHop { get; }     // Path, Verdict
      public static TargetRole Anchor { get; }      // Public, Verdict
      public static TargetRole LocalDevice { get; } // Local, Local
      public static TargetRole Resolver { get; }    // Resolver, Resolver, Probe "dns"
  }
  ```
  Each `Profile` reads `PingProfile.<Preset>.Name`.

- [ ] **Step 1: Write the test**

```csharp
public class TargetRoleTests
{
    [Fact]
    public void Roles_are_the_documented_defaults()
    {
        Assert.Equal(new TargetRole("Internal", TargetScope.Verdict, Critical: true), TargetRole.Router);
        Assert.Equal(new TargetRole("Path", TargetScope.Verdict), TargetRole.PathHop);
        Assert.Equal(new TargetRole("Public", TargetScope.Verdict), TargetRole.Anchor);
        Assert.Equal(new TargetRole("Local", TargetScope.Local), TargetRole.LocalDevice);
        Assert.Equal(new TargetRole("Resolver", TargetScope.Resolver, Probe: "dns"), TargetRole.Resolver);
    }
}
```

- [ ] **Step 2: Run it and see it fail to compile**

Run: `dotnet test Tests/ConEngine.Tests -c Debug --filter "FullyQualifiedName~TargetRoleTests"`
Expected: build error.

- [ ] **Step 3: Implement `TargetRole` and switch the four files**

`AdoptUrl` writes `role.Scope.ToString()` and adds `critical=true` when `role.Critical`. The query strings stay byte-identical.

- [ ] **Step 4: Run the suites**

Run: `dotnet test Tests/ConEngine.Tests -c Debug; dotnet test Tests/ConWatch.Tests -c Debug`
Expected: 0 failed. `SetupApiTests`, `DiscoveriesPageTests`, `CriticalFieldTests`, `SystemResolverReconcilerTests` and `LocalScopeApiTests` pin the values on the wire.

- [ ] **Step 5: Commit**

```powershell
git add -A; git commit -m "refactor: target-role defaults and built-in profile names have one home (D12)"
```

---

## After the last task

- [ ] Run the full gate once more: `dotnet build ConnectionWatcher.sln -c Debug` (no new warnings), `dotnet test ConnectionWatcher.sln -c Debug` (0 failed), and `npx vitest run` from `Programs/ConWatch/ClientApp`.
- [ ] Hand the branch back for review and merge. Do not push.

## Owner rulings (2026-10-30)

1. **Ruling 1 and the Targets-page pipeline:** keep drawing a nested anchor inside the path chain of the anchor below it. The drawing shows the real path; only the trunk/anchor *roles* change.
2. **`ConfigHealth` counts only enabled targets,** `anchors-below-recommended` included: kept. A disabled anchor proves nothing about the internet. Plan 5 Task 3 fixes the vacuous test meant to pin this.
