# Evidence-Based Verdict — Engine Implementation Plan (Plan A of 4)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stop a single path hop's failed probe from declaring the internet down. Targets beyond a failing hop keep probing and are marked Unreachable only on their own evidence. The verdict is decided by the anchors (one off-net anchor when the ISP is tagged, two independent anchors when it isn't), and blame goes to the first silent hop after the last one with proven forwarding.

**Architecture:** Every change lives in the ConEngine core:
- `PingDiscipline` stops parking.
- `HostAnalysisEngine` separates its *own* threshold state from the topology's *explanation* ("Unreachable" = own Down + an upstream root cause).
- `TopologyCoordinator` computes root causes from evidence and bursts the targets beyond a hop when it changes.
- `VerdictCoordinator` classifies anchors first and blames by frontier walk.

A small notifier change adds the "skip explained transitions" webhook setting. Model warnings, critical targets and loss attribution are Plans B, C and D.

**Tech Stack:** .NET 10, C#, System.Reactive, xunit 2.9.3, `Microsoft.Extensions.Time.Testing.FakeTimeProvider`.

**Spec:** `docs/superpowers/specs/2026-10-09-evidence-based-verdict-design.md`. This plan implements §4.1–4.4, the D5 bursts of §4.5, the webhook part of §4.4 and §4.8, i.e. rollout steps 1–2 of §8. The rest of §4.5 (loss attribution) is Plan D. §4.6 (model warnings, dashboard and tray badge) is Plan B. §4.7 (critical targets) is Plan C.

## Global Constraints

- File-scoped namespaces; `Nullable` and `ImplicitUsings` enabled; match the surrounding comment density and naming.
- **Never sleep-then-assert in tests:** wait on conditions. CI runners have 2 cores. Engine unit tests use `FakeTimeProvider` and feed results directly.
- Keep the verdict state names (`Unknown`, `Healthy`, `SiteIncident`, `InternetWeather`, `LinkDegraded`, `LinkDown`) and `HostState` values unchanged. Every consumer (tray, MQTT, metrics, UI, ISP report) keys on them.
- Keep the wording format `"{Verdict}: {Operator} — {Hop} is {State}"` (and `"{Verdict}: {Hop} is {State}"` without an operator). The dashboard banner and the tray parse nothing but show it verbatim.
- "Answering" = `Responding`, `Faltering` or `Recovering` (the state machine saw replies in its window). `Down` and `Unreachable` are not answering. `Initializing` and `Starting` are undetermined.
- Operator tags compare **trimmed and case-insensitive** (the existing `CountOperators` rule).
- **Spec D2 (corrected 2026-10-09):** the internet is up when **≥ 1 off-net anchor** answers, provided the ISP is known (an operator tag on some path hop). When the ISP is unknown, **≥ min(2, independent anchors configured)** independent anchors must answer. When the ISP is known but every anchor carries the ISP's tag, the anchors are used as the unknown case.
- Test commands, per project (never the whole solution): `dotnet test Tests/ConEngine.Tests -c Debug`, `Tests/ConNotify.Tests`, `Tests/ConWatch.Tests`; filter with `--filter "FullyQualifiedName~<Class>"`. `ConWatch.Tests` has known cold-start flakes (`ProbeFieldTests`, the `SettingsApiTests` constructor, `NotifierIntegrationTests.VerdictOnly_…`). Rerun once before calling a failure real.
- Commit after every task. **Never push.** Commit messages carry no attribution lines. Work on a feature branch in a git worktree (`evidence-verdict-engine`). Do not edit `README.md` (the user has an uncommitted rewrite).

## Review Focus

The failure modes most likely to bite, each pinned by a test in the named task:

1. **The 2026-10-08 incident:** a mid-path hop goes Down while everything beyond it keeps answering. Expect no `LinkDown`, the descendants staying `Responding` (not Unreachable), and the verdict `Healthy` (Tasks 3 and 4).
2. **A real router failure** (the router and everything beyond stop answering). Expect `LinkDown` blaming the router, every descendant `Unreachable` with `ParentDown` naming the router as root cause, and recovery releasing them to `Starting` with a burst (Tasks 3 and 4).
3. **An ISP cut off from the internet** (the ISP-tagged anchor answers, off-net anchors don't). Expect `LinkDown` blamed "beyond" the last answering hop, never `Healthy` (Task 4).
4. **Re-entrancy:** explanation changes publish transitions that re-enter the coordinator on the same thread or another one. Expect no deadlock, no lost update, and the final explanations matching the final states (Task 3, two-thread test).
5. **Warm start with a seeded `Unreachable`:** the presumption survives when topology confirms the root cause, and is corrected (to `Down`) when it doesn't, with no spurious transition when confirmed (Task 2).

---

## File Structure

| File | Responsibility | Task |
|---|---|---|
| `Core/ConEngine/Scheduling/PingDiscipline.cs` | No parking; `ForceBurst()` | 1 |
| `Core/ConEngine/Tracking/HostAnalysisEngine.cs` | Own state + explanation; Unreachable derived | 2 |
| `Core/ConEngine/Tracking/HostTracker.cs` | `SetExplanation`, `ExplainedBy` pass-through | 2 |
| `Core/ConEngine/Tracking/TargetMonitor.cs` | `SetExplanation`, `ExplainedBy`; `SetUnreachable` removed | 2 |
| `Core/ConEngine/Topology/TopologyCoordinator.cs` | Root causes from evidence; D5 bursts; release on root recovery | 3 |
| `Core/ConEngine/Topology/VerdictCoordinator.cs` | Anchor-first classification, ISP/off-net, frontier blame | 4 |
| `Libraries/ConNotify/WebhookFilter.cs` (new), `WebhookNotifier.cs`, `Libraries/ConPersistence/Settings/SettingsStore.cs`, `Programs/ConWatch/Api/SettingsEndpoints.cs`, `Programs/ConWatch/Pages/Settings.cshtml` | "Skip explained transitions" (default on) | 5 |

Tests: `Tests/ConEngine.Tests/{PingDisciplineTests, HostAnalysisEngineTests, WarmStartTests, TargetMonitorTests, TopologyCoordinatorTests, VerdictCoordinatorTests, WatcherEngineTests}.cs`, `Tests/ConEngine.Tests/Fakes/TrackerProbe.cs`, `Tests/ConNotify.Tests/WebhookFilterTests.cs` (new), `Tests/ConWatch.Tests/{HistoryApiTests, SettingsApiTests}.cs`.

---

### Task 1: PingDiscipline — no parking, ForceBurst

**Files:**
- Modify: `Core/ConEngine/Scheduling/PingDiscipline.cs`
- Test: `Tests/ConEngine.Tests/PingDisciplineTests.cs`, `Tests/ConEngine.Tests/TargetMonitorTests.cs` (remove the two `IsParked` assertions only)

**Interfaces:**
- Produces: `PingDiscipline.ForceBurst()`. It starts a burst now regardless of the 60 s suspicion guard and never extends an already-active burst. `Unreachable` is a burst state like `Down`. `IsParked` is **removed**.

- [ ] **Step 1: Write the failing tests**

In `PingDisciplineTests.cs`, **replace** `Unreachable_parks_until_state_change` and `TriggerBurst_is_ignored_while_parked` with:

```csharp
    [Fact]
    public async Task Unreachable_keeps_probing_at_burst_cadence()
    {
        // Spec 2026-10-09 D4: a target is never parked; Unreachable is a
        // failing state like Down and keeps proving itself.
        var time = new FakeTimeProvider();
        var d = new PingDiscipline(Profile, time);
        d.OnStateChanged(HostState.Unreachable);
        Assert.NotNull(d.BurstStartedAt);

        var wait = d.WaitForNextPingAsync(CancellationToken.None);
        Assert.True(await Completes(wait, time, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void TriggerBurst_works_while_unreachable()
    {
        var time = new FakeTimeProvider();
        var d = new PingDiscipline(Profile, time);
        d.OnStateChanged(HostState.Unreachable);
        time.Advance(TimeSpan.FromSeconds(120)); // the state-change burst and the guard have expired
        d.TriggerBurst();
        Assert.True(d.IsBurstActive);
    }

    [Fact]
    public void ForceBurst_ignores_the_suspicion_guard()
    {
        var time = new FakeTimeProvider();
        var shortBurst = Profile with { BurstMaxDuration = TimeSpan.FromSeconds(10) };
        var d = new PingDiscipline(shortBurst, time);
        d.OnStateChanged(HostState.Responding);

        d.OnPingResult(success: false);       // suspicion burst starts the 60 s guard
        time.Advance(TimeSpan.FromSeconds(30)); // burst over, guard still active
        d.TriggerBurst();
        Assert.False(d.IsBurstActive);         // the guard refuses a polite burst…

        d.ForceBurst();
        Assert.True(d.IsBurstActive);          // …but topology's "reach beyond now" is not polite
    }

    [Fact]
    public void ForceBurst_never_extends_an_active_burst()
    {
        var time = new FakeTimeProvider();
        var d = new PingDiscipline(Profile, time);
        d.OnStateChanged(HostState.Responding);
        d.ForceBurst();
        var started = d.BurstStartedAt;

        time.Advance(TimeSpan.FromSeconds(30));
        d.ForceBurst();
        Assert.Equal(started, d.BurstStartedAt);
    }
```

In `TargetMonitorTests.cs`, delete every line that mentions `IsParked` (three lines, in `SetUnreachable_and_release_work_after_tracking` and `SetUnreachable_before_tracking_starts_parked`; those tests are rewritten in Task 2).

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test Tests/ConEngine.Tests -c Debug --filter "FullyQualifiedName~PingDisciplineTests"`
Expected: a build error (`ForceBurst` does not exist), then after a stub the three new behaviours fail.

- [ ] **Step 3: Implement**

In `PingDiscipline.cs`:

1. Change the class summary's first sentence to: `Adaptive per-target ping cadence: normal interval when healthy, burst interval (time-capped) while Faltering/Recovering/Down/Unreachable.` Delete the words `parked while Unreachable`.
2. Delete the field `_releaseTcs`, the `IsParked` property, and both `Unreachable` branches in `OnStateChanged` (the `_releaseTcs.TrySetResult()` / `_releaseTcs = NewTcs(); _nextNormalDue = null;` blocks).
3. In `OnStateChanged`, make Unreachable a burst state:

```csharp
            if (newState is HostState.Faltering or HostState.Recovering or HostState.Down or HostState.Unreachable)
```

4. In `TriggerBurst`, delete the `if (_state == HostState.Unreachable) return;` guard.
5. Add after `TriggerBurst`:

```csharp
    /// <summary>Commanded by topology when a hop on the path changes state
    /// (spec 2026-10-09 D5): reach what lies beyond it NOW. Ignores the
    /// suspicion guard but, like every burst, never extends an active one.</summary>
    public void ForceBurst()
    {
        lock (_lock)
        {
            var now = _time.GetUtcNow();
            if (_burstStartedAt is { } active && now - active < _profile.BurstMaxDuration)
                return;
            _burstStartedAt = now;
            _lastBurstStart = now;
            WakeLocked();
        }
    }
```

6. Replace `WaitForNextPingAsync` with the park-free version:

```csharp
    public async Task WaitForNextPingAsync(CancellationToken ct)
    {
        while (true)
        {
            Task wake;
            TimeSpan delay;
            lock (_lock)
            {
                wake = _wakeTcs.Task;
                delay = ComputeDelay();
            }

            if (delay <= TimeSpan.Zero)
                return;

            // Race the sleep against a burst start: a sweep, a lost ping,
            // or a state change must not wait out a normal interval.
            var sleep = Task.Delay(delay, _time, ct);
            var first = await Task.WhenAny(sleep, wake).ConfigureAwait(false);
            if (first == wake)
            {
                ct.ThrowIfCancellationRequested();
                continue; // ComputeDelay now returns the burst interval
            }
            await sleep.ConfigureAwait(false); // propagates cancellation
            return;
        }
    }
```

- [ ] **Step 4: Run to verify they pass**

Run: `dotnet test Tests/ConEngine.Tests -c Debug --filter "FullyQualifiedName~PingDisciplineTests"`, then the full `Tests/ConEngine.Tests`.
Expected: PingDisciplineTests all PASS. Elsewhere, the tests that expected parking may fail (`TopologyCoordinatorTests`, `WatcherEngineTests.Router_down_cascades…`). That is expected and fixed in Tasks 2 and 3. Record the list in the report. Everything else passes.

- [ ] **Step 5: Commit**

```bash
git add Core Tests
git commit -m "feat(engine): never park a target; ForceBurst for topology-driven re-checks"
```

---

### Task 2: Own evidence first — Unreachable is "own Down + explained"

**Files:**
- Modify: `Core/ConEngine/Tracking/HostAnalysisEngine.cs`, `Core/ConEngine/Tracking/HostTracker.cs`, `Core/ConEngine/Tracking/TargetMonitor.cs`, `Tests/ConEngine.Tests/Fakes/TrackerProbe.cs`
- Test: `Tests/ConEngine.Tests/HostAnalysisEngineTests.cs`, `Tests/ConEngine.Tests/WarmStartTests.cs`, `Tests/ConEngine.Tests/TargetMonitorTests.cs`

**Interfaces:**
- Consumes: Task 1 (no parking).
- Produces:
  - `HostAnalysisEngine.SetExplanation(Guid? ancestorId) : AnalysisOutcome?` and `HostAnalysisEngine.ExplainedBy : Guid?` (non-null only while shown `Unreachable`).
  - `HostTracker.SetExplanation(Guid?)` and `HostTracker.ExplainedBy`.
  - `TargetMonitor.SetExplanation(Guid? ancestorId)` and `TargetMonitor.ExplainedBy : Guid?`.
  - `MarkUnreachable` / `SetUnreachable` are **removed**. `Release()` is unchanged: it acts only when shown `Unreachable`, clears history, goes to `Starting` with `ParentRecovered`, and clears the explanation.
  - **Rules:**
    - Shown state = `Unreachable` when own state is `Down` and an explanation is set; otherwise the own state.
    - Samples are **always** analysed, including while Unreachable.
    - Own state turning Down while explained gives `→ Unreachable` with `ParentDown` + `AncestorId`.
    - An explanation set while own state is Down gives `Down → Unreachable` (`ParentDown`).
    - An explanation removed while own state is Down gives `Unreachable → Down` (`ThresholdCrossed`, Detail `"no longer explained by an upstream failure"`).
    - Own recovery while Unreachable gives `Unreachable → Recovering` (`ThresholdCrossed`).
    - `Seed(Unreachable)` = own `Down` (seeded) + a placeholder explanation (`Guid.Empty`), shown `Unreachable` silently. Topology's first `SetExplanation` replaces the placeholder: silently when it confirms a cause, with `Unreachable → Down` when it doesn't.

- [ ] **Step 1: Write the failing tests**

`Tests/ConEngine.Tests/Fakes/TrackerProbe.cs`: replace the `MarkUnreachable` line with:

```csharp
    public void SetExplanation(Guid? ancestorId) => Keep(_tracker.SetExplanation(ancestorId));
    public Guid? ExplainedBy => _tracker.ExplainedBy;
```

`HostAnalysisEngineTests.cs`: **replace** `MarkUnreachable_parks_and_ignores_stale_results`, `Release_returns_to_starting_with_fresh_history` and `MarkUnreachable_is_idempotent` with:

```csharp
    [Fact]
    public void An_explanation_alone_does_not_make_a_healthy_host_unreachable()
    {
        // Spec 2026-10-09 D4: never infer Unreachable; own evidence first.
        var (tracker, transitions, time) = Make();
        Feed(tracker, time, 5, success: true);
        var count = transitions.Count;

        tracker.SetExplanation(Guid.NewGuid());
        Assert.Equal(HostState.Responding, tracker.CurrentState);
        Assert.Null(tracker.ExplainedBy);
        Assert.Equal(count, transitions.Count);

        Feed(tracker, time, 5, success: true); // still answering: still Responding
        Assert.Equal(HostState.Responding, tracker.CurrentState);
    }

    [Fact]
    public void Own_down_with_an_explanation_is_unreachable_naming_the_root_cause()
    {
        var (tracker, transitions, time) = Make();
        Feed(tracker, time, 5, success: true);
        var root = Guid.NewGuid();
        tracker.SetExplanation(root);

        Feed(tracker, time, 20, success: false); // Responding -> Faltering -> (Down, explained)
        Assert.Equal(HostState.Unreachable, tracker.CurrentState);
        Assert.Equal(root, tracker.ExplainedBy);
        var t = transitions.Last();
        Assert.Equal(HostState.Unreachable, t.To);
        Assert.Equal(TransitionTrigger.ParentDown, t.Reason.Trigger);
        Assert.Equal(root, t.Reason.AncestorId);
        Assert.DoesNotContain(transitions, x => x.To == HostState.Down); // never shown Down first
    }

    [Fact]
    public void Explaining_an_already_down_host_turns_it_unreachable()
    {
        var (tracker, transitions, time) = Make();
        Feed(tracker, time, 5, success: true);
        Feed(tracker, time, 20, success: false);
        Assert.Equal(HostState.Down, tracker.CurrentState);

        var root = Guid.NewGuid();
        tracker.SetExplanation(root);
        Assert.Equal(HostState.Unreachable, tracker.CurrentState);
        Assert.Equal((HostState.Down, HostState.Unreachable), (transitions.Last().From, transitions.Last().To));
        Assert.Equal(root, transitions.Last().Reason.AncestorId);
    }

    [Fact]
    public void Withdrawing_the_explanation_leaves_a_still_down_host_down_on_its_own()
    {
        var (tracker, transitions, time) = Make();
        Feed(tracker, time, 5, success: true);
        tracker.SetExplanation(Guid.NewGuid());
        Feed(tracker, time, 20, success: false);
        Assert.Equal(HostState.Unreachable, tracker.CurrentState);

        tracker.SetExplanation(null);
        Assert.Equal(HostState.Down, tracker.CurrentState);
        var t = transitions.Last();
        Assert.Equal((HostState.Unreachable, HostState.Down), (t.From, t.To));
        Assert.Equal(TransitionTrigger.ThresholdCrossed, t.Reason.Trigger);
        Assert.Contains("no longer explained", t.Reason.Detail);
    }

    [Fact]
    public void An_unreachable_host_that_answers_again_recovers_on_its_own_evidence()
    {
        var (tracker, transitions, time) = Make();
        Feed(tracker, time, 5, success: true);
        tracker.SetExplanation(Guid.NewGuid());
        Feed(tracker, time, 20, success: false);
        Assert.Equal(HostState.Unreachable, tracker.CurrentState);

        Feed(tracker, time, 3, success: true); // 3 of 5 >= RecoveringThreshold
        Assert.Equal(HostState.Recovering, tracker.CurrentState);
        Assert.Equal((HostState.Unreachable, HostState.Recovering), (transitions.Last().From, transitions.Last().To));
    }

    [Fact]
    public void Release_returns_an_unreachable_host_to_starting_with_fresh_history()
    {
        var (tracker, transitions, time) = Make();
        Feed(tracker, time, 5, success: true);
        tracker.SetExplanation(Guid.NewGuid());
        Feed(tracker, time, 20, success: false);

        tracker.Release();
        Assert.Equal(HostState.Starting, tracker.CurrentState);
        Assert.Equal(0, tracker.HistoryCount);
        Assert.Null(tracker.ExplainedBy);
        Assert.Equal(TransitionTrigger.ParentRecovered, transitions.Last().Reason.Trigger);

        Feed(tracker, time, 5, success: true);
        Assert.Equal(HostState.Responding, tracker.CurrentState);
    }

    [Fact]
    public void Release_and_repeated_explanations_are_idempotent()
    {
        var (tracker, transitions, time) = Make();
        Feed(tracker, time, 5, success: true);
        tracker.Release();                      // not Unreachable: no-op
        var root = Guid.NewGuid();
        tracker.SetExplanation(root);
        tracker.SetExplanation(root);           // same: no-op
        Feed(tracker, time, 20, success: false);
        var count = transitions.Count;
        tracker.SetExplanation(Guid.NewGuid()); // another cause, still Unreachable: silent
        Assert.Equal(count, transitions.Count);
        Assert.Equal(HostState.Unreachable, tracker.CurrentState);
    }
```

`WarmStartTests.cs`: **replace** `Seeded_unreachable_stays_parked_until_release` with:

```csharp
    [Fact]
    public void Seeded_unreachable_confirmed_by_topology_stays_silent()
    {
        var (tracker, transitions, time) = Make();
        tracker.Seed(HostState.Unreachable);
        Assert.Equal(HostState.Unreachable, tracker.CurrentState);

        tracker.SetExplanation(Guid.NewGuid()); // topology names the root cause
        Feed(tracker, time, 5, success: false);  // fresh samples confirm: still down
        Assert.Equal(HostState.Unreachable, tracker.CurrentState);
        Assert.Empty(transitions);
    }

    [Fact]
    public void Seeded_unreachable_without_a_root_cause_is_corrected_to_down()
    {
        var (tracker, transitions, _) = Make();
        tracker.Seed(HostState.Unreachable);

        tracker.SetExplanation(null); // topology: nothing upstream explains it
        Assert.Equal(HostState.Down, tracker.CurrentState);
        Assert.Equal((HostState.Unreachable, HostState.Down), (transitions.Single().From, transitions.Single().To));
    }

    [Fact]
    public void Seeded_unreachable_that_proves_alive_exits_via_recovering()
    {
        var (tracker, transitions, time) = Make();
        tracker.Seed(HostState.Unreachable);
        tracker.SetExplanation(Guid.NewGuid());

        Feed(tracker, time, 6, success: true);
        Assert.Equal((HostState.Unreachable, HostState.Recovering), (transitions[0].From, transitions[0].To));
        Assert.Equal(HostState.Responding, tracker.CurrentState);
    }
```

`TargetMonitorTests.cs`: **replace** `SetUnreachable_and_release_work_after_tracking` and `SetUnreachable_before_tracking_starts_parked` with the tests below. In `Transitions_from_two_threads_arrive_in_state_order` (the test using `Parallel.Invoke`), replace `() => m.SetUnreachable(ancestor),` with `() => m.SetExplanation(ancestor),` and replace its whole assertion block (the 4 `Assert.Equal` lines after `var states = …`) with the block shown after the new tests.

```csharp
    [Fact]
    public void An_explained_monitor_becomes_unreachable_only_on_its_own_down()
    {
        var (m, transitions, time) = Make();
        m.StartTracking(Ip);
        Feed(m, time, 5, success: true);
        var root = Guid.NewGuid();

        m.SetExplanation(root);
        Assert.Equal(HostState.Responding, m.CurrentState);
        Assert.Null(m.ExplainedBy);

        Feed(m, time, 20, success: false);
        Assert.Equal(HostState.Unreachable, m.CurrentState);
        Assert.Equal(root, m.ExplainedBy);
        Assert.Equal(root, transitions.Last().Reason.AncestorId);
        Assert.NotNull(m.Discipline.BurstStartedAt); // still probing, at burst cadence

        m.Release();
        Assert.Equal(HostState.Starting, m.CurrentState);
        Assert.Null(m.ExplainedBy);
    }

    [Fact]
    public void An_explanation_given_before_tracking_applies_once_tracking_starts()
    {
        var (m, transitions, time) = Make();
        var root = Guid.NewGuid();
        m.SetExplanation(root);
        Assert.Equal(HostState.Initializing, m.CurrentState);
        Assert.Empty(transitions);

        m.StartTracking(Ip);
        Feed(m, time, 5, success: false); // first window: straight to (Down, explained)
        Assert.Equal(HostState.Unreachable, m.CurrentState);
        Assert.Equal(root, m.ExplainedBy);
    }
```

The new assertion block for `Transitions_from_two_threads_arrive_in_state_order`:

```csharp
        // Whatever order the two threads ran in, delivery order equals state
        // order: every transition starts where the previous one ended.
        var states = transitions.Select(t => (t.From, t.To)).ToList();
        Assert.Equal((HostState.Starting, HostState.Responding), states[0]);
        for (int i = 1; i < states.Count; i++)
            Assert.Equal(states[i - 1].To, states[i].From);
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test Tests/ConEngine.Tests -c Debug --filter "FullyQualifiedName~HostAnalysisEngineTests|FullyQualifiedName~WarmStartTests|FullyQualifiedName~TargetMonitorTests"`
Expected: a build error (`SetExplanation`/`ExplainedBy` missing).

- [ ] **Step 3: Implement `HostAnalysisEngine`**

Replace the file's contents from the class summary through the end of `Recalculate`'s state assignment so that the class reads as follows. Unchanged parts are kept verbatim: `UpdateAddress`, the statistics properties, `ClassifyFreshSample` and `BuildStatus`.

```csharp
/// <summary>
/// Per-host state machine. Its OWN state comes only from this host's
/// samples; topology may add an EXPLANATION — the upstream root cause of an
/// own Down — which shows the host as Unreachable (spec 2026-10-09 §4.4).
/// Samples are always analysed, explained or not. Pure: every step returns
/// its outcome and never publishes, so the owner decides when to emit.
/// </summary>
internal class HostAnalysisEngine
{
    /// <summary>Seed(Unreachable): an upstream cause topology has not named yet.</summary>
    private static readonly Guid SeededCause = Guid.Empty;

    private readonly PingProfile _profile;
    private readonly TimeProvider _timeProvider;
    private IPAddress _hostAddress;
    private HostState _own = HostState.Initializing;
    private Guid? _explainedBy;

    public HostAnalysisEngine(PingProfile profile, IPAddress hostAddress, TimeProvider timeProvider)
    {
        _profile = profile;
        _hostAddress = hostAddress;
        _timeProvider = timeProvider;
    }

    /// <summary>What everyone sees: Unreachable = own Down + an upstream root cause.</summary>
    public HostState CurrentState => Show(_own, _explainedBy);

    /// <summary>The root cause while shown Unreachable; null otherwise.</summary>
    public Guid? ExplainedBy => CurrentState == HostState.Unreachable ? _explainedBy : null;

    private static HostState Show(HostState own, Guid? explainedBy) =>
        own == HostState.Down && explainedBy is not null ? HostState.Unreachable : own;

    /// <summary>True while running on a seeded (presumed) state that fresh
    /// samples have not yet confirmed.</summary>
    public bool Assumed => _seeded;
    private bool _seeded;

    public DateTimeOffset TrackingStarted { get; private set; }
    public DateTimeOffset LastUpdated { get; private set; }

    public int SampleSize { get; private set; }
    public int NumSuccess { get; private set; }
    public int NumFailed { get; private set; }

    public float SuccessRate { get; private set; }
    public float FailureRate { get; private set; }
    public float AverageRoundtripTime { get; private set; }

    /// <summary>DNS moved the host: later statuses carry the new address.</summary>
    public void UpdateAddress(IPAddress address) => _hostAddress = address;

    /// <summary>
    /// Warm restart: adopt a presumed state silently — no transition. Fresh
    /// samples confirm it (nothing emitted) or correct it (one transition
    /// from the seeded state) once MinUsableSample is reached; until then no
    /// threshold transition can fire. Unreachable seeds as own Down with a
    /// placeholder cause that topology's first explanation replaces.
    /// Valid only before the first sample. Returns the status to publish, or
    /// null when ignored.
    /// </summary>
    public AnalysisOutcome? Seed(HostState state)
    {
        if (_own != HostState.Initializing)
            return null;
        if (state == HostState.Unreachable)
        {
            _own = HostState.Down;
            _explainedBy = SeededCause;
        }
        else
        {
            _own = state;
        }
        _seeded = true;
        TrackingStarted = _timeProvider.GetUtcNow();
        return new AnalysisOutcome(BuildStatus(TrackingStarted), null);
    }

    /// <summary>
    /// Topology names (or withdraws) the upstream root cause. It only shows
    /// when this host's own evidence says Down; an explanation never makes a
    /// healthy host Unreachable.
    /// </summary>
    public AnalysisOutcome? SetExplanation(Guid? ancestorId)
    {
        var before = CurrentState;
        _explainedBy = ancestorId;
        var after = CurrentState;
        if (after == before)
            return null;
        var reason = after == HostState.Unreachable
            ? new TransitionReason { Trigger = TransitionTrigger.ParentDown, AncestorId = ancestorId }
            : new TransitionReason
            {
                Trigger = TransitionTrigger.ThresholdCrossed,
                SuccessRate = SuccessRate,
                SampleSize = SampleSize,
                Detail = "no longer explained by an upstream failure",
            };
        return Emit(before, after, reason, _timeProvider.GetUtcNow());
    }

    /// <summary>Commanded by topology: the root cause recovered; re-prove from Starting.</summary>
    public AnalysisOutcome? Release()
    {
        if (CurrentState != HostState.Unreachable)
            return null;
        _seeded = false;
        SampleSize = 0; NumSuccess = 0; NumFailed = 0;
        SuccessRate = 0; FailureRate = 0; AverageRoundtripTime = 0;
        _own = HostState.Starting;
        _explainedBy = null;
        return Emit(HostState.Unreachable, HostState.Starting,
            new TransitionReason { Trigger = TransitionTrigger.ParentRecovered }, _timeProvider.GetUtcNow());
    }

    /// <summary>Folds the sample window into statistics and state — always,
    /// explained or not.</summary>
    public AnalysisOutcome Recalculate(IEnumerable<PingHistoryEntry> samples)
    {
        SampleSize = 0; NumSuccess = 0; NumFailed = 0;
        float sumRoundtrip = 0f;

        foreach (PingHistoryEntry item in samples)
        {
            if (_own == HostState.Initializing)
            {
                TrackingStarted = item.Timestamp;
                _own = HostState.Starting;
            }
            if (item.Timestamp > LastUpdated)
                LastUpdated = item.Timestamp;

            SampleSize++;
            if (item.Success) NumSuccess++; else NumFailed++;
            sumRoundtrip += item.RoundtripTime;
        }

        SuccessRate = SampleSize > 0 ? (float)NumSuccess / SampleSize : 0f;
        FailureRate = SampleSize > 0 ? (float)NumFailed / SampleSize : 0f;
        AverageRoundtripTime = SampleSize > 0 ? sumRoundtrip / SampleSize : 0f;

        HostState next;
        if (_seeded)
        {
            // A seeded state is a presumption under test: below MinUsableSample
            // no threshold transition may fire (a single lost ping right after
            // boot must not flip a presumed-Responding host); at MinUsableSample
            // classify the fresh sample exactly like a Starting exit.
            if (SampleSize < _profile.MinUsableSample)
            {
                next = _own;
            }
            else
            {
                _seeded = false;
                next = ClassifyFreshSample();
                // A seeded Down that proves alive must exit via Recovering:
                // topology releases the children it explained only on a
                // Down -> Recovering/Responding transition.
                if (_own == HostState.Down && next != HostState.Down)
                    next = HostState.Recovering;
            }
        }
        else
        {
            next = _own switch
            {
                HostState.Starting when SampleSize >= _profile.MinUsableSample =>
                    ClassifyFreshSample(),
                HostState.Responding when SuccessRate < _profile.FalteringThreshold => HostState.Faltering,
                HostState.Faltering when SuccessRate >= _profile.RespondingThreshold => HostState.Responding,
                HostState.Faltering when SuccessRate < _profile.DownThreshold => HostState.Down,
                HostState.Down when SuccessRate >= _profile.RecoveringThreshold => HostState.Recovering,
                HostState.Recovering when SuccessRate >= _profile.RespondingThreshold => HostState.Responding,
                HostState.Recovering when SuccessRate < _profile.DownThreshold => HostState.Down,
                _ => _own,
            };
        }

        var before = CurrentState;
        _own = next;
        var after = CurrentState;
        if (after != before)
        {
            var reason = after == HostState.Unreachable
                ? new TransitionReason
                {
                    Trigger = TransitionTrigger.ParentDown, AncestorId = _explainedBy,
                    SuccessRate = SuccessRate, SampleSize = SampleSize,
                }
                : new TransitionReason
                {
                    Trigger = TransitionTrigger.ThresholdCrossed,
                    SuccessRate = SuccessRate, SampleSize = SampleSize,
                };
            return Emit(before, after, reason, LastUpdated);
        }
        return new AnalysisOutcome(BuildStatus(LastUpdated), null);
    }

    private AnalysisOutcome Emit(HostState from, HostState to, TransitionReason reason, DateTimeOffset timestamp)
    {
        var status = BuildStatus(timestamp);
        return new AnalysisOutcome(status, new AnalysisTransition
        {
            From = from,
            To = to,
            Timestamp = timestamp,
            Reason = reason,
            Status = status,
        });
    }
```

Delete the old `MarkUnreachable` and `Transition` methods. Keep `ClassifyFreshSample` and `BuildStatus` unchanged (`BuildStatus` reads `CurrentState`, which is now the shown state).

- [ ] **Step 4: Implement `HostTracker`**

Replace the `MarkUnreachable` line with:

```csharp
    /// <summary>Topology names (or withdraws) the upstream root cause of an own Down.</summary>
    public AnalysisOutcome? SetExplanation(Guid? ancestorId) => _analysisEngine.SetExplanation(ancestorId);

    /// <summary>The root cause while shown Unreachable; null otherwise.</summary>
    public Guid? ExplainedBy => _analysisEngine.ExplainedBy;
```

Change the `Release` summary to `/// <summary>Commanded by topology: the root cause recovered; clear history and re-prove.</summary>`.

- [ ] **Step 5: Implement `TargetMonitor`**

- Replace the field `private Guid? _pendingUnreachableAncestor;` with `private Guid? _explanation; // topology's latest word, applied once tracking starts`.
- In `StartTracking`, replace the "Honor topology commands" block with:

```csharp
            // Honor the topology's explanation if it spoke before we started.
            if (_explanation is { } cause)
                QueueOutcomeLocked(_tracker.SetExplanation(cause));
```

- Replace `SetUnreachable` with:

```csharp
    /// <summary>Topology names (or withdraws) the upstream root cause. Only an
    /// own Down shows it (as Unreachable); before tracking it is remembered.</summary>
    public void SetExplanation(Guid? ancestorId)
    {
        lock (_lock)
        {
            _explanation = ancestorId;
            if (_tracker is not null)
                QueueOutcomeLocked(_tracker.SetExplanation(ancestorId));
        }
        DrainOutbox();
    }

    /// <summary>The root cause while shown Unreachable; null otherwise.</summary>
    public Guid? ExplainedBy
    {
        get { lock (_lock) { return _tracker?.ExplainedBy; } }
    }
```

- In `Release`, in the tracker branch, add `_explanation = null;` before `QueueOutcomeLocked(_tracker.Release());`. In the pre-tracker branch, replace `_pendingUnreachableAncestor = null;` with `_explanation = null;`.

- [ ] **Step 6: Run to verify they pass**

Run: `dotnet test Tests/ConEngine.Tests -c Debug --filter "FullyQualifiedName~HostAnalysisEngineTests|FullyQualifiedName~WarmStartTests|FullyQualifiedName~TargetMonitorTests"`
Expected: PASS. `TopologyCoordinator.cs` still calls `SetUnreachable`, so the project won't build until Task 3. **In this task, change its two calls minimally so the build is green:** `monitor.SetUnreachable(transition.TargetId);` becomes `monitor.SetExplanation(transition.TargetId);`. Task 3 rewrites the class. Then run the full `Tests/ConEngine.Tests` and record which topology/engine tests fail (expected: those asserting cascade-to-Unreachable with descendants still answering).

- [ ] **Step 7: Commit**

```bash
git add Core Tests
git commit -m "feat(engine): Unreachable is own Down plus an upstream explanation; samples always analysed"
```

---

### Task 3: TopologyCoordinator — root causes from evidence, reach beyond on change

**Files:**
- Modify: `Core/ConEngine/Topology/TopologyCoordinator.cs` (rewrite)
- Test: `Tests/ConEngine.Tests/TopologyCoordinatorTests.cs` (rewrite), `Tests/ConEngine.Tests/WatcherEngineTests.cs`, `Tests/ConWatch.Tests/HistoryApiTests.cs`

**Interfaces:**
- Consumes: `TargetMonitor.SetExplanation`, `ExplainedBy`, `Release`, `Discipline.ForceBurst` (Tasks 1 and 2).
- Produces:
  - `TopologyCoordinator.Answering(HostState) : bool` (public static): `Responding`, `Faltering` or `Recovering`.
  - `TopologyCoordinator.ProvenForwarding(Guid targetId) : bool`: some monitored descendant is answering.
  - `TopologyCoordinator.RootCause(Guid targetId) : Guid?`: the shallowest monitored ancestor that is `Down` with no proven forwarding.
  - **Behaviour:**
    - On every monitor transition, every monitor's explanation is recomputed.
    - When a target with descendants goes to `Faltering`/`Down`, or leaves `Down`/`Faltering` for `Recovering`/`Responding`, every descendant gets `ForceBurst()`.
    - When a target leaves `Down`, descendants whose `ExplainedBy` is that target are `Release()`d (and burst).
    - The ctor signature is unchanged.

- [ ] **Step 1: Write the failing tests**

Replace `Tests/ConEngine.Tests/TopologyCoordinatorTests.cs` with:

```csharp
using System.Net;
using ConCore;
using ConCore.Targets;
using ConEngine.Configuration;
using ConEngine.Events;
using ConEngine.Scheduling;
using ConEngine.Topology;
using ConEngine.Tracking;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace ConEngine.Tests;

/// <summary>Spec 2026-10-09 §4.4–4.5: Unreachable only on own evidence, the
/// shallowest unproven Down ancestor is the root cause, and a hop changing
/// state makes everything beyond it re-check at once.</summary>
public class TopologyCoordinatorTests
{
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-08-14T00:00:00Z"));
    private readonly TargetGraph _graph;
    private readonly Dictionary<Guid, TargetMonitor> _monitors = new();
    private readonly TopologyCoordinator _coordinator;
    private readonly List<TargetTransition> _transitions = new();

    private static readonly PingProfile Fast = new()
    {
        Name = "Fast", HistorySize = 20, MinUsableSample = 5,
    };

    // Router -> Inside -> Edge -> {Google, Cloudflare}; Router -> Switch (a side branch).
    public TopologyCoordinatorTests()
    {
        _graph = TargetGraph.FromOptions(new WatcherOptions
        {
            Targets =
            [
                new TargetConfig { Name = "Router", Address = "192.168.1.1", Profile = "Internal" },
                new TargetConfig { Name = "Inside", Address = "10.1.10.1", Parent = "Router", Profile = "Internal" },
                new TargetConfig { Name = "Edge", Address = "203.0.113.1", Parent = "Inside", Profile = "Internal" },
                new TargetConfig { Name = "Google", Address = "8.8.8.8", Parent = "Edge" },
                new TargetConfig { Name = "Cloudflare", Address = "1.1.1.1", Parent = "Edge" },
                new TargetConfig { Name = "Switch", Address = "192.168.1.228", Parent = "Router", Profile = "Internal" },
            ],
        });
        foreach (var target in _graph.Targets.Values)
        {
            var monitor = new TargetMonitor(target, Fast, new PingDiscipline(Fast, _time),
                _time, NullLoggerFactory.Instance);
            monitor.StartTracking(IPAddress.Parse(target.Address));
            monitor.TransitionSource.Subscribe(t => { lock (_transitions) _transitions.Add(t); });
            _monitors[target.Id] = monitor;
        }
        _coordinator = new TopologyCoordinator(_graph, _monitors, NullLogger<TopologyCoordinator>.Instance);
        _coordinator.Start();
    }

    private TargetMonitor M(string name) => _monitors[_graph.GetByName(name).Id];
    private Guid Id(string name) => _graph.GetByName(name).Id;
    private HostState State(string name) => M(name).CurrentState;

    private void Feed(string name, int count, bool success)
    {
        var monitor = M(name);
        for (int i = 0; i < count; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            monitor.RecordPingResult(new PingResult(monitor.ResolvedAddress!, success,
                success ? 5 : 0, timestamp: _time.GetUtcNow()));
        }
    }

    private void AllHealthy()
    {
        foreach (var name in new[] { "Router", "Inside", "Edge", "Google", "Cloudflare", "Switch" })
            Feed(name, 5, true);
    }

    [Fact]
    public void A_hop_down_while_everything_beyond_answers_parks_nothing()
    {
        // The 2026-10-08 incident: Inside's probe fails, traffic passes it.
        AllHealthy();
        Feed("Inside", 20, false);
        Feed("Edge", 5, true);
        Feed("Google", 5, true);
        Feed("Cloudflare", 5, true);

        Assert.Equal(HostState.Down, State("Inside"));
        Assert.Equal(HostState.Responding, State("Edge"));
        Assert.Equal(HostState.Responding, State("Google"));
        Assert.Equal(HostState.Responding, State("Cloudflare"));
        Assert.True(_coordinator.ProvenForwarding(Id("Inside")));
        Assert.Null(_coordinator.RootCause(Id("Google")));
        lock (_transitions)
            Assert.DoesNotContain(_transitions, t => t.To == HostState.Unreachable);
    }

    // Failure order matters for WHICH root a target names on the way: while
    // the side-branch Switch still answers, the router is proven to forward and
    // a mid-path hop is the root cause. Failing the Switch first makes the
    // router the root the moment the last target beyond it goes quiet.
    private static readonly string[] BeyondRouter = { "Switch", "Inside", "Edge", "Google", "Cloudflare" };

    [Fact]
    public void A_dead_router_explains_every_descendant_that_proves_itself_down()
    {
        AllHealthy();
        Feed("Router", 20, false);
        foreach (var name in BeyondRouter)
            Feed(name, 20, false);

        Assert.Equal(HostState.Down, State("Router"));
        foreach (var name in new[] { "Inside", "Edge", "Google", "Cloudflare", "Switch" })
        {
            Assert.Equal(HostState.Unreachable, State(name));
            Assert.Equal(Id("Router"), M(name).ExplainedBy); // the shallowest, not the nearest
        }
        lock (_transitions)
            Assert.All(_transitions.Where(t => t.To == HostState.Unreachable), t =>
            {
                Assert.Equal(TransitionTrigger.ParentDown, t.Reason.Trigger);
                Assert.Equal(Id("Router"), t.Reason.AncestorId);
            });
    }

    [Fact]
    public void A_descendant_that_still_answers_is_never_unreachable_even_behind_a_dead_hop()
    {
        AllHealthy();
        Feed("Router", 20, false);
        Feed("Switch", 20, true); // answers: the router forwards to it

        Assert.Equal(HostState.Responding, State("Switch"));
        Assert.Null(M("Switch").ExplainedBy);
    }

    [Fact]
    public void A_bypassed_hop_explains_nothing_so_a_down_descendant_is_its_own_problem()
    {
        AllHealthy();
        Feed("Inside", 20, false);    // Inside not answering…
        Feed("Edge", 5, true);        // …but Edge answers: Inside is bypassed
        Feed("Google", 20, false);    // Google really is down

        Assert.Equal(HostState.Down, State("Google"));
        Assert.Null(M("Google").ExplainedBy);
    }

    [Fact]
    public void When_proof_of_forwarding_appears_an_explained_target_becomes_down_on_its_own()
    {
        AllHealthy();
        Feed("Inside", 20, false);
        Feed("Edge", 20, false);
        Feed("Google", 20, false);
        Feed("Cloudflare", 20, false);
        Assert.Equal(HostState.Unreachable, State("Google"));

        Feed("Cloudflare", 5, true); // something beyond Inside answers again
        Assert.Equal(HostState.Down, State("Google"));
        Assert.Null(M("Google").ExplainedBy);
    }

    [Fact]
    public void Root_cause_recovery_releases_its_explained_descendants_and_bursts_them()
    {
        AllHealthy();
        Feed("Router", 20, false);
        foreach (var name in BeyondRouter)
            Feed(name, 20, false);

        Feed("Router", 10, true); // Down -> Recovering
        foreach (var name in new[] { "Inside", "Edge", "Google", "Cloudflare", "Switch" })
        {
            Assert.Equal(HostState.Starting, State(name));
            Assert.True(M(name).Discipline.IsBurstActive);
        }
    }

    [Fact]
    public void A_hop_starting_to_fail_bursts_everything_beyond_it_at_once()
    {
        AllHealthy();
        _time.Advance(TimeSpan.FromMinutes(5)); // all earlier bursts and guards long expired
        Feed("Inside", 2, false);               // Responding -> Faltering
        Assert.Equal(HostState.Faltering, State("Inside"));
        Assert.True(M("Edge").Discipline.IsBurstActive);
        Assert.True(M("Google").Discipline.IsBurstActive);
        Assert.False(M("Switch").Discipline.IsBurstActive); // not beyond Inside
    }

    [Fact]
    public void Recomputation_from_two_threads_converges_on_the_final_states()
    {
        AllHealthy();
        // Two threads drive opposite halves of a router failure at once.
        Parallel.Invoke(
            () => { Feed("Router", 20, false); Feed("Google", 20, false); },
            () => { Feed("Cloudflare", 20, false); Feed("Edge", 20, false); });
        Feed("Inside", 20, false);
        Feed("Switch", 20, false);

        foreach (var name in new[] { "Inside", "Edge", "Google", "Cloudflare", "Switch" })
            Assert.Equal(Id("Router"), M(name).ExplainedBy);
    }
}
```

(The two-thread test shares `_time`. `FakeTimeProvider.Advance` is thread-safe, and the assertions concern only the final explanations.)

`WatcherEngineTests.cs`: **replace** `Router_down_cascades_and_pinging_pauses_for_descendants` with:

```csharp
    [Fact]
    public async Task Router_down_with_everything_beyond_down_is_explained_by_the_router()
    {
        await Eventually(() => State("Google") == HostState.Responding);

        _factory.SetUp(RouterIp, false);
        _factory.SetUp(GoogleIp, false);
        _factory.SetUp(IPAddress.Parse("1.1.1.1"), false);
        await Eventually(() => State("Router") == HostState.Down);
        await Eventually(() => State("Google") == HostState.Unreachable);
        await Eventually(() => State("Cloudflare") == HostState.Unreachable);

        bool sawParentDown;
        lock (_transitions)
        {
            sawParentDown = _transitions.Any(t =>
                t.TargetName == "Google" && t.Reason.Trigger == TransitionTrigger.ParentDown);
        }
        Assert.True(sawParentDown);

        // Recovery: everything back -> descendants released and re-prove.
        _factory.SetUp(RouterIp, true);
        _factory.SetUp(GoogleIp, true);
        _factory.SetUp(IPAddress.Parse("1.1.1.1"), true);
        await Eventually(() => State("Router") == HostState.Responding);
        await Eventually(() => State("Google") == HostState.Responding);
    }

    [Fact]
    public async Task Router_down_while_everything_beyond_answers_parks_nothing()
    {
        await Eventually(() => State("Google") == HostState.Responding);

        _factory.SetUp(RouterIp, false); // only the router's own probe fails
        await Eventually(() => State("Router") == HostState.Down);

        // Wait for enough fresh probes beyond it to have been judged.
        var google = _engine.Monitors.Values.Single(m => m.Target.Name == "Google");
        var after = google.PingCount;
        await Eventually(() => google.PingCount >= after + 10);
        Assert.Equal(HostState.Responding, State("Google"));
        Assert.Equal(HostState.Responding, State("Cloudflare"));
    }
```

(If `_engine.Monitors` is not the property name, use the engine's existing monitors accessor. Check `WatcherEngine.cs` and adapt.)

`Tests/ConWatch.Tests/HistoryApiTests.cs`, in `Outage_produces_queryable_transitions_and_rollups`: replace

```csharp
        _factory.Pings.SetUp(RouterIp, false);
```
with
```csharp
        // A real outage: the router and everything beyond it stop answering.
        _factory.Pings.SetUp(RouterIp, false);
        _factory.Pings.SetUp(System.Net.IPAddress.Parse("8.8.8.8"), false);
        _factory.Pings.SetUp(System.Net.IPAddress.Parse("1.1.1.1"), false);
```
and replace
```csharp
        _factory.Pings.SetUp(RouterIp, true);
```
with
```csharp
        _factory.Pings.SetUp(RouterIp, true);
        _factory.Pings.SetUp(System.Net.IPAddress.Parse("8.8.8.8"), true);
        _factory.Pings.SetUp(System.Net.IPAddress.Parse("1.1.1.1"), true);
```

(`SeedHomeAsync` creates Router 192.168.1.1, Google 8.8.8.8, Cloudflare 1.1.1.1.)

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test Tests/ConEngine.Tests -c Debug --filter "FullyQualifiedName~TopologyCoordinatorTests"`
Expected: a build error (`ProvenForwarding`/`RootCause` missing), then behaviour failures.

- [ ] **Step 3: Implement**

Replace `Core/ConEngine/Topology/TopologyCoordinator.cs` with:

```csharp
using ConEngine.Events;
using ConEngine.Tracking;
using Microsoft.Extensions.Logging;

namespace ConEngine.Topology;

/// <summary>
/// Reads the reachability tree from evidence (spec 2026-10-09 §4.4–4.5).
/// A target is never silenced by its ancestors: it is Unreachable only when
/// its OWN probes show it Down and an ancestor is Down with nothing beyond it
/// answering. The shallowest such ancestor is the root cause. When a hop
/// starts failing or recovers, everything beyond it re-checks at once, so a
/// hop whose probe lies is contradicted in seconds.
/// </summary>
public class TopologyCoordinator : IDisposable
{
    private readonly IReadOnlyDictionary<Guid, TargetMonitor> _monitors;
    private readonly ILogger<TopologyCoordinator> _log;
    private readonly List<IDisposable> _subscriptions = new();
    // Monitored ancestors per target, root first; monitored descendants per target.
    private readonly Dictionary<Guid, List<Guid>> _ancestors = new();
    private readonly Dictionary<Guid, List<Guid>> _descendants = new();
    private int _recomputing;
    private int _dirty;

    public TopologyCoordinator(TargetGraph graph,
        IReadOnlyDictionary<Guid, TargetMonitor> monitors,
        ILogger<TopologyCoordinator> log)
    {
        _monitors = monitors;
        _log = log;
        foreach (var id in monitors.Keys)
        {
            var chain = new List<Guid>();
            var parent = graph.Targets[id].ParentId;
            while (parent is { } pid)
            {
                if (monitors.ContainsKey(pid))
                    chain.Add(pid);
                parent = graph.Targets[pid].ParentId;
            }
            chain.Reverse();
            _ancestors[id] = chain;
            _descendants[id] = graph.GetDescendants(id).Select(d => d.Id).Where(monitors.ContainsKey).ToList();
        }
    }

    /// <summary>The state machine saw replies in its window.</summary>
    public static bool Answering(HostState state) =>
        state is HostState.Responding or HostState.Faltering or HostState.Recovering;

    /// <summary>Something beyond this target answers: packets get through it,
    /// whatever its own probe says.</summary>
    public bool ProvenForwarding(Guid targetId) =>
        _descendants.TryGetValue(targetId, out var beyond) &&
        beyond.Any(id => Answering(_monitors[id].CurrentState));

    /// <summary>The shallowest ancestor that is Down with no proven forwarding, or null.</summary>
    public Guid? RootCause(Guid targetId)
    {
        if (!_ancestors.TryGetValue(targetId, out var chain))
            return null;
        foreach (var ancestor in chain)
        {
            if (_monitors[ancestor].CurrentState == HostState.Down && !ProvenForwarding(ancestor))
                return ancestor;
        }
        return null;
    }

    /// <summary>Subscribes to all monitors and settles every explanation once
    /// (a warm start may have seeded Unreachable presumptions).</summary>
    public void Start()
    {
        foreach (var monitor in _monitors.Values)
            _subscriptions.Add(monitor.TransitionSource.Subscribe(OnTransition));
        Recompute();
    }

    private void OnTransition(TargetTransition transition)
    {
        if (_descendants.TryGetValue(transition.TargetId, out var beyond) && beyond.Count > 0)
        {
            bool failing = transition.To is HostState.Faltering or HostState.Down;
            bool recovering = transition.From is HostState.Down or HostState.Faltering &&
                              transition.To is HostState.Recovering or HostState.Responding;
            if (failing || recovering)
            {
                // Reach beyond NOW (D5): its evidence decides what this hop's state means.
                foreach (var id in beyond)
                    _monitors[id].Discipline.ForceBurst();
            }
            if (transition.From == HostState.Down && recovering)
            {
                _log.LogInformation("{Target} recovered; releasing what it explained", transition.TargetName);
                foreach (var id in beyond)
                {
                    var monitor = _monitors[id];
                    if (monitor.ExplainedBy == transition.TargetId)
                    {
                        monitor.Release();
                        monitor.Discipline.ForceBurst();
                    }
                }
            }
        }
        Recompute();
    }

    /// <summary>
    /// One thread at a time recomputes; a request that arrives meanwhile (from
    /// another thread, or re-entrantly from a transition this pass published)
    /// marks the pass dirty and returns, and the running thread goes round
    /// again. No lock is held while monitors publish.
    /// </summary>
    private void Recompute()
    {
        Interlocked.Exchange(ref _dirty, 1);
        while (Interlocked.CompareExchange(ref _recomputing, 1, 0) == 0)
        {
            try
            {
                while (Interlocked.Exchange(ref _dirty, 0) == 1)
                {
                    foreach (var (id, monitor) in _monitors)
                        monitor.SetExplanation(RootCause(id));
                }
            }
            finally
            {
                Volatile.Write(ref _recomputing, 0);
            }
            if (Volatile.Read(ref _dirty) == 0)
                return;
        }
    }

    private readonly Lock _disposeLock = new();

    public void Dispose()
    {
        List<IDisposable> toDispose;
        lock (_disposeLock)
        {
            toDispose = new List<IDisposable>(_subscriptions);
            _subscriptions.Clear();
        }
        foreach (var s in toDispose)
            s.Dispose();
        GC.SuppressFinalize(this);
    }
}
```

- [ ] **Step 4: Run to verify they pass**

Run: `dotnet test Tests/ConEngine.Tests -c Debug`, then `dotnet test Tests/ConWatch.Tests -c Debug --filter "FullyQualifiedName~HistoryApiTests|FullyQualifiedName~EndToEndTests|FullyQualifiedName~SignalRTests|FullyQualifiedName~WarmRestartTests"`.

Expected: TopologyCoordinatorTests, WatcherEngineTests, WarmStartTests, HostAnalysisEngineTests and TargetMonitorTests PASS. **VerdictCoordinatorTests**: the tests that expect a down *hop alone* to produce `LinkDown`/`LinkDegraded` now fail (descendants are no longer parked, so their anchors stay healthy). Task 4 rewrites them, so record which ones. Every other test passes. If `EndToEndTests` fails here, record it too; Task 4 re-checks it.

- [ ] **Step 5: Commit**

```bash
git add Core Tests
git commit -m "feat(engine): root causes from evidence; reach beyond a hop the moment it changes"
```

---

### Task 4: VerdictCoordinator — anchors decide, the path assigns blame

**Files:**
- Modify: `Core/ConEngine/Topology/VerdictCoordinator.cs`
- Test: `Tests/ConEngine.Tests/VerdictCoordinatorTests.cs`

**Interfaces:**
- Consumes: Task 3's states (no parking, Unreachable only when explained).
- Produces:
  - **Classification** (spec §4.1–4.3):
    1. If no anchor is determined: `Unknown`.
    2. Otherwise compute **reachability**:
       - If the ISP is known (some path hop has an operator tag) and at least one anchor is off-net (its operator isn't an ISP tag): `required = 1`, counting off-net anchors that are answering.
       - Otherwise `required = min(2, independent anchor operators configured)`, counting answering independent operators.
    3. If `answering ≥ required`, classify quality from the anchors: `Healthy` / `SiteIncident` / `InternetWeather`. At weather level, if some path hop is `Faltering` or `Down`, it is `LinkDegraded` blaming the deepest such hop. This interim attribution is refined by Plan D's loss analysis.
    4. If `answering < required` and some counted anchor is undetermined: `Unknown` ("Waiting for anchors to report").
    5. If `answering == 0`: `LinkDown`, frontier blame.
    6. Otherwise: `LinkDegraded`, frontier blame, plus `"; only {answering} answering, {required} needed"`. With no path hops at all, the detail is just `"LinkDegraded: only 1 answering, 2 needed"`.
  - **Frontier blame:** for each failing anchor's path (root first), a hop is proven when it or any monitored descendant answers. The frontier is the last proven index, and the blamed hop is the next one.
    - Wording: `"{V}: {Op} — {Hop} is {State}"`.
    - When every path hop is proven: `"{V}: {LastHopOp} — nothing answers beyond {LastHop}"`.
    - With no path hops at all: `"{V}: no anchor is answering"`.
    - The shallowest blamed index across anchors wins.
  - `CurrentOperator` = the blamed hop's operator (or the last hop's, for "beyond").
  - The verdict now **subscribes to every monitor**: proof of forwarding can come from any descendant.

- [ ] **Step 1: Write the failing tests**

In `VerdictCoordinatorTests.cs`:

(a) Add helpers after `AllHealthy()`:

```csharp
    private void Fail(params string[] names)
    {
        foreach (var name in names)
            Feed(name, 20, false);
    }

    private VerdictState Verdict => _verdict.CurrentVerdict;
```

(b) **Replace** `Link_verdicts_name_the_operator_when_the_blamed_hop_has_one`, `Link_verdicts_without_an_operator_keep_plain_wording`, `Ancestor_down_beats_weather` and `Ancestor_faltering_is_link_degraded` with:

```csharp
    [Fact]
    public void A_down_hop_alone_is_not_an_internet_outage()
    {
        // The 2026-10-08 incident at verdict level: the anchors still answer.
        AllHealthy();
        Fail("Modem");
        Assert.Equal(HostState.Down, _monitors[_graph.GetByName("Modem").Id].CurrentState);
        Assert.Equal(VerdictState.Healthy, Verdict);
        Assert.DoesNotContain(_verdictTransitions, t => t.Reason.Verdict == "LinkDown");
    }

    [Fact]
    public void Link_verdicts_name_the_operator_when_the_blamed_hop_has_one()
    {
        AllHealthy();
        Fail("Modem", "Google", "Cloudflare", "Quad9"); // Modem carries Operator = "Comcast"
        Assert.Equal(VerdictState.LinkDown, Verdict);
        Assert.Equal("LinkDown: Comcast — Modem is Down", _verdict.CurrentDetail);
        Assert.Equal("Comcast", _verdict.CurrentOperator);

        Feed("Modem", 20, true);
        AllHealthy(); // released anchors re-prove from Starting
        Assert.Equal(VerdictState.Healthy, Verdict);
        Assert.Null(_verdict.CurrentOperator);
    }

    [Fact]
    public void Link_verdicts_without_an_operator_keep_plain_wording()
    {
        AllHealthy();
        Fail("Router", "Modem", "Google", "Cloudflare", "Quad9"); // Router: Mark's own gear, untagged
        Assert.Equal(VerdictState.LinkDown, Verdict);
        Assert.Equal("LinkDown: Router is Down", _verdict.CurrentDetail);
        Assert.Null(_verdict.CurrentOperator);
    }

    [Fact]
    public void Blame_skips_a_hop_that_traffic_passes_and_lands_after_the_last_proven_one()
    {
        AllHealthy();
        Fail("Router");                            // Router's probe fails…
        Fail("Google", "Cloudflare", "Quad9");     // …and the anchors fail, but Modem still answers
        Assert.Equal(VerdictState.LinkDown, Verdict);
        Assert.Equal("LinkDown: Comcast — nothing answers beyond Modem", _verdict.CurrentDetail);
        Assert.Equal("Comcast", _verdict.CurrentOperator);
    }

    [Fact]
    public void A_faltering_hop_with_healthy_anchors_is_still_healthy()
    {
        AllHealthy();
        Feed("Modem", 3, false);
        Assert.Equal(HostState.Faltering, _monitors[_graph.GetByName("Modem").Id].CurrentState);
        Assert.Equal(VerdictState.Healthy, Verdict);
    }

    [Fact]
    public void Weather_shared_by_a_faltering_path_hop_is_link_degraded()
    {
        AllHealthy();
        Fail("Google", "Cloudflare");   // weather level (threshold 2)…
        Feed("Modem", 3, false);        // …and our own hop is losing too
        Assert.Equal(VerdictState.LinkDegraded, Verdict);
        Assert.Contains("Modem", _verdict.CurrentDetail);
    }

    [Fact]
    public void One_off_net_anchor_answering_proves_the_internet_is_up()
    {
        AllHealthy();
        Fail("Google", "Cloudflare");   // Quad9 still answers; the ISP (Comcast) is known
        Assert.NotEqual(VerdictState.LinkDown, Verdict);
        Assert.NotEqual(VerdictState.LinkDegraded, Verdict);
        Assert.Equal(VerdictState.InternetWeather, Verdict);
    }
```

(c) Change `OperatorTopology()` so that the ISP is known. Insert an ISP-tagged edge between the router and the anchors:

```csharp
                new TargetConfig { Name = "Router", Address = "192.168.1.1", Profile = "Internal" },
                new TargetConfig { Name = "Edge", Address = "203.0.113.1", Parent = "Router", Operator = "Comcast" },
                new TargetConfig { Name = "MS-1", Address = "20.0.0.1", Parent = "Edge", Operator = "Microsoft" },
                new TargetConfig { Name = "MS-2", Address = "20.0.0.2", Parent = "Edge", Operator = "microsoft " },
                new TargetConfig { Name = "MS-3", Address = "20.0.0.3", Parent = "Edge", Operator = "Microsoft" },
                new TargetConfig { Name = "Google", Address = "8.8.8.8", Parent = "Edge" },
```

(d) Add, after the operator tests:

```csharp
    /// <summary>Builds a coordinator stack (topology + verdict) for an ad-hoc topology and makes it healthy.</summary>
    private (VerdictCoordinator Verdict, Dictionary<Guid, TargetMonitor> Monitors, TargetGraph Graph) Stack(WatcherOptions options)
    {
        var graph = TargetGraph.FromOptions(options);
        var monitors = new Dictionary<Guid, TargetMonitor>();
        foreach (var target in graph.Targets.Values)
        {
            var monitor = new TargetMonitor(target, Fast, new PingDiscipline(Fast, _time),
                _time, NullLoggerFactory.Instance);
            monitor.StartTracking(IPAddress.Parse(target.Address));
            monitors[target.Id] = monitor;
        }
        new TopologyCoordinator(graph, monitors, NullLogger<TopologyCoordinator>.Instance).Start();
        var verdict = new VerdictCoordinator(graph, graph.GetPoolByName("Internet"), monitors,
            weatherThreshold: 2, _time, NullLogger<VerdictCoordinator>.Instance);
        verdict.Start();
        foreach (var m in monitors.Values)
            for (int i = 0; i < 5; i++)
            {
                _time.Advance(TimeSpan.FromSeconds(1));
                m.RecordPingResult(new PingResult(m.ResolvedAddress!, true, 5, timestamp: _time.GetUtcNow()));
            }
        return (verdict, monitors, graph);
    }

    [Fact]
    public void An_isp_owned_anchor_never_proves_the_internet_is_up()
    {
        // The ISP is cut off from the rest of the internet: its own DNS still answers.
        var (verdict, monitors, graph) = Stack(new WatcherOptions
        {
            Targets =
            [
                new TargetConfig { Name = "Router", Address = "192.168.1.1", Profile = "Internal" },
                new TargetConfig { Name = "Edge", Address = "203.0.113.1", Parent = "Router", Operator = "Comcast" },
                new TargetConfig { Name = "Comcast DNS", Address = "75.75.75.75", Parent = "Edge", Operator = " comcast" },
                new TargetConfig { Name = "Google", Address = "8.8.8.8", Parent = "Edge" },
            ],
            Pools = [new PoolConfig { Name = "Internet", Members = ["Comcast DNS", "Google"] }],
        });
        Assert.Equal(VerdictState.Healthy, verdict.CurrentVerdict);

        FeedDown(monitors, graph, "Google");
        Assert.Equal(VerdictState.LinkDown, verdict.CurrentVerdict);
        Assert.Equal("LinkDown: Comcast — nothing answers beyond Edge", verdict.CurrentDetail);
    }

    [Fact]
    public void With_the_isp_unknown_one_answering_anchor_is_not_enough()
    {
        var (verdict, monitors, graph) = Stack(new WatcherOptions
        {
            Targets =
            [
                new TargetConfig { Name = "Router", Address = "192.168.1.1", Profile = "Internal" },
                new TargetConfig { Name = "Google", Address = "8.8.8.8", Parent = "Router" },
                new TargetConfig { Name = "Cloudflare", Address = "1.1.1.1", Parent = "Router" },
                new TargetConfig { Name = "Quad9", Address = "9.9.9.9", Parent = "Router" },
            ],
            Pools = [new PoolConfig { Name = "Internet", Members = ["Google", "Cloudflare", "Quad9"] }],
        });

        FeedDown(monitors, graph, "Google");
        FeedDown(monitors, graph, "Cloudflare");
        Assert.Equal(VerdictState.LinkDegraded, verdict.CurrentVerdict);
        Assert.Contains("only 1 answering, 2 needed", verdict.CurrentDetail);
        Assert.Contains("nothing answers beyond Router", verdict.CurrentDetail);
    }

    [Fact]
    public void With_the_isp_unknown_two_answering_anchors_prove_it_up()
    {
        var (verdict, monitors, graph) = Stack(new WatcherOptions
        {
            Targets =
            [
                new TargetConfig { Name = "Router", Address = "192.168.1.1", Profile = "Internal" },
                new TargetConfig { Name = "Google", Address = "8.8.8.8", Parent = "Router" },
                new TargetConfig { Name = "Cloudflare", Address = "1.1.1.1", Parent = "Router" },
                new TargetConfig { Name = "Quad9", Address = "9.9.9.9", Parent = "Router" },
            ],
            Pools = [new PoolConfig { Name = "Internet", Members = ["Google", "Cloudflare", "Quad9"] }],
        });

        FeedDown(monitors, graph, "Google");
        Assert.Equal(VerdictState.SiteIncident, verdict.CurrentVerdict);
    }

    [Fact]
    public void Anchors_hung_directly_off_the_root_with_none_answering_say_so()
    {
        var (verdict, monitors, graph) = Stack(new WatcherOptions
        {
            Targets =
            [
                new TargetConfig { Name = "Google", Address = "8.8.8.8" },
                new TargetConfig { Name = "Cloudflare", Address = "1.1.1.1" },
            ],
            Pools = [new PoolConfig { Name = "Internet", Members = ["Google", "Cloudflare"] }],
        });

        FeedDown(monitors, graph, "Google");
        Assert.Equal(VerdictState.LinkDegraded, verdict.CurrentVerdict); // ISP unknown: one of two can't prove it
        Assert.Equal("LinkDegraded: only 1 answering, 2 needed", verdict.CurrentDetail);

        FeedDown(monitors, graph, "Cloudflare");
        Assert.Equal(VerdictState.LinkDown, verdict.CurrentVerdict);
        Assert.Equal("LinkDown: no anchor is answering", verdict.CurrentDetail);
    }
```

(`FeedDown` already exists in this file. Keep `Below_threshold_anchor_count_carries_limited_confidence_qualifier` unchanged. In its 1-anchor topology, required = 1 and the qualifier text stays.)

(e) In `Current_is_one_snapshot_whose_detail_matches_its_state`, replace `Feed("Modem", 10, false);` with `Fail("Modem", "Google", "Cloudflare", "Quad9");`. A down Modem alone is no longer an outage. The anchors must agree.

(f) **`Tests/ConWatch.Tests` expectations that change by design.** `ConfigApiTests.SeedHomeAsync` builds Router (no operator) → Google, Cloudflare (no operators): the ISP is **unknown** and there are only **two** anchors. Under D2, *one* anchor down means the internet can no longer be proven up (one answering, two needed), which is `LinkDegraded`, not `SiteIncident`. With both down it is `LinkDown`. So update:
- `EndToEndTests.Verdict_walks_the_full_scenario_and_history_records_it`:
  - The awaited verdicts become: Google down → `"LinkDegraded"`, Cloudflare down → `"LinkDown"`, router down → still `"LinkDown"` (now blaming the router), full recovery → `"Healthy"`.
  - Change `expected` to `{ "Healthy", "LinkDegraded", "LinkDown", "Healthy" }`.
  - Update the comments: "One anchor down: with the ISP unknown, one answering anchor can't prove the internet up", "Both anchors down: nothing answers beyond the router", "Router down too: now the router itself is blamed".
  - Keep the SiteIncident and weather coverage in the engine unit tests (they use an ISP-tagged hop).
- `MetricsEndpointTests.Downed_target_flips_gauges_and_bumps_the_transition_counter`: replace the expected `conwatch_verdict{state="SiteIncident"} 1` with `conwatch_verdict{state="LinkDegraded"} 1` (keep the C# escaping the test already uses) and add the comment `// two untagged anchors, one down: "up" can no longer be proven (spec 2026-10-09 D2)`.

Run them in Step 4.

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test Tests/ConEngine.Tests -c Debug --filter "FullyQualifiedName~VerdictCoordinatorTests"`
Expected: the new tests FAIL, for example `A_down_hop_alone_is_not_an_internet_outage` gives `LinkDown`, and the ISP tests fail.

- [ ] **Step 3: Implement**

In `VerdictCoordinator.cs`:

1. Change the class summary to:

```csharp
/// <summary>
/// Classifies overall internet health (spec 2026-10-09 §4.1–4.3). The ANCHORS
/// decide whether the internet is reachable — one answering off-net anchor
/// when the ISP is known (an operator tag on the path), otherwise two
/// independent ones — and how good it is (Healthy / SiteIncident /
/// InternetWeather). The PATH only assigns blame when the anchors say it is
/// down: the first silent hop after the last one with proven forwarding.
/// Evidence from farther away outranks evidence from nearer.
/// </summary>
```

2. Add fields after `_ancestors`:

```csharp
    private readonly IReadOnlyDictionary<Guid, TargetMonitor> _all;
    // Each anchor with its path, root first (monitored hops only).
    private readonly List<(TargetMonitor Anchor, List<TargetMonitor> Path)> _paths = new();
    private readonly Dictionary<Guid, List<Guid>> _descendants = new();
    // Anchors outside the ISP; empty when the ISP is unknown or every anchor is the ISP's.
    private readonly List<TargetMonitor> _offNet;
```

3. At the end of the constructor, add:

```csharp
        _all = monitors;
        foreach (var anchor in _anchors)
        {
            var path = new List<TargetMonitor>();
            var parent = _graph.Targets[anchor.Target.Id].ParentId;
            while (parent is { } pid)
            {
                if (monitors.TryGetValue(pid, out var hop))
                    path.Add(hop);
                parent = _graph.Targets[pid].ParentId;
            }
            path.Reverse();
            _paths.Add((anchor, path));
        }
        foreach (var (monitor, _) in _ancestors)
        {
            _descendants[monitor.Target.Id] = _graph.GetDescendants(monitor.Target.Id)
                .Select(d => d.Id).Where(monitors.ContainsKey).ToList();
        }
        // The ISP is whoever operates the path; an anchor sharing that tag is on-net.
        var isp = _ancestors.Select(a => Normalize(a.Monitor.Target.Operator))
            .OfType<string>().ToHashSet();
        _offNet = isp.Count == 0
            ? new List<TargetMonitor>()
            : _anchors.Where(a => Normalize(a.Target.Operator) is not { } op || !isp.Contains(op)).ToList();
```

4. In `Start()`, subscribe to **every** monitor (proof of forwarding can come from any descendant):

```csharp
        foreach (var m in _all.Values)
            _subscriptions.Add(m.TransitionSource.Subscribe(_ => Recalculate()));
```

(This replaces the `foreach (var m in _anchors.Concat(_ancestors…` line.)

5. In `Recalculate`, replace the two snapshot lines and the `Classify` call with one snapshot of every state:

```csharp
            // One read per monitor: classification, detail and blame all come
            // from the same snapshot, so the detail can never contradict the state.
            var states = _all.ToDictionary(kv => kv.Key, kv => kv.Value.CurrentState);
            var (verdict, detail, operatorName) = Classify(states);
```

6. Replace `Classify` (and its local `Blame`) with:

```csharp
    private static bool Answering(HostState s) => TopologyCoordinator.Answering(s);

    private static bool Undetermined(HostState s) => s is HostState.Initializing or HostState.Starting;

    private static string? Normalize(string? op) =>
        op?.Trim() is { Length: > 0 } t ? t.ToLowerInvariant() : null;

    private (VerdictState, string, string? Operator) Classify(IReadOnlyDictionary<Guid, HostState> states)
    {
        var anchors = _anchors.Select(a => (Monitor: a, State: states[a.Target.Id])).ToList();
        var determined = anchors.Where(a => !Undetermined(a.State)).ToList();
        if (determined.Count == 0)
            return (VerdictState.Unknown, "No anchors determined yet", null);

        // 1. Reachability: did packets get out and back? (spec §4.1, D2)
        int answering, required;
        bool waiting;
        if (_offNet.Count > 0)
        {
            var counted = anchors.Where(a => _offNet.Contains(a.Monitor)).ToList();
            answering = counted.Count(a => Answering(a.State));
            required = 1;
            waiting = counted.Any(a => Undetermined(a.State));
        }
        else
        {
            answering = CountOperators(anchors.Where(a => Answering(a.State)).Select(a => a.Monitor));
            required = Math.Min(2, CountOperators(_anchors));
            waiting = anchors.Any(a => Undetermined(a.State));
        }

        if (answering >= required)
            return Quality(determined, states);
        if (waiting)
            return (VerdictState.Unknown, "Waiting for anchors to report", null);

        // 2. Down or partial: the path says where (spec §4.3).
        return answering == 0
            ? Blame(VerdictState.LinkDown, states, suffix: "")
            : Blame(VerdictState.LinkDegraded, states, suffix: $"only {answering} answering, {required} needed");
    }

    /// <summary>The internet is reachable: how good is it? Anchor evidence only,
    /// except that weather our own path shares is our link (interim attribution;
    /// Plan D replaces it with burst-aligned loss attribution).</summary>
    private (VerdictState, string, string? Operator) Quality(
        List<(TargetMonitor Monitor, HostState State)> determined, IReadOnlyDictionary<Guid, HostState> states)
    {
        var degraded = determined.Where(a => a.State
            is HostState.Down or HostState.Faltering or HostState.Recovering).ToList();
        string List() => string.Join(", ", degraded.Select(a => $"{a.Monitor.Target.Name}={a.State}"));

        // Weather needs INDEPENDENT evidence. Anchors that share an operator
        // ("Microsoft" four times, one per A record) share a path and fail
        // together, so they count once; untagged anchors count individually.
        var determinedUnits = CountOperators(determined.Select(a => a.Monitor));
        var degradedUnits = CountOperators(degraded.Select(a => a.Monitor));
        var grouped = degradedUnits != degraded.Count || determinedUnits != determined.Count;

        // With fewer independent anchors than the weather threshold, weather can
        // never be diagnosed — say so rather than implying full confidence.
        var qualifier = determinedUnits < _weatherThreshold
            ? $" (only {determinedUnits} {(grouped ? "operator(s)" : "anchor(s)")} — cannot distinguish a site incident from internet weather)"
            : "";

        if (degraded.Count == 0)
            return (VerdictState.Healthy, $"Healthy: {determined.Count} anchors responding{qualifier}", null);
        if (degradedUnits < _weatherThreshold)
        {
            var sharedOperator = degraded.Count > 1 && degradedUnits == 1
                ? $" — all {degraded[0].Monitor.Target.Operator}" : "";
            return (VerdictState.SiteIncident, $"SiteIncident: {List()}{sharedOperator}{qualifier}", null);
        }

        var losingHop = _ancestors
            .Where(a => states[a.Monitor.Target.Id] is HostState.Faltering or HostState.Down)
            .OrderByDescending(a => a.Depth).FirstOrDefault();
        if (losingHop.Monitor is not null)
            return Wording(VerdictState.LinkDegraded, losingHop.Monitor,
                $"{losingHop.Monitor.Target.Name} is {states[losingHop.Monitor.Target.Id]}", "");

        var counts = grouped
            ? $"{degradedUnits} of {determinedUnits} operators ({degraded.Count} of {determined.Count} anchors)"
            : $"{degraded.Count} of {determined.Count} anchors";
        return (VerdictState.InternetWeather, $"InternetWeather: {counts} degraded ({List()})", null);
    }

    /// <summary>Frontier blame: along each failing anchor's path, the last hop
    /// with proven forwarding (it or anything beyond it answers) is the
    /// frontier; the hop after it is blamed. The shallowest blame wins.</summary>
    private (VerdictState, string, string? Operator) Blame(
        VerdictState verdict, IReadOnlyDictionary<Guid, HostState> states, string suffix)
    {
        bool Proven(TargetMonitor hop) =>
            Answering(states[hop.Target.Id]) ||
            _descendants[hop.Target.Id].Any(id => Answering(states[id]));

        TargetMonitor? blamed = null, beyond = null;
        int bestIndex = int.MaxValue;
        foreach (var (anchor, path) in _paths)
        {
            if (Answering(states[anchor.Target.Id]))
                continue;
            int frontier = -1;
            for (int i = 0; i < path.Count; i++)
                if (Proven(path[i]))
                    frontier = i;
            int index = frontier + 1;
            if (index >= bestIndex)
                continue;
            bestIndex = index;
            (blamed, beyond) = index < path.Count
                ? (path[index], (TargetMonitor?)null)
                : ((TargetMonitor?)null, path.Count > 0 ? path[^1] : null);
        }

        if (blamed is not null)
            return Wording(verdict, blamed, $"{blamed.Target.Name} is {states[blamed.Target.Id]}", suffix);
        if (beyond is not null)
            return Wording(verdict, beyond, $"nothing answers beyond {beyond.Target.Name}", suffix);
        // No path hops at all (anchors hung off the root).
        return (verdict, $"{verdict}: {(suffix.Length > 0 ? suffix : "no anchor is answering")}", null);
    }

    /// <summary>Link verdicts name the blamed hop's operator ("Comcast") when tagged;
    /// an optional suffix ("only 1 answering, 2 needed") follows after "; ".</summary>
    private static (VerdictState, string, string?) Wording(VerdictState verdict, TargetMonitor hop, string what, string suffix)
    {
        var op = hop.Target.Operator;
        var text = op is { Length: > 0 } ? $"{verdict}: {op} — {what}" : $"{verdict}: {what}";
        if (suffix.Length > 0)
            text += "; " + suffix;
        return (verdict, text, op is { Length: > 0 } ? op : null);
    }
```

Keep `CountOperators`, `Map` and `Dispose` unchanged. Delete the old `Classify` signature, which took the anchors and ancestors lists.

- [ ] **Step 4: Run to verify they pass**

Run: `dotnet test Tests/ConEngine.Tests -c Debug` (all of it), then `dotnet test Tests/ConWatch.Tests -c Debug --filter "FullyQualifiedName~EndToEndTests|FullyQualifiedName~HistoryApiTests|FullyQualifiedName~McpEndpointTests|FullyQualifiedName~MetricsEndpointTests|FullyQualifiedName~WarmRestartTests|FullyQualifiedName~CloneTests"`.

Expected: PASS with the (f) expectation changes. If any other ConWatch test asserted `SiteIncident`/`InternetWeather` after taking down one of SeedHome's two anchors, it changes the same way. List it in the report; don't weaken unrelated assertions.

- [ ] **Step 5: Commit**

```bash
git add Core Tests
git commit -m "feat(verdict): anchors decide reachability (one off-net, else two); the path only assigns blame"
```

---

### Task 5: Webhooks skip explained transitions (default on)

**Files:**
- Create: `Libraries/ConNotify/WebhookFilter.cs`
- Modify: `Libraries/ConNotify/WebhookNotifier.cs`, `Libraries/ConPersistence/Settings/SettingsStore.cs`, `Programs/ConWatch/Api/SettingsEndpoints.cs`, `Programs/ConWatch/Pages/Settings.cshtml`
- Test: create `Tests/ConNotify.Tests/WebhookFilterTests.cs`; modify `Tests/ConWatch.Tests/SettingsApiTests.cs`

**Interfaces:**
- Produces:
  - `SettingKeys.WebhookSkipExplained = "Webhook.SkipExplained"`. Anything but `"false"` means on.
  - `WebhookFilter.ShouldDeliver(TargetTransition t, bool verdictOnly, bool skipExplained) : bool`.
  - Explained traffic is a target transition **to** `Unreachable`, or **from** `Unreachable` to `Starting`/`Recovering`/`Responding` (the incident's own recovery). `Unreachable → Down` ("no longer explained": it is now its own problem) is **delivered**.

- [ ] **Step 1: Write the failing tests**

Create `Tests/ConNotify.Tests/WebhookFilterTests.cs`:

```csharp
using ConEngine.Events;
using ConEngine.Tracking;
using ConNotify;

namespace ConNotify.Tests;

public class WebhookFilterTests
{
    private static TargetTransition T(HostState from, HostState to,
        TransitionSubjectKind kind = TransitionSubjectKind.Target, TransitionTrigger trigger = TransitionTrigger.ThresholdCrossed) => new()
    {
        Kind = kind, TargetId = Guid.NewGuid(), TargetName = "Google", From = from, To = to,
        Timestamp = DateTimeOffset.Parse("2026-10-09T00:00:00Z"), Reason = new TransitionReason { Trigger = trigger },
    };

    [Fact]
    public void An_explained_failure_and_its_recovery_are_skipped_by_default()
    {
        Assert.False(WebhookFilter.ShouldDeliver(T(HostState.Faltering, HostState.Unreachable, trigger: TransitionTrigger.ParentDown), false, true));
        Assert.False(WebhookFilter.ShouldDeliver(T(HostState.Unreachable, HostState.Starting, trigger: TransitionTrigger.ParentRecovered), false, true));
        Assert.False(WebhookFilter.ShouldDeliver(T(HostState.Unreachable, HostState.Recovering), false, true));
    }

    [Fact]
    public void A_target_no_longer_explained_is_its_own_problem_and_is_delivered()
    {
        Assert.True(WebhookFilter.ShouldDeliver(T(HostState.Unreachable, HostState.Down), false, true));
    }

    [Fact]
    public void The_root_cause_and_ordinary_transitions_are_delivered()
    {
        Assert.True(WebhookFilter.ShouldDeliver(T(HostState.Faltering, HostState.Down), false, true));
        Assert.True(WebhookFilter.ShouldDeliver(T(HostState.Down, HostState.Recovering), false, true));
    }

    [Fact]
    public void With_skipping_off_everything_is_delivered()
    {
        Assert.True(WebhookFilter.ShouldDeliver(T(HostState.Faltering, HostState.Unreachable, trigger: TransitionTrigger.ParentDown), false, false));
    }

    [Fact]
    public void Verdict_only_still_drops_targets_and_keeps_verdicts()
    {
        Assert.False(WebhookFilter.ShouldDeliver(T(HostState.Responding, HostState.Faltering), true, false));
        Assert.True(WebhookFilter.ShouldDeliver(
            T(HostState.Responding, HostState.Down, TransitionSubjectKind.Verdict, TransitionTrigger.VerdictChanged), true, true));
    }
}
```

In `Tests/ConWatch.Tests/SettingsApiTests.cs`, add to the `Valid_notifier_settings_are_accepted` theory:

```csharp
    [InlineData("Webhook.SkipExplained", "false")]
```

and add to the invalid-values theory next to it (the one with `[InlineData("Mqtt.Port", "70000")]`):

```csharp
    [InlineData("Webhook.SkipExplained", "maybe")]
```

(If that theory asserts a specific error text, match the boolean wording `must be 'true' or 'false'`.)

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test Tests/ConNotify.Tests -c Debug --filter "FullyQualifiedName~WebhookFilterTests"`
Expected: a build error (`WebhookFilter` missing).

- [ ] **Step 3: Implement**

Create `Libraries/ConNotify/WebhookFilter.cs`:

```csharp
using ConEngine.Events;
using ConEngine.Tracking;

namespace ConNotify;

/// <summary>Which transitions a webhook receives (pure, unit-tested).</summary>
public static class WebhookFilter
{
    /// <param name="verdictOnly">Only the headline verdict.</param>
    /// <param name="skipExplained">Drop a failure explained by an upstream root
    /// cause, and that failure's recovery: one router outage is one incident,
    /// not one per target behind it (spec 2026-10-09 §4.4, D7).</param>
    public static bool ShouldDeliver(TargetTransition t, bool verdictOnly, bool skipExplained)
    {
        if (verdictOnly && t.Kind != TransitionSubjectKind.Verdict)
            return false;
        if (skipExplained && t.Kind == TransitionSubjectKind.Target)
        {
            if (t.To == HostState.Unreachable)
                return false;
            if (t.From == HostState.Unreachable &&
                t.To is HostState.Starting or HostState.Recovering or HostState.Responding)
                return false;
        }
        return true;
    }
}
```

`SettingsStore.cs`, after `WebhookVerdictOnly`:

```csharp
    /// <summary>Anything but "false" (including absent): skip target transitions
    /// explained by an upstream root cause, and their recovery.</summary>
    public const string WebhookSkipExplained = "Webhook.SkipExplained";
```

`WebhookNotifier.DeliverAsync`, replace the verdict-only block

```csharp
        if (await settings.GetAsync(SettingKeys.WebhookVerdictOnly) == "true" &&
            transition.Kind != TransitionSubjectKind.Verdict)
            return;
```

with

```csharp
        var verdictOnly = await settings.GetAsync(SettingKeys.WebhookVerdictOnly) == "true";
        var skipExplained = await settings.GetAsync(SettingKeys.WebhookSkipExplained) != "false";
        if (!WebhookFilter.ShouldDeliver(transition, verdictOnly, skipExplained))
            return;
```

`SettingsEndpoints.cs`: add `case SettingKeys.WebhookSkipExplained:` to the boolean case list (next to `case SettingKeys.WebhookVerdictOnly:`).

`Settings.cshtml`: after the "Verdict changes only" `<label>`, add:

```html
        <label>Skip explained failures
            <select name="@SettingKeys.WebhookSkipExplained">
                <option value="true" selected="@(Model.Setting(SettingKeys.WebhookSkipExplained, "true") != "false")">yes — one message per root cause</option>
                <option value="false" selected="@(Model.Setting(SettingKeys.WebhookSkipExplained) == "false")">no — every target behind it too</option>
            </select>
        </label>
```

(If the surrounding grid has a fixed column count, keep the layout sane. Check the page in a browser in Step 4.)

- [ ] **Step 4: Run to verify they pass**

Run: `dotnet test Tests/ConNotify.Tests -c Debug`, then `dotnet test Tests/ConWatch.Tests -c Debug --filter "FullyQualifiedName~SettingsApiTests|FullyQualifiedName~NotifierIntegrationTests|FullyQualifiedName~PageSmokeTests"`.
Expected: PASS.

Then run this worktree's server on a scratch database: `dotnet run --project Programs/ConWatch -c Release -- --urls http://localhost:18099 --Persistence:DbPath <scratch>/conwatch.db` (never `preview_start conwatch-preview`, which runs the main checkout). Open `/Settings` and confirm the new select renders in the webhook section, defaults to "yes", and saves. Stop the server and delete the scratch data.

- [ ] **Step 5: Commit**

```bash
git add Libraries Programs Tests
git commit -m "feat(webhooks): skip transitions explained by an upstream root cause (default on)"
```

---

### Task 6: Whole-branch verification

**Files:** none, unless a defect is found (fix it in the owning task's files).

- [ ] **Step 1: Run every suite**

```bash
dotnet test Tests/ConEngine.Tests -c Debug
dotnet test Tests/ConPersistence.Tests -c Debug
dotnet test Tests/ConWatch.Tests -c Debug
dotnet test Tests/ConConfig.Tests -c Debug
dotnet test Tests/ConWatchCli.Tests -c Debug
dotnet test Tests/ConNotify.Tests -c Debug
dotnet build Programs/ConWatchTray -c Debug
```

Expected: all PASS, apart from the known flakes (rerun once). Baselines before this plan: ConEngine 285, ConPersistence 203, ConWatch 335, ConConfig 36, ConWatchCli 64, ConNotify 32.

- [ ] **Step 2: Replay the incident end to end on a scratch server**

Run this worktree's Release build on port 18099 with a scratch database (as in Task 5). Through the REST API, create the profile "Quick" (`normalIntervalSeconds 1`, `burstIntervalSeconds 0.5`, `burstMaxDurationSeconds 5`, `historySize 20`, `minUsableSample 5`). Then create:
- Router `127.0.0.1`.
- Inside `127.0.0.2` (parent Router, probe `tcp:9` — a closed port, so its probe fails while ICMP-probed targets beyond it answer).
- Edge `127.0.0.3` (parent Inside, operator "Comcast").
- Anchors Google `127.0.0.4` and Cloudflare `127.0.0.5` (parent Edge).
- A pool "Internet" of the two anchors, set as the anchor pool in Settings.

Expected over about 60 s:
- Inside goes Down or stays Down (if the closed port is answered with a refusal, Inside will instead show Responding; in that case use an unroutable test address for Inside, e.g. `192.0.2.1`, and keep everything else on loopback).
- Edge and the anchors stay Responding.
- `/api/v1/status` reports verdict `Healthy` throughout.
- No transition with `verdict: "LinkDown"` appears in `/api/v1/transitions`.

Record the counts and states (no real addresses involved; all are loopback or RFC 5737). Stop the server and delete the scratch data.

- [ ] **Step 3: Commit only if something was fixed**

```bash
git status --short
git add -A -- Core Libraries Programs Tests
git commit -m "fix: address verification findings for the evidence-based verdict engine"
```

(Skip if clean. Do not push; hand the branch back for review before merging to `master`.)
