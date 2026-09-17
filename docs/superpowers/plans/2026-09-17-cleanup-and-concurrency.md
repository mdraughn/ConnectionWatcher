# Cleanup and Concurrency Hardening Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Remove the race conditions and lifecycle holes found in the 2026-09-16 review, make targets independent of each other in the ping layer (per-target controllers, DNS re-resolution), and fold the duplicated code into single implementations, without rewriting the architecture.

**Architecture:** The engine keeps its shape (PingManager → channel → single result thread → TargetMonitor → coordinators → Rx subjects → channel-buffered consumers). Three changes to that shape: results are routed by target id instead of IP, hostname targets keep a refresh loop that can re-point their controller, and monitors publish transitions after releasing their state lock through a per-monitor FIFO outbox. Everything else is local fixes and de-duplication behind existing tests.

**Tech Stack:** .NET 10, xUnit, FakeTimeProvider (Microsoft.Extensions.Time.Testing), System.Reactive, EF Core SQLite, ASP.NET Core minimal APIs + Razor Pages.

**Spec:** the review report (`codebase-review-2026-09-16.md`, sent in chat; findings are referenced below by their ids E1..E6, P1..P2, H1..H2, S1..S7) plus the decisions recorded in the next section.

## Decisions recorded from review (2026-09-17)

- **Take-over transport check (H1):** current behavior is unintended; add the check.
- **Solution layout (S1):** move `ConInfrastructure`, `ConNotify`, `ConPersistence` under `Libraries/`. `Core/`, `Programs/`, `Tests/` stay.
- **IP collisions (E5):** option 2. Each target gets its own controller and its own results regardless of address. Two targets on one IP are the user's business; the engine does what it is told. The validator keeps one guard: the same *address and probe* twice is a config typo and stays an error.
- **DNS (new):** hostname targets must follow record changes so a retired address never reads as downtime. Rule: keep the current address while it is still in the answer set; re-point when it is not. Placed at the end of the engine phase because it needs per-target controllers and the deadlock-free publish path.

## Global Constraints

- No behavior change without a test that fails first, except pure moves (Task 0.1) and renames.
- Existing public behavior of the REST API stays byte-compatible except where a task says otherwise (Task 3.1 changes how offset-less timestamps are interpreted, documented there).
- Never push to origin. Commit per task on a branch (`cleanup-2026-09`).
- Baseline before Task 0.1: `dotnet test ConnectionWatcher.sln --filter "FullyQualifiedName!~NotifierIntegrationTests"` passes 393/393.
- Test commands run from the repo root. After Task 0.1, paths of the three moved libraries change; test project paths do not.
- All timestamps stay `DateTimeOffset`/UTC end to end (existing rule).

## Ordering and rationale

| Phase | Tasks | Why here |
|---|---|---|
| 0 Layout | 0.1 | Pure mechanical move; do it before any branch is in flight so every later diff lands on final paths. |
| 1 Engine | 1.1–1.7 | The risky, interdependent work. 1.1/1.2 are independent quick wins. 1.3 (async stop) prepares 1.4. 1.4 (per-target controllers) is the base for 1.6 (DNS). 1.5 (publish outside locks) must precede 1.6 because the DNS loop is a second publisher thread. 1.7 tidies while the engine is in context. |
| 2 Lifecycle + persistence | 2.1–2.4 | Independent of the engine internals; needs the engine's new `StopAsync` from 1.3. |
| 3 API edge | 3.1–3.2 | Small, isolated, user-visible; after the risky work so they do not sit unreleased. |
| 4 De-duplication | 4.1–4.9 | Pure refactors under existing tests. Last, because they touch the most files and are cheapest to redo if earlier phases move lines. |

DNS placement: doing it first would mean building it on the IP-keyed routing table and then rewriting it in 1.4; doing it last (after Phase 4) would leave the engine phase's context cold. End of Phase 1 is the sweet spot.

---

## Phase 0: Layout

### Task 0.1: Move the three root libraries under `Libraries/`

**Files:**
- Move: `ConInfrastructure/` → `Libraries/ConInfrastructure/`, `ConNotify/` → `Libraries/ConNotify/`, `ConPersistence/` → `Libraries/ConPersistence/`
- Modify: `ConnectionWatcher.sln` (three project paths), `Core/ConEngine/ConEngine.csproj:17-18`, `Libraries/ConInfrastructure/ConInfrastructure.csproj:10`, `Libraries/ConNotify/ConNotify.csproj:4-5`, `Libraries/ConPersistence/ConPersistence.csproj:4-5`, `Programs/ConWatch/ConWatch.csproj:4-7`, `Tests/ConEngine.Tests/ConEngine.Tests.csproj:23-25`, `Tests/ConNotify.Tests/ConNotify.Tests.csproj:22`, `Tests/ConPersistence.Tests/ConPersistence.Tests.csproj:23`, `README.md:313-318`, `CONTRIBUTING.md` (no path change needed; verify), `Dockerfile` (no change: it copies the whole context and publishes `Programs/ConWatch`; verify the `COPY` lines still resolve)
- Delete: empty `Tinker/` directory (untracked leftover)

**Interfaces:** none; namespaces and assembly names are unchanged.

- [ ] **Step 1: Confirm the ConEngine → ConInfrastructure reference is unused**

Run: `rg "ConInfrastructure" Core/ConEngine --type cs`
Expected: no matches. (Tests reference `ConInfrastructure.DomainNameResolverFactory` themselves via their own csproj.)

- [ ] **Step 2: Move with git so history follows**

```powershell
git mv ConInfrastructure Libraries/ConInfrastructure
git mv ConNotify Libraries/ConNotify
git mv ConPersistence Libraries/ConPersistence
Remove-Item Tinker -Recurse -Force -ErrorAction SilentlyContinue
```

- [ ] **Step 3: Fix project references**

`Core/ConEngine/ConEngine.csproj`: delete line 17 (`..\..\ConInfrastructure\ConInfrastructure.csproj`). Keep `..\ConCore\ConCore.csproj`.

`Libraries/ConInfrastructure/ConInfrastructure.csproj`: `..\..\Core\ConCore\ConCore.csproj`.

`Libraries/ConNotify/ConNotify.csproj`: `..\..\Core\ConEngine\ConEngine.csproj`, `..\ConPersistence\ConPersistence.csproj`.

`Libraries/ConPersistence/ConPersistence.csproj`: `..\..\Core\ConCore\ConCore.csproj`, `..\..\Core\ConEngine\ConEngine.csproj`.

`Programs/ConWatch/ConWatch.csproj`: `..\..\Libraries\ConInfrastructure\ConInfrastructure.csproj`, `..\..\Libraries\ConPersistence\ConPersistence.csproj`, `..\..\Libraries\ConNotify\ConNotify.csproj` (ConEngine path unchanged).

`Tests/ConEngine.Tests/ConEngine.Tests.csproj:25`: `..\..\Libraries\ConInfrastructure\ConInfrastructure.csproj`.
`Tests/ConNotify.Tests/ConNotify.Tests.csproj:22`: `..\..\Libraries\ConNotify\ConNotify.csproj`.
`Tests/ConPersistence.Tests/ConPersistence.Tests.csproj:23`: `..\..\Libraries\ConPersistence\ConPersistence.csproj`.

`ConnectionWatcher.sln`: change the three `Project(...)` lines for ConInfrastructure, ConPersistence, ConNotify to `Libraries\ConInfrastructure\ConInfrastructure.csproj` etc. Optionally add a `Libraries` solution folder and nest the three GUIDs under it in `NestedProjects`.

- [ ] **Step 4: Update the README project table**

`README.md:316-317`: `Libraries/ConInfrastructure`, `Libraries/ConPersistence`; add a row `Libraries/ConNotify | MQTT, webhook and heartbeat notifiers`.

- [ ] **Step 5: Build and run the full suite**

Run: `dotnet build ConnectionWatcher.sln` then `dotnet test ConnectionWatcher.sln --filter "FullyQualifiedName!~NotifierIntegrationTests"`
Expected: build clean (the CS9113 warning on PingController remains until Task 1.7); 393 passed.

- [ ] **Step 6: Build the container to prove the Dockerfile still resolves**

Run: `docker build -t conwatch:layout-check .`
Expected: image builds. If `docker` is unavailable on this machine, note it in the commit message and rely on CI.

- [ ] **Step 7: Commit**

```powershell
git checkout -b cleanup-2026-09
git add -A
git commit -m "chore: move ConInfrastructure, ConNotify, ConPersistence under Libraries/"
```

---

## Phase 1: Engine

### Task 1.1: A burst that starts mid-sleep wakes the controller (E2)

**Files:**
- Modify: `Core/ConEngine/Scheduling/PingDiscipline.cs:28,54-81,120-165`
- Test: `Tests/ConEngine.Tests/PingDisciplineTests.cs`

**Interfaces:**
- Produces: no signature change. `WaitForNextPingAsync` now returns early when a burst starts.

- [ ] **Step 1: Write the failing test**

Add to `PingDisciplineTests` (uses the class's existing `Profile` with NormalInterval 10 s, BurstInterval 1 s, and `FakeTimeProvider`):

```csharp
[Fact]
public async Task TriggerBurst_wakes_a_pending_normal_wait()
{
    var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-08-14T00:00:00Z"));
    var d = new PingDiscipline(Profile, time);
    d.OnStateChanged(HostState.Responding);
    await d.WaitForNextPingAsync(CancellationToken.None); // consumes the initial slot

    var wait = d.WaitForNextPingAsync(CancellationToken.None); // normal 10 s wait
    Assert.False(wait.IsCompleted);

    d.TriggerBurst();
    time.Advance(TimeSpan.FromSeconds(1)); // one burst interval, far short of 10 s
    await wait.WaitAsync(TimeSpan.FromSeconds(2));
}

[Fact]
public async Task Lost_ping_wakes_a_pending_normal_wait()
{
    var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-08-14T00:00:00Z"));
    var d = new PingDiscipline(Profile, time);
    d.OnStateChanged(HostState.Responding);
    await d.WaitForNextPingAsync(CancellationToken.None);

    var wait = d.WaitForNextPingAsync(CancellationToken.None);
    d.OnPingResult(success: false);
    time.Advance(TimeSpan.FromSeconds(1));
    await wait.WaitAsync(TimeSpan.FromSeconds(2));
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test Tests/ConEngine.Tests --filter "FullyQualifiedName~PingDisciplineTests.TriggerBurst_wakes"`
Expected: FAIL with a `TimeoutException` from `WaitAsync` (the wait is still sleeping 10 s of fake time).

- [ ] **Step 3: Implement the wake-up**

In `PingDiscipline`:

```csharp
private TaskCompletionSource _wakeTcs = NewTcs();

/// <summary>Any transition from "not bursting" to "bursting" wakes a sleeping waiter.</summary>
private void WakeLocked()
{
    _wakeTcs.TrySetResult();
    _wakeTcs = NewTcs();
}
```

In `OnStateChanged`, after setting `_burstStartedAt` in the Faltering/Recovering/Down branch: `WakeLocked();`.
In `StartSuspicionBurstLocked`, after `_lastBurstStart = now;`: `WakeLocked();`.

In `WaitForNextPingAsync`, capture the wake task under the lock and race it against the delay:

```csharp
Task? parkTask = null;
Task wake;
TimeSpan delay = TimeSpan.Zero;
lock (_lock)
{
    wake = _wakeTcs.Task;
    if (_state == HostState.Unreachable) parkTask = _releaseTcs.Task;
    else delay = ComputeDelay();
}
if (parkTask is not null) { await parkTask.WaitAsync(ct).ConfigureAwait(false); continue; }
if (delay > TimeSpan.Zero)
{
    var sleep = Task.Delay(delay, _time, ct);
    var first = await Task.WhenAny(sleep, wake).ConfigureAwait(false);
    if (first == wake)
    {
        ct.ThrowIfCancellationRequested();
        continue; // re-evaluate: ComputeDelay now returns the burst interval
    }
    await sleep.ConfigureAwait(false); // propagate cancellation
}
```

Note `ComputeDelay` sets `_nextNormalDue = null` when bursting, so the re-evaluation lands on `BurstInterval` immediately. The abandoned `Task.Delay` is harmless (it completes later against the fake clock or is cancelled with `ct`).

- [ ] **Step 4: Run the discipline tests**

Run: `dotnet test Tests/ConEngine.Tests --filter "FullyQualifiedName~PingDisciplineTests"`
Expected: all pass, including `Normal_cadence_returns_to_the_phase_slot_after_a_burst` (phase-lock unchanged).

- [ ] **Step 5: Commit**

```powershell
git add Core/ConEngine/Scheduling/PingDiscipline.cs Tests/ConEngine.Tests/PingDisciplineTests.cs
git commit -m "fix(engine): a burst that starts mid-sleep wakes the controller immediately"
```

### Task 1.2: ICMP transceiver honors cancellation; fix the name (E3 part 1, H-C4)

**Files:**
- Rename: `Libraries/ConInfrastructure/PingTranceiverIcmp.cs` → `PingTransceiverIcmp.cs` (class `PingTransceiverIcmp`)
- Modify: `Libraries/ConInfrastructure/PingTransceiverFactoryIcmp.cs:12` (constructor call)
- Test: `Tests/ConEngine.Tests/PingTransceiverIcmpTests.cs` (new)

**Interfaces:** `IPingTransceiver` unchanged.

- [ ] **Step 1: Write the failing test**

```csharp
using System.Net;
using ConCore;
using ConInfrastructure;

namespace ConEngine.Tests;

public class PingTransceiverIcmpTests
{
    [Fact]
    public async Task Cancellation_ends_an_in_flight_ping_before_its_timeout()
    {
        // 192.0.2.1 is TEST-NET-1: never routed, never answers.
        using var cts = new CancellationTokenSource();
        using var ping = new PingTransceiverIcmp(IPAddress.Parse("192.0.2.1"), cts.Token);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var task = ping.PingAsync(timeoutMs: 10_000);
        cts.CancelAfter(200);
        var result = await task;
        Assert.False(result.Success);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"took {sw.Elapsed}");
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test Tests/ConEngine.Tests --filter "FullyQualifiedName~PingTransceiverIcmpTests"`
Expected: compile error (class name) first; after renaming only, FAIL on the elapsed assertion (the ping waits the full 10 s).

- [ ] **Step 3: Rename and pass the token**

`git mv Libraries/ConInfrastructure/PingTranceiverIcmp.cs Libraries/ConInfrastructure/PingTransceiverIcmp.cs`; rename the class and finalizer. Replace the send:

```csharp
PingReply reply = await _ping.SendPingAsync(_ipAddress, TimeSpan.FromMilliseconds(timeoutMs),
    buffer: null, options: null, _cancellationToken).ConfigureAwait(false);
```

The existing `catch (OperationCanceledException)` returns a failed `PingResult` with message "Cancelled", which is what the test expects. Delete the finalizer and `Dispose(bool)`; `Dispose()` just disposes `_ping` (no unmanaged resources here).

- [ ] **Step 4: Run tests**

Run: `dotnet test Tests/ConEngine.Tests --filter "FullyQualifiedName~PingTransceiverIcmpTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add -A Libraries/ConInfrastructure Tests/ConEngine.Tests/PingTransceiverIcmpTests.cs
git commit -m "fix(infra): ICMP transceiver honors cancellation; rename PingTranceiverIcmp -> PingTransceiverIcmp"
```

### Task 1.3: Engine stop is asynchronous and bounded (E3 part 2)

**Files:**
- Modify: `Core/ConEngine/Network/PingController.cs` (remove blocking `Wait`, finalizer, `Console.WriteLine`, `NullReferenceException`; add `StopAsync`), `Core/ConEngine/Network/PingManager.cs` (add `StopAsync`, remove finalizer, drop `ReplaySubject` for `Subject`), `Core/ConEngine/WatcherEngine.cs:270-290` (`StopAsync` awaits the manager)
- Test: `Tests/ConEngine.Tests/PingManagerTests.cs` (new)

**Interfaces:**
- Produces: `Task PingController.StopAsync(TimeSpan grace)`, `Task PingManager.StopAsync(TimeSpan grace)`; `WatcherEngine.StopAsync()` now genuinely awaits.

- [ ] **Step 1: Write the failing test**

```csharp
using System.Net;
using ConCore;
using ConEngine.Network;
using ConEngine.Scheduling;
using Microsoft.Extensions.Logging.Abstractions;

namespace ConEngine.Tests;

public class PingManagerTests
{
    /// <summary>A transceiver whose ping never completes unless cancelled.</summary>
    private sealed class HangingFactory : IPingTransceiverFactory
    {
        public IPingTransceiver Create(IPAddress ip, ProbeSpec probe, CancellationToken ct) => new Hanging(ip, ct);
        private sealed class Hanging(IPAddress ip, CancellationToken ct) : IPingTransceiver
        {
            public IPAddress IpAddress => ip;
            public async Task<PingResult> PingAsync(int timeoutMs)
            {
                await Task.Delay(Timeout.Infinite, ct);
                return new PingResult(ip, false, 0, "unreachable");
            }
            public void Dispose() { }
        }
    }

    [Fact]
    public async Task StopAsync_with_ten_hanging_controllers_finishes_within_the_grace_period()
    {
        using var cts = new CancellationTokenSource();
        var manager = new PingManager(TimeProvider.System, new HangingFactory(), cts.Token, NullLoggerFactory.Instance);
        var cadence = new FixedIntervalCadence(TimeSpan.FromMilliseconds(1), TimeProvider.System);
        for (int i = 1; i <= 10; i++)
            manager.GetOrCreateController(IPAddress.Parse($"10.9.0.{i}"), ProbeSpec.Icmp, cadence, () => 60_000);
        await Task.Delay(50); // let every loop enter its ping

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await manager.StopAsync(TimeSpan.FromSeconds(2));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"took {sw.Elapsed}");
    }
}
```

(After Task 1.4 this test's `GetOrCreateController` call gains a `Guid targetId` first argument; update it then.)

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test Tests/ConEngine.Tests --filter "FullyQualifiedName~PingManagerTests"`
Expected: compile error, `StopAsync` does not exist.

- [ ] **Step 3: Implement**

`PingController`:

```csharp
/// <summary>Cancels the loop and waits (bounded) for it to exit. Never throws.</summary>
public async Task StopAsync(TimeSpan grace)
{
    _localCancellationTokenSource.Cancel();
    if (_pingTask is { } t)
    {
        try { await t.WaitAsync(grace).ConfigureAwait(false); }
        catch (TimeoutException) { log.LogWarning("Ping loop for {Ip} did not exit within {Grace}", _ipAddress, grace); }
        catch (OperationCanceledException) { }
    }
}

public void Dispose()
{
    if (_disposed) return;
    _disposed = true;
    _localCancellationTokenSource.Cancel();
    _localCancellationTokenSource.Dispose();
}
```

Delete `Dispose(bool)`, the finalizer, the `Console.WriteLine`, and the `_pingTask.Dispose()` call. In `Launch`, keep the linked CTS in a field (`_linkedCts`) and dispose it in `Dispose`. In `SendMessage`, replace the `NullReferenceException` with `if (_resultWriter is null) return;`.

`PingManager`:

```csharp
private readonly Subject<PingResult> _pingResultSubject = new(); // was ReplaySubject(5); nobody subscribes late

/// <summary>Stops every controller concurrently, closes the channel, and waits for the
/// result pump to drain. Bounded by <paramref name="grace"/> per phase.</summary>
public async Task StopAsync(TimeSpan grace)
{
    List<PingController> controllers;
    lock (_lock)
    {
        if (_disposed) return;
        _disposed = true;
        controllers = _pingControllers.Values.ToList();
        _pingControllers.Clear();
    }
    await Task.WhenAll(controllers.Select(c => c.StopAsync(grace))).ConfigureAwait(false);
    foreach (var c in controllers) c.Dispose();
    _resultWriter.TryComplete();
    try { await _messageProcessingTask.WaitAsync(grace).ConfigureAwait(false); }
    catch (Exception ex) { _log.LogWarning(ex, "Result pump did not drain within {Grace}", grace); }
}

public void Dispose()
{
    // Synchronous best effort for callers that cannot await; StopAsync is the real path.
    _ = StopAsync(TimeSpan.FromSeconds(5));
}
```

Delete `Dispose(bool)`, the finalizer, `StopAllControllers`, `StopController` (Task 1.4 re-adds a target-keyed one), `CloseChannel`, and `ActiveControllerCount`. Keep `MaxEventBuffer` only if something references it (grep; else delete).

`WatcherEngine.StopAsync` becomes `async`:

```csharp
public async Task StopAsync()
{
    _log.LogInformation("WatcherEngine stopping...");
    _cts?.Cancel();
    foreach (var s in _subscriptions) s.Dispose();
    _subscriptions.Clear();
    _topology?.Dispose();
    Verdict?.Dispose();
    Suspicion?.Dispose();
    foreach (var pool in _poolCoordinators) pool.Dispose();
    if (_manager is { } m) await m.StopAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
    foreach (var monitor in _monitors.Values) monitor.Dispose();
    _log.LogInformation("WatcherEngine stopped.");
}
```

- [ ] **Step 4: Run the engine suite**

Run: `dotnet test Tests/ConEngine.Tests`
Expected: all pass (`PingControllerTests.Stopping_one_controller_does_not_complete_shared_channel` still passes: `Stop()` remains as the synchronous cancel).

- [ ] **Step 5: Commit**

```powershell
git add Core/ConEngine Tests/ConEngine.Tests/PingManagerTests.cs
git commit -m "fix(engine): stop controllers concurrently and asynchronously; drop finalizers"
```

### Task 1.4: Per-target controllers, results routed by target id (E5, option 2)

**Files:**
- Create: `Core/ConEngine/Network/RoutedPingResult.cs`
- Modify: `Core/ConEngine/Network/PingController.cs` (`Launch` takes `Guid targetId`, writes `RoutedPingResult`), `Core/ConEngine/Network/PingManager.cs` (dictionary keyed by `Guid`, `GetOrCreateController(Guid targetId, ...)`, `StopControllerAsync(Guid targetId, TimeSpan grace)`, `PingResultSource : IObservable<RoutedPingResult>`), `Core/ConEngine/WatcherEngine.cs:32,177-227` (drop `_routingTable`; route via `_monitors`), `Core/ConEngine/Configuration/WatcherOptionsValidator.cs:59,68-71` (duplicate rule keyed by address+probe)
- Modify tests: `Tests/ConEngine.Tests/PingControllerTests.cs` (channel type + `Launch` signature), `Tests/ConEngine.Tests/PingManagerTests.cs` (target id arg), `Tests/ConEngine.Tests/Fakes/FakePingTransceiverFactory.cs` (record every `Create`), `Tests/ConEngine.Tests/WatcherOptionsValidatorTests.cs`
- Test: `Tests/ConEngine.Tests/WatcherEngineTests.cs` (new cases)

**Interfaces:**
- Produces: `public sealed record RoutedPingResult(Guid TargetId, PingResult Result);`
  `PingController GetOrCreateController(Guid targetId, IPAddress ip, ProbeSpec probe, IPingCadence cadence, Func<int> timeoutMsProvider)`,
  `Task StopControllerAsync(Guid targetId, TimeSpan grace)` (no-op when absent),
  `Task Launch(Guid targetId, IPAddress ip, ChannelWriter<RoutedPingResult> writer, ProbeSpec probe, IPingCadence cadence, Func<int> timeoutMsProvider, CancellationToken ct)`.

- [ ] **Step 1: Write the failing engine tests**

Add to `WatcherEngineTests` a second fixture-free test method (the class fixture uses distinct IPs; build a local engine):

```csharp
[Fact]
public async Task Two_targets_on_one_ip_are_tracked_independently()
{
    var options = new WatcherOptions
    {
        Profiles = { ["Fast"] = new PingProfileConfig { NormalIntervalSeconds = 0.02, MinUsableSample = 5, HistorySize = 20 } },
        Targets =
        [
            new TargetConfig { Name = "RouterIcmp", Address = "192.168.9.1", Profile = "Fast" },
            new TargetConfig { Name = "RouterHttps", Address = "192.168.9.1", Profile = "Fast", Probe = "tcp:443" },
        ],
    };
    var fake = new FakePingTransceiverFactory();
    var engine = new WatcherEngine(TimeProvider.System, fake,
        new ConInfrastructure.DomainNameResolverFactory(), NullLoggerFactory.Instance,
        Microsoft.Extensions.Options.Options.Create(options));
    using var cts = new CancellationTokenSource();
    await engine.StartAsync(cts.Token);

    var icmp = engine.Monitors[engine.Graph.GetByName("RouterIcmp").Id];
    var tcp = engine.Monitors[engine.Graph.GetByName("RouterHttps").Id];
    await Eventually(() => icmp.CurrentState == HostState.Responding && tcp.CurrentState == HostState.Responding);
    await Eventually(() => icmp.PingCount >= 5 && tcp.PingCount >= 5);

    cts.Cancel();
    await engine.StopAsync();

    var created = fake.Created.Where(c => c.Ip.Equals(IPAddress.Parse("192.168.9.1"))).Select(c => c.Probe).ToList();
    Assert.Contains(ProbeSpec.Icmp, created);
    Assert.Contains(new ProbeSpec(ProbeKind.Tcp, 443), created);
}
```

Add to `FakePingTransceiverFactory`: `public ConcurrentBag<(IPAddress Ip, ProbeSpec Probe)> Created { get; } = new();` and `Created.Add((ipAddress, probe));` in `Create`.

Add to `WatcherOptionsValidatorTests`:

```csharp
[Fact]
public void Same_address_with_different_probes_is_allowed()
{
    var options = new WatcherOptions
    {
        Targets =
        [
            new TargetConfig { Name = "A", Address = "10.0.0.1" },
            new TargetConfig { Name = "B", Address = "10.0.0.1", Probe = "tcp:443" },
        ],
    };
    Assert.True(new WatcherOptionsValidator().Validate(null, options).Succeeded);
}

[Fact]
public void Same_address_and_probe_twice_fails()
{
    var options = new WatcherOptions
    {
        Targets =
        [
            new TargetConfig { Name = "A", Address = "10.0.0.1", Probe = "tcp:443" },
            new TargetConfig { Name = "B", Address = "10.0.0.1", Probe = "TCP:443" },
        ],
    };
    var result = new WatcherOptionsValidator().Validate(null, options);
    Assert.True(result.Failed);
    Assert.Contains("Duplicate target address", result.FailureMessage);
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test Tests/ConEngine.Tests --filter "FullyQualifiedName~Two_targets_on_one_ip|FullyQualifiedName~Same_address"`
Expected: validator test `Same_address_with_different_probes_is_allowed` FAILS ("Duplicate target address"); the engine test FAILS (validation is not run by the engine, so it starts, but `RouterIcmp.PingCount` never grows because the routing table was overwritten).

- [ ] **Step 3: Implement**

`RoutedPingResult.cs`:

```csharp
namespace ConEngine.Network;

/// <summary>A ping result tagged with the target it was probed for. Results are
/// routed by this id, never by IP: two targets may share an address.</summary>
public sealed record RoutedPingResult(Guid TargetId, PingResult Result);
```

`PingController.Launch`: add `Guid targetId` as the first parameter, store `_targetId`, and `SendMessage` writes `new RoutedPingResult(_targetId, result)`. Channel writer type becomes `ChannelWriter<RoutedPingResult>`.

`PingManager`: `Dictionary<Guid, PingController> _pingControllers`; channel of `RoutedPingResult`; `Subject<RoutedPingResult>`; logger category `$"ConEngine.Network.PingController[{ip}#{targetId:N}"`;

```csharp
public PingController GetOrCreateController(Guid targetId, IPAddress ip, ProbeSpec probe,
    IPingCadence cadence, Func<int> timeoutMsProvider)
{
    lock (_lock)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_pingControllers.TryGetValue(targetId, out var existing)) return existing;
        var controller = new PingController(_pingTransceiverFactory, _loggerFactory.CreateLogger(...));
        controller.Launch(targetId, ip, _resultWriter, probe, cadence, timeoutMsProvider, _globalCancellationToken);
        _pingControllers[targetId] = controller;
        return controller;
    }
}

public async Task StopControllerAsync(Guid targetId, TimeSpan grace)
{
    PingController? controller;
    lock (_lock) { _pingControllers.Remove(targetId, out controller); }
    if (controller is null) return;
    await controller.StopAsync(grace).ConfigureAwait(false);
    controller.Dispose();
}
```

`WatcherEngine`: delete `_routingTable`. The subscription becomes:

```csharp
_subscriptions.Add(_manager.PingResultSource.Subscribe(routed =>
{
    if (!_monitors.TryGetValue(routed.TargetId, out var monitor)) return;
    var result = routed.Result;
    monitor.Discipline.OnPingResult(result.Success);
    monitor.RecordPingResult(result);
    _pingSampleSubject.OnNext((monitor.Target.Id, result));
    Suspicion?.OnPingSample(monitor.Target.Id, result);
}, ...));
```

`Activate` calls `_manager!.GetOrCreateController(monitor.Target.Id, ip, probe, monitor.Discipline, timeoutProvider)`.

`WatcherOptionsValidator`: replace the `addresses` set with `HashSet<string> addressProbes` keyed by `$"{t.Address}|{ProbeSpec.TryParse(t.Probe, out var p) ? p.ToString() : t.Probe}"` (case-insensitive) and the failure text `$"Duplicate target address '{t.Address}' with probe '{probe}'."`. Keep the message prefix "Duplicate target address" so the existing test string matches.

Update `PingControllerTests` (channel of `RoutedPingResult`, `Launch(Guid.NewGuid(), IpA, ...)`, assertions read `r.Result.Success`) and `PingManagerTests` (`GetOrCreateController(Guid.NewGuid(), ip, ...)`).

- [ ] **Step 4: Run the engine suite**

Run: `dotnet test Tests/ConEngine.Tests`
Expected: all pass, including `ProbeThreadingTests` (it keys `ObservedProbes` by IP with distinct IPs, unaffected).

- [ ] **Step 5: Run the web suite (validator change is user-visible through config endpoints)**

Run: `dotnet test Tests/ConWatch.Tests --filter "FullyQualifiedName!~NotifierIntegrationTests"`
Expected: pass. If a `ConfigApiTests` case asserted that a duplicate address is rejected, update it to use the same probe twice.

- [ ] **Step 6: Commit**

```powershell
git add -A Core Tests
git commit -m "feat(engine): one controller per target; results routed by target id, not IP"
```

### Task 1.5: Publish transitions outside the state lock (E1)

**Files:**
- Modify: `Core/ConEngine/Tracking/HostAnalysisEngine.cs` (methods return an `AnalysisOutcome`; subjects removed), `Core/ConEngine/Tracking/HostTracker.cs` (returns outcomes; publishes to its own subjects after releasing its lock; keeps `HostStateChangeSource`/`HostStatusSource` for tests), `Core/ConEngine/Tracking/TargetMonitor.cs` (outbox; no tracker subscriptions)
- Create: `Core/ConEngine/Tracking/AnalysisOutcome.cs`
- Test: `Tests/ConEngine.Tests/TargetMonitorTests.cs` (new cases)

**Interfaces:**
- Produces: `public sealed record AnalysisOutcome(HostStatus Status, AnalysisTransition? Transition);`
  `AnalysisOutcome HostTracker.RecordPingResult(PingResult)`, `AnalysisOutcome? HostTracker.MarkUnreachable(Guid)`, `AnalysisOutcome? HostTracker.Release()`, `AnalysisOutcome? HostTracker.Seed(HostState)` (null when the command was a no-op).
- Invariant after this task: **no `_transitionSubject.OnNext` or `_statusSubject.OnNext` executes while `TargetMonitor._lock` or `HostTracker._lock` is held.**

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public void Transition_is_published_after_the_monitor_lock_is_released()
{
    var (monitor, _, time) = Make();
    var lockWasFreeDuringPublish = false;
    monitor.TransitionSource.Subscribe(_ =>
    {
        // Another thread must be able to read state while we are inside a subscriber.
        var t = Task.Run(() => monitor.CurrentState);
        lockWasFreeDuringPublish = t.Wait(TimeSpan.FromSeconds(1));
    });
    monitor.StartTracking(Ip);
    Feed(monitor, time, 5, success: true); // Starting -> Responding
    Assert.True(lockWasFreeDuringPublish);
}

[Fact]
public void Transitions_from_two_threads_arrive_in_state_order()
{
    var (monitor, transitions, time) = Make();
    monitor.StartTracking(Ip);
    Feed(monitor, time, 5, success: true);          // Responding
    var ancestor = Guid.NewGuid();
    Parallel.Invoke(
        () => monitor.SetUnreachable(ancestor),      // Responding -> Unreachable
        () => Feed(monitor, time, 1, success: false)); // ignored while Unreachable
    monitor.Release();                                // Unreachable -> Starting
    var states = transitions.Select(t => (t.From, t.To)).ToList();
    Assert.Equal((HostState.Starting, HostState.Responding), states[0]);
    Assert.Equal((HostState.Responding, HostState.Unreachable), states[1]);
    Assert.Equal((HostState.Unreachable, HostState.Starting), states[2]);
}
```

- [ ] **Step 2: Run to verify the first fails**

Run: `dotnet test Tests/ConEngine.Tests --filter "FullyQualifiedName~Transition_is_published_after"`
Expected: FAIL (`lockWasFreeDuringPublish` false: the `Task.Run` blocks on the monitor lock and times out).

- [ ] **Step 3: Implement**

`AnalysisOutcome.cs`:

```csharp
namespace ConEngine.Tracking;

/// <summary>What one analysis step produced: always a status, sometimes a transition.
/// Returned rather than pushed so the caller can publish after releasing its locks.</summary>
public sealed record AnalysisOutcome(HostStatus Status, AnalysisTransition? Transition);
```

`HostAnalysisEngine`: remove both `ReplaySubject`s. `Seed` returns `AnalysisOutcome?` (`new(BuildStatus(TrackingStarted), null)` or null when ignored). `MarkUnreachable`/`Release` return `AnalysisOutcome?`. `Recalculate` returns `AnalysisOutcome`. `Transition(...)` builds and returns `new AnalysisOutcome(status, analysisTransition)`.

`HostTracker`: keep two plain `Subject<T>`s for `HostStatusSource`/`HostStateChangeSource` (tests subscribe to them). Each public method does `AnalysisOutcome? outcome; lock (_lock) { outcome = _analysisEngine.X(...); } Publish(outcome); return outcome;` where

```csharp
private void Publish(AnalysisOutcome? o)
{
    if (o is null) return;
    _statusSubject.OnNext(o.Status);
    if (o.Transition is { } t) _stateChangeSubject.OnNext(t);
}
```

`TargetMonitor`: remove `_transitionSubscription`/`_statusSubscription`. Add:

```csharp
private readonly ConcurrentQueue<Action> _outbox = new();
private readonly Lock _publishLock = new();

/// <summary>Enqueue under _lock (so order == state order); drain after releasing it.
/// The publish lock keeps delivery serialized and FIFO across threads.</summary>
private void DrainOutbox()
{
    lock (_publishLock)
    {
        while (_outbox.TryDequeue(out var publish)) publish();
    }
}
```

Every method that previously emitted inline now: under `_lock`, compute (calling the tracker, which returns an outcome), build the `TargetTransition`/`TargetStatus`, and `_outbox.Enqueue(() => { LastStatus = ts; _statusSubject.OnNext(ts); })` / `_outbox.Enqueue(() => _transitionSubject.OnNext(transition))`; after the lock, `DrainOutbox()`. The burst-loss computation in `OnAnalysisTransition` (reads `_tracker.EntriesSince`) and `Discipline.OnStateChanged` stay under `_lock` before enqueueing; the log line moves into the enqueued action. `StartTracking` applies `Seed` and any pending Unreachable through the same path. Keep the `ReplaySubject(1)` on `_transitionSubject`/`_statusSubject` for now (Task 1.7 revisits).

- [ ] **Step 4: Run the engine suite**

Run: `dotnet test Tests/ConEngine.Tests`
Expected: all pass. `HostAnalysisEngineTests` and `WarmStartTests` still subscribe to `HostStateChangeSource`; `TargetMonitorTests` unchanged otherwise.

- [ ] **Step 5: Commit**

```powershell
git add Core/ConEngine/Tracking Tests/ConEngine.Tests/TargetMonitorTests.cs
git commit -m "fix(engine): publish transitions after releasing the monitor lock (deadlock with DNS thread)"
```

### Task 1.6: Hostname targets follow DNS changes

**Files:**
- Create: `Tests/ConEngine.Tests/Fakes/FakeDomainNameResolverFactory.cs`
- Modify: `Core/ConEngine/WatcherEngine.cs:44-55,193-268` (refresh loop, `RepointAsync`, optional `dnsRefreshInterval` ctor arg), `Core/ConEngine/Tracking/TargetMonitor.cs` (`Repoint(IPAddress)`), `Core/ConEngine/Tracking/HostTracker.cs` (`UpdateAddress`), `Core/ConEngine/Tracking/HostAnalysisEngine.cs` (`_hostAddress` becomes settable via `UpdateAddress`)
- Test: `Tests/ConEngine.Tests/DnsRefreshTests.cs` (new)

**Interfaces:**
- Consumes: `PingManager.StopControllerAsync`, `GetOrCreateController(Guid, ...)` from 1.4; outbox publish from 1.5.
- Produces: `WatcherEngine(..., IOptions<VerdictOptions>? verdictOptions = null, TimeSpan? dnsRefreshInterval = null)` (default 5 minutes); `void TargetMonitor.Repoint(IPAddress)`.

Rules (from the decision above): a hostname is re-resolved every `dnsRefreshInterval` and immediately when the target transitions to Down. If resolution fails, keep the current address. If the current address is still in the IPv4 answer set, keep it. Otherwise stop the controller, re-point the monitor (state and history are kept: same host from the user's point of view), start a controller on the first address, and burst to re-prove quickly.

- [ ] **Step 1: Write the fake resolver**

```csharp
using System.Collections.Concurrent;
using System.Net;
using ConCore;

namespace ConEngine.Tests.Fakes;

public sealed class FakeDomainNameResolverFactory : IDomainNameResolverFactory
{
    public ConcurrentDictionary<string, IPAddress[]> Answers { get; } = new(StringComparer.OrdinalIgnoreCase);
    public ConcurrentDictionary<string, int> ResolveCount { get; } = new(StringComparer.OrdinalIgnoreCase);

    public IDomainNameResolver Create(CancellationToken ct) => new Resolver(this);

    private sealed class Resolver(FakeDomainNameResolverFactory owner) : IDomainNameResolver
    {
        public Task<DomainResolutionResult> ResolveAsync(string domainName, int timeoutMs)
        {
            owner.ResolveCount.AddOrUpdate(domainName, 1, (_, n) => n + 1);
            if (!owner.Answers.TryGetValue(domainName, out var ips))
                throw new System.Net.Sockets.SocketException(11001); // host not found
            return Task.FromResult(new DomainResolutionResult(domainName, success: true, ips));
        }
        public void Dispose() { }
    }
}
```

(`DomainResolutionResult(string domainName, bool success, IReadOnlyList<IPAddress> ipAddresses, string? status = null, long resolutionTimeMs = 0)` is the real constructor; `IPv4Addresses` is a filtered `IEnumerable<IPAddress>`, so the engine code below calls `.ToList()` on it.)

- [ ] **Step 2: Write the failing test**

```csharp
using System.Net;
using ConEngine.Configuration;
using ConEngine.Tests.Fakes;
using ConEngine.Tracking;
using Microsoft.Extensions.Logging.Abstractions;

namespace ConEngine.Tests;

public class DnsRefreshTests
{
    private static readonly IPAddress OldIp = IPAddress.Parse("203.0.113.10");
    private static readonly IPAddress NewIp = IPAddress.Parse("203.0.113.20");

    private static async Task Eventually(Func<bool> condition, int timeoutMs = 10000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline && !condition()) await Task.Delay(20);
        Assert.True(condition());
    }

    [Fact]
    public async Task Retired_address_is_replaced_and_the_target_keeps_responding()
    {
        var options = new WatcherOptions
        {
            Profiles = { ["Fast"] = new PingProfileConfig { NormalIntervalSeconds = 0.02, MinUsableSample = 5, HistorySize = 20 } },
            Targets = [new TargetConfig { Name = "Site", Address = "site.example", Profile = "Fast" }],
        };
        var pings = new FakePingTransceiverFactory();
        var dns = new FakeDomainNameResolverFactory();
        dns.Answers["site.example"] = [OldIp];
        var engine = new WatcherEngine(TimeProvider.System, pings, dns, NullLoggerFactory.Instance,
            Microsoft.Extensions.Options.Options.Create(options),
            dnsRefreshInterval: TimeSpan.FromMilliseconds(100));
        using var cts = new CancellationTokenSource();
        await engine.StartAsync(cts.Token);
        var monitor = engine.Monitors[engine.Graph.GetByName("Site").Id];
        await Eventually(() => monitor.CurrentState == HostState.Responding);
        Assert.Equal(OldIp, monitor.ResolvedAddress);

        dns.Answers["site.example"] = [NewIp]; // the old address left the record set
        pings.SetUp(OldIp, up: false);          // and stopped answering

        await Eventually(() => Equals(monitor.ResolvedAddress, NewIp));
        await Eventually(() => pings.ObservedTimeouts.ContainsKey(NewIp));
        await Eventually(() => monitor.CurrentState == HostState.Responding);

        cts.Cancel();
        await engine.StopAsync();
    }

    [Fact]
    public async Task Address_still_in_the_answer_set_is_kept()
    {
        var options = new WatcherOptions
        {
            Profiles = { ["Fast"] = new PingProfileConfig { NormalIntervalSeconds = 0.02, MinUsableSample = 5, HistorySize = 20 } },
            Targets = [new TargetConfig { Name = "Site", Address = "site.example", Profile = "Fast" }],
        };
        var pings = new FakePingTransceiverFactory();
        var dns = new FakeDomainNameResolverFactory();
        dns.Answers["site.example"] = [OldIp, NewIp];
        var engine = new WatcherEngine(TimeProvider.System, pings, dns, NullLoggerFactory.Instance,
            Microsoft.Extensions.Options.Options.Create(options),
            dnsRefreshInterval: TimeSpan.FromMilliseconds(50));
        using var cts = new CancellationTokenSource();
        await engine.StartAsync(cts.Token);
        var monitor = engine.Monitors[engine.Graph.GetByName("Site").Id];
        await Eventually(() => monitor.CurrentState == HostState.Responding);

        dns.Answers["site.example"] = [NewIp, OldIp]; // rotated order, same set
        await Eventually(() => dns.ResolveCount["site.example"] >= 4);
        Assert.Equal(OldIp, monitor.ResolvedAddress);
        Assert.False(pings.ObservedTimeouts.ContainsKey(NewIp));

        cts.Cancel();
        await engine.StopAsync();
    }
}
```

- [ ] **Step 3: Run to verify they fail**

Run: `dotnet test Tests/ConEngine.Tests --filter "FullyQualifiedName~DnsRefreshTests"`
Expected: compile error on the `dnsRefreshInterval` argument; after adding the parameter only, `Retired_address...` FAILS on `ResolvedAddress == NewIp`.

- [ ] **Step 4: Implement**

`TargetMonitor.Repoint`:

```csharp
/// <summary>DNS moved the target: keep state and history, change the address.</summary>
public void Repoint(IPAddress newAddress)
{
    lock (_lock)
    {
        ResolvedAddress = newAddress;
        _tracker?.UpdateAddress(newAddress);
    }
}
```

`HostTracker.UpdateAddress(IPAddress)` sets `HostAddress` (make the setter private) and calls `_analysisEngine.UpdateAddress(...)` under its lock; `HostAnalysisEngine._hostAddress` becomes a mutable field used by `BuildStatus`.

`WatcherEngine`: store `_dnsRefreshInterval = dnsRefreshInterval ?? TimeSpan.FromMinutes(5)`. Add `private readonly ConcurrentDictionary<Guid, TaskCompletionSource> _dnsRefreshRequests = new();`. In `StartAsync`, subscribe once to `_transitionSubject`: when `t.Kind == Target && t.To == HostState.Down && _dnsRefreshRequests.TryGetValue(t.TargetId, out var tcs)` → `tcs.TrySetResult()`.

Rewrite `ResolveAndActivateAsync` so it never returns while the engine runs:

```csharp
private async Task ResolveAndActivateAsync(TargetMonitor monitor, string hostname, CancellationToken ct)
{
    var backoff = TimeSpan.FromSeconds(30);
    var maxBackoff = TimeSpan.FromMinutes(5);
    using var resolver = _resolverFactory.Create(ct);
    IPAddress? current = null;

    while (!ct.IsCancellationRequested)
    {
        IReadOnlyList<IPAddress> answers = [];
        try
        {
            answers = (await resolver.ResolveAsync(hostname, 5000).ConfigureAwait(false)).IPv4Addresses.ToList();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        catch (Exception ex)
        {
            if (current is null)
            {
                _log.LogWarning(ex, "DNS resolution failed for {Hostname}", hostname);
                monitor.ReportResolutionFailure($"DNS lookup failed for '{hostname}': {ex.Message}");
            }
            else _log.LogDebug(ex, "DNS refresh failed for {Hostname}; keeping {Ip}", hostname, current);
        }

        if (current is null)
        {
            if (answers.Count > 0) { current = answers[0]; Activate(monitor, current); }
            else if (answers.Count == 0 && !ct.IsCancellationRequested)
            {
                monitor.ReportResolutionFailure($"No IPv4 address for '{hostname}'");
                try { await Task.Delay(backoff, _timeProvider, ct).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
                backoff = backoff * 2 > maxBackoff ? maxBackoff : backoff * 2;
                continue;
            }
        }
        else if (answers.Count > 0 && !answers.Contains(current))
        {
            _log.LogInformation("{Target}: {Old} left the DNS answer set; re-pointing to {New}", monitor.Target.Name, current, answers[0]);
            await RepointAsync(monitor, answers[0], ct).ConfigureAwait(false);
            current = answers[0];
        }

        // Sleep until the refresh interval or a Down transition, whichever first.
        var request = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _dnsRefreshRequests[monitor.Target.Id] = request;
        try
        {
            await Task.WhenAny(Task.Delay(_dnsRefreshInterval, _timeProvider, ct), request.Task).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return; }
        if (ct.IsCancellationRequested) return;
    }
}

private async Task RepointAsync(TargetMonitor monitor, IPAddress ip, CancellationToken ct)
{
    await _manager!.StopControllerAsync(monitor.Target.Id, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
    monitor.Repoint(ip);
    StartController(monitor, ip);   // the controller-creation half of Activate, extracted
    monitor.Discipline.TriggerBurst();
}
```

Split the current `Activate` into `Activate` (calls `monitor.StartTracking(ip)` then `StartController`, then the warm-start burst) and `StartController(monitor, ip)` (probe parse, timeout provider, `GetOrCreateController`).

- [ ] **Step 5: Run the engine suite**

Run: `dotnet test Tests/ConEngine.Tests`
Expected: all pass. `WatcherEngineTests`/`WarmStartTests` use literal IPs and never enter the loop.

- [ ] **Step 6: Update the README's "Configuration" paragraph**

Add one sentence under the Web UI/Targets bullet: "Hostname targets are re-resolved every five minutes and immediately when they go Down; an address that leaves the DNS answer set is replaced without resetting the target's history."

- [ ] **Step 7: Commit**

```powershell
git add -A Core Tests README.md
git commit -m "feat(engine): hostname targets follow DNS changes (re-point on retired address)"
```

### Task 1.7: Engine tidy-up (E6 and dead code)

**Files:**
- Modify: `Core/ConEngine/Tracking/TargetMonitor.cs` (subjects → `Subject<T>`; `StartTracking` seeds before it would have subscribed, now irrelevant: verify `LastStatus` is set from the Seed outcome), `Core/ConEngine/Topology/PoolCoordinator.cs:100`, `VerdictCoordinator.cs:39` (`ReplaySubject(1)` → `Subject`), `Core/ConEngine/Tracking/HostTracker.cs` (delete `GetHistory`, `GetHistoryReversed`, `ClearHistory`, `LatestTimestamp`, `LatestResult`, `HistoryCapacity`, `IsUsableSample`; fix `ToString`), `Core/ConEngine/Tracking/CircularBuffer.cs` (delete `GetOldest`, `IsFull`, `IsEmpty`, `InOrder`, `ToArrayReversed`, and the internal lock: HostTracker owns synchronization, documented in the class summary; file-scoped namespace), `Core/ConEngine/Tracking/PingHistoryEntry.cs` (file-scoped namespace), `Core/ConEngine/Topology/TargetGraph.cs` (keep `GetChildren`/`GetPoolByName`, tests use them), `Core/ConEngine/Network/PingController.cs` (drop the unread `timeProvider` primary-constructor parameter and its call sites), `Core/ConEngine/WatcherEngine.cs:146-171` (resolve the anchor pool once), `Core/ConCore/PingResult.cs:116-119` (add optional `TimeProvider? timeProvider` parameter? No: keep ctor; instead the transceivers pass `timestamp: timeProvider.GetUtcNow()` where a provider is available. Only the fake and the two real transceivers construct results; leave the default for the real ones, which run on the system clock anyway. Document in the XML summary.)

- [ ] **Step 1: Confirm each deletion is unreferenced**

Run: `rg "GetHistoryReversed|GetHistory\b|ClearHistory|LatestTimestamp|LatestResult|HistoryCapacity|IsUsableSample|GetOldest|IsFull|InOrder\(|ToArrayReversed" --type cs Core Programs Libraries Tests`
Expected: hits only inside the defining files (and `EngineSeed.IsEmpty`, which is a different member).

- [ ] **Step 2: Make the edits; add one guard test for the replay change**

```csharp
[Fact]
public void Seeded_monitor_exposes_last_status_before_any_ping()
{
    var (monitor, _, _) = Make();
    monitor.Seed(HostState.Responding);
    monitor.StartTracking(Ip);
    Assert.NotNull(monitor.LastStatus);
    Assert.True(monitor.LastStatus!.Host.Assumed);
}
```

- [ ] **Step 3: Run the full engine + web suites**

Run: `dotnet test ConnectionWatcher.sln --filter "FullyQualifiedName!~NotifierIntegrationTests"`
Expected: pass; build has zero warnings from `Core/`.

- [ ] **Step 4: Commit**

```powershell
git add -A Core Tests
git commit -m "refactor(engine): plain subjects, drop dead tracker/buffer members and double locking"
```

---

## Phase 2: Host lifecycle and persistence

### Task 2.1: Migrate and enable WAL at startup; order hosted services (P2, E4 part 1)

**Files:**
- Create: `Libraries/ConPersistence/DatabaseStartup.cs`
- Modify: `Programs/ConWatch/Program.cs` (call `DatabaseStartup.PrepareAsync` before `app.Run()`; move `AddHostedService` for `WatcherHostService` after `StatusBroadcaster`), `Programs/ConWatch/WatcherHostService.cs:51-54` (remove the migrate block)
- Test: `Tests/ConPersistence.Tests/DatabaseStartupTests.cs` (new)

**Interfaces:**
- Produces: `public static Task DatabaseStartup.PrepareAsync(IDbContextFactory<ConWatchDbContext> factory, CancellationToken ct)`: runs migrations, then `PRAGMA journal_mode=WAL` for file-backed databases (skips `:memory:`).

- [ ] **Step 1: Write the failing test**

```csharp
using ConPersistence;
using Microsoft.EntityFrameworkCore;

namespace ConPersistence.Tests;

public class DatabaseStartupTests
{
    [Fact]
    public async Task Prepare_migrates_and_switches_a_file_database_to_wal()
    {
        var dir = Directory.CreateTempSubdirectory("conwatch-wal");
        var path = Path.Combine(dir.FullName, "t.db");
        var options = new DbContextOptionsBuilder<ConWatchDbContext>().UseSqlite($"Data Source={path}").Options;
        var factory = new FileDbFactory(options); // TestDbFactory is :memory:-only; add this 4-line IDbContextFactory<ConWatchDbContext> over the options

        await DatabaseStartup.PrepareAsync(factory, CancellationToken.None);

        await using var db = await factory.CreateDbContextAsync();
        var mode = await db.Database.SqlQueryRaw<string>("PRAGMA journal_mode").SingleAsync();
        Assert.Equal("wal", mode);
        Assert.True((await db.Database.GetAppliedMigrationsAsync()).Any());
    }
}
```

`TestDbFactory` opens `Data Source=:memory:` (TestDbFactory.cs:17), so WAL cannot be observed through it; the file-backed `FileDbFactory` is required here. Note `TestDbFactory` uses `EnsureCreated`, not migrations; `PrepareAsync` uses `MigrateAsync`, which is what production runs.

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test Tests/ConPersistence.Tests --filter "FullyQualifiedName~DatabaseStartupTests"`
Expected: compile error, `DatabaseStartup` missing.

- [ ] **Step 3: Implement**

```csharp
namespace ConPersistence;

/// <summary>One-time database preparation, run before any hosted service starts.</summary>
public static class DatabaseStartup
{
    public static async Task PrepareAsync(IDbContextFactory<ConWatchDbContext> factory, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.Database.MigrateAsync(ct);
        var source = db.Database.GetDbConnection().DataSource;
        if (string.IsNullOrEmpty(source) || source.Contains(":memory:", StringComparison.OrdinalIgnoreCase))
            return;
        // WAL: readers never block behind the recorders' or retention's write transactions.
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", ct);
    }
}
```

`Program.cs`: after `var app = builder.Build();` add

```csharp
await ConPersistence.DatabaseStartup.PrepareAsync(
    app.Services.GetRequiredService<IDbContextFactory<ConWatchDbContext>>(), CancellationToken.None);
```

(top-level statements already allow `await`; confirm `Program.cs` does not have a synchronous `Main`). Remove the migrate block from `WatcherHostService.StartAsync`. Move line 59 (`AddHostedService(sp => sp.GetRequiredService<WatcherHostService>())`) to just after line 116 (StatusBroadcaster) with the comment: "Registered last: starts after every consumer is listening, stops first so nothing emits into a closed recorder."

Note: Microsoft.Data.Sqlite already applies a 30 s busy timeout per command (`CommandTimeout`), so no connection-string change is needed.

- [ ] **Step 4: Run persistence + web suites**

Run: `dotnet test Tests/ConPersistence.Tests` then `dotnet test Tests/ConWatch.Tests --filter "FullyQualifiedName!~NotifierIntegrationTests"`
Expected: pass. `WarmRestartTests` (which reopen a DB with a second factory) must still pass with WAL files beside the DB.

- [ ] **Step 5: Commit**

```powershell
git add -A Libraries/ConPersistence Programs/ConWatch Tests/ConPersistence.Tests
git commit -m "fix(host): migrate + WAL before hosted services; engine service starts last, stops first"
```

### Task 2.2: WatcherHostService uses the real stopping token and refuses to rebuild after stop (E4 part 2)

**Files:**
- Modify: `Programs/ConWatch/WatcherHostService.cs:17-49,85-105,285-333`
- Test: `Tests/ConWatch.Tests/HostLifecycleTests.cs` (new)

**Interfaces:**
- Consumes: `IHostApplicationLifetime` (new constructor parameter, DI-provided).
- Produces: no public change. Internal: `_stopped` flag; `_debounceLock`.

- [ ] **Step 1: Write the failing test**

```csharp
using System.Net.Http.Json;
using ConWatch;
using Microsoft.Extensions.DependencyInjection;

namespace ConWatch.Tests;

public class HostLifecycleTests
{
    [Fact]
    public async Task Config_change_racing_shutdown_does_not_start_a_new_engine()
    {
        using var factory = new ConWatchFactory();
        var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/v1/setup/apply",
            new SetupApplyRequest(new SetupTarget("Router", "192.168.1.1"), null, ["8.8.8.8", "1.1.1.1"]));
        var host = factory.Services.GetRequiredService<WatcherHostService>();
        await Task.Delay(200); // debounce is 0 s in tests; let the first rebuild land
        Assert.NotNull(host.Engine);

        // Debounce is 0 s in tests: the rebuild is queued the moment the config changes.
        var change = client.PostAsJsonAsync("/api/v1/config/targets", new { name = "X", address = "10.7.7.7" });
        await host.StopAsync(CancellationToken.None);
        await change;
        await Task.Delay(300); // give a rogue rebuild time to run

        Assert.Null(host.Engine);
    }
}
```

`SetupApplyRequest`/`SetupTarget` are the same records `SetupApiTests` posts (see `SetupApiTests.cs:79-80`).

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test Tests/ConWatch.Tests --filter "FullyQualifiedName~HostLifecycleTests"`
Expected: FAIL, `host.Engine` is not null (the queued rebuild started a new engine).

- [ ] **Step 3: Implement**

Constructor gains `IHostApplicationLifetime lifetime`. Replace `_appStopping = cancellationToken;` with `_appStopping = lifetime.ApplicationStopping;`. Add `private bool _stopped;` and `private readonly Lock _debounceLock = new();`.

`StopAsync`: inside the `_rebuildLock` block, set `_stopped = true;` before stopping the engine.

`RebuildAsync`: after acquiring `_rebuildLock`, `if (_stopped) return;`.

`OnConfigChanged`:

```csharp
private void OnConfigChanged()
{
    CancellationTokenSource cts;
    lock (_debounceLock)
    {
        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
        cts = CancellationTokenSource.CreateLinkedTokenSource(_appStopping);
        _debounceCts = cts;
    }
    var delay = TimeSpan.FromSeconds(persistenceOptions.Value.RebuildDebounceSeconds);
    _ = Task.Run(async () =>
    {
        try
        {
            if (delay > TimeSpan.Zero) await Task.Delay(delay, timeProvider, cts.Token);
            await RebuildAsync();
        }
        catch (OperationCanceledException) { }
    }, CancellationToken.None);
}
```

- [ ] **Step 4: Run the web suite**

Run: `dotnet test Tests/ConWatch.Tests --filter "FullyQualifiedName!~NotifierIntegrationTests"`
Expected: pass.

- [ ] **Step 5: Commit**

```powershell
git add Programs/ConWatch/WatcherHostService.cs Tests/ConWatch.Tests/HostLifecycleTests.cs
git commit -m "fix(host): use ApplicationStopping; no engine rebuild after stop; guard the debounce swap"
```

### Task 2.3: `ChannelRecorder<T>` base with a retried pending batch (P1, S2)

**Files:**
- Create: `Libraries/ConPersistence/Services/ChannelRecorder.cs`
- Modify: `Libraries/ConPersistence/Services/TransitionRecorder.cs`, `Libraries/ConPersistence/Services/GlitchRecorder.cs` (derive; keep only `WriteBatchAsync` and row mapping)
- Test: `Tests/ConPersistence.Tests/RecorderCollectorTests.cs` (new case), `Tests/ConPersistence.Tests/Fakes/FailingOnceDbFactory.cs` (new)

**Interfaces:**
- Produces:

```csharp
public abstract class ChannelRecorder<TEvent>(ILogger log, int capacity = 1000, int batchSize = 500) : BackgroundService
{
    public void Attach(IObservable<TEvent> source);          // replaces prior subscription
    public Task FlushAsync(CancellationToken ct = default);   // drains; retries the pending batch first
    protected abstract Task WriteBatchAsync(IReadOnlyList<TEvent> batch, CancellationToken ct);
    protected abstract string EventNoun { get; }              // "transitions" / "glitches" for log lines
}
```

- [ ] **Step 1: Write the failing test**

```csharp
public sealed class FailingOnceDbFactory(IDbContextFactory<ConWatchDbContext> inner) : IDbContextFactory<ConWatchDbContext>
{
    private int _calls;
    public ConWatchDbContext CreateDbContext() => CreateDbContextAsync().GetAwaiter().GetResult();
    public Task<ConWatchDbContext> CreateDbContextAsync(CancellationToken ct = default)
    {
        if (Interlocked.Increment(ref _calls) == 1) throw new InvalidOperationException("simulated SQLITE_BUSY");
        return inner.CreateDbContextAsync(ct);
    }
}

[Fact]
public async Task A_failed_write_is_retried_and_lands_exactly_once()
{
    var recorder = new TransitionRecorder(new FailingOnceDbFactory(_factory), NullLogger<TransitionRecorder>.Instance);
    using var source = new Subject<TargetTransition>();
    recorder.Attach(source);
    source.OnNext(Transition(TransitionSubjectKind.Target, "Google", HostState.Down));

    await Assert.ThrowsAsync<InvalidOperationException>(() => recorder.FlushAsync());
    await recorder.FlushAsync(); // second attempt succeeds

    await using var db = _factory.CreateDbContext();
    Assert.Equal(1, await db.Transitions.CountAsync());
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test Tests/ConPersistence.Tests --filter "FullyQualifiedName~A_failed_write_is_retried"`
Expected: FAIL, count is 0 (the batch was dequeued and lost on the first flush).

- [ ] **Step 3: Implement the base**

```csharp
public abstract class ChannelRecorder<TEvent>(ILogger log, int capacity = 1000, int batchSize = 500) : BackgroundService
{
    private readonly Channel<TEvent> _channel = Channel.CreateBounded<TEvent>(
        new BoundedChannelOptions(capacity) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly SemaphoreSlim _flushLock = new(1, 1);
    private List<TEvent>? _pending; // a batch whose write failed; retried before reading more
    private IDisposable? _subscription;

    protected abstract string EventNoun { get; }
    protected abstract Task WriteBatchAsync(IReadOnlyList<TEvent> batch, CancellationToken ct);

    public void Attach(IObservable<TEvent> source)
    {
        _subscription?.Dispose();
        _subscription = source.Subscribe(
            e => { if (!_channel.Writer.TryWrite(e)) log.LogWarning("{Noun} channel full; dropped {Event}", EventNoun, e); },
            ex => log.LogError(ex, "{Noun} stream error", EventNoun));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (await _channel.Reader.WaitToReadAsync(stoppingToken).ConfigureAwait(false))
            {
                try { await FlushAsync(stoppingToken).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogError(ex, "Failed writing {Noun}; retrying in 5s", EventNoun);
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            try { await FlushAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { log.LogWarning(ex, "Final {Noun} flush failed", EventNoun); }
        }
    }

    /// <summary>Drains the channel in batches. Serialized: the loop and a test-driven flush
    /// must not split one batch. A batch stays pending until its write succeeds.</summary>
    public async Task FlushAsync(CancellationToken ct = default)
    {
        await _flushLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            while (true)
            {
                var batch = _pending ?? new List<TEvent>();
                while (batch.Count < batchSize && _channel.Reader.TryRead(out var e)) batch.Add(e);
                if (batch.Count == 0) return;
                _pending = batch;
                await WriteBatchAsync(batch, ct).ConfigureAwait(false);
                _pending = null;
                log.LogDebug("Recorded {Count} {Noun}", batch.Count, EventNoun);
                if (batch.Count < batchSize) return;
            }
        }
        finally { _flushLock.Release(); }
    }

    public override void Dispose() { _subscription?.Dispose(); base.Dispose(); }
}
```

`TransitionRecorder` becomes `ChannelRecorder<TargetTransition>` with `EventNoun => "transitions"` and `WriteBatchAsync` = the existing db-add-range-save; `GlitchRecorder` likewise with its coalescing write moved into `WriteBatchAsync`. Note the loop change: `FlushAsync` previously wrote at most one batch of 500 per call; the new loop keeps going while full batches come out, which is what "drain" always meant.

- [ ] **Step 4: Run the persistence suite**

Run: `dotnet test Tests/ConPersistence.Tests`
Expected: pass.

- [ ] **Step 5: Commit**

```powershell
git add -A Libraries/ConPersistence/Services Tests/ConPersistence.Tests
git commit -m "refactor(persistence): ChannelRecorder<T> base; a failed batch is retried, not dropped"
```

### Task 2.4: RollupCollector keeps failed buckets and serializes flushes (P1 for rollups, S2 gap)

**Files:**
- Modify: `Libraries/ConPersistence/Services/RollupCollector.cs:16-17,52-80`
- Test: `Tests/ConPersistence.Tests/RecorderCollectorTests.cs` (new case)

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public async Task A_failed_rollup_write_is_retried_next_flush()
{
    var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-01T00:00:00Z"));
    var collector = new RollupCollector(new FailingOnceDbFactory(_factory), time, NullLogger<RollupCollector>.Instance);
    using var source = new Subject<(Guid, PingResult)>();
    collector.Attach(source);
    var target = Guid.NewGuid();
    source.OnNext((target, new PingResult(Ip, true, 10, timestamp: time.GetUtcNow())));

    await Assert.ThrowsAsync<InvalidOperationException>(() => collector.FlushAllAsync());
    await collector.FlushAllAsync();

    await using var db = _factory.CreateDbContext();
    var row = await db.Rollups.SingleAsync(r => r.TargetId == target);
    Assert.Equal(1, row.PingCount);
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test Tests/ConPersistence.Tests --filter "FullyQualifiedName~A_failed_rollup_write"`
Expected: FAIL, `SingleAsync` finds no row.

- [ ] **Step 3: Implement**

```csharp
private readonly SemaphoreSlim _flushLock = new(1, 1);
private List<RollupBucket> _pending = new();

private async Task WriteAsync(List<RollupBucket> buckets, CancellationToken ct)
{
    await _flushLock.WaitAsync(ct).ConfigureAwait(false);
    try
    {
        _pending.AddRange(buckets);
        if (_pending.Count == 0) return;
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        foreach (var bucket in _pending) { /* existing upsert via RollupMerge */ }
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        log.LogDebug("Wrote {Count} rollup buckets", _pending.Count);
        _pending = new();
    }
    finally { _flushLock.Release(); }
}
```

Two buckets for the same (target, minute) can now sit in `_pending` (one failed, one taken later); `RollupMerge.Combine` on the existing row handles the second, so no double counting.

- [ ] **Step 4: Run the persistence suite and commit**

Run: `dotnet test Tests/ConPersistence.Tests`
Expected: pass.

```powershell
git add Libraries/ConPersistence/Services/RollupCollector.cs Tests/ConPersistence.Tests/RecorderCollectorTests.cs
git commit -m "fix(persistence): rollup buckets survive a failed write; flushes are serialized"
```

---

## Phase 3: API edge

### Task 3.1: Timestamps in query strings parse as UTC unless an offset is given (H2)

**Files:**
- Create: `Programs/ConWatch/Api/TimeRange.cs`
- Modify: `Programs/ConWatch/Api/HistoryEndpoints.cs:13,18,35,43,52`, `Api/GlitchEndpoints.cs:12-19`, `Api/ExportEndpoints.cs:22-23,42-43,88-89`, `Api/AdminEndpoints.cs:105`, `Mcp/ConWatchMcpTools.cs:76,92-93,117-118`, `Pages/Report.cshtml.cs:10-13`
- Test: `Tests/ConWatch.Tests/TimeRangeTests.cs` (new), `Tests/ConWatch.Tests/HistoryApiTests.cs` (one API-level case)

**Interfaces:**
- Produces: `public static class TimeRange { public static DateTime? ParseUtc(string? text); }` returning `Kind=Utc`, throwing `ArgumentException` on garbage. Contract: "no offset means UTC". Every endpoint binds `string? from, string? to` and calls it; a parse failure returns 400 `{ errors: ["'from' is not a valid timestamp"] }`.
- Behavior change (document in README API section): an offset-less timestamp used to be interpreted as the server's local time; it is now UTC. Callers that already send `Z` or an offset see no change.

- [ ] **Step 1: Write the failing tests**

```csharp
public class TimeRangeTests
{
    [Fact] public void Offset_less_text_is_utc() =>
        Assert.Equal(new DateTime(2026, 9, 12, 0, 0, 0, DateTimeKind.Utc), TimeRange.ParseUtc("2026-09-12T00:00:00"));
    [Fact] public void Explicit_offset_is_honored() =>
        Assert.Equal(new DateTime(2026, 9, 11, 22, 0, 0, DateTimeKind.Utc), TimeRange.ParseUtc("2026-09-12T00:00:00+02:00"));
    [Fact] public void Z_is_utc() =>
        Assert.Equal(new DateTime(2026, 9, 12, 0, 0, 0, DateTimeKind.Utc), TimeRange.ParseUtc("2026-09-12T00:00:00Z"));
    [Fact] public void Null_passes_through() => Assert.Null(TimeRange.ParseUtc(null));
    [Fact] public void Garbage_throws() => Assert.Throws<ArgumentException>(() => TimeRange.ParseUtc("yesterday"));
}
```

API case in `HistoryApiTests`: seed two transitions at `2026-09-12T01:00Z` and `2026-09-12T03:00Z`; GET `/api/v1/transitions?from=2026-09-12T02:00:00` returns only the second; GET with `from=2026-09-12T04:00:00%2B02:00` (02:00Z) also returns only the second.

- [ ] **Step 2: Run to verify they fail**

Expected: compile error (`TimeRange` missing).

- [ ] **Step 3: Implement**

```csharp
namespace ConWatch.Api;

/// <summary>Query-string timestamps: ISO-8601; an explicit offset is honored, no offset means UTC.
/// (ASP.NET's DateTime binder would treat offset-less text as server-local time.)</summary>
public static class TimeRange
{
    public static DateTime? ParseUtc(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto))
            throw new ArgumentException($"'{text}' is not a valid timestamp");
        return dto.UtcDateTime;
    }
}
```

Replace each `DateTime? from` parameter with `string? from`, wrap the two parses in a `try { } catch (ArgumentException ex) { return Results.BadRequest(new { errors = new[] { ex.Message } }); }` block, and pass the `DateTime?` results where `.ToUniversalTime()` was called before. In the MCP tools, the parameters are already strings or `DateTime?`; switch to strings and let the tool return the error text. In `Report.cshtml.cs` bind `string? from, string? to`.

- [ ] **Step 4: Run the web suite and commit**

Run: `dotnet test Tests/ConWatch.Tests --filter "FullyQualifiedName!~NotifierIntegrationTests"`
Expected: pass.

```powershell
git add -A Programs/ConWatch Tests/ConWatch.Tests
git commit -m "fix(api): offset-less timestamps mean UTC everywhere; one parser for all range queries"
```

### Task 3.2: Take-over runs the transport policy (H1)

**Files:**
- Modify: `Programs/ConWatch/Clone/CloneService.cs:78-90` (`TakeOverAsync(string? sourcePassword, bool allowInsecure, CancellationToken ct)`), `Programs/ConWatch/Api/SetupEndpoints.cs:12,35-36` (`TakeoverRequest(string? SourcePassword, bool AllowInsecure = false)`), `Programs/ConWatch/wwwroot/lib/site-admin.js:127-140` (send `allowInsecure` from a checkbox next to the take-over button; add the checkbox in `Pages/Admin.cshtml` beside `takeover-password`, mirroring the clone page's insecure-override control in `site-setup.js:121`)
- Test: `Tests/ConWatch.Tests/CloneTests.cs` (new case)

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public async Task Takeover_over_plain_http_to_a_non_loopback_source_requires_the_override()
{
    using var factory = new ConWatchFactory();
    var client = factory.CreateClient();
    var settings = factory.Services.GetRequiredService<SettingsStore>();
    await settings.SetAsync(SettingKeys.CloneSourceUrl, "http://10.0.0.5:8080/");

    var takeover = await client.PostAsJsonAsync("/api/v1/setup/takeover", new { sourcePassword = "pw" });
    Assert.Equal(HttpStatusCode.BadRequest, takeover.StatusCode);
    Assert.Contains("insecure-transport", await takeover.Content.ReadAsStringAsync());
}
```

(Existing take-over tests use `http://source.test/` routed to an in-memory app through `TestServices`; `source.test` is not loopback, so they must pass `allowInsecure = true` after this task. Update them.)

- [ ] **Step 2: Run to verify it fails**

Expected: FAIL, the request attempts the network call and returns 502 `source-unreachable`.

- [ ] **Step 3: Implement**

At the top of `TakeOverAsync` after parsing `source`:

```csharp
// Take-over always carries the password and always pulls secrets; the consent
// given at clone time may have been for a non-sensitive transfer, so ask again.
if (TransportPolicy.Evaluate(source, sensitive: true, allowInsecure) == TransportPolicy.Verdict.RequiresOverride)
    return CloneOutcome.Fail(400, "insecure-transport",
        "Taking over would send the source admin password and receive its secrets over " +
        "unencrypted HTTP. Use the source's HTTPS address or confirm the insecure transfer.");
```

Thread `AllowInsecure` through `TakeoverRequest` and the endpoint; add the checkbox and JS field.

- [ ] **Step 4: Run the web suite and commit**

```powershell
git add -A Programs/ConWatch Tests/ConWatch.Tests
git commit -m "fix(clone): take-over enforces the transport policy like clone does"
```

---

## Phase 4: De-duplication

Pure refactors; the existing suites are the safety net. Each task: make the change, run `dotnet test ConnectionWatcher.sln --filter "FullyQualifiedName!~NotifierIntegrationTests"`, commit. Line numbers below are from the review and will have drifted; locate by the quoted expression.

### Task 4.1: Program.cs service registration as extension methods (S3)

**Files:** Create `Programs/ConWatch/Startup/ServiceCollectionExtensions.cs`; modify `Programs/ConWatch/Program.cs:23-118`.

Move the blocks verbatim into `AddConWatchOptions(IConfiguration)`, `AddConWatchDatabase(IConfiguration)` (returns the resolved `dbDir` via an `out string stateDir`, fixing the duplicated `dbDir is { Length: > 0 } ? dbDir : "."` at lines 84 and 88), `AddConWatchEngine()`, `AddConWatchPersistence()`, `AddConWatchHostedServices()` (with the ordering comment from Task 2.1), `AddConWatchNotifiers()`, `AddConWatchAdminAuth(string stateDir)`, `AddConWatchWeb()`. Program.cs becomes eight calls plus the pipeline. Commit: `refactor(host): Program.cs registrations as named extension methods`.

### Task 4.2: One config-loading routine (S4)

**Files:** `Libraries/ConPersistence/ConfigStore.cs:30-99` (`SeedAsync`), `Libraries/ConPersistence/ConfigService.cs:264-347` (`ImportAsync`).

Extract `internal static async Task ReplaceAllAsync(ConWatchDbContext db, ConfigDocument doc)` in `ConfigService` (the body of `ImportAsync` after validation, including `NormalizeProbe`). `SeedAsync` maps `WatcherOptions` → `ConfigDocument` using the existing `FromProfile`/target/pool DTO mappers and calls `ReplaceAllAsync`. Add a `ConfigStoreTests` case: seeding a target with `Probe = "TCP:80"` stores `"tcp:80"`. Commit: `refactor(persistence): SeedAsync reuses the import routine (fixes probe normalization gap)`.

### Task 4.3: Admin-lock page filter (S5)

**Files:** Create `Programs/ConWatch/Auth/AdminLockPageFilter.cs` (`IAsyncPageFilter`: if `gate.IsLocked(context.HttpContext.User)` redirect to `/?admin=1`, else next); register in `AddRazorPages(o => o.Conventions.AddFolderApplicationModelConvention("/Config", m => m.Filters.Add(new AdminLockPageFilter(...)))` plus `AddPageApplicationModelConvention` for `/Setup`, `/Traceroute`, `/Admin`, `/Data`. Delete the seven inline checks. `AdminLockTests` already covers redirects; add one case per previously-inline page if missing. Commit: `refactor(web): admin-lock redirect as a page filter`.

### Task 4.4: `HistoryQueries.GlitchesAsync`; MCP reuses REST queries (S6)

**Files:** `Libraries/ConPersistence/HistoryQueries.cs` (add `GlitchesAsync(db, targetId, fromUtc, toUtc, limit)` mirroring `TransitionsAsync`), `Programs/ConWatch/Api/GlitchEndpoints.cs:16-20`, `Programs/ConWatch/Mcp/ConWatchMcpTools.cs:84-119`. `McpEndpointTests` and `HistoryApiTests` cover both surfaces. Commit: `refactor: one glitch/transition query for REST and MCP`.

### Task 4.5: Profile/target/pool mapping helpers (P-C1)

**Files:** Create `Libraries/ConPersistence/Dto/ConfigMapping.cs` with extension methods `ProfileRow.ToDto()`, `ProfileDto.ToConfig()`, `ProfileRow.CopyFrom(ProfileDto)`, `PingProfileConfig.ToDto(string name)`, and the target/pool equivalents; replace the six profile sites (`ConfigService.cs:60-70, 298-310, 391-403`; `ConfigStore.cs:121-133, 167-179, 195-209`) and the target/pool sites. `ConfigServiceTests`/`ConfigStoreTests` cover round trips. Commit: `refactor(persistence): single mapping helpers for profiles, targets, pools`.

### Task 4.6: Latest-transition-per-subject helper (P-C2)

**Files:** `Libraries/ConPersistence/HistoryQueries.cs` (add `public static IQueryable<long> LatestTransitionIdsPerSubject(ConWatchDbContext db) => db.Transitions.GroupBy(t => t.SubjectId).Select(g => g.Max(t => t.Id));`), replace in `Services/DataPruner.cs:24`, `Rollups/RetentionPass.cs:41`, `WarmStartQueries.cs:37`. Commit: `refactor(persistence): one definition of "latest transition per subject"`.

### Task 4.7: Admin password verify helper (H-C1)

**Files:** `Programs/ConWatch/Auth/AdminGate.cs` (add `public async Task<bool> VerifyOrDelayAsync(string? password) { if (!IsEnabled || Verify(password)) return true; await Task.Delay(1000); return false; }`), replace the five blocks in `AuthEndpoints.cs:36-40, 55-59, 69-73`, `AdminEndpoints.cs:30-34`, `ExportEndpoints.cs:70-74`. `AuthApiTests`/`AdminApiTests` cover them. Commit: `refactor(auth): one verify-or-delay helper`.

### Task 4.8: IspReportBuilder batched queries (P-C3)

**Files:** `Libraries/ConPersistence/Report/IspReportBuilder.cs:155-211`. Compute `Dictionary<Guid, DateTime?> resetCutoffs` (one query: last `HistoryReset` per target) and `Dictionary<Guid, (long pings, long successes)> totals` (one grouped rollup query bounded by the window and each target's cutoff, applied in memory) once, then build target rows and anchor rows from the dictionaries. `IspReportBuilderTests` covers the numbers. Commit: `perf(report): two batched queries instead of 1 + 2N`.

### Task 4.9: One trunk walker (H-C3)

**Files:** Create `Programs/ConWatch/TrunkWalker.cs` with `public static IReadOnlyList<T> AncestorsOf<T>(T node, Func<T, T?> parentOf)` and `public static (T? trunkRoot, List<T> trunk) SharedTrunk<T>(IEnumerable<T> members, Func<T, T?> parentOf)`; adapt `TopologyNames.FromConfigAsync`, `Pages/History.cshtml.cs:27-52`, `Pages/Config/Targets.cshtml.cs:35-72` to call it with their own `parentOf` lambdas. `SnapshotTests`/page tests cover output. Commit: `refactor(web): one ancestor walker for config, live graph, and DTO views`.

---

## Self-review notes

- Spec coverage: E1→1.5, E2→1.1, E3→1.2+1.3, E4→2.1+2.2, E5→1.4, E6→1.7, P1→2.3+2.4, P2→2.1, H1→3.2, H2→3.1, S1→0.1, S2→2.3, S3→4.1, S4→4.2, S5→4.3, S6→4.4, S7→4.9, remaining C-items→4.5–4.8, DNS→1.6.
- Type consistency: `RoutedPingResult`, `StopControllerAsync(Guid, TimeSpan)`, `GetOrCreateController(Guid, IPAddress, ProbeSpec, IPingCadence, Func<int>)`, `AnalysisOutcome`, `ChannelRecorder<TEvent>`, `TimeRange.ParseUtc(string?)`, `DatabaseStartup.PrepareAsync` are used with the same shapes in every task that references them.
- The three details that were unverified in the first draft (the `DomainResolutionResult` constructor, the setup request records, and `TestDbFactory` being in-memory) were checked against the source and are now written in exactly.
