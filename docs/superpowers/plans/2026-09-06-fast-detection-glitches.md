# Fast Detection & Glitches Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Accelerate sampling on the first sign of trouble (suspicion burst + corroboration sweep + 1s burst timeout) and record sub-Faltering trouble as "glitch" events with their own table, API, recurrence heatmap, and ISP-report section.

**Architecture:** `PingDiscipline` gains result-driven bursts; a new `SuspicionCoordinator` in the engine sweeps anchors+trunk on suspicion, tracks per-target glitch episodes (open on loss, close on 5 successes, discard on any state transition), coalesces within 60s, and correlates overlapping anchor episodes into Internet glitches. A `GlitchRecorder` persists events; open read APIs and a History heatmap present them. States, thresholds, verdicts, and history semantics are untouched.

**Tech Stack:** existing (.NET 10, Rx, EF Core/SQLite, React islands, Vitest).

**Spec:** `docs/superpowers/specs/2026-09-06-fast-detection-glitches-design.md`

## Global Constraints

- Branch `glitches` off master. TDD per task; commit per task.
- Constants (hardcoded, from spec): suspicion-burst politeness guard 60s since last burst start; sweep cooldown 30s; episode close after 5 consecutive successes; glitch floor 2 losses; coalesce gap 60s; internet-glitch episode join ±10s; burst timeout `min(profile.PingTimeout, 1s)`.
- Glitch rows only for episodes with NO state transition; `Faltering`+/`Unreachable` discard the episode.
- New API routes are OPEN (watching surface): add `/api/v1/glitches` to `OpenPrefixes` in `ApiSurfaceLockTests`.
- Engine tests use `FakeTimeProvider`/fake pings per existing ConEngine.Tests patterns.

---

### Task 1: PingDiscipline — result-driven bursts

**Files:**
- Modify: `Core/ConEngine/Scheduling/PingDiscipline.cs`
- Modify: `Core/ConEngine/Scheduling/IPingCadence.cs` (no change needed unless tests want the interface; keep `TriggerBurst`/`OnPingResult`/`IsBurstActive` on the class)
- Test: `Tests/ConEngine.Tests/PingDisciplineTests.cs` (extend)

**Interfaces:**
- Produces: `void OnPingResult(bool success)`, `void TriggerBurst()`, `bool IsBurstActive { get; }` on `PingDiscipline`. Task 2 uses `IsBurstActive`; Task 3 uses `TriggerBurst` and calls `OnPingResult` from the engine's result path.

- [ ] **Step 1: Failing tests** — using the existing FakeTimeProvider pattern in `PingDisciplineTests`:
  - `Failed_ping_while_responding_starts_burst`: state Responding, `OnPingResult(false)` → next delay is `BurstInterval`; `IsBurstActive` true.
  - `Success_does_not_start_or_extend_burst`: `OnPingResult(true)` never sets burst; during an active burst a success leaves `BurstStartedAt` unchanged.
  - `Suspicion_burst_respects_politeness_guard`: after a burst starts, `OnPingResult(false)` at +30s does NOT restart the window (still expires at +cap); at +61s with no active window a new failure starts a fresh burst.
  - `TriggerBurst_starts_burst_without_state_change`: from Responding, `TriggerBurst()` → burst delay; same politeness guard.
  - `Burst_applies_in_responding_state`: ComputeDelay path — burst window active while state Responding returns `BurstInterval` (today it requires Faltering/Down/Recovering).
  - `State_change_bursts_unchanged`: existing tests still pass (state change always restarts the window — exempt from the guard).

- [ ] **Step 2: Run, verify failures.** `dotnet test Tests/ConEngine.Tests --filter PingDiscipline`

- [ ] **Step 3: Implement.** In `PingDiscipline`: add `_lastBurstStart` (DateTimeOffset?). `OnStateChanged` keeps unconditional restart for Faltering/Recovering/Down and records `_lastBurstStart`. New:

```csharp
public bool IsBurstActive
{
    get { lock (_lock) { return _burstStartedAt is { } s && _time.GetUtcNow() - s < _profile.BurstMaxDuration; } }
}

public void OnPingResult(bool success)
{
    if (success) return;
    lock (_lock)
    {
        if (_state is not (HostState.Responding or HostState.Starting)) return;
        StartSuspicionBurstLocked();
    }
}

public void TriggerBurst()
{
    lock (_lock)
    {
        if (_state == HostState.Unreachable) return;
        StartSuspicionBurstLocked();
    }
}

private void StartSuspicionBurstLocked()
{
    var now = _time.GetUtcNow();
    bool active = _burstStartedAt is { } s && now - s < _profile.BurstMaxDuration;
    if (active) return;                                   // don't extend
    if (_lastBurstStart is { } last && now - last < TimeSpan.FromSeconds(60))
        return;                                           // politeness guard
    _burstStartedAt = now;
    _lastBurstStart = now;
}
```

`ComputeDelay` changes: burst when the window is active regardless of state:

```csharp
private TimeSpan ComputeDelay()
{
    if (_burstStartedAt is { } start && _time.GetUtcNow() - start < _profile.BurstMaxDuration)
        return _profile.BurstInterval;
    return _profile.NormalInterval;
}
```

(State-change path also sets `_lastBurstStart = _time.GetUtcNow()` where it sets `_burstStartedAt`.)

- [ ] **Step 4: Run ConEngine tests green.**
- [ ] **Step 5: Commit** `feat(engine): suspicion bursts in PingDiscipline`

---

### Task 2: Burst-shortened ping timeout

**Files:**
- Modify: `Core/ConEngine/Network/PingController.cs` (`int timeoutMs` → `Func<int> timeoutProvider`; read per iteration at the `PingAsync` call)
- Modify: `Core/ConEngine/Network/PingManager.cs` (`GetOrCreateController` signature)
- Modify: `Core/ConEngine/WatcherEngine.cs:169` `Activate` — pass provider:

```csharp
var steadyMs = (int)monitor.Profile.PingTimeout.TotalMilliseconds;
_manager!.GetOrCreateController(ip, monitor.Discipline,
    () => monitor.Discipline.IsBurstActive ? Math.Min(steadyMs, 1000) : steadyMs);
```

- Test: `Tests/ConEngine.Tests/PingControllerTests.cs` (extend): fake transceiver records the timeout it was called with; drive a burst; assert steady calls get 3000 and burst calls get 1000.

- [ ] **Step 1: Failing test.**
- [ ] **Step 2: Verify failure.**
- [ ] **Step 3: Implement (mechanical signature change; fix all callers/tests).**
- [ ] **Step 4: All ConEngine + ConWatch suites green** (engine rebuild path exercises Activate).
- [ ] **Step 5: Commit** `feat(engine): 1s ping timeout while bursting`

---

### Task 3: SuspicionCoordinator — sweep, episodes, glitch events

**Files:**
- Create: `Core/ConEngine/Topology/SuspicionCoordinator.cs`
- Create: `Core/ConEngine/Events/GlitchEvent.cs`
- Modify: `Core/ConEngine/WatcherEngine.cs` (construct coordinator; route ping results through it; expose `GlitchSource`; call `Discipline.OnPingResult` in the result subscription)
- Test: `Tests/ConEngine.Tests/SuspicionCoordinatorTests.cs`

**Interfaces:**
- Produces:

```csharp
public record GlitchEvent
{
    public required Guid TargetId { get; init; }
    public required string TargetName { get; init; }
    public GlitchClass Class { get; init; } = GlitchClass.Target; // Target=0, Internet=1
    public required DateTimeOffset StartUtc { get; init; }
    public required DateTimeOffset EndUtc { get; init; }
    public int LostCount { get; init; }
    public int SampleCount { get; init; }
    public string? Detail { get; init; }
    public bool Coalesced { get; init; } // recorder: extend previous row for this target
}
```

  `WatcherEngine.GlitchSource` (`IObservable<GlitchEvent>`).
- Coordinator ctor: `(TopologyGraph graph, Pool? anchorPool, IReadOnlyDictionary<Guid, TargetMonitor> monitors, int weatherThreshold, TimeProvider time, ILogger log)` — mirror `VerdictCoordinator`'s style; compute sweep set = anchor members + parent chain from the pool's parent to root.
- Coordinator input: `void OnPingSample(Guid targetId, PingResult result)` called by the engine's result subscription (after `monitor.RecordPingResult`, so `CurrentState` reflects the new sample when deciding discard).

**Behavior (implement exactly):**
- Sweep: on a failed sample from a sweep-set member whose state (before the sample: track prior state per target) was Responding/Starting, and ≥30s since last sweep: call `TriggerBurst()` on every sweep-set monitor's discipline.
- Episode per target: open on failure from Responding/Starting; count losses/samples and first/last-loss timestamps; a state transition observed for that target during the episode (poll `monitor.CurrentState != Responding/Starting` after each sample, or subscribe to transitions) → discard; 5 consecutive successes → close; emit iff losses ≥ 2; set `Coalesced = true` when the previous glitch for this target ended <60s before this episode's start.
- Internet correlation: on emitting a Target glitch for an anchor member, check other anchor members' just-emitted or open episodes; if ≥ `weatherThreshold` anchors have episodes overlapping (±10s), emit one Internet glitch (TargetId = `VerdictCoordinator.SubjectId`, name "Internet", span = union, Detail = `"anchors: X, Y" + (trunk glitched ? "; trunk affected (likely link/ISP)" : "; trunk clean (upstream)")`), at most one Internet glitch per overlapping cluster (mark contributing episodes consumed).
- Everything inside try/catch-log; the coordinator must never throw into the ping path.

- [ ] **Step 1: Failing tests** (fake time; drive `OnPingSample` directly with monitors built on fake pings as in `TopologyCoordinatorTests`): sweep triggering + cooldown + non-member no-op; 1-loss no glitch; 2-loss glitch span/counts; Faltering discard; coalesce flag; internet correlation incl. trunk wording; threshold not met → target glitches only.
- [ ] **Step 2: Verify failures.**
- [ ] **Step 3: Implement coordinator + engine wiring.** Engine result subscription becomes:

```csharp
if (_routingTable.TryGetValue(result.IpAddress, out var monitor))
{
    monitor.Discipline.OnPingResult(result.Success);
    monitor.RecordPingResult(result);
    _pingSampleSubject.OnNext((monitor.Target.Id, result));
    Suspicion?.OnPingSample(monitor.Target.Id, result);
}
```

- [ ] **Step 4: ConEngine suite green.**
- [ ] **Step 5: Commit** `feat(engine): SuspicionCoordinator sweeps and glitch episodes`

---

### Task 4: Persistence — Glitches table, recorder, retention

**Files:**
- Modify: `ConPersistence/ConWatchDbContext.cs` (+`DbSet<GlitchRow> Glitches`), create `ConPersistence/Entities/GlitchRow.cs`, migration `AddGlitches`
- Create: `Programs/ConWatch/GlitchRecorder.cs` (mirror `TransitionRecorder`: channel, batch write; on `Coalesced` update the target's latest row's `EndUtc += span`, `LostCount/SampleCount +=` instead of insert)
- Modify: `Programs/ConWatch/WatcherHostService.cs` (attach `GlitchSource`), `Programs/ConWatch/Program.cs` (DI + hosted), `Programs/ConWatch/RetentionService.cs` (delete `Glitches` where `StartUtc` older than MinuteRetentionDays, same run)
- Test: `Tests/ConWatch.Tests/GlitchPersistenceTests.cs` (recorder writes/coalesces via factory + fake pings), retention test extension

`GlitchRow`: `long Id` (PK, autoincrement), `Guid TargetId`, `string TargetName`, `int Class`, `DateTime StartUtc`, `DateTime EndUtc`, `int LostCount`, `int SampleCount`, `string? Detail`; indexes `(StartUtc)`, `(TargetId, StartUtc)`.

- [ ] Steps: failing tests → migration + entities → recorder + wiring → retention → green → **Commit** `feat(persistence): glitch recording and retention`

---

### Task 5: API + export + lock contract

**Files:**
- Create: `Programs/ConWatch/Api/GlitchEndpoints.cs`: `GET /glitches?from&to&targetId&limit(<=1000, default 100)` newest-first `{ items: [...] }` with UTC-kinded timestamps (copy the `SpecifyKind` pattern from `HistoryEndpoints`); map on the OPEN group in `Program.cs`.
- Modify: `Programs/ConWatch/Api/ExportEndpoints.cs`: `/export/glitches?format=csv|json`.
- Modify: `Tests/ConWatch.Tests/ApiSurfaceLockTests.cs`: add `"/api/v1/glitches"` to `OpenPrefixes`.
- Test: `Tests/ConWatch.Tests/GlitchApiTests.cs` (filters, paging, export formats, open-when-locked).

- [ ] Steps: failing tests → implement → full ConWatch suite green (lock contract proves classification) → **Commit** `feat(api): glitch query and export endpoints`

---

### Task 6: History UI — recurrence heatmap, glitch list, chronic badge

**Files:**
- Create: `Programs/ConWatch/ClientApp/src/lib/glitchData.ts` — pure functions: `bucketByHour(glitches, outageTransitions, days, tzOffsetProvider)` → `{grid: count[24][days], outageHours: Set}`; `chronicTargets(glitches, now)` → targets with glitches in ≥3 distinct hours of last 24h.
- Test: `Programs/ConWatch/ClientApp/src/lib/glitchData.test.ts` — bucketing incl. UTC→local mapping and a DST-boundary day; chronic threshold edges (2 hours no, 3 yes).
- Modify: history island (`src/history/...`): "Glitches & recurrence" section — range selector reuse, heatmap rendered as CSS grid (24 rows × N day columns; intensity by count; outage overlay in the Down color; title-attr tooltips), recent-glitch list, chronic badges beside per-target stats.
- Modify: `Tests/ConWatch.Tests/PageSmokeTests.cs`: History page marker `"Glitches"` (server-rendered section shell).

- [ ] Steps: failing Vitest → implement lib → island UI → `npm test` + smoke green → **Commit** `feat(ui): glitch recurrence heatmap and chronic badges`

---

### Task 7: ISP report section + wrap-up

**Files:**
- Modify: `ConPersistence/Report/IspReportBuilder.cs` (+`GlitchSummary`: total, byClass, top-3 hours with counts, chronic targets), `Programs/ConWatch/Pages/Report.cshtml` ("Brief disturbances (glitches)" after Incidents; UTC labels on times per the existing convention)
- Modify: `README.md` (glitches paragraph in Web UI section), `docs/superpowers/BACKLOG.md` (mark done; note latency-glitches as explicit non-goal)
- Test: `Tests/ConPersistence.Tests` builder summary test; Report page smoke marker.

- [ ] Steps: failing tests → implement → **full suites** (`dotnet test` + `npm test`) → **Commit** `feat(report): glitch summary section` → finishing-a-development-branch.
