# Phase 1 Engine Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Upgrade the ConnectionWatcher engine to the Phase 1 design: Target domain model, reachability hierarchy with `Unreachable` state, adaptive per-target ping disciplines with burst mode, rotation pools, and structured transition events — all config-driven and unit-tested.

**Architecture:** Evolve the existing engine in place (Approach A from the spec at `docs/superpowers/specs/2026-08-14-connectionwatcher-engine-design.md`). Keep `PingManager`/`PingController`/`HostTracker`/`HostAnalysisEngine`; add `Target`/`PingProfile`/`PoolDefinition` domain types in ConCore, and `PingDiscipline`, `TargetMonitor`, `TopologyCoordinator`, `PoolCoordinator`, plus a rewritten `WatcherEngine` orchestrator in ConEngine. Rx observables remain the event backbone.

**Tech Stack:** .NET 10 (`net10.0`), System.Reactive 6.1.0, Microsoft.Extensions.* 10.0.0, xUnit, Microsoft.Extensions.TimeProvider.Testing (`FakeTimeProvider`), Serilog (ConTinker only).

## Global Constraints

- **NEVER `git push`.** Commit locally only, on branch `intersim`.
- Shell is **PowerShell** on Windows 11. Use `;` or `&&` chaining, not bash-isms.
- All engine/test projects target `net10.0`; ConTinker targets `net10.0-windows` (WPF simulator).
- Solution file: `ConnectionWatcher.sln` at repo root. Build with `dotnet build ConnectionWatcher.sln`.
- TDD: write the failing test first, watch it fail, implement, watch it pass, commit. One commit per task minimum.
- Test command form: `dotnet test Tests/ConEngine.Tests --filter "FullyQualifiedName~<TestClassName>"`.
- Existing thresholds semantics (from `HostAnalysisEngine`): success-rate compared against `RespondingThreshold` (0.95), `FalteringThreshold` (0.75), `DownThreshold` (0.10), `RecoveringThreshold` (0.25). Keep these semantics.
- Spec politeness invariants: externals never pinged faster than `BurstInterval`; bursts time-capped by `BurstMaxDuration` (cap applies in every burst-eligible state; window restarts on each entry into `Faltering`/`Recovering`/`Down`); `Unreachable` targets not pinged at all.
- XML doc comments on public types/members, matching existing code style.

---

### Task 1: Test project scaffold

**Files:**
- Create: `Tests/ConEngine.Tests/ConEngine.Tests.csproj` (via `dotnet new`)
- Create: `Tests/ConEngine.Tests/SmokeTests.cs`
- Delete: `Tests/ConEngine.Tests/UnitTest1.cs` (template artifact)

**Interfaces:**
- Consumes: nothing.
- Produces: a runnable xUnit project named `ConEngine.Tests` referencing `ConEngine` and `ConCore`, with `FakeTimeProvider` available (namespace `Microsoft.Extensions.Time.Testing`). All later tasks put tests here.

- [ ] **Step 1: Scaffold the project and wire references**

```powershell
dotnet new xunit -o Tests/ConEngine.Tests -n ConEngine.Tests
dotnet sln ConnectionWatcher.sln add Tests/ConEngine.Tests/ConEngine.Tests.csproj
dotnet add Tests/ConEngine.Tests reference Core/ConEngine/ConEngine.csproj
dotnet add Tests/ConEngine.Tests reference Core/ConCore/ConCore.csproj
dotnet add Tests/ConEngine.Tests package Microsoft.Extensions.TimeProvider.Testing
```

If the generated csproj's `<TargetFramework>` is not `net10.0`, edit it to `net10.0`.

- [ ] **Step 2: Replace the template test with a FakeTimeProvider smoke test**

Delete `UnitTest1.cs`. Create `Tests/ConEngine.Tests/SmokeTests.cs`:

```csharp
using Microsoft.Extensions.Time.Testing;

namespace ConEngine.Tests;

public class SmokeTests
{
    [Fact]
    public async Task FakeTimeProvider_drives_task_delay()
    {
        var time = new FakeTimeProvider();
        var delayTask = Task.Delay(TimeSpan.FromMinutes(5), time);

        Assert.False(delayTask.IsCompleted);
        time.Advance(TimeSpan.FromMinutes(5));
        await delayTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(delayTask.IsCompletedSuccessfully);
    }
}
```

- [ ] **Step 3: Run the test**

Run: `dotnet test Tests/ConEngine.Tests`
Expected: 1 passed.

- [ ] **Step 4: Commit**

```powershell
git add Tests/ ConnectionWatcher.sln
git commit -m "test: add ConEngine.Tests project with FakeTimeProvider"
```

---

### Task 2: Target domain types in ConCore

**Files:**
- Create: `Core/ConCore/Targets/Target.cs`
- Create: `Core/ConCore/Targets/PingProfile.cs`
- Create: `Core/ConCore/Targets/PoolDefinition.cs`
- Modify: `Core/ConCore/PingResult.cs` (injectable timestamp)
- Test: `Tests/ConEngine.Tests/PingProfileTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces (namespace `ConCore.Targets`):
  - `record Target { Guid Id; string Name; string Address; Guid? ParentId; string ProfileName; bool Enabled = true }` (all `required` except `ParentId`, `Enabled`)
  - `record PingProfile { string Name; TimeSpan NormalInterval; TimeSpan PingTimeout; TimeSpan BurstInterval; TimeSpan BurstMaxDuration; int HistorySize; int MinUsableSample; double RespondingThreshold; double FalteringThreshold; double DownThreshold; double RecoveringThreshold }` with static presets `PingProfile.Internal` and `PingProfile.External`
  - `record PoolDefinition { Guid Id; string Name; IReadOnlyList<Guid> MemberTargetIds; Guid? ParentId }`
  - `PingResult` gains optional ctor parameter `DateTimeOffset? timestamp = null` (defaults to `DateTimeOffset.UtcNow` as before).

- [ ] **Step 1: Write the failing test**

`Tests/ConEngine.Tests/PingProfileTests.cs`:

```csharp
using ConCore.Targets;

namespace ConEngine.Tests;

public class PingProfileTests
{
    [Fact]
    public void Internal_preset_pings_fast()
    {
        Assert.Equal("Internal", PingProfile.Internal.Name);
        Assert.Equal(TimeSpan.FromSeconds(1), PingProfile.Internal.NormalInterval);
    }

    [Fact]
    public void External_preset_pings_politely()
    {
        Assert.Equal("External", PingProfile.External.Name);
        Assert.Equal(TimeSpan.FromSeconds(10), PingProfile.External.NormalInterval);
        Assert.Equal(TimeSpan.FromSeconds(1), PingProfile.External.BurstInterval);
        Assert.Equal(TimeSpan.FromSeconds(60), PingProfile.External.BurstMaxDuration);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test Tests/ConEngine.Tests --filter "FullyQualifiedName~PingProfileTests"`
Expected: FAIL (compile error: `ConCore.Targets` does not exist).

- [ ] **Step 3: Implement the domain types**

`Core/ConCore/Targets/PingProfile.cs`:

```csharp
namespace ConCore.Targets;

/// <summary>
/// A named, reusable bundle of ping-discipline and analysis settings.
/// </summary>
public record PingProfile
{
    public required string Name { get; init; }
    public TimeSpan NormalInterval { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan PingTimeout { get; init; } = TimeSpan.FromSeconds(3);
    public TimeSpan BurstInterval { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan BurstMaxDuration { get; init; } = TimeSpan.FromSeconds(60);
    public int HistorySize { get; init; } = 100;
    public int MinUsableSample { get; init; } = 10;
    public double RespondingThreshold { get; init; } = 0.95;
    public double FalteringThreshold { get; init; } = 0.75;
    public double DownThreshold { get; init; } = 0.10;
    public double RecoveringThreshold { get; init; } = 0.25;

    /// <summary>Preset for LAN targets: fast steady pinging is fine.</summary>
    public static PingProfile Internal { get; } = new() { Name = "Internal", NormalInterval = TimeSpan.FromSeconds(1) };

    /// <summary>Preset for external targets: polite steady cadence.</summary>
    public static PingProfile External { get; } = new() { Name = "External", NormalInterval = TimeSpan.FromSeconds(10) };
}
```

`Core/ConCore/Targets/Target.cs`:

```csharp
namespace ConCore.Targets;

/// <summary>
/// A monitored endpoint: the engine's unit of work.
/// </summary>
public record Target
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    /// <summary>IP literal or DNS hostname.</summary>
    public required string Address { get; init; }
    /// <summary>Optional reachability parent (forms the topology tree).</summary>
    public Guid? ParentId { get; init; }
    public required string ProfileName { get; init; }
    public bool Enabled { get; init; } = true;
}
```

`Core/ConCore/Targets/PoolDefinition.cs`:

```csharp
namespace ConCore.Targets;

/// <summary>
/// A virtual aggregate target whose state derives from its members.
/// Pools are never pinged directly.
/// </summary>
public record PoolDefinition
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required IReadOnlyList<Guid> MemberTargetIds { get; init; }
    public Guid? ParentId { get; init; }
}
```

In `Core/ConCore/PingResult.cs`, change the constructor signature and timestamp assignment (rest of the file unchanged):

```csharp
public PingResult(IPAddress ipAddress, bool success, long roundtripTime, string? message = null,
    DateTimeOffset? timestamp = null)
{
    Timestamp = timestamp ?? DateTimeOffset.UtcNow;
    IpAddress = ipAddress ?? throw new ArgumentNullException(nameof(ipAddress));
    Success = success;
    RoundtripTime = roundtripTime;
    Message = message ?? string.Empty;
}
```

- [ ] **Step 4: Run tests and build**

Run: `dotnet test Tests/ConEngine.Tests --filter "FullyQualifiedName~PingProfileTests"` → PASS.
Run: `dotnet build ConnectionWatcher.sln` → succeeds.

- [ ] **Step 5: Commit**

```powershell
git add Core/ConCore Tests/
git commit -m "feat: add Target, PingProfile, PoolDefinition domain types"
```

---

### Task 3: Transition event types

**Files:**
- Create: `Core/ConEngine/Events/TargetTransition.cs`
- Test: none (pure data records; exercised heavily by Tasks 4+)

**Interfaces:**
- Consumes: `HostState`, `HostStatus` from `ConEngine.Tracking`.
- Produces (namespace `ConEngine.Events`):
  - `enum TransitionTrigger { ThresholdCrossed, ParentDown, ParentRecovered, ResolutionFailed, PoolMembersChanged }`
  - `record TransitionReason { TransitionTrigger Trigger; double? SuccessRate; int? SampleSize; double? BurstLossRate; Guid? AncestorId; string? Detail }` (only `Trigger` required)
  - `record AnalysisTransition { HostState From; HostState To; DateTimeOffset Timestamp; TransitionReason Reason; HostStatus Status }` (all required)
  - `record TargetTransition { Guid TargetId; string TargetName; HostState From; HostState To; DateTimeOffset Timestamp; TransitionReason Reason; float AverageRoundtripTime }` (all required except `AverageRoundtripTime`)
  - `record TargetStatus { Guid TargetId; string TargetName; HostStatus Host }` (all required)

- [ ] **Step 1: Create the file**

`Core/ConEngine/Events/TargetTransition.cs`:

```csharp
using ConEngine.Tracking;

namespace ConEngine.Events;

/// <summary>What caused a state transition.</summary>
public enum TransitionTrigger
{
    ThresholdCrossed,
    ParentDown,
    ParentRecovered,
    ResolutionFailed,
    PoolMembersChanged
}

/// <summary>Structured evidence behind a transition.</summary>
public record TransitionReason
{
    public required TransitionTrigger Trigger { get; init; }
    public double? SuccessRate { get; init; }
    public int? SampleSize { get; init; }
    /// <summary>Loss rate measured over the current burst window, if a burst was active.</summary>
    public double? BurstLossRate { get; init; }
    /// <summary>The ancestor target responsible, for ParentDown/ParentRecovered.</summary>
    public Guid? AncestorId { get; init; }
    public string? Detail { get; init; }

    public override string ToString()
    {
        var parts = new List<string> { Trigger.ToString() };
        if (SuccessRate is { } sr) parts.Add($"success {sr:P0}");
        if (SampleSize is { } n) parts.Add($"over {n} pings");
        if (BurstLossRate is { } lr) parts.Add($"burst loss {lr:P0}");
        if (Detail is { Length: > 0 }) parts.Add(Detail);
        return string.Join("; ", parts);
    }
}

/// <summary>A host-level state transition emitted by the analysis engine.</summary>
public record AnalysisTransition
{
    public required HostState From { get; init; }
    public required HostState To { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required TransitionReason Reason { get; init; }
    public required HostStatus Status { get; init; }
}

/// <summary>A target-level state transition: the event the whole system revolves around.</summary>
public record TargetTransition
{
    public required Guid TargetId { get; init; }
    public required string TargetName { get; init; }
    public required HostState From { get; init; }
    public required HostState To { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required TransitionReason Reason { get; init; }
    public float AverageRoundtripTime { get; init; }

    public override string ToString() => $"{TargetName}: {From} -> {To} ({Reason})";
}

/// <summary>Periodic status for a target (statistics snapshot, not a transition).</summary>
public record TargetStatus
{
    public required Guid TargetId { get; init; }
    public required string TargetName { get; init; }
    public required HostStatus Host { get; init; }
}
```

- [ ] **Step 2: Build**

Run: `dotnet build Core/ConEngine/ConEngine.csproj` → succeeds.

- [ ] **Step 3: Commit**

```powershell
git add Core/ConEngine/Events
git commit -m "feat: add transition event types"
```

---

### Task 4: Unreachable state and analysis-engine upgrade

**Files:**
- Modify: `Core/ConEngine/Tracking/HostStatus.cs` (add enum member)
- Modify: `Core/ConEngine/Tracking/HostAnalysisEngine.cs` (rework)
- Modify: `Core/ConEngine/Tracking/HostTracker.cs` (new ctor, passthroughs)
- Modify: `Core/ConEngine/WatcherEngine.cs` (temporary compile bridge — replaced wholesale in Task 12)
- Test: `Tests/ConEngine.Tests/HostAnalysisEngineTests.cs`

**Interfaces:**
- Consumes: `PingProfile` (Task 2), event types (Task 3).
- Produces:
  - `HostState` gains member `Unreachable` (after `Recovering`).
  - `HostAnalysisEngine` ctor becomes `(PingProfile profile, IPAddress hostAddress, TimeProvider timeProvider)`.
  - `HostAnalysisEngine.HostStateChangeSource` becomes `IObservable<AnalysisTransition>` (was `IObservable<HostStatus>`).
  - New methods: `void MarkUnreachable(Guid ancestorId)`, `void Release()` (→ `Starting`, emits `ParentRecovered`).
  - `Recalculate` ignores data while `Unreachable`; `Starting` now exits to `Responding`/`Faltering`/`Down` once `MinUsableSample` is reached; every threshold transition carries `TransitionReason { Trigger = ThresholdCrossed, SuccessRate, SampleSize }`.
  - `HostTracker` ctor becomes `(IPAddress hostAddress, PingProfile profile, TimeProvider timeProvider, ILogger<HostTracker> log)`; exposes `HostStateChangeSource: IObservable<AnalysisTransition>`, `MarkUnreachable(Guid)`, `Release()` (clears history then releases analysis), `IEnumerable<PingHistoryEntry> EntriesSince(DateTimeOffset since)`, and `HostState CurrentState`.

- [ ] **Step 1: Write the failing tests**

`Tests/ConEngine.Tests/HostAnalysisEngineTests.cs`:

```csharp
using System.Net;
using ConCore;
using ConCore.Targets;
using ConEngine.Events;
using ConEngine.Tracking;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace ConEngine.Tests;

public class HostAnalysisEngineTests
{
    private static readonly IPAddress Ip = IPAddress.Parse("10.0.0.1");
    private static readonly PingProfile Profile = new()
    {
        Name = "Test",
        HistorySize = 20,
        MinUsableSample = 5,
        RespondingThreshold = 0.95,
        FalteringThreshold = 0.75,
        DownThreshold = 0.10,
        RecoveringThreshold = 0.25,
    };

    private static (HostTracker tracker, List<AnalysisTransition> transitions, FakeTimeProvider time) Make()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-08-14T00:00:00Z"));
        var tracker = new HostTracker(Ip, Profile, time, NullLogger<HostTracker>.Instance);
        var transitions = new List<AnalysisTransition>();
        tracker.HostStateChangeSource.Subscribe(transitions.Add);
        return (tracker, transitions, time);
    }

    private static void Feed(HostTracker tracker, FakeTimeProvider time, int count, bool success)
    {
        for (int i = 0; i < count; i++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            tracker.RecordPingResult(new PingResult(Ip, success, success ? 10 : 0,
                timestamp: time.GetUtcNow()));
        }
    }

    [Fact]
    public void Starting_exits_to_responding_on_good_sample()
    {
        var (tracker, transitions, time) = Make();
        Feed(tracker, time, 5, success: true);
        Assert.Equal(HostState.Responding, tracker.CurrentState);
        var t = transitions.Last();
        Assert.Equal(HostState.Starting, t.From);
        Assert.Equal(TransitionTrigger.ThresholdCrossed, t.Reason.Trigger);
        Assert.Equal(1.0, t.Reason.SuccessRate!.Value, 3);
        Assert.Equal(5, t.Reason.SampleSize);
    }

    [Fact]
    public void Starting_exits_to_down_when_host_never_answers()
    {
        var (tracker, _, time) = Make();
        Feed(tracker, time, 5, success: false);
        Assert.Equal(HostState.Down, tracker.CurrentState);
    }

    [Fact]
    public void Starting_exits_to_faltering_on_middling_sample()
    {
        var (tracker, _, time) = Make();
        // 3 of 5 = 60%: below Responding (95%), above Down (10%)
        Feed(tracker, time, 3, success: true);
        Feed(tracker, time, 2, success: false);
        Assert.Equal(HostState.Faltering, tracker.CurrentState);
    }

    [Fact]
    public void Responding_falls_to_faltering_then_down()
    {
        var (tracker, _, time) = Make();
        Feed(tracker, time, 20, success: true);
        Assert.Equal(HostState.Responding, tracker.CurrentState);
        Feed(tracker, time, 20, success: false);
        Assert.Equal(HostState.Down, tracker.CurrentState);
    }

    [Fact]
    public void MarkUnreachable_parks_and_ignores_stale_results()
    {
        var (tracker, transitions, time) = Make();
        Feed(tracker, time, 5, success: true);
        var ancestor = Guid.NewGuid();

        tracker.MarkUnreachable(ancestor);
        Assert.Equal(HostState.Unreachable, tracker.CurrentState);
        var t = transitions.Last();
        Assert.Equal(TransitionTrigger.ParentDown, t.Reason.Trigger);
        Assert.Equal(ancestor, t.Reason.AncestorId);

        // A stale in-flight result must not move the state.
        Feed(tracker, time, 1, success: false);
        Assert.Equal(HostState.Unreachable, tracker.CurrentState);
    }

    [Fact]
    public void Release_returns_to_starting_with_fresh_history()
    {
        var (tracker, transitions, time) = Make();
        Feed(tracker, time, 5, success: true);
        tracker.MarkUnreachable(Guid.NewGuid());

        tracker.Release();
        Assert.Equal(HostState.Starting, tracker.CurrentState);
        Assert.Equal(0, tracker.HistoryCount);
        Assert.Equal(TransitionTrigger.ParentRecovered, transitions.Last().Reason.Trigger);

        // Must re-prove itself: 5 good pings to get back to Responding.
        Feed(tracker, time, 5, success: true);
        Assert.Equal(HostState.Responding, tracker.CurrentState);
    }

    [Fact]
    public void MarkUnreachable_is_idempotent()
    {
        var (tracker, transitions, time) = Make();
        Feed(tracker, time, 5, success: true);
        tracker.MarkUnreachable(Guid.NewGuid());
        var countAfterFirst = transitions.Count;
        tracker.MarkUnreachable(Guid.NewGuid());
        Assert.Equal(countAfterFirst, transitions.Count);
    }

    [Fact]
    public void EntriesSince_returns_only_entries_in_window()
    {
        var (tracker, _, time) = Make();
        Feed(tracker, time, 5, success: true);
        var cutoff = time.GetUtcNow();
        Feed(tracker, time, 3, success: false);
        Assert.Equal(3, tracker.EntriesSince(cutoff.AddTicks(1)).Count());
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test Tests/ConEngine.Tests --filter "FullyQualifiedName~HostAnalysisEngineTests"`
Expected: FAIL (compile errors: ctor signatures, `CurrentState`, `MarkUnreachable` missing).

- [ ] **Step 3: Implement**

3a. `HostStatus.cs` — add to the enum:

```csharp
public enum HostState
{
    Initializing,
    Starting,
    Responding,
    Faltering,
    Down,
    Recovering,
    Unreachable
}
```

3b. Rewrite `HostAnalysisEngine.cs`:

```csharp
using ConCore.Targets;
using ConEngine.Events;
using System.Net;
using System.Reactive.Subjects;

namespace ConEngine.Tracking;

/// <summary>
/// Per-host state machine. Derives state from recent ping samples and from
/// topology commands (MarkUnreachable/Release).
/// </summary>
internal class HostAnalysisEngine
{
    private readonly PingProfile _profile;
    private readonly IPAddress _hostAddress;
    private readonly TimeProvider _timeProvider;

    public HostAnalysisEngine(PingProfile profile, IPAddress hostAddress, TimeProvider timeProvider)
    {
        _profile = profile;
        _hostAddress = hostAddress;
        _timeProvider = timeProvider;
    }

    public HostState CurrentState { get; private set; } = HostState.Initializing;

    public DateTimeOffset TrackingStarted { get; private set; }
    public DateTimeOffset LastUpdated { get; private set; }

    public int SampleSize { get; private set; }
    public int NumSuccess { get; private set; }
    public int NumFailed { get; private set; }

    public float SuccessRate { get; private set; }
    public float FailureRate { get; private set; }
    public float AverageRoundtripTime { get; private set; }

    private readonly ReplaySubject<HostStatus> _hostStatusSubject = new(bufferSize: 1);
    public IObservable<HostStatus> HostStatusSource => _hostStatusSubject;
    private readonly ReplaySubject<AnalysisTransition> _stateChangeSubject = new(bufferSize: 1);
    public IObservable<AnalysisTransition> HostStateChangeSource => _stateChangeSubject;

    /// <summary>Commanded by topology: an ancestor is Down, stop deriving state from data.</summary>
    public void MarkUnreachable(Guid ancestorId)
    {
        if (CurrentState == HostState.Unreachable)
            return;
        Transition(HostState.Unreachable, new TransitionReason
        {
            Trigger = TransitionTrigger.ParentDown,
            AncestorId = ancestorId,
        });
    }

    /// <summary>Commanded by topology: the ancestor recovered; re-prove from Starting.</summary>
    public void Release()
    {
        if (CurrentState != HostState.Unreachable)
            return;
        SampleSize = 0; NumSuccess = 0; NumFailed = 0;
        SuccessRate = 0; FailureRate = 0; AverageRoundtripTime = 0;
        Transition(HostState.Starting, new TransitionReason { Trigger = TransitionTrigger.ParentRecovered });
    }

    public void Recalculate(IEnumerable<PingHistoryEntry> samples)
    {
        if (CurrentState == HostState.Unreachable)
            return; // stale in-flight results must not move a parked host

        SampleSize = 0; NumSuccess = 0; NumFailed = 0;
        float sumRoundtrip = 0f;

        foreach (PingHistoryEntry item in samples)
        {
            if (CurrentState == HostState.Initializing)
            {
                TrackingStarted = item.Timestamp;
                CurrentState = HostState.Starting;
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

        HostState next = CurrentState switch
        {
            HostState.Starting when SampleSize >= _profile.MinUsableSample =>
                SuccessRate >= _profile.RespondingThreshold ? HostState.Responding
                : SuccessRate < _profile.DownThreshold ? HostState.Down
                : HostState.Faltering,
            HostState.Responding when SuccessRate < _profile.FalteringThreshold => HostState.Faltering,
            HostState.Faltering when SuccessRate >= _profile.RespondingThreshold => HostState.Responding,
            HostState.Faltering when SuccessRate < _profile.DownThreshold => HostState.Down,
            HostState.Down when SuccessRate >= _profile.RecoveringThreshold => HostState.Recovering,
            HostState.Recovering when SuccessRate >= _profile.RespondingThreshold => HostState.Responding,
            HostState.Recovering when SuccessRate < _profile.DownThreshold => HostState.Down,
            _ => CurrentState,
        };

        if (next != CurrentState)
        {
            Transition(next, new TransitionReason
            {
                Trigger = TransitionTrigger.ThresholdCrossed,
                SuccessRate = SuccessRate,
                SampleSize = SampleSize,
            });
        }
        else
        {
            _hostStatusSubject.OnNext(BuildStatus(LastUpdated));
        }
    }

    private void Transition(HostState to, TransitionReason reason)
    {
        var from = CurrentState;
        CurrentState = to;
        var timestamp = reason.Trigger == TransitionTrigger.ThresholdCrossed
            ? LastUpdated
            : _timeProvider.GetUtcNow();
        var status = BuildStatus(timestamp);
        _hostStatusSubject.OnNext(status);
        _stateChangeSubject.OnNext(new AnalysisTransition
        {
            From = from,
            To = to,
            Timestamp = timestamp,
            Reason = reason,
            Status = status,
        });
    }

    private HostStatus BuildStatus(DateTimeOffset timestamp) => new()
    {
        HostAddress = _hostAddress,
        State = CurrentState,
        TrackingStarted = TrackingStarted,
        SampleSize = SampleSize,
        SuccessRate = SuccessRate,
        FailureRate = FailureRate,
        AverageRoundtripTime = AverageRoundtripTime,
        Timestamp = timestamp,
    };
}
```

3c. `HostTracker.cs` changes — new ctor and members (keep the rest; remove the old `HostStateChangeSource` ReplaySubject plumbing in favor of forwarding):

```csharp
public HostTracker(IPAddress hostAddress, PingProfile profile, TimeProvider timeProvider, ILogger<HostTracker> log)
{
    _log = log;
    HostAddress = hostAddress ?? throw new ArgumentNullException(nameof(hostAddress));
    ArgumentNullException.ThrowIfNull(profile);

    _history = new CircularBuffer<PingHistoryEntry>(profile.HistorySize);
    _minUsableSample = profile.MinUsableSample;
    _analysisEngine = new HostAnalysisEngine(profile, hostAddress, timeProvider);
}

/// <summary>Current state of the host state machine.</summary>
public HostState CurrentState => _analysisEngine.CurrentState;

/// <summary>Stream of periodic status snapshots.</summary>
public IObservable<HostStatus> HostStatusSource => _analysisEngine.HostStatusSource;

/// <summary>Stream of state transitions with structured reasons.</summary>
public IObservable<AnalysisTransition> HostStateChangeSource => _analysisEngine.HostStateChangeSource;

/// <summary>Commanded by topology: ancestor Down.</summary>
public void MarkUnreachable(Guid ancestorId) { lock (_lock) { _analysisEngine.MarkUnreachable(ancestorId); } }

/// <summary>Commanded by topology: ancestor recovered; clear history and re-prove.</summary>
public void Release() { lock (_lock) { _history.Clear(); _analysisEngine.Release(); } }

/// <summary>History entries with Timestamp >= since, oldest first.</summary>
public IEnumerable<PingHistoryEntry> EntriesSince(DateTimeOffset since) =>
    _history.InReverseOrder().TakeWhile(e => e.Timestamp >= since).Reverse();
```

Required edits to the existing file: add `using ConCore.Targets;` and `using ConEngine.Events;`, delete the two old `ReplaySubject<HostStatus>` fields and the subscriptions in the old ctor (the engine's subjects are now exposed directly), delete the old ctor.

3d. **Temporary compile bridge** in `WatcherEngine.cs` (this class is fully rewritten in Task 12; make the minimal edits so the solution builds):

- Add `using ConCore.Targets;` and `using ConEngine.Events;`.
- Add a private helper and use it at both `HostTracker` construction sites (in `HandlePingResult` and `StartWatching`):

```csharp
private PingProfile BridgeProfile => new()
{
    Name = "Bridge",
    HistorySize = _trackingOptions.HistorySize,
    MinUsableSample = _trackingOptions.MinUsableSample,
    RespondingThreshold = _trackingOptions.RespondingThreshold,
    FalteringThreshold = _trackingOptions.FalteringThreshold,
    DownThreshold = _trackingOptions.DownThreshold,
    RecoveringThreshold = _trackingOptions.RecoveringThreshold,
};
```

Replace `new HostTracker(ipAddress, _trackingOptions, _loggerFactory.CreateLogger<HostTracker>())` with `new HostTracker(ipAddress, BridgeProfile, _timeProvider, _loggerFactory.CreateLogger<HostTracker>())` (both sites). In `StartWatching`, the subscription lambda parameter is now an `AnalysisTransition`; change the log line to:

```csharp
tracker.HostStateChangeSource.Subscribe(t =>
{
    _log.LogInformation("Host state changed: {From} -> {To} ({Reason})", t.From, t.To, t.Reason);
});
```

- [ ] **Step 4: Run tests and build**

Run: `dotnet test Tests/ConEngine.Tests --filter "FullyQualifiedName~HostAnalysisEngineTests"` → PASS (8 tests).
Run: `dotnet build ConnectionWatcher.sln` → succeeds.

- [ ] **Step 5: Commit**

```powershell
git add Core/ConEngine Tests/
git commit -m "feat: Unreachable state, transition reasons, Starting exit paths"
```

---

### Task 5: PingDiscipline and cadence interface

**Files:**
- Create: `Core/ConEngine/Scheduling/IPingCadence.cs`
- Create: `Core/ConEngine/Scheduling/FixedIntervalCadence.cs`
- Create: `Core/ConEngine/Scheduling/PingDiscipline.cs`
- Test: `Tests/ConEngine.Tests/PingDisciplineTests.cs`

**Interfaces:**
- Consumes: `PingProfile` (Task 2), `HostState` (Task 4).
- Produces (namespace `ConEngine.Scheduling`):
  - `interface IPingCadence { Task WaitForNextPingAsync(CancellationToken ct); }`
  - `class FixedIntervalCadence(TimeSpan interval, TimeProvider timeProvider) : IPingCadence`
  - `class PingDiscipline(PingProfile profile, TimeProvider timeProvider, TimeSpan initialDelay = default) : IPingCadence` with `void OnStateChanged(HostState newState)`, `DateTimeOffset? BurstStartedAt { get; }`, `bool IsParked { get; }`.

Burst rules (per spec): burst-eligible states are `Faltering`, `Recovering`, `Down`. Entering any of them (re)starts the burst window at now. While in a burst-eligible state and within `BurstMaxDuration` of the window start, cadence = `BurstInterval`; past the cap, cadence = `NormalInterval`. All other states: `NormalInterval`. `Unreachable`: parked (no pings) until a state change unparks. `initialDelay` applies once, before the first ping (pool phase stagger).

- [ ] **Step 1: Write the failing tests**

`Tests/ConEngine.Tests/PingDisciplineTests.cs`:

```csharp
using ConCore.Targets;
using ConEngine.Scheduling;
using ConEngine.Tracking;
using Microsoft.Extensions.Time.Testing;

namespace ConEngine.Tests;

public class PingDisciplineTests
{
    private static readonly PingProfile Profile = new()
    {
        Name = "Test",
        NormalInterval = TimeSpan.FromSeconds(10),
        BurstInterval = TimeSpan.FromSeconds(1),
        BurstMaxDuration = TimeSpan.FromSeconds(60),
    };

    private static async Task<bool> Completes(Task task, FakeTimeProvider time, TimeSpan advance)
    {
        time.Advance(advance);
        var winner = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(2)));
        return winner == task;
    }

    [Fact]
    public async Task Normal_state_uses_normal_interval()
    {
        var time = new FakeTimeProvider();
        var d = new PingDiscipline(Profile, time);
        d.OnStateChanged(HostState.Responding);

        var wait = d.WaitForNextPingAsync(CancellationToken.None);
        Assert.False(await Completes(wait, time, TimeSpan.FromSeconds(9)));
        Assert.True(await Completes(wait, time, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task Faltering_uses_burst_interval()
    {
        var time = new FakeTimeProvider();
        var d = new PingDiscipline(Profile, time);
        d.OnStateChanged(HostState.Faltering);
        Assert.NotNull(d.BurstStartedAt);

        var wait = d.WaitForNextPingAsync(CancellationToken.None);
        Assert.True(await Completes(wait, time, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task Down_burst_expires_after_cap()
    {
        var time = new FakeTimeProvider();
        var d = new PingDiscipline(Profile, time);
        d.OnStateChanged(HostState.Down);

        // Within the 60s window: burst cadence.
        var wait = d.WaitForNextPingAsync(CancellationToken.None);
        Assert.True(await Completes(wait, time, TimeSpan.FromSeconds(1)));

        // Push past the cap: back to normal cadence.
        time.Advance(TimeSpan.FromSeconds(70));
        wait = d.WaitForNextPingAsync(CancellationToken.None);
        Assert.False(await Completes(wait, time, TimeSpan.FromSeconds(1)));
        Assert.True(await Completes(wait, time, TimeSpan.FromSeconds(9)));
    }

    [Fact]
    public async Task Unreachable_parks_until_state_change()
    {
        var time = new FakeTimeProvider();
        var d = new PingDiscipline(Profile, time);
        d.OnStateChanged(HostState.Unreachable);
        Assert.True(d.IsParked);

        var wait = d.WaitForNextPingAsync(CancellationToken.None);
        Assert.False(await Completes(wait, time, TimeSpan.FromMinutes(30)));

        d.OnStateChanged(HostState.Starting);
        Assert.False(d.IsParked);
        Assert.True(await Completes(wait, time, TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task Initial_delay_applies_once()
    {
        var time = new FakeTimeProvider();
        var d = new PingDiscipline(Profile, time, initialDelay: TimeSpan.FromSeconds(3));
        d.OnStateChanged(HostState.Responding);

        var wait = d.WaitForNextPingAsync(CancellationToken.None);
        Assert.True(await Completes(wait, time, TimeSpan.FromSeconds(3)));

        wait = d.WaitForNextPingAsync(CancellationToken.None);
        Assert.False(await Completes(wait, time, TimeSpan.FromSeconds(3)));
        Assert.True(await Completes(wait, time, TimeSpan.FromSeconds(7)));
    }

    [Fact]
    public async Task Reentering_down_restarts_burst_window()
    {
        var time = new FakeTimeProvider();
        var d = new PingDiscipline(Profile, time);
        d.OnStateChanged(HostState.Down);
        time.Advance(TimeSpan.FromSeconds(70)); // burst expired
        d.OnStateChanged(HostState.Recovering); // window restarts
        d.OnStateChanged(HostState.Down);       // window restarts again

        var wait = d.WaitForNextPingAsync(CancellationToken.None);
        Assert.True(await Completes(wait, time, TimeSpan.FromSeconds(1)));
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test Tests/ConEngine.Tests --filter "FullyQualifiedName~PingDisciplineTests"`
Expected: FAIL (namespace `ConEngine.Scheduling` does not exist).

- [ ] **Step 3: Implement**

`Core/ConEngine/Scheduling/IPingCadence.cs`:

```csharp
namespace ConEngine.Scheduling;

/// <summary>
/// Decides when the next ping happens. A PingController awaits this between pings.
/// </summary>
public interface IPingCadence
{
    /// <summary>Completes when it is time for the next ping. May park indefinitely.</summary>
    Task WaitForNextPingAsync(CancellationToken ct);
}
```

`Core/ConEngine/Scheduling/FixedIntervalCadence.cs`:

```csharp
namespace ConEngine.Scheduling;

/// <summary>Simple constant-interval cadence (used by tests and as a plain default).</summary>
public class FixedIntervalCadence(TimeSpan interval, TimeProvider timeProvider) : IPingCadence
{
    public Task WaitForNextPingAsync(CancellationToken ct) =>
        Task.Delay(interval, timeProvider, ct);
}
```

`Core/ConEngine/Scheduling/PingDiscipline.cs`:

```csharp
using ConCore.Targets;
using ConEngine.Tracking;

namespace ConEngine.Scheduling;

/// <summary>
/// Adaptive per-target ping cadence: normal interval when healthy, burst interval
/// (time-capped) while Faltering/Recovering/Down, parked while Unreachable.
/// </summary>
public class PingDiscipline : IPingCadence
{
    private readonly PingProfile _profile;
    private readonly TimeProvider _time;
    private readonly Lock _lock = new();
    private HostState _state = HostState.Initializing;
    private DateTimeOffset? _burstStartedAt;
    private TimeSpan? _initialDelay;
    private TaskCompletionSource _releaseTcs = NewTcs();

    private static TaskCompletionSource NewTcs() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public PingDiscipline(PingProfile profile, TimeProvider timeProvider, TimeSpan initialDelay = default)
    {
        _profile = profile;
        _time = timeProvider;
        _initialDelay = initialDelay > TimeSpan.Zero ? initialDelay : null;
    }

    /// <summary>Start of the active burst window, or null when not bursting.</summary>
    public DateTimeOffset? BurstStartedAt
    {
        get { lock (_lock) { return _burstStartedAt; } }
    }

    /// <summary>True while the target is Unreachable and pinging is suspended.</summary>
    public bool IsParked
    {
        get { lock (_lock) { return _state == HostState.Unreachable; } }
    }

    /// <summary>Feed state transitions in; the cadence adapts.</summary>
    public void OnStateChanged(HostState newState)
    {
        lock (_lock)
        {
            var oldState = _state;
            _state = newState;

            if (newState is HostState.Faltering or HostState.Recovering or HostState.Down)
                _burstStartedAt = _time.GetUtcNow(); // (re)start the burst window
            else
                _burstStartedAt = null;

            if (oldState == HostState.Unreachable && newState != HostState.Unreachable)
                _releaseTcs.TrySetResult(); // unpark any waiter
            if (newState == HostState.Unreachable && oldState != HostState.Unreachable)
                _releaseTcs = NewTcs();
        }
    }

    public async Task WaitForNextPingAsync(CancellationToken ct)
    {
        while (true)
        {
            Task? parkTask = null;
            TimeSpan delay = TimeSpan.Zero;
            lock (_lock)
            {
                if (_state == HostState.Unreachable)
                {
                    parkTask = _releaseTcs.Task;
                }
                else if (_initialDelay is { } initial)
                {
                    _initialDelay = null;
                    delay = initial;
                }
                else
                {
                    delay = ComputeDelay();
                }
            }

            if (parkTask is not null)
            {
                await parkTask.WaitAsync(ct).ConfigureAwait(false);
                continue; // re-evaluate now that we're released
            }

            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, _time, ct).ConfigureAwait(false);

            lock (_lock)
            {
                if (_state != HostState.Unreachable)
                    return;
                // Went unreachable while we were waiting: loop around and park.
            }
        }
    }

    private TimeSpan ComputeDelay()
    {
        bool burstEligible = _state is HostState.Faltering or HostState.Recovering or HostState.Down;
        if (burstEligible && _burstStartedAt is { } start &&
            _time.GetUtcNow() - start < _profile.BurstMaxDuration)
        {
            return _profile.BurstInterval;
        }
        return _profile.NormalInterval;
    }
}
```

- [ ] **Step 4: Run tests**

Run: `dotnet test Tests/ConEngine.Tests --filter "FullyQualifiedName~PingDisciplineTests"` → PASS (6 tests).

- [ ] **Step 5: Commit**

```powershell
git add Core/ConEngine/Scheduling Tests/
git commit -m "feat: adaptive PingDiscipline with burst windows and parking"
```

---

### Task 6: Cadence-driven PingController; fix shared-channel completion bug

**Files:**
- Modify: `Core/ConEngine/Network/PingController.cs`
- Modify: `Core/ConEngine/Network/PingManager.cs`
- Modify: `Core/ConEngine/WatcherEngine.cs` (bridge again; rewritten in Task 12)
- Test: `Tests/ConEngine.Tests/PingControllerTests.cs`, `Tests/ConEngine.Tests/Fakes/FakePingTransceiverFactory.cs`

**Interfaces:**
- Consumes: `IPingCadence`, `FixedIntervalCadence` (Task 5).
- Produces:
  - `PingController.Launch(IPAddress ipAddress, ChannelWriter<PingResult> resultWriter, IPingCadence cadence, int timeoutMs, CancellationToken cancellationToken)`.
  - `PingManager.GetOrCreateController(IPAddress ipAddress, IPingCadence cadence, int timeoutMs)`.
  - **Bug fix:** `PingController` no longer calls `_resultWriter?.Complete()` — the writer is shared by all controllers and only `PingManager` may complete it. (Today, one controller stopping kills the result stream for every host.)
  - **Fault isolation:** an unexpected exception inside a controller's loop is logged, reported as a failed `PingResult`, and the loop continues after the next cadence wait; it never exits except on cancellation.
  - Test helper `ConEngine.Tests.Fakes.FakePingTransceiverFactory : IPingTransceiverFactory` with `void SetUp(IPAddress ip, bool up)`, `void SetThrow(IPAddress ip, bool throwOnPing)` — instant, thread-safe fake used by all later engine tests.

- [ ] **Step 1: Write the fake and the failing tests**

`Tests/ConEngine.Tests/Fakes/FakePingTransceiverFactory.cs`:

```csharp
using System.Collections.Concurrent;
using System.Net;
using ConCore;

namespace ConEngine.Tests.Fakes;

/// <summary>
/// Instant, scriptable transceiver: per-IP up/down and throw switches.
/// Default behavior for unknown IPs: success with 10ms RTT.
/// </summary>
public class FakePingTransceiverFactory : IPingTransceiverFactory
{
    private readonly ConcurrentDictionary<IPAddress, bool> _up = new();
    private readonly ConcurrentDictionary<IPAddress, bool> _throw = new();

    public void SetUp(IPAddress ip, bool up) => _up[ip] = up;
    public void SetThrow(IPAddress ip, bool throwOnPing) => _throw[ip] = throwOnPing;

    public IPingTransceiver Create(IPAddress ipAddress, CancellationToken cancellationToken) =>
        new FakeTransceiver(ipAddress, this);

    private sealed class FakeTransceiver(IPAddress ip, FakePingTransceiverFactory owner) : IPingTransceiver
    {
        public IPAddress IpAddress => ip;

        public Task<PingResult> PingAsync(int timeoutMs)
        {
            if (owner._throw.GetValueOrDefault(ip))
                throw new InvalidOperationException("Simulated transceiver failure");
            bool up = owner._up.GetValueOrDefault(ip, true);
            return Task.FromResult(new PingResult(ip, up, up ? 10 : 0, up ? null : "Timed out"));
        }

        public void Dispose() { }
    }
}
```

`Tests/ConEngine.Tests/PingControllerTests.cs`:

```csharp
using System.Net;
using System.Threading.Channels;
using ConCore;
using ConEngine.Network;
using ConEngine.Scheduling;
using ConEngine.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace ConEngine.Tests;

public class PingControllerTests
{
    private static readonly IPAddress IpA = IPAddress.Parse("10.0.0.1");
    private static readonly IPAddress IpB = IPAddress.Parse("10.0.0.2");

    private static async Task<List<PingResult>> Collect(ChannelReader<PingResult> reader, int count, int timeoutMs = 5000)
    {
        var results = new List<PingResult>();
        using var cts = new CancellationTokenSource(timeoutMs);
        while (results.Count < count)
            results.Add(await reader.ReadAsync(cts.Token));
        return results;
    }

    [Fact]
    public async Task Controller_pings_at_cadence_and_reports_results()
    {
        var factory = new FakePingTransceiverFactory();
        var channel = Channel.CreateUnbounded<PingResult>();
        using var cts = new CancellationTokenSource();
        using var controller = new PingController(TimeProvider.System, factory, NullLogger.Instance);

        controller.Launch(IpA, channel.Writer,
            new FixedIntervalCadence(TimeSpan.FromMilliseconds(10), TimeProvider.System),
            timeoutMs: 1000, cts.Token);

        var results = await Collect(channel.Reader, 3);
        Assert.All(results, r => Assert.True(r.Success));
        cts.Cancel();
    }

    [Fact]
    public async Task Stopping_one_controller_does_not_complete_shared_channel()
    {
        var factory = new FakePingTransceiverFactory();
        var channel = Channel.CreateUnbounded<PingResult>();
        using var cts = new CancellationTokenSource();
        var cadence = new FixedIntervalCadence(TimeSpan.FromMilliseconds(10), TimeProvider.System);

        using var controllerA = new PingController(TimeProvider.System, factory, NullLogger.Instance);
        using var controllerB = new PingController(TimeProvider.System, factory, NullLogger.Instance);
        controllerA.Launch(IpA, channel.Writer, cadence, 1000, cts.Token);
        controllerB.Launch(IpB, channel.Writer, cadence, 1000, cts.Token);

        await Collect(channel.Reader, 2);
        controllerA.Stop();
        await Task.Delay(100);

        // Channel must still be open and still carrying B's results.
        Assert.False(channel.Reader.Completion.IsCompleted);
        var afterStop = await Collect(channel.Reader, 3);
        Assert.All(afterStop.Where(r => !r.IpAddress.Equals(IpA)), r => Assert.True(r.Success));
        cts.Cancel();
    }

    [Fact]
    public async Task Transceiver_exception_reports_failure_and_loop_survives()
    {
        var factory = new FakePingTransceiverFactory();
        factory.SetThrow(IpA, true);
        var channel = Channel.CreateUnbounded<PingResult>();
        using var cts = new CancellationTokenSource();
        using var controller = new PingController(TimeProvider.System, factory, NullLogger.Instance);

        controller.Launch(IpA, channel.Writer,
            new FixedIntervalCadence(TimeSpan.FromMilliseconds(10), TimeProvider.System),
            1000, cts.Token);

        var failures = await Collect(channel.Reader, 2);
        Assert.All(failures, r => Assert.False(r.Success));

        // Heal the transceiver: the same loop must recover and report successes.
        factory.SetThrow(IpA, false);
        using var cts2 = new CancellationTokenSource(5000);
        PingResult? healed = null;
        while (healed is not { Success: true })
            healed = await channel.Reader.ReadAsync(cts2.Token);
        Assert.True(healed.Success);
        cts.Cancel();
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test Tests/ConEngine.Tests --filter "FullyQualifiedName~PingControllerTests"`
Expected: FAIL (compile error: `Launch` has no cadence overload).

- [ ] **Step 3: Implement**

`PingController.cs` — replace `Launch` and `PollingLoop` (add `using ConEngine.Scheduling;`; remove the `timeProvider` primary-ctor usage inside the loop delays; keep Stop/Dispose/SendMessage as-is except where noted):

```csharp
private IPingCadence? _cadence;
private int _timeoutMs;

/// <summary>
/// Starts pinging the specified IP address, pacing pings via the cadence,
/// and sending results through the channel.
/// </summary>
public Task Launch(IPAddress ipAddress, ChannelWriter<PingResult> resultWriter,
    IPingCadence cadence, int timeoutMs, CancellationToken cancellationToken)
{
    lock (_lock)
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(PingController));

        _cancellationToken = CancellationTokenSource
            .CreateLinkedTokenSource(cancellationToken, _localCancellationTokenSource.Token).Token;
        _ipAddress = ipAddress;
        _resultWriter = resultWriter;
        _cadence = cadence;
        _timeoutMs = timeoutMs;

        _pingTask = Task.Run(() => PollingLoop(ipAddress), _cancellationToken);
        return _pingTask;
    }
}

private async Task PollingLoop(IPAddress ipAddress)
{
    try
    {
        log.LogTrace("Creating ping transceiver for {ipAddress} ...", ipAddress);
        using var ping = pingTransceiverFactory.Create(ipAddress, _cancellationToken);

        while (IsRunnable())
        {
            try
            {
                // Cadence first: honors initial phase offsets and parking.
                await _cadence!.WaitForNextPingAsync(_cancellationToken).ConfigureAwait(false);
                if (!IsRunnable())
                    break;

                var reply = await ping.PingAsync(_timeoutMs).ConfigureAwait(false);
                log.LogTrace("Ping result for {ipAddress}: {reply}", ipAddress, reply);
                SendMessage(ipAddress, reply);
            }
            catch (OperationCanceledException) when (_cancellationToken.IsCancellationRequested)
            {
                log.LogTrace("Ping for {ipAddress} has been cancelled", ipAddress);
                break;
            }
            catch (Exception pingEx)
            {
                // Fault isolation: report a failed sample and keep the loop alive.
                log.LogError(pingEx, "Error pinging {ipAddress}", ipAddress);
                SendMessage(ipAddress, new PingResult(ipAddress, false, 0, $"Ping error: {pingEx.Message}"));
            }
        }
    }
    catch (OperationCanceledException)
    {
        log.LogTrace("Ping for {ipAddress} has been cancelled", _ipAddress);
    }
    catch (Exception ex)
    {
        log.LogError(ex, "Ping loop for {ipAddress} terminated unexpectedly", _ipAddress);
    }
    // NOTE: no writer completion here. The result channel is shared by all
    // controllers and owned by PingManager; completing it would kill the
    // stream for every other host.
}
```

`PingManager.cs` — change `GetOrCreateController`:

```csharp
public PingController GetOrCreateController(IPAddress ipAddress, IPingCadence cadence, int timeoutMs)
{
    lock (_lock)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(PingManager));

        if (!_pingControllers.TryGetValue(ipAddress, out var controller))
        {
            var controllerLogger = _loggerFactory.CreateLogger($"ConEngine.Network.PingController[{ipAddress}]");
            controller = new PingController(_timeProvider, _pingTransceiverFactory, controllerLogger);
            controller.Launch(ipAddress, _resultWriter, cadence, timeoutMs, _globalCancellationToken);
            _pingControllers[ipAddress] = controller;
            _log.LogInformation("Created PingController for {IpAddress}", ipAddress);
        }
        return controller;
    }
}
```

Add `using ConEngine.Scheduling;` to both files.

**Bridge** in `WatcherEngine.StartWatching` (temporary, dies in Task 12):

```csharp
_manager?.GetOrCreateController(ipAddress,
    new FixedIntervalCadence(TimeSpan.FromSeconds(1), _timeProvider), timeoutMs: 3000);
```

(add `using ConEngine.Scheduling;`).

- [ ] **Step 4: Run tests and build**

Run: `dotnet test Tests/ConEngine.Tests --filter "FullyQualifiedName~PingControllerTests"` → PASS (3 tests).
Run: `dotnet build ConnectionWatcher.sln` → succeeds.

- [ ] **Step 5: Commit**

```powershell
git add Core/ConEngine Tests/
git commit -m "feat: cadence-driven ping loop; fix shared channel completion bug"
```

---

### Task 7: Watcher configuration options and validator

**Files:**
- Create: `Core/ConEngine/Configuration/WatcherOptions.cs`
- Create: `Core/ConEngine/Configuration/WatcherOptionsValidator.cs`
- Test: `Tests/ConEngine.Tests/WatcherOptionsValidatorTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces (namespace `ConEngine.Configuration`):
  - `class WatcherOptions { const string SectionName = "Watcher"; Dictionary<string, PingProfileConfig> Profiles; List<TargetConfig> Targets; List<PoolConfig> Pools }`
  - `class TargetConfig { string Name; string Address; string? Parent; string Profile = "External"; bool Enabled = true }`
  - `class PoolConfig { string Name; List<string> Members; string? Parent }`
  - `class PingProfileConfig` — seconds-based numeric mirror of `PingProfile` (fields listed in implementation) with `PingProfile ToProfile(string name)`
  - `class WatcherOptionsValidator : IValidateOptions<WatcherOptions>` enforcing all rules below.

Validation rules (each produces a distinct failure message):
1. Target names non-empty and unique (case-insensitive). Pool names non-empty, unique, and not colliding with target names.
2. Target addresses non-empty and unique.
3. Every `TargetConfig.Profile` resolves to a built-in preset (`Internal`/`External`, case-insensitive) or a key in `Profiles`.
4. Every `TargetConfig.Parent` / `PoolConfig.Parent` names an existing **target** (a pool cannot be a parent).
5. Parent links contain no cycle.
6. Every pool has ≥ 1 member; every member names an existing target (not a pool).
7. Profile numbers sane: intervals and timeout > 0; thresholds in [0,1]; `MinUsableSample` ≥ 1 and ≤ `HistorySize`.

- [ ] **Step 1: Write the failing tests**

`Tests/ConEngine.Tests/WatcherOptionsValidatorTests.cs`:

```csharp
using ConEngine.Configuration;
using Microsoft.Extensions.Options;

namespace ConEngine.Tests;

public class WatcherOptionsValidatorTests
{
    private static ValidateOptionsResult Validate(WatcherOptions options) =>
        new WatcherOptionsValidator().Validate(null, options);

    private static WatcherOptions ValidHome() => new()
    {
        Targets =
        [
            new TargetConfig { Name = "Router", Address = "192.168.1.1", Profile = "Internal" },
            new TargetConfig { Name = "Modem", Address = "192.168.100.1", Parent = "Router", Profile = "Internal" },
            new TargetConfig { Name = "Google", Address = "8.8.8.8", Parent = "Modem" },
            new TargetConfig { Name = "Cloudflare", Address = "1.1.1.1", Parent = "Modem" },
        ],
        Pools =
        [
            new PoolConfig { Name = "Internet", Members = ["Google", "Cloudflare"], Parent = "Modem" },
        ],
    };

    [Fact]
    public void Valid_config_passes()
    {
        Assert.True(Validate(ValidHome()).Succeeded);
    }

    [Fact]
    public void Duplicate_target_name_fails()
    {
        var o = ValidHome();
        o.Targets.Add(new TargetConfig { Name = "router", Address = "192.168.1.2" });
        var result = Validate(o);
        Assert.True(result.Failed);
        Assert.Contains("duplicate", result.FailureMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Unknown_profile_fails()
    {
        var o = ValidHome();
        o.Targets[0].Profile = "NoSuchProfile";
        Assert.True(Validate(o).Failed);
    }

    [Fact]
    public void Custom_profile_is_accepted()
    {
        var o = ValidHome();
        o.Profiles["Slow"] = new PingProfileConfig { NormalIntervalSeconds = 30 };
        o.Targets[0].Profile = "Slow";
        Assert.True(Validate(o).Succeeded);
    }

    [Fact]
    public void Unknown_parent_fails()
    {
        var o = ValidHome();
        o.Targets[2].Parent = "Ghost";
        Assert.True(Validate(o).Failed);
    }

    [Fact]
    public void Pool_as_parent_fails()
    {
        var o = ValidHome();
        o.Targets[2].Parent = "Internet";
        Assert.True(Validate(o).Failed);
    }

    [Fact]
    public void Parent_cycle_fails()
    {
        var o = new WatcherOptions
        {
            Targets =
            [
                new TargetConfig { Name = "A", Address = "10.0.0.1", Parent = "B" },
                new TargetConfig { Name = "B", Address = "10.0.0.2", Parent = "A" },
            ],
        };
        var result = Validate(o);
        Assert.True(result.Failed);
        Assert.Contains("cycle", result.FailureMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Empty_pool_fails()
    {
        var o = ValidHome();
        o.Pools.Add(new PoolConfig { Name = "Empty", Members = [] });
        Assert.True(Validate(o).Failed);
    }

    [Fact]
    public void Pool_member_referencing_pool_fails()
    {
        var o = ValidHome();
        o.Pools.Add(new PoolConfig { Name = "Meta", Members = ["Internet"] });
        Assert.True(Validate(o).Failed);
    }

    [Fact]
    public void Bad_thresholds_fail()
    {
        var o = ValidHome();
        o.Profiles["Bad"] = new PingProfileConfig { RespondingThreshold = 1.5 };
        o.Targets[0].Profile = "Bad";
        Assert.True(Validate(o).Failed);
    }

    [Fact]
    public void MinUsableSample_exceeding_history_fails()
    {
        var o = ValidHome();
        o.Profiles["Bad"] = new PingProfileConfig { HistorySize = 5, MinUsableSample = 10 };
        o.Targets[0].Profile = "Bad";
        Assert.True(Validate(o).Failed);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test Tests/ConEngine.Tests --filter "FullyQualifiedName~WatcherOptionsValidatorTests"`
Expected: FAIL (types missing).

- [ ] **Step 3: Implement**

`Core/ConEngine/Configuration/WatcherOptions.cs`:

```csharp
using ConCore.Targets;

namespace ConEngine.Configuration;

/// <summary>Root configuration for the watcher: profiles, targets, pools.</summary>
public class WatcherOptions
{
    public const string SectionName = "Watcher";

    /// <summary>Named custom profiles; built-ins "Internal" and "External" always exist.</summary>
    public Dictionary<string, PingProfileConfig> Profiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<TargetConfig> Targets { get; set; } = new();
    public List<PoolConfig> Pools { get; set; } = new();
}

/// <summary>One monitored endpoint, as authored in configuration.</summary>
public class TargetConfig
{
    public string Name { get; set; } = "";
    /// <summary>IP literal or DNS hostname.</summary>
    public string Address { get; set; } = "";
    /// <summary>Name of the parent target in the reachability tree.</summary>
    public string? Parent { get; set; }
    public string Profile { get; set; } = "External";
    public bool Enabled { get; set; } = true;
}

/// <summary>A rotation pool, as authored in configuration.</summary>
public class PoolConfig
{
    public string Name { get; set; } = "";
    public List<string> Members { get; set; } = new();
    public string? Parent { get; set; }
}

/// <summary>Seconds-based configuration mirror of <see cref="PingProfile"/>.</summary>
public class PingProfileConfig
{
    public double NormalIntervalSeconds { get; set; } = 10;
    public double PingTimeoutSeconds { get; set; } = 3;
    public double BurstIntervalSeconds { get; set; } = 1;
    public double BurstMaxDurationSeconds { get; set; } = 60;
    public int HistorySize { get; set; } = 100;
    public int MinUsableSample { get; set; } = 10;
    public double RespondingThreshold { get; set; } = 0.95;
    public double FalteringThreshold { get; set; } = 0.75;
    public double DownThreshold { get; set; } = 0.10;
    public double RecoveringThreshold { get; set; } = 0.25;

    public PingProfile ToProfile(string name) => new()
    {
        Name = name,
        NormalInterval = TimeSpan.FromSeconds(NormalIntervalSeconds),
        PingTimeout = TimeSpan.FromSeconds(PingTimeoutSeconds),
        BurstInterval = TimeSpan.FromSeconds(BurstIntervalSeconds),
        BurstMaxDuration = TimeSpan.FromSeconds(BurstMaxDurationSeconds),
        HistorySize = HistorySize,
        MinUsableSample = MinUsableSample,
        RespondingThreshold = RespondingThreshold,
        FalteringThreshold = FalteringThreshold,
        DownThreshold = DownThreshold,
        RecoveringThreshold = RecoveringThreshold,
    };
}
```

`Core/ConEngine/Configuration/WatcherOptionsValidator.cs`:

```csharp
using Microsoft.Extensions.Options;

namespace ConEngine.Configuration;

/// <summary>Validates the watcher configuration at startup: names, references, tree shape, numbers.</summary>
public class WatcherOptionsValidator : IValidateOptions<WatcherOptions>
{
    public ValidateOptionsResult Validate(string? name, WatcherOptions options)
    {
        var failures = new List<string>();
        var targetNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var addresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var t in options.Targets)
        {
            if (string.IsNullOrWhiteSpace(t.Name))
                failures.Add("A target has an empty Name.");
            else if (!targetNames.Add(t.Name))
                failures.Add($"Duplicate target name '{t.Name}'.");

            if (string.IsNullOrWhiteSpace(t.Address))
                failures.Add($"Target '{t.Name}' has an empty Address.");
            else if (!addresses.Add(t.Address))
                failures.Add($"Duplicate target address '{t.Address}'.");

            if (!IsKnownProfile(options, t.Profile))
                failures.Add($"Target '{t.Name}' references unknown profile '{t.Profile}'.");
        }

        var poolNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in options.Pools)
        {
            if (string.IsNullOrWhiteSpace(p.Name))
                failures.Add("A pool has an empty Name.");
            else if (!poolNames.Add(p.Name))
                failures.Add($"Duplicate pool name '{p.Name}'.");
            else if (targetNames.Contains(p.Name))
                failures.Add($"Pool name '{p.Name}' collides with a target name.");

            if (p.Members.Count == 0)
                failures.Add($"Pool '{p.Name}' has no members.");
            foreach (var m in p.Members)
            {
                if (!targetNames.Contains(m))
                    failures.Add(poolNames.Contains(m) || options.Pools.Any(x => x.Name.Equals(m, StringComparison.OrdinalIgnoreCase))
                        ? $"Pool '{p.Name}' member '{m}' is a pool; pools cannot contain pools."
                        : $"Pool '{p.Name}' references unknown member '{m}'.");
            }

            if (p.Parent is { Length: > 0 } && !targetNames.Contains(p.Parent))
                failures.Add($"Pool '{p.Name}' references unknown parent target '{p.Parent}'.");
        }

        foreach (var t in options.Targets)
        {
            if (t.Parent is not { Length: > 0 })
                continue;
            if (poolNames.Contains(t.Parent))
                failures.Add($"Target '{t.Name}' has pool '{t.Parent}' as parent; pools cannot be parents.");
            else if (!targetNames.Contains(t.Parent))
                failures.Add($"Target '{t.Name}' references unknown parent '{t.Parent}'.");
        }

        // Cycle detection: walk up from every target.
        var byName = options.Targets
            .Where(t => !string.IsNullOrWhiteSpace(t.Name))
            .GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var t in options.Targets)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var current = t;
            while (current?.Parent is { Length: > 0 } parentName)
            {
                if (!seen.Add(current.Name))
                {
                    failures.Add($"Parent cycle detected involving target '{t.Name}'.");
                    break;
                }
                byName.TryGetValue(parentName, out current);
            }
        }

        foreach (var (profileName, p) in options.Profiles)
        {
            if (p.NormalIntervalSeconds <= 0 || p.BurstIntervalSeconds <= 0 ||
                p.BurstMaxDurationSeconds <= 0 || p.PingTimeoutSeconds <= 0)
                failures.Add($"Profile '{profileName}': intervals and timeout must be positive.");
            foreach (var (label, v) in new (string, double)[]
                     {
                         ("RespondingThreshold", p.RespondingThreshold),
                         ("FalteringThreshold", p.FalteringThreshold),
                         ("DownThreshold", p.DownThreshold),
                         ("RecoveringThreshold", p.RecoveringThreshold),
                     })
            {
                if (v is < 0 or > 1)
                    failures.Add($"Profile '{profileName}': {label} must be between 0 and 1.");
            }
            if (p.MinUsableSample < 1 || p.MinUsableSample > p.HistorySize)
                failures.Add($"Profile '{profileName}': MinUsableSample must be between 1 and HistorySize ({p.HistorySize}).");
        }

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }

    private static bool IsKnownProfile(WatcherOptions options, string profileName) =>
        profileName.Equals("Internal", StringComparison.OrdinalIgnoreCase) ||
        profileName.Equals("External", StringComparison.OrdinalIgnoreCase) ||
        options.Profiles.ContainsKey(profileName);
}
```

- [ ] **Step 4: Run tests**

Run: `dotnet test Tests/ConEngine.Tests --filter "FullyQualifiedName~WatcherOptionsValidatorTests"` → PASS (11 tests).

- [ ] **Step 5: Commit**

```powershell
git add Core/ConEngine/Configuration Tests/
git commit -m "feat: WatcherOptions config model with full validation"
```

---

### Task 8: TargetGraph

**Files:**
- Create: `Core/ConEngine/Topology/TargetGraph.cs`
- Test: `Tests/ConEngine.Tests/TargetGraphTests.cs`

**Interfaces:**
- Consumes: `WatcherOptions` (Task 7), domain types (Task 2).
- Produces (namespace `ConEngine.Topology`):
  - `class TargetGraph` with:
    - `static TargetGraph FromOptions(WatcherOptions options)` — assumes options already validated; generates GUIDs
    - `IReadOnlyDictionary<Guid, Target> Targets`
    - `IReadOnlyDictionary<Guid, PoolDefinition> Pools`
    - `PingProfile ProfileFor(Target target)`
    - `Target GetByName(string name)` (throws `KeyNotFoundException` for unknown)
    - `PoolDefinition GetPoolByName(string name)`
    - `IReadOnlyList<Target> GetChildren(Guid parentId)`
    - `IReadOnlyList<Target> GetDescendants(Guid ancestorId)` (transitive, excludes self)

- [ ] **Step 1: Write the failing tests**

`Tests/ConEngine.Tests/TargetGraphTests.cs`:

```csharp
using ConEngine.Configuration;
using ConEngine.Topology;

namespace ConEngine.Tests;

public class TargetGraphTests
{
    private static TargetGraph Home() => TargetGraph.FromOptions(new WatcherOptions
    {
        Profiles = { ["Slow"] = new PingProfileConfig { NormalIntervalSeconds = 30 } },
        Targets =
        [
            new TargetConfig { Name = "Router", Address = "192.168.1.1", Profile = "Internal" },
            new TargetConfig { Name = "Modem", Address = "192.168.100.1", Parent = "Router", Profile = "Internal" },
            new TargetConfig { Name = "Google", Address = "8.8.8.8", Parent = "Modem" },
            new TargetConfig { Name = "Cloudflare", Address = "1.1.1.1", Parent = "Modem", Profile = "Slow" },
            new TargetConfig { Name = "TV", Address = "192.168.1.50", Parent = "Router", Profile = "Internal" },
        ],
        Pools = [new PoolConfig { Name = "Internet", Members = ["Google", "Cloudflare"], Parent = "Modem" }],
    });

    [Fact]
    public void Builds_all_targets_and_pools()
    {
        var g = Home();
        Assert.Equal(5, g.Targets.Count);
        Assert.Single(g.Pools);
    }

    [Fact]
    public void Parent_links_resolve_to_ids()
    {
        var g = Home();
        var router = g.GetByName("Router");
        var modem = g.GetByName("Modem");
        Assert.Null(router.ParentId);
        Assert.Equal(router.Id, modem.ParentId);
    }

    [Fact]
    public void Descendants_are_transitive_and_exclude_self()
    {
        var g = Home();
        var router = g.GetByName("Router");
        var names = g.GetDescendants(router.Id).Select(t => t.Name).Order().ToArray();
        Assert.Equal(new[] { "Cloudflare", "Google", "Modem", "TV" }, names);
        Assert.DoesNotContain("Router", names);
    }

    [Fact]
    public void Children_are_direct_only()
    {
        var g = Home();
        var names = g.GetChildren(g.GetByName("Router").Id).Select(t => t.Name).ToHashSet();
        Assert.Equal(2, names.Count);
        Assert.Contains("Modem", names);
        Assert.Contains("TV", names);
    }

    [Fact]
    public void ProfileFor_resolves_builtins_and_custom()
    {
        var g = Home();
        Assert.Equal(TimeSpan.FromSeconds(1), g.ProfileFor(g.GetByName("Router")).NormalInterval);
        Assert.Equal(TimeSpan.FromSeconds(10), g.ProfileFor(g.GetByName("Google")).NormalInterval);
        Assert.Equal(TimeSpan.FromSeconds(30), g.ProfileFor(g.GetByName("Cloudflare")).NormalInterval);
    }

    [Fact]
    public void Pool_members_resolve_to_target_ids()
    {
        var g = Home();
        var pool = g.GetPoolByName("Internet");
        var memberNames = pool.MemberTargetIds.Select(id => g.Targets[id].Name).ToHashSet();
        Assert.Contains("Google", memberNames);
        Assert.Contains("Cloudflare", memberNames);
        Assert.Equal(g.GetByName("Modem").Id, pool.ParentId);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test Tests/ConEngine.Tests --filter "FullyQualifiedName~TargetGraphTests"`
Expected: FAIL (type missing).

- [ ] **Step 3: Implement**

`Core/ConEngine/Topology/TargetGraph.cs`:

```csharp
using ConCore.Targets;
using ConEngine.Configuration;

namespace ConEngine.Topology;

/// <summary>
/// Immutable view of the configured targets, pools, profiles, and the
/// reachability tree. Built from validated WatcherOptions.
/// </summary>
public class TargetGraph
{
    private readonly Dictionary<Guid, Target> _targets;
    private readonly Dictionary<Guid, PoolDefinition> _pools;
    private readonly Dictionary<string, Guid> _targetIdsByName;
    private readonly Dictionary<string, Guid> _poolIdsByName;
    private readonly Dictionary<string, PingProfile> _profiles;
    private readonly ILookup<Guid, Target> _childrenByParent;

    public IReadOnlyDictionary<Guid, Target> Targets => _targets;
    public IReadOnlyDictionary<Guid, PoolDefinition> Pools => _pools;

    private TargetGraph(Dictionary<Guid, Target> targets, Dictionary<Guid, PoolDefinition> pools,
        Dictionary<string, PingProfile> profiles)
    {
        _targets = targets;
        _pools = pools;
        _profiles = profiles;
        _targetIdsByName = targets.Values.ToDictionary(t => t.Name, t => t.Id, StringComparer.OrdinalIgnoreCase);
        _poolIdsByName = pools.Values.ToDictionary(p => p.Name, p => p.Id, StringComparer.OrdinalIgnoreCase);
        _childrenByParent = targets.Values
            .Where(t => t.ParentId is not null)
            .ToLookup(t => t.ParentId!.Value);
    }

    /// <summary>Builds the graph. Options must already have passed WatcherOptionsValidator.</summary>
    public static TargetGraph FromOptions(WatcherOptions options)
    {
        var profiles = new Dictionary<string, PingProfile>(StringComparer.OrdinalIgnoreCase)
        {
            ["Internal"] = PingProfile.Internal,
            ["External"] = PingProfile.External,
        };
        foreach (var (profileName, config) in options.Profiles)
            profiles[profileName] = config.ToProfile(profileName);

        var idsByName = options.Targets.ToDictionary(t => t.Name, _ => Guid.NewGuid(), StringComparer.OrdinalIgnoreCase);

        var targets = options.Targets.ToDictionary(
            t => idsByName[t.Name],
            t => new Target
            {
                Id = idsByName[t.Name],
                Name = t.Name,
                Address = t.Address,
                ParentId = t.Parent is { Length: > 0 } p ? idsByName[p] : null,
                ProfileName = t.Profile,
                Enabled = t.Enabled,
            });

        var pools = options.Pools.ToDictionary(
            _ => Guid.NewGuid(),
            p => (name: p.Name, config: p));
        var poolDefs = pools.ToDictionary(
            kv => kv.Key,
            kv => new PoolDefinition
            {
                Id = kv.Key,
                Name = kv.Value.name,
                MemberTargetIds = kv.Value.config.Members.Select(m => idsByName[m]).ToList(),
                ParentId = kv.Value.config.Parent is { Length: > 0 } p ? idsByName[p] : null,
            });

        return new TargetGraph(targets, poolDefs, profiles);
    }

    public Target GetByName(string name) => _targets[_targetIdsByName[name]];
    public PoolDefinition GetPoolByName(string name) => _pools[_poolIdsByName[name]];
    public PingProfile ProfileFor(Target target) => _profiles[target.ProfileName];

    /// <summary>Direct children of a target.</summary>
    public IReadOnlyList<Target> GetChildren(Guid parentId) => _childrenByParent[parentId].ToList();

    /// <summary>All transitive descendants of a target (excluding itself).</summary>
    public IReadOnlyList<Target> GetDescendants(Guid ancestorId)
    {
        var result = new List<Target>();
        var queue = new Queue<Guid>();
        queue.Enqueue(ancestorId);
        while (queue.Count > 0)
        {
            foreach (var child in _childrenByParent[queue.Dequeue()])
            {
                result.Add(child);
                queue.Enqueue(child.Id);
            }
        }
        return result;
    }
}
```

- [ ] **Step 4: Run tests**

Run: `dotnet test Tests/ConEngine.Tests --filter "FullyQualifiedName~TargetGraphTests"` → PASS (6 tests).

- [ ] **Step 5: Commit**

```powershell
git add Core/ConEngine/Topology Tests/
git commit -m "feat: TargetGraph builds domain model from validated options"
```

---

### Task 9: TargetMonitor

**Files:**
- Create: `Core/ConEngine/Tracking/TargetMonitor.cs`
- Test: `Tests/ConEngine.Tests/TargetMonitorTests.cs`

**Interfaces:**
- Consumes: `Target`, `PingProfile` (Task 2), events (Task 3), `HostTracker` (Task 4), `PingDiscipline` (Task 5).
- Produces (namespace `ConEngine.Tracking`):

```csharp
public class TargetMonitor : IDisposable
{
    public TargetMonitor(Target target, PingProfile profile, PingDiscipline discipline,
        TimeProvider timeProvider, ILoggerFactory loggerFactory);
    public Target Target { get; }
    public PingProfile Profile { get; }
    public PingDiscipline Discipline { get; }
    public HostState CurrentState { get; }              // Initializing before StartTracking
    public IPAddress? ResolvedAddress { get; }          // null until StartTracking
    public IObservable<TargetTransition> TransitionSource { get; }
    public IObservable<TargetStatus> StatusSource { get; }
    public void StartTracking(IPAddress resolvedAddress); // idempotent
    public void RecordPingResult(PingResult result);
    public void SetUnreachable(Guid ancestorId);          // works before or after StartTracking
    public void Release();
    public void ReportResolutionFailure(string detail);   // emits ResolutionFailed transition
}
```

Behavior contract:
- Every host-level `AnalysisTransition` is mapped to a `TargetTransition` (adds `TargetId`, `TargetName`, `AverageRoundtripTime` from `Status`). If a burst window was active at transition time and burst-window entries exist, `Reason.BurstLossRate` is filled from `HostTracker.EntriesSince(BurstStartedAt)` **before** the discipline is notified (notification may reset the window).
- The monitor itself feeds `Discipline.OnStateChanged(transition.To)` — callers never talk to the discipline directly.
- `SetUnreachable`/`Release` before `StartTracking`: tracked locally, transitions emitted with pre-tracker state bookkeeping (`Initializing`/`Unreachable`/`Starting`), and applied to the tracker when it is created.
- `ReportResolutionFailure`: first call transitions `Initializing → Starting` with `Trigger = ResolutionFailed` and the detail message; subsequent calls emit nothing new (still Starting).

- [ ] **Step 1: Write the failing tests**

`Tests/ConEngine.Tests/TargetMonitorTests.cs`:

```csharp
using System.Net;
using ConCore;
using ConCore.Targets;
using ConEngine.Events;
using ConEngine.Scheduling;
using ConEngine.Tracking;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace ConEngine.Tests;

public class TargetMonitorTests
{
    private static readonly IPAddress Ip = IPAddress.Parse("8.8.8.8");
    private static readonly PingProfile Profile = new()
    {
        Name = "Test",
        NormalInterval = TimeSpan.FromSeconds(10),
        BurstInterval = TimeSpan.FromSeconds(1),
        BurstMaxDuration = TimeSpan.FromSeconds(60),
        HistorySize = 20,
        MinUsableSample = 5,
    };

    private static (TargetMonitor monitor, List<TargetTransition> transitions, FakeTimeProvider time) Make()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-08-14T00:00:00Z"));
        var target = new Target
        {
            Id = Guid.NewGuid(), Name = "Google", Address = "8.8.8.8", ProfileName = "Test",
        };
        var monitor = new TargetMonitor(target, Profile,
            new PingDiscipline(Profile, time), time, NullLoggerFactory.Instance);
        var transitions = new List<TargetTransition>();
        monitor.TransitionSource.Subscribe(transitions.Add);
        return (monitor, transitions, time);
    }

    private static void Feed(TargetMonitor m, FakeTimeProvider time, int count, bool success)
    {
        for (int i = 0; i < count; i++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            m.RecordPingResult(new PingResult(Ip, success, success ? 12 : 0, timestamp: time.GetUtcNow()));
        }
    }

    [Fact]
    public void Transitions_carry_target_identity()
    {
        var (m, transitions, time) = Make();
        m.StartTracking(Ip);
        Feed(m, time, 5, success: true);

        Assert.Equal(HostState.Responding, m.CurrentState);
        var t = transitions.Last();
        Assert.Equal(m.Target.Id, t.TargetId);
        Assert.Equal("Google", t.TargetName);
        Assert.Equal(HostState.Responding, t.To);
        Assert.True(t.AverageRoundtripTime > 0);
    }

    [Fact]
    public void Discipline_follows_state_changes()
    {
        var (m, _, time) = Make();
        m.StartTracking(Ip);
        Feed(m, time, 5, success: true);
        Assert.Null(m.Discipline.BurstStartedAt);

        Feed(m, time, 20, success: false); // Responding -> Faltering -> Down
        Assert.Equal(HostState.Down, m.CurrentState);
        Assert.NotNull(m.Discipline.BurstStartedAt);
    }

    [Fact]
    public void Burst_loss_rate_is_attached_to_transitions_during_burst()
    {
        var (m, transitions, time) = Make();
        m.StartTracking(Ip);
        Feed(m, time, 20, success: true);

        // Failures start: first transition (-> Faltering) starts the burst window.
        Feed(m, time, 20, success: false);
        var toDown = transitions.Single(t => t.To == HostState.Down);
        Assert.NotNull(toDown.Reason.BurstLossRate);
        Assert.Equal(1.0, toDown.Reason.BurstLossRate!.Value, 2);
    }

    [Fact]
    public void SetUnreachable_and_release_work_after_tracking()
    {
        var (m, transitions, time) = Make();
        m.StartTracking(Ip);
        Feed(m, time, 5, success: true);
        var ancestor = Guid.NewGuid();

        m.SetUnreachable(ancestor);
        Assert.Equal(HostState.Unreachable, m.CurrentState);
        Assert.True(m.Discipline.IsParked);
        Assert.Equal(ancestor, transitions.Last().Reason.AncestorId);

        m.Release();
        Assert.Equal(HostState.Starting, m.CurrentState);
        Assert.False(m.Discipline.IsParked);
    }

    [Fact]
    public void SetUnreachable_before_tracking_starts_parked()
    {
        var (m, transitions, _) = Make();
        m.SetUnreachable(Guid.NewGuid());
        Assert.Equal(HostState.Unreachable, m.CurrentState);
        Assert.True(m.Discipline.IsParked);
        Assert.Equal(TransitionTrigger.ParentDown, transitions.Last().Reason.Trigger);

        m.Release();
        Assert.Equal(HostState.Starting, m.CurrentState);
    }

    [Fact]
    public void Resolution_failure_reports_once()
    {
        var (m, transitions, _) = Make();
        m.ReportResolutionFailure("DNS lookup failed for example.com");
        m.ReportResolutionFailure("DNS lookup failed for example.com");

        var t = Assert.Single(transitions);
        Assert.Equal(TransitionTrigger.ResolutionFailed, t.Reason.Trigger);
        Assert.Equal(HostState.Starting, m.CurrentState);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test Tests/ConEngine.Tests --filter "FullyQualifiedName~TargetMonitorTests"`
Expected: FAIL (type missing).

- [ ] **Step 3: Implement**

`Core/ConEngine/Tracking/TargetMonitor.cs`:

```csharp
using System.Net;
using System.Reactive.Subjects;
using ConCore;
using ConCore.Targets;
using ConEngine.Events;
using ConEngine.Scheduling;
using Microsoft.Extensions.Logging;

namespace ConEngine.Tracking;

/// <summary>
/// Binds one Target to its tracker and discipline, and enriches host-level
/// transitions into target-level TargetTransition events.
/// </summary>
public class TargetMonitor : IDisposable
{
    private readonly TimeProvider _timeProvider;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<TargetMonitor> _log;
    private readonly Lock _lock = new();

    private HostTracker? _tracker;
    private IDisposable? _transitionSubscription;
    private IDisposable? _statusSubscription;

    // Pre-tracker state bookkeeping (before StartTracking / for resolution failures).
    private HostState _preTrackerState = HostState.Initializing;
    private Guid? _pendingUnreachableAncestor;
    private bool _resolutionFailureReported;

    private readonly ReplaySubject<TargetTransition> _transitionSubject = new(bufferSize: 1);
    private readonly ReplaySubject<TargetStatus> _statusSubject = new(bufferSize: 1);

    public TargetMonitor(Target target, PingProfile profile, PingDiscipline discipline,
        TimeProvider timeProvider, ILoggerFactory loggerFactory)
    {
        Target = target;
        Profile = profile;
        Discipline = discipline;
        _timeProvider = timeProvider;
        _loggerFactory = loggerFactory;
        _log = loggerFactory.CreateLogger<TargetMonitor>();
    }

    public Target Target { get; }
    public PingProfile Profile { get; }
    public PingDiscipline Discipline { get; }
    public IPAddress? ResolvedAddress { get; private set; }

    public IObservable<TargetTransition> TransitionSource => _transitionSubject;
    public IObservable<TargetStatus> StatusSource => _statusSubject;

    public HostState CurrentState
    {
        get { lock (_lock) { return _tracker?.CurrentState ?? _preTrackerState; } }
    }

    /// <summary>Creates the tracker once the address is known. Idempotent.</summary>
    public void StartTracking(IPAddress resolvedAddress)
    {
        lock (_lock)
        {
            if (_tracker is not null)
                return;

            ResolvedAddress = resolvedAddress;
            _tracker = new HostTracker(resolvedAddress, Profile, _timeProvider,
                _loggerFactory.CreateLogger<HostTracker>());

            _transitionSubscription = _tracker.HostStateChangeSource.Subscribe(OnAnalysisTransition);
            _statusSubscription = _tracker.HostStatusSource.Subscribe(status =>
                _statusSubject.OnNext(new TargetStatus
                {
                    TargetId = Target.Id, TargetName = Target.Name, Host = status,
                }));

            // Honor topology commands that arrived before we started.
            if (_pendingUnreachableAncestor is { } ancestor)
                _tracker.MarkUnreachable(ancestor);
        }
    }

    public void RecordPingResult(PingResult result)
    {
        lock (_lock) { _tracker?.RecordPingResult(result); }
    }

    public void SetUnreachable(Guid ancestorId)
    {
        lock (_lock)
        {
            if (_tracker is not null)
            {
                _tracker.MarkUnreachable(ancestorId);
                return;
            }
            if (_preTrackerState == HostState.Unreachable)
                return;
            _pendingUnreachableAncestor = ancestorId;
            EmitPreTrackerTransition(HostState.Unreachable, new TransitionReason
            {
                Trigger = TransitionTrigger.ParentDown, AncestorId = ancestorId,
            });
        }
    }

    public void Release()
    {
        lock (_lock)
        {
            if (_tracker is not null)
            {
                _tracker.Release();
                return;
            }
            if (_preTrackerState != HostState.Unreachable)
                return;
            _pendingUnreachableAncestor = null;
            EmitPreTrackerTransition(HostState.Starting,
                new TransitionReason { Trigger = TransitionTrigger.ParentRecovered });
        }
    }

    /// <summary>Reports a DNS resolution failure. Emits one transition; later failures are quiet.</summary>
    public void ReportResolutionFailure(string detail)
    {
        lock (_lock)
        {
            if (_resolutionFailureReported || _tracker is not null)
                return;
            _resolutionFailureReported = true;
            EmitPreTrackerTransition(HostState.Starting, new TransitionReason
            {
                Trigger = TransitionTrigger.ResolutionFailed, Detail = detail,
            });
        }
    }

    private void EmitPreTrackerTransition(HostState to, TransitionReason reason)
    {
        var from = _preTrackerState;
        _preTrackerState = to;
        Discipline.OnStateChanged(to);
        _transitionSubject.OnNext(new TargetTransition
        {
            TargetId = Target.Id,
            TargetName = Target.Name,
            From = from,
            To = to,
            Timestamp = _timeProvider.GetUtcNow(),
            Reason = reason,
        });
    }

    private void OnAnalysisTransition(AnalysisTransition at)
    {
        // Compute burst loss BEFORE notifying the discipline (which may reset the window).
        var reason = at.Reason;
        if (Discipline.BurstStartedAt is { } burstStart && _tracker is not null)
        {
            var entries = _tracker.EntriesSince(burstStart).ToList();
            if (entries.Count > 0)
                reason = reason with
                {
                    BurstLossRate = 1.0 - entries.Count(e => e.Success) / (double)entries.Count,
                };
        }

        Discipline.OnStateChanged(at.To);

        var transition = new TargetTransition
        {
            TargetId = Target.Id,
            TargetName = Target.Name,
            From = at.From,
            To = at.To,
            Timestamp = at.Timestamp,
            Reason = reason,
            AverageRoundtripTime = at.Status.AverageRoundtripTime,
        };
        _log.LogInformation("Transition: {Transition}", transition);
        _transitionSubject.OnNext(transition);
    }

    public void Dispose()
    {
        _transitionSubscription?.Dispose();
        _statusSubscription?.Dispose();
        _transitionSubject.OnCompleted();
        _statusSubject.OnCompleted();
        GC.SuppressFinalize(this);
    }
}
```

- [ ] **Step 4: Run tests**

Run: `dotnet test Tests/ConEngine.Tests --filter "FullyQualifiedName~TargetMonitorTests"` → PASS (6 tests).

- [ ] **Step 5: Commit**

```powershell
git add Core/ConEngine/Tracking Tests/
git commit -m "feat: TargetMonitor binds target, tracker, and discipline"
```

---

### Task 10: TopologyCoordinator

**Files:**
- Create: `Core/ConEngine/Topology/TopologyCoordinator.cs`
- Test: `Tests/ConEngine.Tests/TopologyCoordinatorTests.cs`

**Interfaces:**
- Consumes: `TargetGraph` (Task 8), `TargetMonitor` (Task 9), events (Task 3).
- Produces (namespace `ConEngine.Topology`):

```csharp
public class TopologyCoordinator : IDisposable
{
    public TopologyCoordinator(TargetGraph graph,
        IReadOnlyDictionary<Guid, TargetMonitor> monitors,
        ILogger<TopologyCoordinator> log);
    public void Start();   // subscribes to every monitor's TransitionSource
}
```

Behavior contract:
- On a transition **to `Down`** of target T: every descendant of T (via `graph.GetDescendants`) that has a monitor gets `SetUnreachable(T.Id)`. Descendants already `Unreachable` stay put (SetUnreachable is idempotent at the monitor level).
- On a transition **from `Down` to `Recovering` or `Responding`** of target T: every descendant of T whose `CurrentState == Unreachable` gets `Release()`.
- Reacting to `ParentDown`-triggered transitions must not recurse infinitely (SetUnreachable of a subtree root cascades naturally: its own `Down`→? No — `Unreachable` is not `Down`, so no re-cascade; the coordinator handles transitive descendants directly in one pass).

- [ ] **Step 1: Write the failing tests**

`Tests/ConEngine.Tests/TopologyCoordinatorTests.cs`:

```csharp
using System.Net;
using ConCore;
using ConCore.Targets;
using ConEngine.Configuration;
using ConEngine.Scheduling;
using ConEngine.Topology;
using ConEngine.Tracking;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace ConEngine.Tests;

public class TopologyCoordinatorTests
{
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-08-14T00:00:00Z"));
    private readonly TargetGraph _graph;
    private readonly Dictionary<Guid, TargetMonitor> _monitors = new();
    private readonly TopologyCoordinator _coordinator;

    private static readonly PingProfile Fast = new()
    {
        Name = "Fast", HistorySize = 20, MinUsableSample = 5,
    };

    public TopologyCoordinatorTests()
    {
        _graph = TargetGraph.FromOptions(new WatcherOptions
        {
            Targets =
            [
                new TargetConfig { Name = "Router", Address = "192.168.1.1", Profile = "Internal" },
                new TargetConfig { Name = "Modem", Address = "192.168.100.1", Parent = "Router", Profile = "Internal" },
                new TargetConfig { Name = "Google", Address = "8.8.8.8", Parent = "Modem" },
            ],
        });
        foreach (var target in _graph.Targets.Values)
        {
            var monitor = new TargetMonitor(target, Fast, new PingDiscipline(Fast, _time),
                _time, NullLoggerFactory.Instance);
            monitor.StartTracking(IPAddress.Parse(target.Address));
            _monitors[target.Id] = monitor;
        }
        _coordinator = new TopologyCoordinator(_graph, _monitors, NullLogger<TopologyCoordinator>.Instance);
        _coordinator.Start();
    }

    private void Feed(string name, int count, bool success)
    {
        var monitor = _monitors[_graph.GetByName(name).Id];
        for (int i = 0; i < count; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            monitor.RecordPingResult(new PingResult(monitor.ResolvedAddress!, success,
                success ? 5 : 0, timestamp: _time.GetUtcNow()));
        }
    }

    private HostState State(string name) => _monitors[_graph.GetByName(name).Id].CurrentState;

    [Fact]
    public void Router_down_cascades_unreachable_to_all_descendants()
    {
        Feed("Router", 5, true);
        Feed("Modem", 5, true);
        Feed("Google", 5, true);

        Feed("Router", 20, false); // Responding -> Faltering -> Down
        Assert.Equal(HostState.Down, State("Router"));
        Assert.Equal(HostState.Unreachable, State("Modem"));
        Assert.Equal(HostState.Unreachable, State("Google"));
    }

    [Fact]
    public void Router_recovery_releases_descendants_to_starting()
    {
        Feed("Router", 5, true);
        Feed("Modem", 5, true);
        Feed("Google", 5, true);
        Feed("Router", 20, false);
        Assert.Equal(HostState.Unreachable, State("Google"));

        // Router comes back: Down -> Recovering (>= RecoveringThreshold successes).
        Feed("Router", 10, true);
        Assert.NotEqual(HostState.Down, State("Router"));
        Assert.Equal(HostState.Starting, State("Modem"));
        Assert.Equal(HostState.Starting, State("Google"));
    }

    [Fact]
    public void Mid_tree_down_only_affects_subtree()
    {
        Feed("Router", 5, true);
        Feed("Modem", 5, true);
        Feed("Google", 5, true);

        Feed("Modem", 20, false);
        Assert.Equal(HostState.Responding, State("Router"));
        Assert.Equal(HostState.Down, State("Modem"));
        Assert.Equal(HostState.Unreachable, State("Google"));
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test Tests/ConEngine.Tests --filter "FullyQualifiedName~TopologyCoordinatorTests"`
Expected: FAIL (type missing).

- [ ] **Step 3: Implement**

`Core/ConEngine/Topology/TopologyCoordinator.cs`:

```csharp
using ConEngine.Events;
using ConEngine.Tracking;
using Microsoft.Extensions.Logging;

namespace ConEngine.Topology;

/// <summary>
/// Enforces the reachability tree: when a target goes Down its descendants
/// become Unreachable (and stop pinging); when it recovers they are released.
/// </summary>
public class TopologyCoordinator : IDisposable
{
    private readonly TargetGraph _graph;
    private readonly IReadOnlyDictionary<Guid, TargetMonitor> _monitors;
    private readonly ILogger<TopologyCoordinator> _log;
    private readonly List<IDisposable> _subscriptions = new();

    public TopologyCoordinator(TargetGraph graph,
        IReadOnlyDictionary<Guid, TargetMonitor> monitors,
        ILogger<TopologyCoordinator> log)
    {
        _graph = graph;
        _monitors = monitors;
        _log = log;
    }

    /// <summary>Subscribes to all monitors. Call once after monitors are created.</summary>
    public void Start()
    {
        foreach (var monitor in _monitors.Values)
            _subscriptions.Add(monitor.TransitionSource.Subscribe(OnTransition));
    }

    private void OnTransition(TargetTransition transition)
    {
        if (transition.To == HostState.Down)
        {
            _log.LogInformation("{Target} is Down; marking descendants unreachable", transition.TargetName);
            foreach (var descendant in _graph.GetDescendants(transition.TargetId))
            {
                if (_monitors.TryGetValue(descendant.Id, out var monitor))
                    monitor.SetUnreachable(transition.TargetId);
            }
        }
        else if (transition.From == HostState.Down &&
                 transition.To is HostState.Recovering or HostState.Responding)
        {
            _log.LogInformation("{Target} recovered; releasing descendants", transition.TargetName);
            foreach (var descendant in _graph.GetDescendants(transition.TargetId))
            {
                if (_monitors.TryGetValue(descendant.Id, out var monitor) &&
                    monitor.CurrentState == HostState.Unreachable)
                    monitor.Release();
            }
        }
    }

    public void Dispose()
    {
        foreach (var s in _subscriptions)
            s.Dispose();
        _subscriptions.Clear();
        GC.SuppressFinalize(this);
    }
}
```

- [ ] **Step 4: Run tests**

Run: `dotnet test Tests/ConEngine.Tests --filter "FullyQualifiedName~TopologyCoordinatorTests"` → PASS (3 tests).

- [ ] **Step 5: Commit**

```powershell
git add Core/ConEngine/Topology Tests/
git commit -m "feat: TopologyCoordinator cascades Down/recovery through the tree"
```

---

### Task 11: PoolCoordinator

**Files:**
- Create: `Core/ConEngine/Topology/PoolCoordinator.cs`
- Test: `Tests/ConEngine.Tests/PoolCoordinatorTests.cs`

**Interfaces:**
- Consumes: `PoolDefinition` (Task 2), `TargetMonitor` (Task 9), events (Task 3).
- Produces (namespace `ConEngine.Topology`):

```csharp
public class PoolCoordinator : IDisposable
{
    public PoolCoordinator(PoolDefinition pool, IReadOnlyList<TargetMonitor> members,
        TimeProvider timeProvider, ILogger<PoolCoordinator> log);
    public HostState CurrentState { get; }              // starts Initializing
    public IObservable<TargetTransition> TransitionSource { get; }
    public void Start();
    public static TimeSpan PhaseOffset(int memberIndex, int memberCount, TimeSpan interval);
}
```

Behavior contract (spec §5 with startup guard):
- `PhaseOffset(k, n, interval) = interval * k / n`.
- On every member transition, derive pool state:
  1. If **all** members are in `Initializing` or `Starting` → `Starting`.
  2. Else if **any** member is `Responding` → `Responding`.
  3. Else if **all** members are `Down` or `Unreachable` → `Down`.
  4. Else → `Faltering`.
- Emit a `TargetTransition` (with the pool's `Id`/`Name`) only when the derived state changes; `Reason.Trigger = PoolMembersChanged`, `Reason.Detail` lists member states like `"Google=Down, Cloudflare=Responding"`.

- [ ] **Step 1: Write the failing tests**

`Tests/ConEngine.Tests/PoolCoordinatorTests.cs`:

```csharp
using System.Net;
using ConCore;
using ConCore.Targets;
using ConEngine.Events;
using ConEngine.Scheduling;
using ConEngine.Topology;
using ConEngine.Tracking;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace ConEngine.Tests;

public class PoolCoordinatorTests
{
    private static readonly PingProfile Profile = new()
    {
        Name = "Test", HistorySize = 20, MinUsableSample = 5,
    };

    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-08-14T00:00:00Z"));
    private readonly TargetMonitor _google;
    private readonly TargetMonitor _cloudflare;
    private readonly PoolCoordinator _pool;
    private readonly List<TargetTransition> _poolTransitions = new();

    public PoolCoordinatorTests()
    {
        _google = MakeMonitor("Google", "8.8.8.8");
        _cloudflare = MakeMonitor("Cloudflare", "1.1.1.1");
        var def = new PoolDefinition
        {
            Id = Guid.NewGuid(), Name = "Internet",
            MemberTargetIds = [_google.Target.Id, _cloudflare.Target.Id],
        };
        _pool = new PoolCoordinator(def, [_google, _cloudflare], _time,
            NullLogger<PoolCoordinator>.Instance);
        _pool.TransitionSource.Subscribe(_poolTransitions.Add);
        _pool.Start();
    }

    private TargetMonitor MakeMonitor(string name, string ip)
    {
        var target = new Target { Id = Guid.NewGuid(), Name = name, Address = ip, ProfileName = "Test" };
        var monitor = new TargetMonitor(target, Profile, new PingDiscipline(Profile, _time),
            _time, NullLoggerFactory.Instance);
        monitor.StartTracking(IPAddress.Parse(ip));
        return monitor;
    }

    private void Feed(TargetMonitor m, int count, bool success)
    {
        for (int i = 0; i < count; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            m.RecordPingResult(new PingResult(m.ResolvedAddress!, success,
                success ? 5 : 0, timestamp: _time.GetUtcNow()));
        }
    }

    [Fact]
    public void Phase_offsets_stagger_members()
    {
        var interval = TimeSpan.FromSeconds(15);
        Assert.Equal(TimeSpan.Zero, PoolCoordinator.PhaseOffset(0, 3, interval));
        Assert.Equal(TimeSpan.FromSeconds(5), PoolCoordinator.PhaseOffset(1, 3, interval));
        Assert.Equal(TimeSpan.FromSeconds(10), PoolCoordinator.PhaseOffset(2, 3, interval));
    }

    [Fact]
    public void Pool_responds_when_any_member_responds()
    {
        Feed(_google, 5, true);
        Assert.Equal(HostState.Responding, _pool.CurrentState);
    }

    [Fact]
    public void Pool_rides_through_single_member_outage()
    {
        Feed(_google, 5, true);
        Feed(_cloudflare, 5, true);
        Feed(_google, 20, false); // Google: Responding -> ... -> Down
        Assert.Equal(HostState.Down, _google.CurrentState);
        Assert.Equal(HostState.Responding, _pool.CurrentState);
    }

    [Fact]
    public void Pool_down_when_all_members_down_with_detail()
    {
        Feed(_google, 5, true);
        Feed(_cloudflare, 5, true);
        Feed(_google, 20, false);
        Feed(_cloudflare, 20, false);

        Assert.Equal(HostState.Down, _pool.CurrentState);
        var t = _poolTransitions.Last();
        Assert.Equal(TransitionTrigger.PoolMembersChanged, t.Reason.Trigger);
        Assert.Contains("Google=Down", t.Reason.Detail);
    }

    [Fact]
    public void Pool_faltering_when_members_degraded_but_not_all_down()
    {
        Feed(_google, 5, true);
        Feed(_cloudflare, 5, true);
        Feed(_google, 20, false);                 // Google Down
        Feed(_cloudflare, 8, false);              // Cloudflare Faltering (5/13 ~ 38%)
        Assert.Equal(HostState.Faltering, _cloudflare.CurrentState);
        Assert.Equal(HostState.Faltering, _pool.CurrentState);
    }

    [Fact]
    public void Pool_stays_starting_until_a_member_resolves()
    {
        Assert.Equal(HostState.Initializing, _pool.CurrentState);
        Feed(_google, 2, true); // not enough for a usable sample yet
        Assert.True(_pool.CurrentState is HostState.Initializing or HostState.Starting);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test Tests/ConEngine.Tests --filter "FullyQualifiedName~PoolCoordinatorTests"`
Expected: FAIL (type missing).

- [ ] **Step 3: Implement**

`Core/ConEngine/Topology/PoolCoordinator.cs`:

```csharp
using System.Reactive.Subjects;
using ConCore.Targets;
using ConEngine.Events;
using ConEngine.Tracking;
using Microsoft.Extensions.Logging;

namespace ConEngine.Topology;

/// <summary>
/// Derives an aggregate state for a rotation pool from its members and emits
/// pool-level transitions on the shared TargetTransition shape.
/// </summary>
public class PoolCoordinator : IDisposable
{
    private readonly PoolDefinition _pool;
    private readonly IReadOnlyList<TargetMonitor> _members;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<PoolCoordinator> _log;
    private readonly Lock _lock = new();
    private readonly List<IDisposable> _subscriptions = new();

    private readonly ReplaySubject<TargetTransition> _transitionSubject = new(bufferSize: 1);
    public IObservable<TargetTransition> TransitionSource => _transitionSubject;

    public HostState CurrentState { get; private set; } = HostState.Initializing;

    public PoolCoordinator(PoolDefinition pool, IReadOnlyList<TargetMonitor> members,
        TimeProvider timeProvider, ILogger<PoolCoordinator> log)
    {
        _pool = pool;
        _members = members;
        _timeProvider = timeProvider;
        _log = log;
    }

    /// <summary>Stagger offset for member k of n at the given normal interval.</summary>
    public static TimeSpan PhaseOffset(int memberIndex, int memberCount, TimeSpan interval) =>
        interval * memberIndex / memberCount;

    public void Start()
    {
        foreach (var member in _members)
            _subscriptions.Add(member.TransitionSource.Subscribe(_ => Recalculate()));
    }

    private void Recalculate()
    {
        lock (_lock)
        {
            var states = _members.Select(m => m.CurrentState).ToList();

            HostState next;
            if (states.All(s => s is HostState.Initializing or HostState.Starting))
                next = HostState.Starting;
            else if (states.Any(s => s == HostState.Responding))
                next = HostState.Responding;
            else if (states.All(s => s is HostState.Down or HostState.Unreachable))
                next = HostState.Down;
            else
                next = HostState.Faltering;

            if (next == CurrentState)
                return;

            var from = CurrentState;
            CurrentState = next;
            var detail = string.Join(", ",
                _members.Select(m => $"{m.Target.Name}={m.CurrentState}"));
            var transition = new TargetTransition
            {
                TargetId = _pool.Id,
                TargetName = _pool.Name,
                From = from,
                To = next,
                Timestamp = _timeProvider.GetUtcNow(),
                Reason = new TransitionReason
                {
                    Trigger = TransitionTrigger.PoolMembersChanged,
                    Detail = detail,
                },
            };
            _log.LogInformation("Pool transition: {Transition}", transition);
            _transitionSubject.OnNext(transition);
        }
    }

    public void Dispose()
    {
        foreach (var s in _subscriptions)
            s.Dispose();
        _subscriptions.Clear();
        _transitionSubject.OnCompleted();
        GC.SuppressFinalize(this);
    }
}
```

- [ ] **Step 4: Run tests**

Run: `dotnet test Tests/ConEngine.Tests --filter "FullyQualifiedName~PoolCoordinatorTests"` → PASS (6 tests).

- [ ] **Step 5: Commit**

```powershell
git add Core/ConEngine/Topology Tests/
git commit -m "feat: PoolCoordinator derives aggregate pool state with stagger"
```

---

### Task 12: WatcherEngine rewrite

**Files:**
- Rewrite: `Core/ConEngine/WatcherEngine.cs`
- Modify: `ConTinker/Program.cs` (DI registration switch)
- Modify: `ConTinker/TinkerService.cs` (drop `StartWatching` call)
- Test: `Tests/ConEngine.Tests/WatcherEngineTests.cs`

**Interfaces:**
- Consumes: everything above, plus existing `IDomainNameResolverFactory`/`IDomainNameResolver` from ConCore/ConInfrastructure (`Task<DomainResolutionResult> ResolveAsync(string domainName, int timeoutMs)`; result exposes `IPv4Addresses`/`IPv6Addresses` lists).
- Produces (namespace `ConEngine`):

```csharp
public class WatcherEngine
{
    public WatcherEngine(TimeProvider timeProvider, IPingTransceiverFactory pingFactory,
        IDomainNameResolverFactory resolverFactory, ILoggerFactory loggerFactory,
        IOptions<WatcherOptions> options);
    public Task StartAsync(CancellationToken cancellationToken);
    public Task StopAsync();
    public IObservable<TargetTransition> TransitionSource { get; }  // merged: all monitors + pools
    public IReadOnlyDictionary<Guid, TargetMonitor> Monitors { get; }
    public IReadOnlyList<PoolCoordinator> PoolCoordinators { get; }
    public TargetGraph Graph { get; }
}
```

Behavior contract:
- `StartAsync`: build `TargetGraph.FromOptions`; create a `PingDiscipline` per enabled target (pool members get `initialDelay = PoolCoordinator.PhaseOffset(k, n, profile.NormalInterval)` by member order); create `TargetMonitor`s (all targets, including disabled ones excluded — only enabled targets get monitors); wire `TopologyCoordinator` and `PoolCoordinator`s and `Start()` them; create `PingManager`; subscribe `PingResultSource` → route each result to the monitor registered for `result.IpAddress`; then for each enabled target begin **address activation**: IP literal → `IPAddress.TryParse` succeeds → activate immediately; hostname → background resolution loop (below). Activation = `monitor.StartTracking(ip)`, register `ip → monitor` in the routing table, `manager.GetOrCreateController(ip, monitor.Discipline, (int)profile.PingTimeout.TotalMilliseconds)`.
- Resolution loop per hostname target: try `resolver.ResolveAsync(address, 5000)`; on failure or empty `IPv4Addresses` → `monitor.ReportResolutionFailure(detail)` and retry after backoff 30 s, 60 s, 120 s, 240 s, capped at 300 s (all via `Task.Delay(delay, timeProvider, ct)`); on success take the first IPv4 address and activate.
- **Merged transitions:** `TransitionSource` is a single `Subject<TargetTransition>` that every monitor's and pool's `TransitionSource` is forwarded into.
- `StopAsync`: cancel internal CTS, dispose coordinators, manager, monitors.
- Old members (`StartWatching`, `StopWatching`, `RemoveTracking`, `ClearAllTracking`, `TrackedHostCount`, the `HostTrackingOptions` dependency, hardcoded cloudflare resolution, `BridgeProfile`) are **deleted**.

- [ ] **Step 1: Write the failing tests**

`Tests/ConEngine.Tests/WatcherEngineTests.cs` — integration-style with the fake transceiver, real time, and fast profiles:

```csharp
using System.Net;
using ConEngine.Configuration;
using ConEngine.Events;
using ConEngine.Tests.Fakes;
using ConEngine.Tracking;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ConEngine.Tests;

public class WatcherEngineTests : IAsyncLifetime
{
    private static readonly IPAddress RouterIp = IPAddress.Parse("192.168.1.1");
    private static readonly IPAddress GoogleIp = IPAddress.Parse("8.8.8.8");
    private static readonly IPAddress CloudflareIp = IPAddress.Parse("1.1.1.1");

    private readonly FakePingTransceiverFactory _factory = new();
    private readonly CancellationTokenSource _cts = new();
    private WatcherEngine _engine = null!;
    private readonly List<TargetTransition> _transitions = new();

    private static WatcherOptions Options() => new()
    {
        Profiles =
        {
            ["Fast"] = new PingProfileConfig
            {
                NormalIntervalSeconds = 0.02,
                BurstIntervalSeconds = 0.01,
                BurstMaxDurationSeconds = 5,
                HistorySize = 20,
                MinUsableSample = 5,
            },
        },
        Targets =
        [
            new TargetConfig { Name = "Router", Address = "192.168.1.1", Profile = "Fast" },
            new TargetConfig { Name = "Google", Address = "8.8.8.8", Parent = "Router", Profile = "Fast" },
            new TargetConfig { Name = "Cloudflare", Address = "1.1.1.1", Parent = "Router", Profile = "Fast" },
        ],
        Pools = [new PoolConfig { Name = "Internet", Members = ["Google", "Cloudflare"] }],
    };

    public async Task InitializeAsync()
    {
        _engine = new WatcherEngine(TimeProvider.System, _factory,
            new ConInfrastructure.DomainNameResolverFactory(), NullLoggerFactory.Instance,
            Microsoft.Extensions.Options.Options.Create(Options()));
        _engine.TransitionSource.Subscribe(t => { lock (_transitions) { _transitions.Add(t); } });
        await _engine.StartAsync(_cts.Token);
    }

    public async Task DisposeAsync()
    {
        _cts.Cancel();
        await _engine.StopAsync();
    }

    private static async Task Eventually(Func<bool> condition, int timeoutMs = 15000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return;
            await Task.Delay(25);
        }
        Assert.Fail("Condition not met within timeout");
    }

    private HostState State(string name) => _engine.Monitors[_engine.Graph.GetByName(name).Id].CurrentState;

    [Fact]
    public async Task Healthy_network_reaches_responding_and_pool_responds()
    {
        await Eventually(() => State("Router") == HostState.Responding);
        await Eventually(() => State("Google") == HostState.Responding);
        await Eventually(() => _engine.PoolCoordinators.Single().CurrentState == HostState.Responding);
    }

    [Fact]
    public async Task Router_down_cascades_and_pinging_pauses_for_descendants()
    {
        await Eventually(() => State("Google") == HostState.Responding);

        _factory.SetUp(RouterIp, false);
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

        // Recovery: router back up -> descendants released and re-prove.
        _factory.SetUp(RouterIp, true);
        await Eventually(() => State("Router") == HostState.Responding);
        await Eventually(() => State("Google") == HostState.Responding);
    }

    [Fact]
    public async Task Single_external_outage_marks_down_but_pool_survives()
    {
        await Eventually(() => State("Google") == HostState.Responding);
        await Eventually(() => State("Cloudflare") == HostState.Responding);

        _factory.SetUp(GoogleIp, false);
        await Eventually(() => State("Google") == HostState.Down);
        Assert.Equal(HostState.Responding, State("Cloudflare"));
        Assert.Equal(HostState.Responding, _engine.PoolCoordinators.Single().CurrentState);

        bool downHasEvidence;
        lock (_transitions)
        {
            var t = _transitions.Last(x => x.TargetName == "Google" && x.To == HostState.Down);
            downHasEvidence = t.Reason.SuccessRate is not null && t.Reason.SampleSize is not null;
        }
        Assert.True(downHasEvidence);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test Tests/ConEngine.Tests --filter "FullyQualifiedName~WatcherEngineTests"`
Expected: FAIL (WatcherEngine has no such ctor).

- [ ] **Step 3: Rewrite WatcherEngine**

Replace the full contents of `Core/ConEngine/WatcherEngine.cs`:

```csharp
using System.Net;
using System.Reactive.Subjects;
using ConCore;
using ConCore.Targets;
using ConEngine.Configuration;
using ConEngine.Events;
using ConEngine.Network;
using ConEngine.Scheduling;
using ConEngine.Topology;
using ConEngine.Tracking;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;

namespace ConEngine;

/// <summary>
/// Orchestrates the Phase 1 engine: builds the target graph from configuration,
/// wires monitors, disciplines, topology and pool coordinators, and routes
/// ping results from the PingManager to the right monitor.
/// </summary>
public class WatcherEngine
{
    private readonly TimeProvider _timeProvider;
    private readonly IPingTransceiverFactory _pingFactory;
    private readonly IDomainNameResolverFactory _resolverFactory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<WatcherEngine> _log;
    private readonly WatcherOptions _options;

    private readonly Dictionary<Guid, TargetMonitor> _monitors = new();
    private readonly List<PoolCoordinator> _poolCoordinators = new();
    private readonly ConcurrentDictionary<IPAddress, TargetMonitor> _routingTable = new();
    private readonly List<IDisposable> _subscriptions = new();
    private readonly Subject<TargetTransition> _transitionSubject = new();

    private TargetGraph? _graph;
    private PingManager? _manager;
    private TopologyCoordinator? _topology;
    private CancellationTokenSource? _cts;

    public WatcherEngine(TimeProvider timeProvider, IPingTransceiverFactory pingFactory,
        IDomainNameResolverFactory resolverFactory, ILoggerFactory loggerFactory,
        IOptions<WatcherOptions> options)
    {
        _timeProvider = timeProvider;
        _pingFactory = pingFactory;
        _resolverFactory = resolverFactory;
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _log = loggerFactory.CreateLogger<WatcherEngine>();
    }

    /// <summary>Merged transition stream: every monitor and every pool.</summary>
    public IObservable<TargetTransition> TransitionSource => _transitionSubject;
    public IReadOnlyDictionary<Guid, TargetMonitor> Monitors => _monitors;
    public IReadOnlyList<PoolCoordinator> PoolCoordinators => _poolCoordinators;
    public TargetGraph Graph => _graph ?? throw new InvalidOperationException("Engine not started");

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ct = _cts.Token;

        _graph = TargetGraph.FromOptions(_options);
        _log.LogInformation("WatcherEngine starting: {TargetCount} targets, {PoolCount} pools",
            _graph.Targets.Count, _graph.Pools.Count);

        // Pool membership index for phase staggering: targetId -> (memberIndex, memberCount).
        var poolPosition = new Dictionary<Guid, (int index, int count)>();
        foreach (var pool in _graph.Pools.Values)
            for (int i = 0; i < pool.MemberTargetIds.Count; i++)
                poolPosition[pool.MemberTargetIds[i]] = (i, pool.MemberTargetIds.Count);

        // Monitors and disciplines for enabled targets.
        foreach (var target in _graph.Targets.Values.Where(t => t.Enabled))
        {
            var profile = _graph.ProfileFor(target);
            var initialDelay = poolPosition.TryGetValue(target.Id, out var pos)
                ? PoolCoordinator.PhaseOffset(pos.index, pos.count, profile.NormalInterval)
                : TimeSpan.Zero;
            var discipline = new PingDiscipline(profile, _timeProvider, initialDelay);
            var monitor = new TargetMonitor(target, profile, discipline, _timeProvider, _loggerFactory);
            _monitors[target.Id] = monitor;
            _subscriptions.Add(monitor.TransitionSource.Subscribe(_transitionSubject.OnNext));
        }

        // Topology enforcement.
        _topology = new TopologyCoordinator(_graph, _monitors,
            _loggerFactory.CreateLogger<TopologyCoordinator>());
        _topology.Start();

        // Pool aggregation (members that are disabled have no monitor and are skipped).
        foreach (var pool in _graph.Pools.Values)
        {
            var members = pool.MemberTargetIds
                .Where(_monitors.ContainsKey)
                .Select(id => _monitors[id])
                .ToList();
            if (members.Count == 0)
                continue;
            var coordinator = new PoolCoordinator(pool, members, _timeProvider,
                _loggerFactory.CreateLogger<PoolCoordinator>());
            _subscriptions.Add(coordinator.TransitionSource.Subscribe(_transitionSubject.OnNext));
            coordinator.Start();
            _poolCoordinators.Add(coordinator);
        }

        // Ping plumbing: one manager, results routed by IP.
        _manager = new PingManager(_timeProvider, _pingFactory, ct, _loggerFactory);
        _subscriptions.Add(_manager.PingResultSource.Subscribe(
            result =>
            {
                if (_routingTable.TryGetValue(result.IpAddress, out var monitor))
                    monitor.RecordPingResult(result);
            },
            ex => _log.LogError(ex, "Error in ping result stream"),
            () => _log.LogInformation("Ping result stream completed")));

        // Activate every enabled target (immediately for IP literals, via DNS otherwise).
        foreach (var (targetId, monitor) in _monitors)
        {
            var target = _graph.Targets[targetId];
            if (IPAddress.TryParse(target.Address, out var ip))
                Activate(monitor, ip);
            else
                _ = Task.Run(() => ResolveAndActivateAsync(monitor, target.Address, ct), ct);
        }

        _log.LogInformation("WatcherEngine started with {MonitorCount} active monitors", _monitors.Count);
        return Task.CompletedTask;
    }

    private void Activate(TargetMonitor monitor, IPAddress ip)
    {
        monitor.StartTracking(ip);
        _routingTable[ip] = monitor;
        _manager!.GetOrCreateController(ip, monitor.Discipline,
            (int)monitor.Profile.PingTimeout.TotalMilliseconds);
        _log.LogInformation("Activated {Target} at {Ip}", monitor.Target.Name, ip);
    }

    private async Task ResolveAndActivateAsync(TargetMonitor monitor, string hostname, CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(30);
        var maxBackoff = TimeSpan.FromMinutes(5);
        using var resolver = _resolverFactory.Create(ct);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await resolver.ResolveAsync(hostname, 5000).ConfigureAwait(false);
                var ip = result.IPv4Addresses.FirstOrDefault();
                if (ip is not null)
                {
                    Activate(monitor, ip);
                    return;
                }
                monitor.ReportResolutionFailure($"No IPv4 address for '{hostname}'");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "DNS resolution failed for {Hostname}", hostname);
                monitor.ReportResolutionFailure($"DNS lookup failed for '{hostname}': {ex.Message}");
            }

            try
            {
                await Task.Delay(backoff, _timeProvider, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            backoff = backoff * 2 > maxBackoff ? maxBackoff : backoff * 2;
        }
    }

    public Task StopAsync()
    {
        _log.LogInformation("WatcherEngine stopping...");
        _cts?.Cancel();

        foreach (var s in _subscriptions)
            s.Dispose();
        _subscriptions.Clear();

        _topology?.Dispose();
        foreach (var pool in _poolCoordinators)
            pool.Dispose();
        _manager?.Dispose();
        foreach (var monitor in _monitors.Values)
            monitor.Dispose();

        _log.LogInformation("WatcherEngine stopped.");
        return Task.CompletedTask;
    }
}
```

3b. `ConTinker/Program.cs` — replace the `HostTrackingOptions` registration block with:

```csharp
// Configure WatcherOptions with validation
services.AddOptions<WatcherOptions>()
    .Bind(context.Configuration.GetSection(WatcherOptions.SectionName))
    .ValidateOnStart();
services.AddSingleton<IValidateOptions<WatcherOptions>, WatcherOptionsValidator>();
```

(`using ConEngine.Configuration;` is already imported). Also delete the startup lines that read `IOptions<HostTrackingOptions>` and log HistorySize/MinUsableSample (replace with nothing).

3c. `ConTinker/TinkerService.cs` — replace `StartAsync` body's engine calls:

```csharp
await _watcherEngine.StartAsync(cancellationToken);
```

(delete the `StartWatching(IPAddress.Parse(...))` line and the `using System.Net;` if now unused).

- [ ] **Step 4: Run all tests and build**

Run: `dotnet test Tests/ConEngine.Tests` → ALL PASS.
Run: `dotnet build ConnectionWatcher.sln` → succeeds.

- [ ] **Step 5: Commit**

```powershell
git add Core/ConEngine ConTinker Tests/
git commit -m "feat: rewrite WatcherEngine around targets, topology, and pools"
```

---

### Task 13: ConTinker demo config, transition logging, and cleanup

**Files:**
- Modify: `ConTinker/TinkerService.cs` (transition logging)
- Modify: `ConTinker/appsettings.json` and `ConTinker/appsettings.Development.json` (home demo config)
- Delete: `Core/ConEngine/Configuration/HostTrackingOptions.cs`, `Core/ConEngine/Configuration/HostTrackingOptionsValidator.cs` (dead after Task 12)
- Test: existing suite must stay green; manual simulator checklist below.

**Interfaces:**
- Consumes: `WatcherEngine.TransitionSource` (Task 12).
- Produces: a runnable demo. No new public API.

- [ ] **Step 1: Delete dead options types**

Delete `HostTrackingOptions.cs` and `HostTrackingOptionsValidator.cs`. Search for remaining references and remove them:

```powershell
Get-ChildItem -Recurse -Include *.cs,*.json -Exclude bin,obj | Select-String "HostTracking"
```

Expected leftovers to clean: the `"HostTracking"` section in both ConTinker appsettings files (removed in Step 3).

- [ ] **Step 2: Log transitions in TinkerService**

In `TinkerService.StartAsync`, after `await _watcherEngine.StartAsync(cancellationToken);` add:

```csharp
_watcherEngine.TransitionSource.Subscribe(t =>
    _log.LogWarning("TRANSITION {Name}: {From} -> {To} | {Reason}",
        t.TargetName, t.From, t.To, t.Reason));
```

(add `using ConEngine.Events;` if needed; LogWarning so transitions stand out against Trace/Debug ping noise).

- [ ] **Step 3: Write the demo configuration**

Replace the `HostTracking` section of `ConTinker/appsettings.json` with a `Watcher` section, keep `PingTransceiver` as-is:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft": "Warning",
      "Microsoft.Hosting.Lifetime": "Information",
      "ConEngine": "Debug",
      "ConEngine.Network": "Information"
    }
  },
  "Watcher": {
    "Targets": [
      { "Name": "Router", "Address": "192.168.1.1", "Profile": "Internal" },
      { "Name": "Modem", "Address": "192.168.100.1", "Parent": "Router", "Profile": "Internal" },
      { "Name": "Google", "Address": "8.8.8.8", "Parent": "Modem", "Profile": "External" },
      { "Name": "Cloudflare", "Address": "1.1.1.1", "Parent": "Modem", "Profile": "External" },
      { "Name": "Quad9", "Address": "9.9.9.9", "Parent": "Modem", "Profile": "External" }
    ],
    "Pools": [
      { "Name": "Internet", "Members": [ "Google", "Cloudflare", "Quad9" ], "Parent": "Modem" }
    ]
  },
  "PingTransceiver": {
    "UseSimulation": false,
    "ShowControlWindow": false,
    "SimulationPreset": "Stable"
  }
}
```

In `appsettings.Development.json`, set the simulator on with the control window (merge into whatever is there, preserving its Serilog/logging content):

```json
{
  "PingTransceiver": {
    "UseSimulation": true,
    "ShowControlWindow": true,
    "SimulationPreset": "Stable"
  }
}
```

- [ ] **Step 4: Build, test, and run the demo**

Run: `dotnet build ConnectionWatcher.sln` → succeeds.
Run: `dotnet test Tests/ConEngine.Tests` → ALL PASS.
Run: `dotnet run --project ConTinker` (Development environment so the simulator control window appears).

Manual exit-criteria checklist (spec §9) using the simulator control window:

1. **Router kill:** set the Router IP (192.168.1.1) to fail in the simulator → within ~30 s expect `TRANSITION Router: ... -> Down` then `TRANSITION Modem/Google/Cloudflare/Quad9: ... -> Unreachable | ParentDown` and their ping log lines stop.
2. **Router restore:** re-enable → `Router -> Recovering -> Responding`, descendants `-> Starting | ParentRecovered`, pinging resumes, all reach `Responding`.
3. **External degradation:** set Google's loss rate ~50% → `Google: Responding -> Faltering | ThresholdCrossed; success ..%` and observe burst-cadence pinging in the log; push loss to 100% → `-> Down` with `burst loss` in the reason.
4. **Pool ride-through:** with only Google down, `Internet` pool stays `Responding`; take Cloudflare and Quad9 down too → `TRANSITION Internet: ... -> Down | PoolMembersChanged; Google=Down, ...`.

- [ ] **Step 5: Commit**

```powershell
git add -A
git commit -m "feat: ConTinker demo home config with transition logging"
```

---

## Plan Self-Review (completed)

- **Spec coverage:** Target model (T2), config-in-appsettings with CRUD-ready shape (T7), hierarchy + Unreachable (T4, T10), profiles + burst + cap (T5), pools + stagger + any/all rules (T11), transition reasons incl. burst loss rate (T3, T9), DNS retry/backoff (T12), config validation incl. cycles (T7), fault isolation + shared-channel bug fix (T6), no-test-project gap (T1), demo scenario + exit criteria (T13). Persistence/API/UI/packaging are Phase 2–4 by design.
- **Placeholder scan:** none; every step has code or exact commands.
- **Type consistency:** verified — `PingProfile` (T2) consumed by T4/T5/T7/T9; `AnalysisTransition`/`TargetTransition` (T3) consumed by T4/T9/T10/T11/T12; `IPingCadence` (T5) consumed by T6/T12; `TargetGraph` accessors (T8) consumed by T10/T12; monitor API (T9) consumed by T10/T11/T12.
