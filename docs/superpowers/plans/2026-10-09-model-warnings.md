# Model Warnings — Implementation Plan (Plan B of 4)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** When ConnectionWatcher's map of the network disagrees with what the probes prove, the administrator gets a quiet note: a wrench badge in the web header, a "ConnectionWatcher notes" panel on the dashboard and a small dot on the tray icon. Nobody else is bothered, and the verdict is untouched.

**Architecture:**
- A new engine coordinator, `ModelCheckCoordinator`, evaluates the warning *conditions* from live monitor states. There are three kinds: `HopBypassed`, `NeverAnswered` and `UnprovableUp`. It publishes raise/clear changes on a new `WatcherEngine.ModelConditionSource` stream.
- A host-lifetime `ModelWarningLedger` (ConPersistence) turns conditions into warning *instances*. It survives engine rebuilds, so a config edit doesn't end and restart every note. It holds acknowledgements and persists start, end and acknowledge through a `ChannelRecorder` into a new `ModelWarnings` table.
- The status snapshot gains `warnings[]`. One builder already feeds REST, SignalR, MCP and the dashboard's first paint, so every surface gets it. The tray reads it from `/api/v1/status`.
- `ProbeOnlyLoss` is Plan D.

**Tech Stack:** .NET 10, C#, System.Reactive, EF Core + SQLite, xunit 2.9.3, `FakeTimeProvider`; React 18 + vitest 2 (happy-dom); WinForms tray (logic moved into net10.0 `ConInfrastructure` so it is testable on the Linux CI runners).

**Spec:** `docs/superpowers/specs/2026-10-09-evidence-based-verdict-design.md`. This plan implements §4.6 (minus `ProbeOnlyLoss`), the §4.1 advice on the config page and wizard, and the §5 rows for `ModelCheckCoordinator`, status API/MCP `warnings[]`, the acknowledge endpoint, the dashboard badge and panel, and the tray overlay. That is rollout step 3 of §8.

## Global Constraints

- File-scoped namespaces; `Nullable` and `ImplicitUsings` enabled; match the surrounding comment density and naming.
- **D3 (spec §2):** model warnings never touch the verdict, tray toasts, the tray icon's colour, webhooks, MQTT state or availability, the ISP report, or the metrics `up`/verdict series. The only metric is the gauge `conwatch_model_warnings`.
- Warnings are **read-only on the open surface** (`/api/v1/status`, MCP). Acknowledge is **admin-locked** (`POST /api/v1/warnings/{key}/acknowledge` on the `locked` group).
- Acknowledge hides **that instance** until it clears and recurs (spec §9.4). It never disables the check.
- Warning keys: `hop-bypassed:{targetId:D}`, `never-answered:{targetId:D}`, `unprovable-up`. Kinds on the wire are the enum names `HopBypassed`, `NeverAnswered`, `UnprovableUp`.
- The `UnprovableUp` advice sentence, verbatim (spec §4.1): *"Tag your ISP on a path hop (or add an anchor outside your ISP) so ConnectionWatcher can prove your internet is up."*
- "Answering" = `Responding`, `Faltering` or `Recovering` (`TopologyCoordinator.Answering`).
- **Never sleep-then-assert in tests:** wait on conditions. CI runners have 2 cores. Engine and ledger unit tests use `FakeTimeProvider`.
- Test commands, per project (never the whole solution): `dotnet test Tests/ConEngine.Tests -c Debug`, `Tests/ConPersistence.Tests`, `Tests/ConWatch.Tests`, `Tests/ConConfig.Tests`; filter with `--filter "FullyQualifiedName~<Class>"`. UI: `cd Programs/ConWatch/ClientApp && npx vitest run`, and `npm run build` for the type check. `ConWatch.Tests` has known cold-start flakes (`ProbeFieldTests`, the `SettingsApiTests` constructor, `NotifierIntegrationTests.VerdictOnly_…`). Rerun once before calling a failure real.
- Commit after every task. **Never push.** Commit messages carry no attribution lines. Work on a feature branch `model-warnings` in a git worktree (`.worktrees/model-warnings`). Do not edit `README.md` (the user has an uncommitted rewrite). Before merging, check `git status` in the MAIN checkout for stray edits (a subagent once wrote into it from a worktree).

## Decisions this plan makes (flag to Mark at review)

These are not in the spec.

1. **A Faltering hop needs to hold for 2 min** (`FalteringHold`) before `HopBypassed` fires. A Down hop fires at once.
   - *Why:* routers that deprioritise ICMP falter briefly all the time, and each blip would be a stored note. Persistent sub-Down loss is `ProbeOnlyLoss` (Plan D).
   - *Effect:* the 2026-10-08 incident (Down for 27 s) still raises its note immediately.
2. **`NeverAnswered` replaces `HopBypassed`** for a hop with zero replies (accepted + refused) once the engine has run for 5 min (`NeverAnsweredAfter`).
   - Before the 5 min, the same hop shows as `HopBypassed`, which is also true.
   - The counters are per engine, so after a rebuild a hop that answered before can be relabelled `NeverAnswered` if it stays silent for 5 min.
3. **The checks apply to any target with a monitored descendant**, not only anchor-path hops. A LAN switch that is Down while the devices behind it answer is the same map error.
4. **Engine rebuilds keep notes.** After a rebuild, a carried-over note that the new engine doesn't re-raise within 6 min (`ResyncGrace`) is closed.
   - *Effect:* a deleted hop's note can linger up to 6 min, and its stored end time can be up to 6 min late.
5. **Visibility:**
   - The header badge and the dashboard panel show only to the admin: no gate, or unlocked.
   - With the gate on and logged out, nothing is shown. The open `/api/v1/status` still carries `warnings[]`, as the spec says.
   - The tray shows the badge to whoever runs it.
6. **Warnings are not in the snapshot export, clone or the History page yet.** They are stored and logged (spec: "so History can show it later"). Retention prunes ended rows with the transition retention. Clear-history and factory reset delete them.

## Review Focus

The failure modes most likely to bite, each pinned by a test in the named task:

1. **The 2026-10-08 incident.** A mid-path hop goes Down while the things beyond it answer. Expect exactly one `HopBypassed` raise naming the hop, its probe and the first answering target beyond it; the verdict stays `Healthy`; and the note clears when the hop answers again (Task 1, plus the end-to-end test in Task 3).
2. **A real outage**, where the hop and everything beyond it go Down. Expect **no** note, and none left behind (Task 1).
3. **A config edit (engine rebuild) mid-warning.** Expect one stored row (not closed and reopened), the original `since` kept and the acknowledgement kept (Task 2).
4. **Re-entrancy:** a change subscriber that reads the coordinator's `Active` inside `OnNext` must not deadlock. Changes are published outside the coordinator lock (Task 1).
5. **The tray tooltip** overflowing the 127-character `NotifyIcon.Text` limit with long target names or many notes. Expect truncation with an ellipsis, never an exception (Task 6).

---

## File Structure

| File | Responsibility | Task |
|---|---|---|
| `Core/ConEngine/Topology/ModelCheckCoordinator.cs` (new) | `ModelWarningKind`, `ModelCondition`, `ModelConditionChange`; evaluates conditions, publishes changes | 1 |
| `Core/ConEngine/Topology/VerdictCoordinator.cs` | `UsingIspFallback` | 1 |
| `Core/ConEngine/WatcherEngine.cs` | `ModelCheck`, `ModelConditionSource`, `ModelConditions` | 1 |
| `Libraries/ConPersistence/Entities/DataRows.cs`, `ConWatchDbContext.cs`, `Migrations/*_AddModelWarnings.cs` | `ModelWarningRow` table | 2 |
| `Libraries/ConPersistence/Warnings/ModelWarningLedger.cs` (new) | Instances, acknowledgements, rebuild continuity | 2 |
| `Libraries/ConPersistence/Warnings/ModelWarningRecorder.cs` (new) | Persists opened/closed/acknowledged ops | 2 |
| `Libraries/ConPersistence/Retention/RetentionPass.cs` | Prune ended warnings | 2 |
| `Programs/ConWatch/StatusSnapshotBuilder.cs`, `StatusBroadcaster.cs`, `Api/StatusEndpoints.cs`, `Mcp/ConWatchMcpTools.cs`, `Pages/Index.cshtml.cs` | `warnings[]` everywhere the snapshot goes | 3 |
| `Programs/ConWatch/Api/WarningEndpoints.cs` (new), `ConWatchApp.cs` | Locked acknowledge | 3 |
| `Programs/ConWatch/Api/MetricsEndpoint.cs` | `conwatch_model_warnings` | 3 |
| `Programs/ConWatch/Api/AdminEndpoints.cs` | Clear/reset/stats include the table | 3 |
| `Programs/ConWatch/Startup/ConWatchServices.cs` | DI + hosted + observer order | 3 |
| `Libraries/ConPersistence/Config/ConfigHealth.cs`, `Programs/ConWatch/Pages/Setup.cshtml` | `anchors-all-isp` advice | 4 |
| `Programs/ConWatch/ClientApp/src/lib/{types,statusStore}.ts`, `src/components/NotesPanel.tsx` (new), `src/dashboard.tsx` | Notes panel | 5 |
| `Programs/ConWatch/Pages/_Layout.cshtml`, `wwwroot/lib/site-shell.js`, `wwwroot/css/site.css` | Header wrench badge | 5 |
| `Libraries/ConInfrastructure/Tray/TrayStatus.cs` (moved from `Programs/ConWatchTray/TrayStatus.cs`) | Pure status → state + notes + tooltip | 6 |
| `Programs/ConWatchTray/Program.cs` | Overlay dot, tooltip, "ConnectionWatcher notes" menu item | 6 |

Tests: `Tests/ConEngine.Tests/ModelCheckCoordinatorTests.cs` (new), `VerdictCoordinatorTests.cs`, `WatcherEngineTests.cs`; `Tests/ConPersistence.Tests/{ModelWarningLedgerTests (new), RetentionTests, ConfigHealthTests}.cs`; `Tests/ConWatch.Tests/{StatusShapeTests, WarningApiTests (new), McpEndpointTests, MetricsEndpointTests, AdminApiTests, TrayStatusTests (new)}.cs`; `ClientApp/src/components/NotesPanel.test.tsx` (new), `src/lib/statusStore.test.ts`.

---

### Task 1: Engine — ModelCheckCoordinator

**Files:**
- Create: `Core/ConEngine/Topology/ModelCheckCoordinator.cs`
- Modify: `Core/ConEngine/Topology/VerdictCoordinator.cs` (ctor, around L146–157), `Core/ConEngine/WatcherEngine.cs`
- Test: `Tests/ConEngine.Tests/ModelCheckCoordinatorTests.cs` (new), `Tests/ConEngine.Tests/VerdictCoordinatorTests.cs`, `Tests/ConEngine.Tests/WatcherEngineTests.cs`

**Interfaces:**
- Produces (namespace `ConEngine.Topology`):
  - `enum ModelWarningKind { HopBypassed, NeverAnswered, UnprovableUp }`
  - `sealed record ModelCondition(string Key, ModelWarningKind Kind, Guid? TargetId, string? TargetName, string Message)` with `static string KeyFor(ModelWarningKind kind, Guid? targetId)`
  - `sealed record ModelConditionChange(ModelCondition Condition, bool Active, DateTimeOffset AtUtc)`
  - `class ModelCheckCoordinator : IDisposable` with `EvaluateEvery` (5 s), `FalteringHold` (2 min), `NeverAnsweredAfter` (5 min), `const string UnprovableUpMessage`, `IObservable<ModelConditionChange> ChangeSource`, `IReadOnlyList<ModelCondition> Active`, `void Start()`, `void Evaluate()`
  - `VerdictCoordinator.UsingIspFallback` (bool)
  - `WatcherEngine.ModelCheck` (`ModelCheckCoordinator?`), `WatcherEngine.ModelConditionSource` (`IObservable<ModelConditionChange>`, exists from construction), `WatcherEngine.ModelConditions` (`IReadOnlyList<ModelCondition>`, empty before start)

- [ ] **Step 1: Write the failing coordinator tests**

Create `Tests/ConEngine.Tests/ModelCheckCoordinatorTests.cs`:

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

public class ModelCheckCoordinatorTests : IDisposable
{
    private static readonly PingProfile Fast = new()
    {
        Name = "Fast", HistorySize = 20, MinUsableSample = 5,
    };

    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-10-09T00:00:00Z"));
    private readonly TargetGraph _graph;
    private readonly Dictionary<Guid, TargetMonitor> _monitors = new();
    private readonly TopologyCoordinator _topology;
    private ModelCheckCoordinator _check = null!;
    private readonly List<ModelConditionChange> _changes = new();

    public ModelCheckCoordinatorTests()
    {
        _graph = TargetGraph.FromOptions(new WatcherOptions
        {
            Targets =
            [
                new TargetConfig { Name = "Router", Address = "192.168.1.1", Profile = "Internal" },
                new TargetConfig { Name = "Inside", Address = "10.1.10.1", Parent = "Router", Profile = "Internal", Probe = "tcp:80" },
                new TargetConfig { Name = "External", Address = "203.0.113.1", Parent = "Inside", Profile = "Internal" },
                new TargetConfig { Name = "Google", Address = "8.8.8.8", Parent = "External" },
                new TargetConfig { Name = "Cloudflare", Address = "1.1.1.1", Parent = "External" },
            ],
            Pools = [new PoolConfig { Name = "Internet", Members = ["Google", "Cloudflare"] }],
        });
        foreach (var target in _graph.Targets.Values)
        {
            var monitor = new TargetMonitor(target, Fast, new PingDiscipline(Fast, _time),
                _time, NullLoggerFactory.Instance);
            monitor.StartTracking(IPAddress.Parse(target.Address));
            _monitors[target.Id] = monitor;
        }
        _topology = new TopologyCoordinator(_graph, _monitors, NullLogger<TopologyCoordinator>.Instance);
        _topology.Start();
    }

    private void StartCheck(bool unprovableUp = false)
    {
        _check = new ModelCheckCoordinator(_graph, _monitors, unprovableUp, _time,
            NullLogger<ModelCheckCoordinator>.Instance);
        _check.ChangeSource.Subscribe(_changes.Add);
        _check.Start();
    }

    public void Dispose()
    {
        _check?.Dispose();
        _topology.Dispose();
        foreach (var m in _monitors.Values)
            m.Dispose();
    }

    private Guid Id(string name) => _graph.GetByName(name).Id;

    private void Feed(string name, int count, bool success)
    {
        var monitor = _monitors[Id(name)];
        for (int i = 0; i < count; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            monitor.RecordPingResult(new PingResult(monitor.ResolvedAddress!, success,
                success ? 5 : 0, timestamp: _time.GetUtcNow()));
        }
    }

    private void AllHealthy()
    {
        foreach (var name in new[] { "Router", "Inside", "External", "Google", "Cloudflare" })
            Feed(name, 5, true);
    }

    [Fact]
    public void The_2026_10_08_incident_raises_one_hop_bypassed_note_and_clears_it()
    {
        StartCheck();
        AllHealthy();
        Feed("Inside", 20, false); // Inside Down; External and the anchors keep answering

        var raised = Assert.Single(_changes);
        Assert.True(raised.Active);
        Assert.Equal(ModelWarningKind.HopBypassed, raised.Condition.Kind);
        Assert.Equal($"hop-bypassed:{Id("Inside"):D}", raised.Condition.Key);
        Assert.Equal(Id("Inside"), raised.Condition.TargetId);
        Assert.Equal("Inside", raised.Condition.TargetName);
        Assert.StartsWith("Inside isn't answering its probe (tcp/80), but External beyond it is",
            raised.Condition.Message);

        Feed("Inside", 20, true);
        Assert.Equal(2, _changes.Count);
        Assert.False(_changes[1].Active);
        Assert.Equal(raised.Condition.Key, _changes[1].Condition.Key);
        Assert.Empty(_check.Active);
    }

    [Fact]
    public void A_real_outage_beyond_a_hop_raises_no_note()
    {
        StartCheck();
        AllHealthy();
        // Everything beyond fails first, then the hop: nothing beyond ever answers.
        Feed("External", 20, false);
        Feed("Google", 20, false);
        Feed("Cloudflare", 20, false);
        Feed("Inside", 20, false);

        Assert.DoesNotContain(_changes, c => c.Active && c.Condition.TargetId == Id("Inside"));
        Assert.Empty(_check.Active.Where(c => c.TargetId == Id("Inside")));
    }

    [Fact]
    public void A_hop_whose_descendants_go_down_after_it_stops_being_bypassed()
    {
        StartCheck();
        AllHealthy();
        Feed("Inside", 20, false);
        Assert.Single(_check.Active);

        Feed("External", 20, false);
        Feed("Google", 20, false);
        Feed("Cloudflare", 20, false);

        Assert.Empty(_check.Active);
        Assert.False(_changes.Last().Active);
    }

    [Fact]
    public void A_faltering_hop_is_a_note_only_after_it_holds_for_the_faltering_hold()
    {
        StartCheck();
        AllHealthy();
        Feed("Inside", 3, false);
        Assert.Equal(HostState.Faltering, _monitors[Id("Inside")].CurrentState);
        Assert.Empty(_changes);

        // Ticks are aligned to Start, not to the Faltering transition: one
        // extra period guarantees a tick after the hold has passed.
        _time.Advance(ModelCheckCoordinator.FalteringHold + ModelCheckCoordinator.EvaluateEvery);
        var raised = Assert.Single(_changes);
        Assert.Equal(ModelWarningKind.HopBypassed, raised.Condition.Kind);
        Assert.StartsWith("Inside is losing replies to its probe (tcp/80), but External beyond it answers",
            raised.Condition.Message);
    }

    [Fact]
    public void A_faltering_blip_shorter_than_the_hold_leaves_no_note()
    {
        StartCheck();
        AllHealthy();
        Feed("Inside", 3, false);
        Feed("Inside", 20, true); // recovered well inside the hold
        _time.Advance(ModelCheckCoordinator.FalteringHold * 2);
        Assert.Empty(_changes);
    }

    [Fact]
    public void A_hop_that_never_answers_becomes_never_answered_after_the_wait()
    {
        StartCheck();
        foreach (var name in new[] { "Router", "External", "Google", "Cloudflare" })
            Feed(name, 5, true);
        Feed("Inside", 20, false); // never a single reply
        Assert.Equal(ModelWarningKind.HopBypassed, Assert.Single(_check.Active).Kind);

        // Keep the beyond-targets answering while the clock passes the wait.
        while (_time.GetUtcNow() - DateTimeOffset.Parse("2026-10-09T00:00:00Z") < ModelCheckCoordinator.NeverAnsweredAfter)
            Feed("External", 5, true);
        _time.Advance(ModelCheckCoordinator.EvaluateEvery);

        var active = Assert.Single(_check.Active);
        Assert.Equal(ModelWarningKind.NeverAnswered, active.Kind);
        Assert.Equal($"never-answered:{Id("Inside"):D}", active.Key);
        Assert.StartsWith("Inside has never answered its probe (tcp/80)", active.Message);
        Assert.Contains(_changes, c => !c.Active && c.Condition.Kind == ModelWarningKind.HopBypassed);
    }

    [Fact]
    public void A_hop_that_answered_once_is_never_never_answered()
    {
        StartCheck();
        AllHealthy();
        Feed("Inside", 20, false);
        _time.Advance(ModelCheckCoordinator.NeverAnsweredAfter * 2);
        Assert.All(_check.Active, c => Assert.Equal(ModelWarningKind.HopBypassed, c.Kind));
    }

    [Fact]
    public void Unprovable_up_is_raised_at_start_when_every_anchor_is_the_isps()
    {
        StartCheck(unprovableUp: true);
        var raised = Assert.Single(_changes);
        Assert.True(raised.Active);
        Assert.Equal(ModelWarningKind.UnprovableUp, raised.Condition.Kind);
        Assert.Equal("unprovable-up", raised.Condition.Key);
        Assert.Null(raised.Condition.TargetId);
        Assert.Contains("Tag your ISP on a path hop (or add an anchor outside your ISP) so ConnectionWatcher can prove your internet is up.",
            raised.Condition.Message);
    }

    [Fact]
    public void Repeated_evaluations_of_a_standing_condition_publish_nothing_new()
    {
        StartCheck();
        AllHealthy();
        Feed("Inside", 20, false);
        Feed("External", 10, true);
        _time.Advance(ModelCheckCoordinator.EvaluateEvery * 10);
        Assert.Single(_changes);
    }

    [Fact]
    public void Changes_are_published_outside_the_coordinator_lock()
    {
        // A subscriber reading Active from another thread inside OnNext would
        // deadlock if the change were published under the lock.
        StartCheck();
        var reads = new List<int>();
        _check.ChangeSource.Subscribe(_ =>
            reads.Add(Task.Run(() => _check.Active.Count).Wait(TimeSpan.FromSeconds(10)) ? 1 : 0));
        AllHealthy();
        Feed("Inside", 20, false);
        Assert.Equal([1], reads);
    }

    [Fact]
    public void Dispose_stops_the_periodic_evaluation()
    {
        StartCheck();
        AllHealthy();
        Feed("Inside", 3, false);
        _check.Dispose();
        _time.Advance(ModelCheckCoordinator.FalteringHold * 2); // must not throw or publish
        Assert.Empty(_changes);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Tests/ConEngine.Tests -c Debug --filter "FullyQualifiedName~ModelCheckCoordinatorTests"`
Expected: build FAILS with `The type or namespace name 'ModelCheckCoordinator' could not be found`.

- [ ] **Step 3: Implement the coordinator**

Create `Core/ConEngine/Topology/ModelCheckCoordinator.cs`:

```csharp
using System.Collections.Concurrent;
using System.Reactive.Subjects;
using ConCore;
using ConCore.Targets;
using ConEngine.Tracking;
using Microsoft.Extensions.Logging;

namespace ConEngine.Topology;

/// <summary>Ways ConnectionWatcher's map can disagree with what the probes prove (spec 2026-10-09 §4.6).</summary>
public enum ModelWarningKind
{
    /// <summary>A target is Down/Faltering while something beyond it answers: traffic passes it.</summary>
    HopBypassed,
    /// <summary>A target has never answered while something beyond it answers.</summary>
    NeverAnswered,
    /// <summary>Every anchor is the ISP's: "up" is proven only as far as the ISP.</summary>
    UnprovableUp,
}

/// <summary>One condition that holds right now. Keyed so the same condition
/// on the same target is the same note across evaluations.</summary>
public sealed record ModelCondition(string Key, ModelWarningKind Kind, Guid? TargetId,
    string? TargetName, string Message)
{
    public static string KeyFor(ModelWarningKind kind, Guid? targetId)
    {
        var kindKey = kind switch
        {
            ModelWarningKind.HopBypassed => "hop-bypassed",
            ModelWarningKind.NeverAnswered => "never-answered",
            ModelWarningKind.UnprovableUp => "unprovable-up",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        return targetId is { } id ? $"{kindKey}:{id:D}" : kindKey;
    }
}

/// <summary>A condition started (Active) or stopped holding.</summary>
public sealed record ModelConditionChange(ModelCondition Condition, bool Active, DateTimeOffset AtUtc);

/// <summary>
/// Evaluates the model-warning conditions from live monitor states (spec
/// 2026-10-09 §4.6). Admin-facing only: nothing here touches the verdict or
/// any target's state. Re-evaluates on every monitor transition and every
/// <see cref="EvaluateEvery"/> (the time-based conditions need a clock).
/// </summary>
public class ModelCheckCoordinator : IDisposable
{
    public static readonly TimeSpan EvaluateEvery = TimeSpan.FromSeconds(5);
    /// <summary>A Faltering hop is a note only once it has faltered this long;
    /// a Down hop is a note at once.</summary>
    public static readonly TimeSpan FalteringHold = TimeSpan.FromMinutes(2);
    /// <summary>How long a target may go without a single reply before it is
    /// NeverAnswered rather than HopBypassed.</summary>
    public static readonly TimeSpan NeverAnsweredAfter = TimeSpan.FromMinutes(5);

    public const string UnprovableUpMessage =
        "Every anchor is tagged as your ISP's, so ConnectionWatcher can only prove your ISP answers, " +
        "not the rest of the internet. Tag your ISP on a path hop (or add an anchor outside your ISP) " +
        "so ConnectionWatcher can prove your internet is up.";

    private const string BypassTail =
        " — traffic is passing it. Its probe may measure a service rather than forwarding, " +
        "it may not really be on the path, or your network has another route.";

    private readonly IReadOnlyDictionary<Guid, TargetMonitor> _monitors;
    // Targets with at least one monitored descendant -> those descendants, nearest first.
    private readonly Dictionary<Guid, List<Guid>> _beyond = new();
    private readonly bool _unprovableUp;
    private readonly TimeProvider _time;
    private readonly ILogger<ModelCheckCoordinator> _log;
    private readonly Lock _lock = new();
    private readonly Lock _publishLock = new();
    private readonly ConcurrentQueue<ModelConditionChange> _outbox = new();
    private readonly Subject<ModelConditionChange> _changes = new();
    private readonly List<IDisposable> _subscriptions = new();

    // Guarded by _lock.
    private readonly Dictionary<string, ModelCondition> _active = new();
    private readonly Dictionary<Guid, DateTimeOffset> _falteringSince = new();
    private DateTimeOffset _startedUtc;
    private ITimer? _timer;
    private bool _disposed;

    public ModelCheckCoordinator(TargetGraph graph, IReadOnlyDictionary<Guid, TargetMonitor> monitors,
        bool unprovableUp, TimeProvider timeProvider, ILogger<ModelCheckCoordinator> log)
    {
        _monitors = monitors;
        _unprovableUp = unprovableUp;
        _time = timeProvider;
        _log = log;
        foreach (var id in monitors.Keys)
        {
            // GetDescendants is breadth-first: the first answering one is the nearest witness.
            var beyond = graph.GetDescendants(id).Select(d => d.Id).Where(monitors.ContainsKey).ToList();
            if (beyond.Count > 0)
                _beyond[id] = beyond;
        }
    }

    /// <summary>Raise/clear changes. Published outside the coordinator's lock,
    /// in the order they were decided.</summary>
    public IObservable<ModelConditionChange> ChangeSource => _changes;

    /// <summary>The conditions holding as of the last evaluation.</summary>
    public IReadOnlyList<ModelCondition> Active
    {
        get { lock (_lock) { return _active.Values.ToList(); } }
    }

    public void Start()
    {
        lock (_lock)
            _startedUtc = _time.GetUtcNow();
        foreach (var monitor in _monitors.Values)
            _subscriptions.Add(monitor.TransitionSource.Subscribe(_ => Evaluate()));
        _timer = _time.CreateTimer(_ => OnTick(), null, EvaluateEvery, EvaluateEvery);
        Evaluate();
    }

    private void OnTick()
    {
        try
        {
            Evaluate();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Model check evaluation failed");
        }
    }

    public void Evaluate()
    {
        lock (_lock)
        {
            if (_disposed)
                return;
            var now = _time.GetUtcNow();
            var states = _monitors.ToDictionary(kv => kv.Key, kv => kv.Value.CurrentState);
            var found = new Dictionary<string, ModelCondition>();

            if (_unprovableUp)
                Add(found, ModelWarningKind.UnprovableUp, null, UnprovableUpMessage);

            foreach (var (id, beyond) in _beyond)
            {
                var state = states[id];
                if (state == HostState.Faltering)
                    _falteringSince.TryAdd(id, now);
                else
                    _falteringSince.Remove(id);
                if (state is not (HostState.Down or HostState.Faltering))
                    continue;
                var witnessId = beyond.FirstOrDefault(d => TopologyCoordinator.Answering(states[d]));
                if (witnessId == Guid.Empty)
                    continue; // nothing beyond answers: a real failure, not a map error

                var hop = _monitors[id];
                var witness = _monitors[witnessId].Target.Name;
                var probe = ProbeText(hop.Target);
                bool neverAnswered = hop.AcceptedReplies + hop.RefusedReplies == 0;
                if (neverAnswered && now - _startedUtc >= NeverAnsweredAfter)
                {
                    Add(found, ModelWarningKind.NeverAnswered, hop.Target,
                        $"{hop.Target.Name} has never answered its probe ({probe}) since monitoring started, " +
                        $"but {witness} beyond it answers. Change its probe or remove it from the path.");
                }
                else if (state == HostState.Down)
                {
                    Add(found, ModelWarningKind.HopBypassed, hop.Target,
                        $"{hop.Target.Name} isn't answering its probe ({probe}), but {witness} beyond it is{BypassTail}");
                }
                else if (now - _falteringSince[id] >= FalteringHold)
                {
                    Add(found, ModelWarningKind.HopBypassed, hop.Target,
                        $"{hop.Target.Name} is losing replies to its probe ({probe}), but {witness} beyond it answers{BypassTail}");
                }
            }

            foreach (var key in _active.Keys.Where(k => !found.ContainsKey(k)).ToList())
            {
                _outbox.Enqueue(new ModelConditionChange(_active[key], false, now));
                _active.Remove(key);
            }
            foreach (var (key, condition) in found)
            {
                if (!_active.ContainsKey(key))
                    _outbox.Enqueue(new ModelConditionChange(condition, true, now));
                _active[key] = condition; // a standing condition refreshes its message silently
            }
        }
        DrainOutbox();
    }

    private static void Add(Dictionary<string, ModelCondition> found, ModelWarningKind kind,
        Target? target, string message)
    {
        var key = ModelCondition.KeyFor(kind, target?.Id);
        found[key] = new ModelCondition(key, kind, target?.Id, target?.Name, message);
    }

    /// <summary>"tcp/80" for a TCP probe, "ping" for ICMP echo.</summary>
    private static string ProbeText(Target target)
    {
        ProbeSpec.TryParse(target.Probe, out var probe);
        return probe.Kind == ProbeKind.Tcp ? $"tcp/{probe.Port}" : "ping";
    }

    /// <summary>Same discipline as VerdictCoordinator: TryEnter, so a thread
    /// that finds another draining leaves its items to that thread.</summary>
    private void DrainOutbox()
    {
        while (!_outbox.IsEmpty)
        {
            if (!_publishLock.TryEnter())
                return;
            try
            {
                while (_outbox.TryDequeue(out var change))
                {
                    _log.LogDebug("Model condition {Key} {State}", change.Condition.Key,
                        change.Active ? "raised" : "cleared");
                    _changes.OnNext(change);
                }
            }
            finally
            {
                _publishLock.Exit();
            }
        }
    }

    public void Dispose()
    {
        List<IDisposable> toDispose;
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
            toDispose = new List<IDisposable>(_subscriptions);
            _subscriptions.Clear();
        }
        foreach (var s in toDispose)
            s.Dispose();
        _changes.OnCompleted();
        GC.SuppressFinalize(this);
    }
}
```

`ProbeSpec` and `ProbeKind` live in `Core/ConCore/ProbeSpec.cs`. If their namespace is not `ConCore`, fix the `using`. `TryParse(null)` leaves the default (ICMP), which is how `WatcherEngine.StartController` uses it.

- [ ] **Step 4: Run the coordinator tests to verify they pass**

Run: `dotnet test Tests/ConEngine.Tests -c Debug --filter "FullyQualifiedName~ModelCheckCoordinatorTests"`
Expected: all PASS.

If `The_2026_10_08_incident…` sees a second change before the clear, a descendant changed state during `Feed("Inside", 20, false)`. It shouldn't: each `Feed` touches one monitor. Diagnose before you loosen the assertion.

- [ ] **Step 5: Write the failing `UsingIspFallback` and engine-wiring tests**

In `VerdictCoordinatorTests.cs`, add to the end of `When_every_anchor_is_isp_owned_they_count_as_a_fallback` (it builds its own coordinator, so use that local's name):

```csharp
        Assert.True(verdict.UsingIspFallback);
```

And add a new fact using the class fixture (Modem is tagged Comcast; the anchors are untagged):

```csharp
    [Fact]
    public void Untagged_anchors_are_not_the_isp_fallback()
    {
        Assert.False(_verdict.UsingIspFallback);
    }
```

In `WatcherEngineTests.cs` (fixture: Router → Google, Cloudflare; the engine is started in `InitializeAsync` with real time and `_factory` as the fake pings; helpers `Eventually` and `State`), add `using ConEngine.Topology;` and:

```csharp
    [Fact]
    public async Task Model_conditions_flow_through_the_engine_stream()
    {
        var changes = new List<ModelConditionChange>();
        _engine.ModelConditionSource.Subscribe(c => { lock (changes) changes.Add(c); });
        await Eventually(() => State("Google") == HostState.Responding);

        // Router fails its probe while the targets behind it keep answering.
        _factory.SetUp(RouterIp, false);
        await Eventually(() => _engine.ModelConditions.Any(c => c.Kind == ModelWarningKind.HopBypassed));
        lock (changes)
            Assert.Contains(changes, c => c.Active && c.Condition.TargetName == "Router");
        Assert.NotNull(_engine.ModelCheck);

        _factory.SetUp(RouterIp, true);
        await Eventually(() => _engine.ModelConditions.Count == 0);
    }

    [Fact]
    public void Model_conditions_are_empty_not_null_before_start()
    {
        var engine = new WatcherEngine(TimeProvider.System, new FakePingTransceiverFactory(),
            new ConInfrastructure.DomainNameResolverFactory(), NullLoggerFactory.Instance,
            Microsoft.Extensions.Options.Options.Create(Options()));
        Assert.Empty(engine.ModelConditions);
        Assert.Null(engine.ModelCheck);
        Assert.NotNull(engine.ModelConditionSource);
    }
```

- [ ] **Step 6: Run them to verify they fail**

Run: `dotnet test Tests/ConEngine.Tests -c Debug --filter "FullyQualifiedName~VerdictCoordinatorTests|FullyQualifiedName~WatcherEngineTests"`
Expected: build FAILS on `UsingIspFallback` / `ModelConditionSource`.

- [ ] **Step 7: Implement `UsingIspFallback` and the engine wiring**

In `VerdictCoordinator`, add the property next to `CurrentOperator`:

```csharp
    /// <summary>Every anchor is the ISP's, so they all count as a weaker
    /// fallback (spec §4.1 decision (a)): the UnprovableUp model warning.</summary>
    public bool UsingIspFallback { get; }
```

and in the constructor, replace the fallback block:

```csharp
        if (_counted.Count == 0)
        {
            UsingIspFallback = _anchors.Count > 0;
            _counted = _anchors.ToList();
        }
```

In `WatcherEngine`:

```csharp
    private readonly Subject<ModelConditionChange> _modelSubject = new();

    /// <summary>Model-warning conditions (admin notes), or null before start.</summary>
    public ModelCheckCoordinator? ModelCheck { get; private set; }

    /// <summary>Model-warning raise/clear changes. Exists from construction, so
    /// observers can attach before StartAsync.</summary>
    public IObservable<ModelConditionChange> ModelConditionSource => _modelSubject;

    /// <summary>The conditions holding now; empty before start.</summary>
    public IReadOnlyList<ModelCondition> ModelConditions => ModelCheck?.Active ?? [];
```

In `StartAsync`, directly after the `if (anchorPool is not null) { … Verdict.Start(); }` block and before the suspicion layer:

```csharp
        // Model checks: admin-facing notes where the map disagrees with the
        // probes. They never feed the verdict.
        ModelCheck = new ModelCheckCoordinator(_graph, _monitors, Verdict?.UsingIspFallback == true,
            _timeProvider, _loggerFactory.CreateLogger<ModelCheckCoordinator>());
        _subscriptions.Add(ModelCheck.ChangeSource.Subscribe(_modelSubject.OnNext));
        ModelCheck.Start();
```

In `StopAsync`, after `Verdict?.Dispose();`, add `ModelCheck?.Dispose();`.

- [ ] **Step 8: Run the whole engine suite**

Run: `dotnet test Tests/ConEngine.Tests -c Debug`
Expected: all PASS.

- [ ] **Step 9: Commit**

```bash
git add Core/ConEngine/Topology/ModelCheckCoordinator.cs Core/ConEngine/Topology/VerdictCoordinator.cs Core/ConEngine/WatcherEngine.cs Tests/ConEngine.Tests/ModelCheckCoordinatorTests.cs Tests/ConEngine.Tests/VerdictCoordinatorTests.cs Tests/ConEngine.Tests/WatcherEngineTests.cs
git commit -m "feat(engine): model checks raise admin notes when the map disagrees with the probes"
```

---

### Task 2: Persistence — ModelWarningLedger, recorder, table, retention

**Files:**
- Modify: `Libraries/ConPersistence/Entities/DataRows.cs`, `Libraries/ConPersistence/ConWatchDbContext.cs`, `Libraries/ConPersistence/Retention/RetentionPass.cs`
- Create: `Libraries/ConPersistence/Warnings/ModelWarningLedger.cs`, `Libraries/ConPersistence/Warnings/ModelWarningRecorder.cs`, migration `Libraries/ConPersistence/Migrations/*_AddModelWarnings.cs` (generated)
- Test: `Tests/ConPersistence.Tests/ModelWarningLedgerTests.cs` (new), `Tests/ConPersistence.Tests/RetentionTests.cs`

**Interfaces:**
- Consumes: `ModelCondition`, `ModelConditionChange`, `ModelWarningKind`, `ModelCheckCoordinator.NeverAnsweredAfter`, `WatcherEngine.ModelConditionSource`, `WatcherEngine.ModelConditions` (Task 1).
- Produces (namespace `ConPersistence.Warnings`):
  - `sealed record ModelWarningInstance(string Key, ModelWarningKind Kind, Guid? TargetId, string? TargetName, string Message, DateTimeOffset SinceUtc, bool Acknowledged)`
  - `enum ModelWarningOpKind { Opened, Closed, Acknowledged }`
  - `sealed record ModelWarningOp(ModelWarningOpKind Op, ModelWarningInstance Warning, DateTimeOffset AtUtc)`
  - `class ModelWarningRecorder : ChannelRecorder<ModelWarningOp>`
  - `class ModelWarningLedger : IEngineObserver, IHostedService` with `ResyncGrace`, `IReadOnlyList<ModelWarningInstance> Current`, `bool Acknowledge(string key)`, `Task LoadAsync(CancellationToken)`, `void Attach(WatcherEngine)`, `void Attach(IObservable<ModelConditionChange>, Func<IReadOnlyList<ModelCondition>>)`, `void Detach()`
  - `ConWatchDbContext.ModelWarnings` (`DbSet<ModelWarningRow>`)

- [ ] **Step 1: Add the entity and DbSet**

Append to `DataRows.cs`:

```csharp
/// <summary>One model-warning instance (an admin note): opened when its
/// condition starts holding, closed when it stops. Ended rows are pruned with
/// transitions (Retention.TransitionMonths).</summary>
public class ModelWarningRow
{
    public long Id { get; set; }
    public string Key { get; set; } = "";
    public string Kind { get; set; } = "";
    public Guid? TargetId { get; set; }
    public string? TargetName { get; set; }
    public string Message { get; set; } = "";
    public DateTime StartUtc { get; set; }
    public DateTime? EndUtc { get; set; }
    public DateTime? AcknowledgedUtc { get; set; }
}
```

In `ConWatchDbContext`, add `public DbSet<ModelWarningRow> ModelWarnings => Set<ModelWarningRow>();` after `Glitches`. In `OnModelCreating`, next to the `GlitchRow` block:

```csharp
        modelBuilder.Entity<ModelWarningRow>(e =>
        {
            e.HasIndex(w => w.StartUtc);
            e.HasIndex(w => new { w.Key, w.EndUtc });
        });
```

- [ ] **Step 2: Generate the migration**

Run:
```bash
dotnet tool restore
dotnet ef migrations add AddModelWarnings --project Libraries/ConPersistence
```
Use the project-only form: `--startup-project` fails because the EF Design reference is `PrivateAssets=all`. Open the generated file. It must contain one `CreateTable("ModelWarnings")` and two `CreateIndex` calls, and nothing touching other tables. If it touches other tables, the model snapshot was stale: stop and investigate.

Run: `dotnet test Tests/ConPersistence.Tests -c Debug --filter "FullyQualifiedName~MigrationsTests"`
Expected: PASS (no pending model changes).

- [ ] **Step 3: Write the failing ledger tests**

Create `Tests/ConPersistence.Tests/ModelWarningLedgerTests.cs`:

```csharp
using System.Reactive.Subjects;
using ConEngine.Topology;
using ConPersistence.Warnings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace ConPersistence.Tests;

public class ModelWarningLedgerTests : IDisposable
{
    private readonly TestDbFactory _factory = new();
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-10-09T12:00:00Z"));
    private readonly ModelWarningRecorder _recorder;
    private ModelWarningLedger _ledger;
    private Subject<ModelConditionChange> _source = new();
    private List<ModelCondition> _active = new();

    private static readonly Guid Inside = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly ModelCondition Bypassed = new(
        ModelCondition.KeyFor(ModelWarningKind.HopBypassed, Inside), ModelWarningKind.HopBypassed,
        Inside, "Inside", "Inside isn't answering its probe (tcp/80), but External beyond it is");

    public ModelWarningLedgerTests()
    {
        _recorder = new ModelWarningRecorder(_factory, NullLogger<ModelWarningRecorder>.Instance);
        _ledger = NewLedger();
        _ledger.Attach(_source, () => _active);
    }

    private ModelWarningLedger NewLedger() =>
        new(_recorder, _factory, _time, NullLogger<ModelWarningLedger>.Instance);

    public void Dispose()
    {
        _recorder.Dispose();
        _factory.Dispose();
    }

    private void Raise(ModelCondition c) => _source.OnNext(new(c, true, _time.GetUtcNow()));
    private void Clear(ModelCondition c) => _source.OnNext(new(c, false, _time.GetUtcNow()));

    private async Task<List<Entities.ModelWarningRow>> Rows()
    {
        await _recorder.FlushAsync();
        await using var db = _factory.CreateDbContext();
        return await db.ModelWarnings.OrderBy(r => r.Id).ToListAsync();
    }

    [Fact]
    public async Task A_raise_opens_one_instance_and_one_row()
    {
        Raise(Bypassed);

        var w = Assert.Single(_ledger.Current);
        Assert.Equal(Bypassed.Key, w.Key);
        Assert.Equal(_time.GetUtcNow(), w.SinceUtc);
        Assert.False(w.Acknowledged);
        var row = Assert.Single(await Rows());
        Assert.Equal("HopBypassed", row.Kind);
        Assert.Equal(Inside, row.TargetId);
        Assert.Null(row.EndUtc);
    }

    [Fact]
    public async Task A_clear_closes_the_instance_and_its_row()
    {
        Raise(Bypassed);
        _time.Advance(TimeSpan.FromSeconds(27));
        Clear(Bypassed);

        Assert.Empty(_ledger.Current);
        var row = Assert.Single(await Rows());
        Assert.Equal(_time.GetUtcNow().UtcDateTime, row.EndUtc);
    }

    [Fact]
    public async Task Acknowledge_marks_the_instance_and_row_and_unknown_keys_are_refused()
    {
        Raise(Bypassed);
        Assert.True(_ledger.Acknowledge(Bypassed.Key));
        Assert.False(_ledger.Acknowledge("hop-bypassed:nope"));

        Assert.True(Assert.Single(_ledger.Current).Acknowledged);
        Assert.NotNull(Assert.Single(await Rows()).AcknowledgedUtc);
    }

    [Fact]
    public async Task A_recurrence_after_a_clear_is_a_new_unacknowledged_instance()
    {
        Raise(Bypassed);
        _ledger.Acknowledge(Bypassed.Key);
        Clear(Bypassed);
        _time.Advance(TimeSpan.FromMinutes(1));
        Raise(Bypassed);

        Assert.False(Assert.Single(_ledger.Current).Acknowledged);
        Assert.Equal(2, (await Rows()).Count);
    }

    [Fact]
    public async Task An_engine_rebuild_that_re_raises_continues_the_same_instance()
    {
        Raise(Bypassed);
        var since = _time.GetUtcNow();
        _ledger.Acknowledge(Bypassed.Key);

        _ledger.Detach();
        _source = new Subject<ModelConditionChange>();
        _time.Advance(TimeSpan.FromSeconds(3));
        _ledger.Attach(_source, () => _active);
        Raise(Bypassed); // the new engine's first evaluation

        var w = Assert.Single(_ledger.Current);
        Assert.Equal(since, w.SinceUtc);
        Assert.True(w.Acknowledged);
        var row = Assert.Single(await Rows());
        Assert.Null(row.EndUtc);
    }

    [Fact]
    public async Task Conditions_already_active_at_attach_are_adopted()
    {
        _ledger.Detach();
        _active = [Bypassed];
        _ledger.Attach(new Subject<ModelConditionChange>(), () => _active);

        Assert.Single(_ledger.Current);
        Assert.Single(await Rows());
    }

    [Fact]
    public async Task A_carried_note_the_new_engine_never_re_raises_closes_after_the_grace()
    {
        Raise(Bypassed);
        _ledger.Detach();
        _ledger.Attach(new Subject<ModelConditionChange>(), () => []);

        _time.Advance(ModelWarningLedger.ResyncGrace - TimeSpan.FromSeconds(1));
        Assert.Single(_ledger.Current); // still within the grace

        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Empty(_ledger.Current);
        Assert.NotNull(Assert.Single(await Rows()).EndUtc);
    }

    [Fact]
    public async Task A_stale_grace_timer_from_an_earlier_attach_closes_nothing()
    {
        Raise(Bypassed);
        _ledger.Detach();
        _ledger.Attach(new Subject<ModelConditionChange>(), () => []);
        _time.Advance(ModelWarningLedger.ResyncGrace / 2);
        _ledger.Detach();
        var source = new Subject<ModelConditionChange>();
        _ledger.Attach(source, () => []);
        source.OnNext(new(Bypassed, true, _time.GetUtcNow()));

        _time.Advance(ModelWarningLedger.ResyncGrace); // both timers' deadlines pass
        Assert.Single(_ledger.Current);
        Assert.Null(Assert.Single(await Rows()).EndUtc);
    }

    [Fact]
    public async Task A_process_restart_loads_open_rows_with_their_acknowledgement()
    {
        Raise(Bypassed);
        _ledger.Acknowledge(Bypassed.Key);
        var since = _time.GetUtcNow();
        await _recorder.FlushAsync();

        var restarted = NewLedger();
        await restarted.LoadAsync();
        var w = Assert.Single(restarted.Current);
        Assert.Equal(since, w.SinceUtc);
        Assert.True(w.Acknowledged);
        Assert.Equal(ModelWarningKind.HopBypassed, w.Kind);

        var source = new Subject<ModelConditionChange>();
        restarted.Attach(source, () => []);
        source.OnNext(new(Bypassed, true, _time.GetUtcNow()));
        Assert.Single(await Rows()); // continued, not reopened
    }

    [Fact]
    public async Task Rows_deleted_under_an_open_instance_do_not_break_the_close()
    {
        Raise(Bypassed);
        await _recorder.FlushAsync();
        await using (var db = _factory.CreateDbContext())
            await db.ModelWarnings.ExecuteDeleteAsync(); // clear-history ran

        Clear(Bypassed);
        Assert.Empty(await Rows());
        Assert.Empty(_ledger.Current);
    }
}
```

- [ ] **Step 4: Run them to verify they fail**

Run: `dotnet test Tests/ConPersistence.Tests -c Debug --filter "FullyQualifiedName~ModelWarningLedgerTests"`
Expected: build FAILS (`ConPersistence.Warnings` does not exist).

If the test project lacks `Microsoft.Extensions.TimeProvider.Testing`, add the same `PackageReference` version `Tests/ConEngine.Tests/ConEngine.Tests.csproj` uses.

- [ ] **Step 5: Implement the recorder**

Create `Libraries/ConPersistence/Warnings/ModelWarningRecorder.cs`:

```csharp
using ConEngine;
using ConEngine.Topology;
using ConPersistence.Entities;
using ConPersistence.Recording;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ConPersistence.Warnings;

/// <summary>A model-warning instance as the ledger holds it.</summary>
public sealed record ModelWarningInstance(string Key, ModelWarningKind Kind, Guid? TargetId,
    string? TargetName, string Message, DateTimeOffset SinceUtc, bool Acknowledged);

public enum ModelWarningOpKind { Opened, Closed, Acknowledged }

/// <summary>One lifecycle step of an instance, in ledger order.</summary>
public sealed record ModelWarningOp(ModelWarningOpKind Op, ModelWarningInstance Warning, DateTimeOffset AtUtc);

/// <summary>
/// Persists the ledger's lifecycle ops (see <see cref="ChannelRecorder{TEvent}"/>
/// for the mechanics). Fed by <see cref="ModelWarningLedger"/>, never attached
/// to an engine directly: the ledger decides what is a new instance.
/// </summary>
public class ModelWarningRecorder(IDbContextFactory<ConWatchDbContext> dbFactory,
    ILogger<ModelWarningRecorder> log, TimeSpan? retryDelay = null)
    : ChannelRecorder<ModelWarningOp>(log, retryDelay: retryDelay)
{
    protected override string EventNoun => "model warnings";

    protected override IObservable<ModelWarningOp> SourceOf(WatcherEngine engine) =>
        throw new NotSupportedException("Fed by ModelWarningLedger, not by the engine.");

    protected override async Task WriteBatchAsync(IReadOnlyList<ModelWarningOp> batch, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        // The open row may still be unsaved from this same batch.
        var openedThisBatch = new Dictionary<string, ModelWarningRow>();
        foreach (var op in batch)
        {
            var w = op.Warning;
            if (op.Op == ModelWarningOpKind.Opened)
            {
                var row = new ModelWarningRow
                {
                    Key = w.Key,
                    Kind = w.Kind.ToString(),
                    TargetId = w.TargetId,
                    TargetName = w.TargetName,
                    Message = w.Message,
                    StartUtc = w.SinceUtc.UtcDateTime,
                };
                db.ModelWarnings.Add(row);
                openedThisBatch[w.Key] = row;
                continue;
            }

            var open = openedThisBatch.GetValueOrDefault(w.Key)
                ?? await db.ModelWarnings
                    .Where(r => r.Key == w.Key && r.EndUtc == null)
                    .OrderByDescending(r => r.StartUtc)
                    .FirstOrDefaultAsync(ct).ConfigureAwait(false);
            if (open is null)
                continue; // history was cleared under an open instance
            if (op.Op == ModelWarningOpKind.Closed)
            {
                open.EndUtc = op.AtUtc.UtcDateTime;
                openedThisBatch.Remove(w.Key);
            }
            else
            {
                open.AcknowledgedUtc = op.AtUtc.UtcDateTime;
            }
        }
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
```

- [ ] **Step 6: Implement the ledger**

Create `Libraries/ConPersistence/Warnings/ModelWarningLedger.cs`:

```csharp
using System.Reactive.Subjects;
using ConEngine;
using ConEngine.Topology;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ConPersistence.Warnings;

/// <summary>
/// The administrator's notes (spec 2026-10-09 §4.6) as INSTANCES: the engine
/// says which conditions hold; the ledger decides what is one note. It
/// outlives engines, so a rebuild (any config edit) continues a note rather
/// than ending and reopening it: a carried note the new engine re-raises
/// keeps its start and its acknowledgement; one it never re-raises within
/// <see cref="ResyncGrace"/> is closed. Acknowledging hides an instance until
/// it clears and recurs.
/// </summary>
public class ModelWarningLedger : IEngineObserver, IHostedService
{
    /// <summary>Long enough for a new engine to re-raise every kind (the slowest
    /// is NeverAnswered).</summary>
    public static readonly TimeSpan ResyncGrace = ModelCheckCoordinator.NeverAnsweredAfter + TimeSpan.FromMinutes(1);

    private readonly IDbContextFactory<ConWatchDbContext> _dbFactory;
    private readonly TimeProvider _time;
    private readonly ILogger<ModelWarningLedger> _log;
    private readonly Lock _lock = new();
    private readonly Subject<ModelWarningOp> _ops = new();

    // Guarded by _lock.
    private readonly Dictionary<string, ModelWarningInstance> _instances = new();
    private readonly HashSet<string> _unconfirmed = new();
    private IDisposable? _subscription;
    private ITimer? _resync;
    private int _generation;

    public ModelWarningLedger(ModelWarningRecorder recorder, IDbContextFactory<ConWatchDbContext> dbFactory,
        TimeProvider time, ILogger<ModelWarningLedger> log)
    {
        _dbFactory = dbFactory;
        _time = time;
        _log = log;
        recorder.Attach(_ops);
    }

    /// <summary>Active instances, oldest first.</summary>
    public IReadOnlyList<ModelWarningInstance> Current
    {
        get { lock (_lock) { return _instances.Values.OrderBy(w => w.SinceUtc).ToList(); } }
    }

    public Task StartAsync(CancellationToken cancellationToken) => LoadAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Process start: open rows become carried (unconfirmed) instances.</summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var open = await db.ModelWarnings.Where(r => r.EndUtc == null)
            .OrderBy(r => r.StartUtc).ToListAsync(ct).ConfigureAwait(false);
        lock (_lock)
        {
            foreach (var r in open)
            {
                if (!Enum.TryParse<ModelWarningKind>(r.Kind, out var kind))
                    continue;
                _instances[r.Key] = new ModelWarningInstance(r.Key, kind, r.TargetId, r.TargetName, r.Message,
                    new DateTimeOffset(DateTime.SpecifyKind(r.StartUtc, DateTimeKind.Utc)),
                    r.AcknowledgedUtc is not null);
                _unconfirmed.Add(r.Key);
            }
        }
    }

    public void Attach(WatcherEngine engine) =>
        Attach(engine.ModelConditionSource, () => engine.ModelConditions);

    /// <summary>Follows one engine's conditions. Everything held so far is carried:
    /// it continues if re-raised, and closes at the grace otherwise.</summary>
    public void Attach(IObservable<ModelConditionChange> source, Func<IReadOnlyList<ModelCondition>> active)
    {
        lock (_lock)
        {
            DetachCore();
            _unconfirmed.UnionWith(_instances.Keys);
            var generation = _generation;
            _subscription = source.Subscribe(OnChange);
            var now = _time.GetUtcNow();
            foreach (var condition in active())
                Raise(condition, now);
            _resync = _time.CreateTimer(_ => Resync(generation), null, ResyncGrace, Timeout.InfiniteTimeSpan);
        }
    }

    public void Detach()
    {
        lock (_lock)
            DetachCore();
    }

    /// <summary>Under _lock.</summary>
    private void DetachCore()
    {
        _subscription?.Dispose();
        _subscription = null;
        _resync?.Dispose();
        _resync = null;
        _generation++; // any timer still in flight is now stale
    }

    /// <summary>Hides the instance until it clears and recurs. False when no
    /// such note is active.</summary>
    public bool Acknowledge(string key)
    {
        lock (_lock)
        {
            if (!_instances.TryGetValue(key, out var w))
                return false;
            if (w.Acknowledged)
                return true;
            var acknowledged = w with { Acknowledged = true };
            _instances[key] = acknowledged;
            _ops.OnNext(new ModelWarningOp(ModelWarningOpKind.Acknowledged, acknowledged, _time.GetUtcNow()));
            _log.LogInformation("Model warning acknowledged: {Key}", key);
            return true;
        }
    }

    private void OnChange(ModelConditionChange change)
    {
        lock (_lock)
        {
            if (change.Active)
                Raise(change.Condition, change.AtUtc);
            else
                Close(change.Condition.Key, change.AtUtc);
        }
    }

    /// <summary>Under _lock.</summary>
    private void Raise(ModelCondition c, DateTimeOffset at)
    {
        _unconfirmed.Remove(c.Key);
        if (_instances.TryGetValue(c.Key, out var existing))
        {
            _instances[c.Key] = existing with { Message = c.Message }; // continuation
            return;
        }
        var w = new ModelWarningInstance(c.Key, c.Kind, c.TargetId, c.TargetName, c.Message, at, false);
        _instances[c.Key] = w;
        _ops.OnNext(new ModelWarningOp(ModelWarningOpKind.Opened, w, at));
        _log.LogInformation("Model warning raised: {Message}", c.Message);
    }

    /// <summary>Under _lock.</summary>
    private void Close(string key, DateTimeOffset at)
    {
        _unconfirmed.Remove(key);
        if (!_instances.Remove(key, out var w))
            return;
        _ops.OnNext(new ModelWarningOp(ModelWarningOpKind.Closed, w, at));
        _log.LogInformation("Model warning cleared: {Key}", key);
    }

    /// <summary>Timer thread: the grace is over; close what the engine never
    /// re-raised. Never throws.</summary>
    private void Resync(int generation)
    {
        try
        {
            lock (_lock)
            {
                if (generation != _generation)
                    return;
                var now = _time.GetUtcNow();
                foreach (var key in _unconfirmed.ToList())
                    Close(key, now);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Model warning resync failed");
        }
    }
}
```

- [ ] **Step 7: Run the ledger tests to verify they pass**

Run: `dotnet test Tests/ConPersistence.Tests -c Debug --filter "FullyQualifiedName~ModelWarningLedgerTests"`
Expected: all PASS.

- [ ] **Step 8: Write the failing retention test**

Add to `RetentionTests.cs` (it already has `_factory`; reuse how its other tests build `RetentionPass` and `RetentionWindows`):

```csharp
    [Fact]
    public async Task Ended_model_warnings_age_out_with_transitions_and_open_ones_stay()
    {
        var now = DateTime.Parse("2026-10-09T00:00:00Z").ToUniversalTime();
        await using (var db = _factory.CreateDbContext())
        {
            db.ModelWarnings.AddRange(
                new ModelWarningRow { Key = "old-ended", Kind = "HopBypassed", Message = "m",
                    StartUtc = now.AddMonths(-30), EndUtc = now.AddMonths(-30).AddMinutes(1) },
                new ModelWarningRow { Key = "old-open", Kind = "UnprovableUp", Message = "m",
                    StartUtc = now.AddMonths(-30) },
                new ModelWarningRow { Key = "recent", Kind = "HopBypassed", Message = "m",
                    StartUtc = now.AddDays(-1), EndUtc = now.AddDays(-1).AddMinutes(1) });
            await db.SaveChangesAsync();
        }

        await using (var db = _factory.CreateDbContext())
            await new RetentionPass(NullLogger.Instance).RunAsync(db, now,
                new RetentionWindows(MinuteDays: 7, HourlyMonths: 12, TransitionMonths: 24), default);

        await using (var db = _factory.CreateDbContext())
            Assert.Equal(["old-open", "recent"], db.ModelWarnings.OrderBy(w => w.Key).Select(w => w.Key).ToArray());
    }
```

Match the `RetentionWindows` construction to how the existing tests create it (named or positional).

- [ ] **Step 9: Run it to verify it fails, then implement**

Run: `dotnet test Tests/ConPersistence.Tests -c Debug --filter "FullyQualifiedName~Ended_model_warnings"`
Expected: FAIL (`old-ended` still present).

In `RetentionPass.RunAsync`, after the transitions prune and before `return converted;`:

```csharp
        // Ended model warnings (admin notes) age out with transitions; an
        // open one stays however old it is.
        var prunedWarnings = await db.ModelWarnings
            .Where(w => w.EndUtc != null && w.EndUtc < transitionThreshold)
            .ExecuteDeleteAsync(ct);
        if (prunedWarnings > 0)
        {
            log.LogInformation("Pruned {Count} model warnings older than {Months} months",
                prunedWarnings, windows.TransitionMonths);
            converted += prunedWarnings;
        }
```

Run: `dotnet test Tests/ConPersistence.Tests -c Debug`
Expected: all PASS. `SnapshotShapeTests` is unaffected because warnings are not in snapshots (decision 6).

- [ ] **Step 10: Commit**

```bash
git add Libraries/ConPersistence Tests/ConPersistence.Tests
git commit -m "feat(persistence): model warning ledger keeps notes across engine rebuilds and stores their lifecycle"
```

---

### Task 3: Host — status `warnings[]`, acknowledge, MCP, metrics, admin, DI

**Files:**
- Modify: `Programs/ConWatch/StatusSnapshotBuilder.cs`, `Programs/ConWatch/StatusBroadcaster.cs` (ctor + L129), `Programs/ConWatch/Api/StatusEndpoints.cs`, `Programs/ConWatch/Mcp/ConWatchMcpTools.cs` (ctor + `GetCurrentStatus`), `Programs/ConWatch/Pages/Index.cshtml.cs`, `Programs/ConWatch/Api/MetricsEndpoint.cs`, `Programs/ConWatch/Api/AdminEndpoints.cs`, `Programs/ConWatch/ConWatchApp.cs`, `Programs/ConWatch/Startup/ConWatchServices.cs`
- Create: `Programs/ConWatch/Api/WarningEndpoints.cs`
- Test: `Tests/ConWatch.Tests/StatusShapeTests.cs`, `Tests/ConWatch.Tests/WarningApiTests.cs` (new), `Tests/ConWatch.Tests/McpEndpointTests.cs`, `Tests/ConWatch.Tests/MetricsEndpointTests.cs`, `Tests/ConWatch.Tests/AdminApiTests.cs`

**Interfaces:**
- Consumes: `ModelWarningLedger`, `ModelWarningRecorder`, `ModelWarningInstance` (Task 2).
- Produces:
  - `sealed record StatusWarning(string Key, string Kind, Guid? TargetId, string? TargetName, string Message, DateTimeOffset Since, bool Acknowledged)`
  - `StatusSnapshot(bool Enabled, StatusVerdict? Verdict, IReadOnlyList<StatusTarget> Targets, IReadOnlyList<StatusPool> Pools, IReadOnlyList<StatusWarning> Warnings)`
  - `StatusSnapshotBuilder.Build(WatcherEngine? engine, ModelWarningLedger warnings)`
  - JSON: `"warnings":[{"key","kind","targetId","targetName","message","since","acknowledged"}]`
  - `POST /api/v1/warnings/{key}/acknowledge`: 204, or 404 when no such note is active; 401 when locked
  - `WarningEndpoints.MapWarnings(this RouteGroupBuilder)`

- [ ] **Step 1: Write the failing tests**

In `StatusShapeTests.Live_status_shape_is_pinned`, change the top-level key assertion and add the warning shape. Warnings are empty in a healthy home, so pin the record's property names through a serialized instance:

```csharp
        Assert.Equal(new[] { "enabled", "pools", "targets", "verdict", "warnings" }, Keys(status));
        Assert.Equal(JsonValueKind.Array, status.GetProperty("warnings").ValueKind);
        var sample = JsonSerializer.SerializeToElement(
            new StatusWarning("k", "HopBypassed", Guid.Empty, "Router", "m", DateTimeOffset.UnixEpoch, false),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(new[] { "acknowledged", "key", "kind", "message", "since", "targetId", "targetName" },
            Keys(sample));
```

In the disabled-body test (around L56), change the pinned body to:

```csharp
{"enabled":false,"verdict":null,"targets":[],"pools":[],"warnings":[]}
```

Create `Tests/ConWatch.Tests/WarningApiTests.cs`:

```csharp
using System.Net;
using System.Text.Json;
using ConPersistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ConWatch.Tests;

public class WarningApiTests : IDisposable
{
    private static readonly IPAddress RouterIp = IPAddress.Parse("192.168.1.1");
    private readonly ConWatchFactory _factory = new();
    private readonly HttpClient _client;

    public WarningApiTests() => _client = _factory.CreateClient();

    public void Dispose() => _factory.Dispose();

    private async Task<JsonElement> StatusAsync() =>
        JsonSerializer.Deserialize<JsonElement>(await _client.GetStringAsync("/api/v1/status"));

    private static JsonElement[] Warnings(JsonElement status) =>
        status.GetProperty("warnings").EnumerateArray().ToArray();

    [Fact]
    public async Task A_bypassed_router_is_a_note_that_acknowledges_and_clears()
    {
        await ConfigApiTests.SeedHomeAsync(_client);
        await ConfigApiTests.Eventually(async () =>
            (await StatusAsync()).GetProperty("verdict") is { ValueKind: JsonValueKind.Object } v
            && v.GetProperty("state").GetString() == "Healthy");

        // Router stops answering its probe; Google and Cloudflare behind it still answer.
        _factory.Pings.SetUp(RouterIp, false);
        JsonElement note = default;
        await ConfigApiTests.Eventually(async () =>
        {
            var status = await StatusAsync();
            note = Warnings(status).FirstOrDefault(w => w.GetProperty("kind").GetString() == "HopBypassed");
            return note.ValueKind == JsonValueKind.Object;
        });
        Assert.Equal("Router", note.GetProperty("targetName").GetString());
        Assert.False(note.GetProperty("acknowledged").GetBoolean());
        Assert.Equal("Healthy", (await StatusAsync()).GetProperty("verdict").GetProperty("state").GetString());

        var key = note.GetProperty("key").GetString()!;
        var ack = await _client.PostAsync($"/api/v1/warnings/{Uri.EscapeDataString(key)}/acknowledge", null);
        Assert.Equal(HttpStatusCode.NoContent, ack.StatusCode);
        Assert.True(Warnings(await StatusAsync()).Single(w => w.GetProperty("key").GetString() == key)
            .GetProperty("acknowledged").GetBoolean());

        var missing = await _client.PostAsync("/api/v1/warnings/hop-bypassed%3Anope/acknowledge", null);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        var metrics = await _client.GetStringAsync("/metrics");
        Assert.Contains("conwatch_model_warnings 1", metrics);

        _factory.Pings.SetUp(RouterIp, true);
        await ConfigApiTests.Eventually(async () => Warnings(await StatusAsync()).Length == 0);
        await ConfigApiTests.Eventually(async () =>
        {
            await using var db = await _factory.Services
                .GetRequiredService<IDbContextFactory<ConWatchDbContext>>().CreateDbContextAsync();
            return await db.ModelWarnings.AnyAsync(w => w.Key == key && w.EndUtc != null && w.AcknowledgedUtc != null);
        });
    }
}
```

In `McpEndpointTests.Status_tool_reports_the_status_snapshot` (L125), add after the existing assertions on the parsed tool text (use the test's own variable name for the parsed JSON):

```csharp
        Assert.Equal(JsonValueKind.Array, parsed.GetProperty("warnings").ValueKind);
```

In `MetricsEndpointTests.Scrape_works_before_any_configuration`, add:

```csharp
        Assert.Contains("# TYPE conwatch_model_warnings gauge", body);
        Assert.Contains("conwatch_model_warnings 0", body);
```

In `AdminApiTests.Clear_history_removes_measurements_keeps_config`, seed one row before the clear and assert it is gone after (use the test's existing db-factory access pattern):

```csharp
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            db.ModelWarnings.Add(new ConPersistence.Entities.ModelWarningRow
                { Key = "unprovable-up", Kind = "UnprovableUp", Message = "m", StartUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
        // … existing clear-history call …
        await using (var db = await dbFactory.CreateDbContextAsync())
            Assert.Empty(db.ModelWarnings);
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test Tests/ConWatch.Tests -c Debug --filter "FullyQualifiedName~StatusShapeTests|FullyQualifiedName~WarningApiTests|FullyQualifiedName~McpEndpointTests|FullyQualifiedName~MetricsEndpointTests|FullyQualifiedName~AdminApiTests"`
Expected: build FAILS (`StatusWarning` does not exist).

- [ ] **Step 3: Snapshot shape and builder**

In `StatusSnapshotBuilder.cs`:

```csharp
public sealed record StatusSnapshot(bool Enabled, StatusVerdict? Verdict,
    IReadOnlyList<StatusTarget> Targets, IReadOnlyList<StatusPool> Pools,
    IReadOnlyList<StatusWarning> Warnings)
{
    /// <summary>Archive mode (monitoring disabled): no live states.</summary>
    public static StatusSnapshot Disabled { get; } = new(false, null, [], [], []);
}

/// <summary>An admin note (model warning, spec 2026-10-09 §4.6): where the map
/// disagrees with what the probes prove. Read-only here; never part of the verdict.</summary>
/// <param name="Kind">"HopBypassed", "NeverAnswered" or "UnprovableUp".</param>
public sealed record StatusWarning(string Key, string Kind, Guid? TargetId, string? TargetName,
    string Message, DateTimeOffset Since, bool Acknowledged);
```

Change `Build`:

```csharp
    public static StatusSnapshot? Build(WatcherEngine? engine, ModelWarningLedger warnings)
    {
        … existing body …
        var notes = warnings.Current.Select(w => new StatusWarning(w.Key, w.Kind.ToString(),
                w.TargetId, w.TargetName, w.Message, w.SinceUtc, w.Acknowledged))
            .ToList();

        return new StatusSnapshot(true, verdict, targets, pools, notes);
    }
```

Add `using ConPersistence.Warnings;`.

- [ ] **Step 4: Thread the ledger to every caller**

Each caller gains a `ModelWarningLedger` and passes it:

- `Api/StatusEndpoints.cs`: `api.MapGet("/status", (WatcherHostService host, ModelWarningLedger warnings) => { if (StatusSnapshotBuilder.Build(host.Engine, warnings) is { } snapshot) … })`.
- `StatusBroadcaster.cs`: add `ModelWarningLedger warnings` to the primary constructor; L129 becomes `StatusSnapshotBuilder.Build(engine, warnings)`.
- `Mcp/ConWatchMcpTools.cs`: add `ModelWarningLedger warnings` to the primary constructor. In `GetCurrentStatus`, build with it and add `warnings = snapshot?.Warnings` after `targets = snapshot?.Targets`. Append to the tool's `[Description]`: `" Includes warnings: admin notes where ConnectionWatcher's map disagrees with what the probes prove; they never affect the verdict."`
- `Pages/Index.cshtml.cs`: add `ConPersistence.Warnings.ModelWarningLedger warnings` to the constructor; `Snapshot = StatusSnapshotBuilder.Build(engine, warnings)`.

Then: `grep -rn "StatusSnapshotBuilder.Build(" Programs Tests` must show no single-argument call left.

- [ ] **Step 5: Acknowledge endpoint**

Create `Programs/ConWatch/Api/WarningEndpoints.cs`:

```csharp
using ConPersistence.Warnings;

namespace ConWatch.Api;

/// <summary>Admin notes (model warnings). Reading is part of /status; only
/// acknowledging lives here, and it is admin-only.</summary>
public static class WarningEndpoints
{
    public static void MapWarnings(this RouteGroupBuilder api)
    {
        // Hides that instance until it clears and recurs (spec §9.4); the
        // check itself stays on.
        api.MapPost("/warnings/{key}/acknowledge", (string key, ModelWarningLedger warnings) =>
            warnings.Acknowledge(key)
                ? Results.NoContent()
                : ApiResults.Error(StatusCodes.Status404NotFound, null, $"No active note '{key}'."));
    }
}
```

In `ConWatchApp.cs`, add `locked.MapWarnings();` next to the other `locked.Map…()` calls (L100–106). `ApiSurfaceLockTests` needs no change: `/api/v1/warnings` matches no open prefix, so it must 401 when locked, and it does.

- [ ] **Step 6: Metrics gauge**

In `MetricsEndpoint.cs`, add `ModelWarningLedger warnings` to the `MapGet` handler's parameters and to `Render`:

```csharp
    internal static string Render(WatcherEngine? engine, MetricsCounters counters, ModelWarningLedger warnings,
        bool enabled, DateTimeOffset now)
```

Directly after the `conwatch_engine_enabled` series:

```csharp
        // Admin notes only (spec §4.6, D3): never part of up/verdict series.
        Family(sb, "conwatch_model_warnings", "gauge",
            "Active ConnectionWatcher notes (model warnings), acknowledged or not.");
        sb.Append("conwatch_model_warnings ").Append(warnings.Current.Count).Append('\n');
```

- [ ] **Step 7: Admin clear/reset/stats**

In `AdminEndpoints.cs`:
- `/admin/clear-history`: add `await db.ModelWarnings.ExecuteDeleteAsync();` inside the transaction.
- `/admin/factory-reset`: add the same line inside the transaction.
- Stats: add `ModelWarnings = await db.ModelWarnings.CountAsync(),` after `Rollups`.

If an admin stats test pins the table keys, add `modelWarnings` there.

- [ ] **Step 8: DI, hosted order, observer order**

In `ConWatchServices.AddConWatchPersistence`, after `services.AddSingleton<RollupCollector>();`:

```csharp
        services.AddSingleton<ConPersistence.Warnings.ModelWarningRecorder>();
        services.AddSingleton<ConPersistence.Warnings.ModelWarningLedger>();
```

In `AddConWatchHostedServices`, after the `RollupCollector` hosted line:

```csharp
        services.AddHostedService(sp => sp.GetRequiredService<ConPersistence.Warnings.ModelWarningRecorder>());
        // Loads open notes before the engine starts (hosted services start in order).
        services.AddHostedService(sp => sp.GetRequiredService<ConPersistence.Warnings.ModelWarningLedger>());
```

In the observer list, after the `RollupCollector` observer:

```csharp
        services.AddSingleton<ConEngine.IEngineObserver>(sp => sp.GetRequiredService<ConPersistence.Warnings.ModelWarningLedger>());
```

Do **not** register `ModelWarningRecorder` as an `IEngineObserver`: its `SourceOf` throws by design.

- [ ] **Step 9: Run the host tests**

Run: `dotnet test Tests/ConWatch.Tests -c Debug --filter "FullyQualifiedName~StatusShapeTests|FullyQualifiedName~WarningApiTests|FullyQualifiedName~McpEndpointTests|FullyQualifiedName~MetricsEndpointTests|FullyQualifiedName~AdminApiTests|FullyQualifiedName~ApiSurfaceLockTests|FullyQualifiedName~SignalRTests"`
Expected: all PASS.

Then the full suite: `dotnet test Tests/ConWatch.Tests -c Debug` (rerun once for the known flakes).

- [ ] **Step 10: Commit**

```bash
git add Programs/ConWatch Tests/ConWatch.Tests
git commit -m "feat(api): status, MCP and metrics carry admin notes; locked acknowledge endpoint"
```

---

### Task 4: Config advice — `anchors-all-isp` health warning and wizard text

**Files:**
- Modify: `Libraries/ConPersistence/Config/ConfigHealth.cs`, `Programs/ConWatch/Pages/Setup.cshtml` (L82–85)
- Test: `Tests/ConPersistence.Tests/ConfigHealthTests.cs`

**Interfaces:**
- Produces: `HealthWarning("anchors-all-isp", …)` from `ConfigHealth.EvaluateAsync()`. It shows on the Targets, Pools and Profiles pages through `_HealthBanner`, and in the dashboard's "N configuration warnings" hint.

- [ ] **Step 1: Write the failing tests**

Add to `ConfigHealthTests.cs` (the fixture: Router → Modem → Google, Cloudflare; pool "Internet" = Google, Cloudflare; Three_anchors_is_clean shows that upserting by name works):

```csharp
    [Fact]
    public async Task Every_anchor_tagged_as_the_isp_warns_unprovable()
    {
        await _service.UpsertTargetAsync(new TargetDto { Name = "Modem", Address = "192.168.100.1", Parent = "Router", Profile = "Internal", Operator = "Comcast" });
        await _service.UpsertTargetAsync(new TargetDto { Name = "Google", Address = "8.8.8.8", Parent = "Modem", Operator = " comcast" });
        await _service.UpsertTargetAsync(new TargetDto { Name = "Cloudflare", Address = "1.1.1.1", Parent = "Modem", Profile = "Slow", Operator = "Comcast" });

        var warning = (await _health.EvaluateAsync()).Single(w => w.Code == "anchors-all-isp");
        Assert.Contains("Tag your ISP on a path hop (or add an anchor outside your ISP) so ConnectionWatcher can prove your internet is up.",
            warning.Message);
    }

    [Fact]
    public async Task One_anchor_outside_the_isp_is_enough()
    {
        await _service.UpsertTargetAsync(new TargetDto { Name = "Modem", Address = "192.168.100.1", Parent = "Router", Profile = "Internal", Operator = "Comcast" });
        await _service.UpsertTargetAsync(new TargetDto { Name = "Google", Address = "8.8.8.8", Parent = "Modem", Operator = "Comcast" });
        Assert.DoesNotContain("anchors-all-isp", await Codes()); // Cloudflare is untagged
    }

    [Fact]
    public async Task No_isp_tag_on_the_path_never_warns_unprovable()
    {
        await _service.UpsertTargetAsync(new TargetDto { Name = "Google", Address = "8.8.8.8", Parent = "Modem", Operator = "Comcast" });
        await _service.UpsertTargetAsync(new TargetDto { Name = "Cloudflare", Address = "1.1.1.1", Parent = "Modem", Profile = "Slow", Operator = "Comcast" });
        Assert.DoesNotContain("anchors-all-isp", await Codes()); // the path itself is untagged
    }
```

If `ConfigService` stores the operator trimmed (`ConfigMapping.NormalizeOperator`), the `" comcast"` case still exercises the case-insensitive match.

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test Tests/ConPersistence.Tests -c Debug --filter "FullyQualifiedName~ConfigHealthTests"`
Expected: the first new test FAILS (`Sequence contains no matching element`); the other two pass.

- [ ] **Step 3: Implement**

In `ConfigHealth.EvaluateAsync`, before the pool-parent-drift check:

```csharp
        if (await AllAnchorsIspOwnedAsync(db, pool) is { } unprovable)
            warnings.Add(unprovable);
```

Add the method (same rule as `VerdictCoordinator`: the ISP is any operator tag on a non-anchor ancestor of an anchor; trimmed, case-insensitive):

```csharp
    /// <summary>Every anchor carries the ISP's operator tag: the verdict then
    /// falls back to the ISP's own servers and cannot prove the internet
    /// beyond the ISP is up (spec 2026-10-09 §4.1, the UnprovableUp note).</summary>
    private static async Task<HealthWarning?> AllAnchorsIspOwnedAsync(ConWatchDbContext db,
        Entities.PoolRow pool)
    {
        static string? Normalize(string? op) =>
            op?.Trim() is { Length: > 0 } t ? t.ToLowerInvariant() : null;

        var targets = await db.Targets.ToDictionaryAsync(t => t.Id);
        var memberIds = pool.Members.Select(m => m.TargetId).Where(targets.ContainsKey).ToHashSet();
        if (memberIds.Count == 0)
            return null;

        var isp = new HashSet<string>();
        foreach (var id in memberIds)
        {
            var parentId = targets[id].ParentId;
            for (var depth = 0; depth < 20 && parentId is { } pid && targets.TryGetValue(pid, out var hop); depth++)
            {
                if (!memberIds.Contains(pid) && Normalize(hop.Operator) is { } op)
                    isp.Add(op);
                parentId = hop.ParentId;
            }
        }
        if (isp.Count == 0 || !memberIds.All(id => Normalize(targets[id].Operator) is { } op && isp.Contains(op)))
            return null;

        return new HealthWarning("anchors-all-isp",
            $"Every anchor in pool '{pool.Name}' is tagged as your ISP's, so ConnectionWatcher can only " +
            "prove your ISP answers, not the rest of the internet. Tag your ISP on a path hop " +
            "(or add an anchor outside your ISP) so ConnectionWatcher can prove your internet is up.");
    }
```

- [ ] **Step 4: Wizard text**

In `Programs/ConWatch/Pages/Setup.cshtml`, replace the ISP label's `<small>` (L82–83) with:

```html
            <label style="margin-top: 0.8rem">Who is your internet provider? <small>(optional — lets reports
                and alerts say "Comcast is down" instead of naming a router, and lets ConnectionWatcher
                tell your ISP's own servers from the rest of the internet so it can prove your internet is up)</small>
```

Keep the `<input id="isp-name" …>` line unchanged.

- [ ] **Step 5: Run the tests**

Run: `dotnet test Tests/ConPersistence.Tests -c Debug --filter "FullyQualifiedName~ConfigHealthTests"`, then `dotnet test Tests/ConWatch.Tests -c Debug --filter "FullyQualifiedName~Setup"`.
Expected: all PASS.

- [ ] **Step 6: Commit**

```bash
git add Libraries/ConPersistence/Config/ConfigHealth.cs Programs/ConWatch/Pages/Setup.cshtml Tests/ConPersistence.Tests/ConfigHealthTests.cs
git commit -m "feat(config): warn when every anchor is the ISP's; wizard explains why to name the ISP"
```

---

### Task 5: Dashboard — notes panel and header wrench badge

**Files:**
- Modify: `Programs/ConWatch/ClientApp/src/lib/types.ts`, `src/lib/statusStore.ts`, `src/dashboard.tsx`, `Programs/ConWatch/Pages/Index.cshtml.cs`, `Programs/ConWatch/Pages/_Layout.cshtml`, `Programs/ConWatch/wwwroot/lib/site-shell.js`, `Programs/ConWatch/wwwroot/css/site.css`
- Create: `Programs/ConWatch/ClientApp/src/components/NotesPanel.tsx`
- Test: `src/components/NotesPanel.test.tsx` (new), `src/lib/statusStore.test.ts`

**Interfaces:**
- Consumes: `warnings[]` on the snapshot (Task 3); `POST /api/v1/warnings/{key}/acknowledge`.
- Produces:
  - TS `ModelWarning`; `Snapshot.warnings?: ModelWarning[]`; `StoreState.warnings: ModelWarning[]`
  - `NotesPanel({ warnings, onAcknowledge? })`, `unacknowledgedCount(warnings)`, `acknowledgeWarning(key): Promise<boolean>`
  - initial-data `admin: boolean`
  - DOM event `cw:warnings` (`detail`: `ModelWarning[]`), which the header badge listens to

- [ ] **Step 1: Write the failing UI tests**

Create `src/components/NotesPanel.test.tsx`:

```tsx
import { describe, expect, it } from "vitest";
import { renderToStaticMarkup } from "react-dom/server";
import { NotesPanel, unacknowledgedCount } from "./NotesPanel";
import type { ModelWarning } from "../lib/types";

const w = (over: Partial<ModelWarning> = {}): ModelWarning => ({
  key: "hop-bypassed:1", kind: "HopBypassed", targetId: "1", targetName: "Inside",
  message: "Inside isn't answering its probe (tcp/80), but External beyond it is",
  since: "2026-10-08T23:42:16Z", acknowledged: false, ...over,
});

describe("NotesPanel", () => {
  it("renders nothing without notes", () => {
    expect(renderToStaticMarkup(<NotesPanel warnings={[]} onAcknowledge={() => {}} />)).toBe("");
  });
  it("lists a note with its message and an Acknowledge button for the admin", () => {
    const html = renderToStaticMarkup(<NotesPanel warnings={[w()]} onAcknowledge={() => {}} />);
    expect(html).toContain('id="notes"');
    expect(html).toContain("ConnectionWatcher notes");
    expect(html).toContain("Inside isn&#x27;t answering its probe (tcp/80)");
    expect(html).toContain("Acknowledge");
  });
  it("shows an acknowledged note dimmed, without a button", () => {
    const html = renderToStaticMarkup(<NotesPanel warnings={[w({ acknowledged: true })]} onAcknowledge={() => {}} />);
    expect(html).toContain("acknowledged");
    expect(html).not.toContain("<button");
  });
  it("offers no button without an acknowledge handler", () => {
    expect(renderToStaticMarkup(<NotesPanel warnings={[w()]} />)).not.toContain("<button");
  });
  it("counts only unacknowledged notes for the badge", () => {
    expect(unacknowledgedCount([w(), w({ key: "b", acknowledged: true })])).toBe(1);
  });
});
```

In `src/lib/statusStore.test.ts`, add inside `describe("statusStore", …)`:

```ts
  it("keeps the snapshot's notes and defaults them to empty", () => {
    const store = createStatusStore();
    store.applySnapshot({ ...snapshot, warnings: [{ key: "unprovable-up", kind: "UnprovableUp",
      message: "m", since: "2026-10-09T00:00:00Z", acknowledged: false }] });
    expect(store.getState().warnings).toHaveLength(1);
    store.applySnapshot({ ...snapshot, warnings: undefined });
    expect(store.getState().warnings).toEqual([]);
  });
```

- [ ] **Step 2: Run them to verify they fail**

Run: `cd Programs/ConWatch/ClientApp && npx vitest run src/components/NotesPanel.test.tsx src/lib/statusStore.test.ts`
Expected: FAIL (module `./NotesPanel` not found; `warnings` undefined).

- [ ] **Step 3: Types and store**

In `types.ts`, add:

```ts
/** An admin note (model warning): where ConnectionWatcher's map disagrees
 *  with what the probes prove. Never part of the verdict. */
export interface ModelWarning {
  key: string;
  kind: "HopBypassed" | "NeverAnswered" | "UnprovableUp" | string;
  targetId?: string | null;
  targetName?: string | null;
  message: string;
  since: string;
  acknowledged: boolean;
}
```

and to `Snapshot`: `warnings?: ModelWarning[];`.

In `statusStore.ts`: import `ModelWarning`; add `warnings: ModelWarning[];` to `StoreState`; initialise `warnings: []`; in `applySnapshot` add `state.warnings = snap.warnings ?? [];`.

- [ ] **Step 4: NotesPanel**

Create `src/components/NotesPanel.tsx`:

```tsx
// "ConnectionWatcher notes": admin-facing, never alarming (spec 2026-10-09
// §4.6, D3). Where the map disagrees with what the probes prove; acknowledging
// hides a note until it clears and comes back.
import { useEffect } from "react";
import type { ModelWarning } from "../lib/types";

export function unacknowledgedCount(warnings: ModelWarning[]): number {
  return warnings.filter((w) => !w.acknowledged).length;
}

export async function acknowledgeWarning(key: string): Promise<boolean> {
  const r = await fetch(`/api/v1/warnings/${encodeURIComponent(key)}/acknowledge`, { method: "POST" });
  return r.ok;
}

export function NotesPanel({ warnings, onAcknowledge }: {
  warnings: ModelWarning[];
  onAcknowledge?: (key: string) => void;
}) {
  const any = warnings.length > 0;
  useEffect(() => {
    // The header badge links to /#notes; the panel renders after load.
    if (any && location.hash === "#notes")
      document.getElementById("notes")?.scrollIntoView();
  }, [any]);
  if (!any) return null;
  return (
    <section id="notes" className="cw-notes" aria-label="ConnectionWatcher notes">
      <h3>ConnectionWatcher notes</h3>
      <p className="cw-notes-intro">
        Where ConnectionWatcher's map of your network disagrees with what the
        probes prove. These never change the verdict.
      </p>
      <ul>
        {warnings.map((w) => (
          <li key={w.key} className={w.acknowledged ? "acknowledged" : undefined}>
            <span className="message">{w.message}</span>
            <span className="since">since {new Date(w.since).toLocaleString()}</span>
            {w.acknowledged
              ? <span className="ack">acknowledged</span>
              : onAcknowledge && (
                <button type="button" className="secondary outline"
                  onClick={() => onAcknowledge(w.key)}>Acknowledge</button>)}
          </li>
        ))}
      </ul>
    </section>
  );
}
```

- [ ] **Step 5: Wire into the dashboard**

In `Pages/Index.cshtml.cs`, add to the anonymous initial-data object: `Admin = !gate.IsLocked(User),`.

In `dashboard.tsx`:
- Add `admin?: boolean;` to `InitialData`.
- Import `NotesPanel` and `acknowledgeWarning` from `./components/NotesPanel`.
- Inside `App`, after the existing hooks:

```tsx
  // Keep the header's wrench badge (site-shell.js) live between its polls.
  useEffect(() => {
    document.dispatchEvent(new CustomEvent("cw:warnings", { detail: state.warnings }));
  }, [state.warnings]);
```

- In the live (non-archive) return, between `<LanSection … />` and `<RecentEvents … />`:

```tsx
      {initial.admin && (
        <NotesPanel warnings={state.warnings}
          onAcknowledge={(key) => { void acknowledgeWarning(key); }} />
      )}
```

The next one-second `statusChanged` push shows the acknowledgement; no optimistic update is needed.

- [ ] **Step 6: Header wrench badge**

In `_Layout.cshtml`, add to the `icons` dictionary:

```csharp
        ["wrench"] = $"<svg width=\"16\" height=\"16\" viewBox=\"0 0 24 24\" {s}><path d=\"M14.7 6.3a1 1 0 0 0 0 1.4l1.6 1.6a1 1 0 0 0 1.4 0l3.77-3.77a6 6 0 0 1-7.94 7.94l-6.91 6.91a2.12 2.12 0 0 1-3-3l6.91-6.91a6 6 0 0 1 7.94-7.94l-3.76 3.76z\"></path></svg>",
```

In the `cluster`, directly after the `cw-pip` anchor:

```cshtml
            @if (!locked)
            {
                <a class="cw-iconbtn cw-notes-badge" href="/#notes" id="cw-notes" hidden
                   title="ConnectionWatcher notes">@Html.Raw(icons["wrench"])<span class="count"></span></a>
            }
```

In `wwwroot/lib/site-shell.js`, inside the verdict-pip IIFE, add a `renderNotes` function and call it from `render(status)` and from the dashboard's event:

```js
    const notes = document.getElementById("cw-notes");
    function renderNotes(warnings) {
        if (!notes) return;
        const n = (warnings || []).filter((w) => !w.acknowledged).length;
        notes.hidden = n === 0;
        notes.querySelector(".count").textContent = String(n);
        notes.title = n === 1 ? "1 ConnectionWatcher note" : `${n} ConnectionWatcher notes`;
    }
    document.addEventListener("cw:warnings", (e) => renderNotes(e.detail));
```

At the end of `render(status)`: `renderNotes(status?.enabled === false ? [] : status?.warnings);`.

In `wwwroot/css/site.css`, after the `.cw-iconbtn:hover` rule:

```css
.cw-notes-badge { position: relative; }
.cw-notes-badge[hidden] { display: none; }
.cw-notes-badge .count { position: absolute; top: -5px; right: -5px; min-width: 15px; height: 15px;
    padding: 0 3px; border-radius: 8px; background: var(--muted); color: var(--surface);
    font-size: 10px; font-weight: 600; line-height: 15px; text-align: center; }
.cw-notes { border: 1px solid var(--border); border-radius: 8px; background: var(--surface-2);
    padding: 0.8rem 1rem; margin: 1.2rem 0; }
.cw-notes h3 { font-size: 0.95rem; margin: 0 0 0.3rem; }
.cw-notes-intro { color: var(--muted); font-size: 0.8rem; margin: 0 0 0.6rem; }
.cw-notes ul { margin: 0; padding: 0; list-style: none; }
.cw-notes li { display: flex; flex-wrap: wrap; align-items: baseline; gap: 0.3rem 0.8rem;
    padding: 0.45rem 0; border-top: 1px solid var(--border); font-size: 0.85rem; }
.cw-notes li:first-child { border-top: none; }
.cw-notes li .message { flex: 1 1 28rem; }
.cw-notes li .since, .cw-notes li .ack { color: var(--faint); font-size: 0.75rem; }
.cw-notes li button { padding: 0.15rem 0.6rem; font-size: 0.75rem; margin: 0; width: auto; }
.cw-notes li.acknowledged .message { color: var(--muted); }
```

The badge is neutral grey (`--muted`), not warn amber: D3 says nobody should be alarmed.

- [ ] **Step 7: Run the UI tests and type check**

Run: `cd Programs/ConWatch/ClientApp && npx vitest run && npm run build`
Expected: all PASS; the build succeeds. The build regenerates `wwwroot/js/dashboard.js`; commit it only if the repo tracks it (`git ls-files Programs/ConWatch/wwwroot/js`).

- [ ] **Step 8: Commit**

```bash
git add Programs/ConWatch/ClientApp/src Programs/ConWatch/Pages Programs/ConWatch/wwwroot/lib/site-shell.js Programs/ConWatch/wwwroot/css/site.css
git commit -m "feat(ui): ConnectionWatcher notes panel and header wrench badge for the admin"
```

---

### Task 6: Tray — overlay dot, tooltip notes, menu item

**Files:**
- Move: `Programs/ConWatchTray/TrayStatus.cs` → `Libraries/ConInfrastructure/Tray/TrayStatus.cs` (namespace `ConInfrastructure.Tray`)
- Modify: `Programs/ConWatchTray/Program.cs`
- Test: `Tests/ConWatch.Tests/TrayStatusTests.cs` (new; `ConWatch.Tests` already references `ConInfrastructure`, see `ScoutClientTests`)

**Interfaces:**
- Consumes: `warnings[]` in `/api/v1/status` (Task 3).
- Produces (namespace `ConInfrastructure.Tray`):
  - `TrayState` (unchanged)
  - `record TrayInfo(TrayState State, string? Detail, IReadOnlyList<string> Notes)`: `Notes` holds the short labels of **unacknowledged** notes
  - `TrayStatus.FromStatusJson(string)`, `TrayStatus.Describe(TrayState)`, `TrayStatus.NoteLabel(JsonElement warning)`, `TrayStatus.Tooltip(TrayInfo info)` (≤ 127 chars)

- [ ] **Step 1: Move the file**

```bash
git mv Programs/ConWatchTray/TrayStatus.cs Libraries/ConInfrastructure/Tray/TrayStatus.cs
```

Change its namespace to `ConInfrastructure.Tray`, and add `using ConInfrastructure.Tray;` to `Programs/ConWatchTray/Program.cs`. Confirm `ConWatchTray.csproj` references `ConInfrastructure` (it does today), then run `dotnet build Programs/ConWatchTray -c Debug`. Expected: builds (Windows only). On a non-Windows box, build `Libraries/ConInfrastructure` instead.

- [ ] **Step 2: Write the failing tests**

Create `Tests/ConWatch.Tests/TrayStatusTests.cs`:

```csharp
using ConInfrastructure.Tray;

namespace ConWatch.Tests;

public class TrayStatusTests
{
    private const string Healthy = """{"enabled":true,"verdict":{"state":"Healthy","detail":"Healthy: 2 anchors responding","operator":null},"targets":[],"pools":[]""";

    private static string With(string warnings) => Healthy + $",\"warnings\":{warnings}}}";

    [Fact]
    public void Notes_never_change_the_state_and_only_unacknowledged_ones_count()
    {
        var info = TrayStatus.FromStatusJson(With("""
            [{"key":"hop-bypassed:1","kind":"HopBypassed","targetName":"Inside","message":"m","since":"2026-10-08T23:42:16Z","acknowledged":false},
             {"key":"unprovable-up","kind":"UnprovableUp","targetName":null,"message":"m","since":"2026-10-08T23:42:16Z","acknowledged":true}]
            """));
        Assert.Equal(TrayState.Healthy, info.State);
        Assert.Equal(["Inside isn't answering (traffic passes it)"], info.Notes);
    }

    [Fact]
    public void A_status_without_warnings_has_no_notes()
    {
        Assert.Empty(TrayStatus.FromStatusJson(Healthy + "}").Notes);
        Assert.Empty(TrayStatus.FromStatusJson("not json").Notes);
    }

    [Fact]
    public void Each_kind_has_a_short_label()
    {
        var info = TrayStatus.FromStatusJson(With("""
            [{"key":"a","kind":"HopBypassed","targetName":"Inside","message":"m","since":"x","acknowledged":false},
             {"key":"b","kind":"NeverAnswered","targetName":"Comcast Edge","message":"m","since":"x","acknowledged":false},
             {"key":"c","kind":"UnprovableUp","targetName":null,"message":"m","since":"x","acknowledged":false},
             {"key":"d","kind":"SomethingNew","targetName":null,"message":"m","since":"x","acknowledged":false}]
            """));
        Assert.Equal([
            "Inside isn't answering (traffic passes it)",
            "Comcast Edge never answers its probe",
            "Can't prove the internet is up beyond your ISP",
            "ConnectionWatcher note",
        ], info.Notes);
    }

    [Fact]
    public void Tooltip_lists_notes_after_the_state_and_detail()
    {
        var info = new TrayInfo(TrayState.Healthy, "Healthy: 2 anchors responding",
            ["Inside isn't answering (traffic passes it)"]);
        Assert.Equal("Internet: healthy\nHealthy: 2 anchors responding\n🔧 Inside isn't answering (traffic passes it)",
            TrayStatus.Tooltip(info));
    }

    [Fact]
    public void Tooltip_never_exceeds_the_notify_icon_limit()
    {
        var info = new TrayInfo(TrayState.Healthy, new string('d', 90),
            Enumerable.Range(0, 6).Select(i => $"A very long target name number {i} isn't answering (traffic passes it)").ToList());
        var text = TrayStatus.Tooltip(info);
        Assert.True(text.Length <= 127, $"{text.Length} chars");
        Assert.EndsWith("…", text);
    }

    [Fact]
    public void Tooltip_without_notes_matches_the_old_text()
    {
        Assert.Equal("Internet: DOWN\nLinkDown: Router is Down",
            TrayStatus.Tooltip(new TrayInfo(TrayState.Down, "LinkDown: Router is Down", [])));
    }
}
```

- [ ] **Step 3: Run them to verify they fail**

Run: `dotnet test Tests/ConWatch.Tests -c Debug --filter "FullyQualifiedName~TrayStatusTests"`
Expected: build FAILS (`TrayInfo` has two parameters; `Tooltip` does not exist).

- [ ] **Step 4: Implement**

In `TrayStatus.cs`:

```csharp
/// <summary>State, the human-readable evidence line, and the short labels of
/// unacknowledged admin notes (never part of the state: spec 2026-10-09 D3).</summary>
public record TrayInfo(TrayState State, string? Detail, IReadOnlyList<string> Notes);
```

Every `new TrayInfo(state, detail)` in `FromStatusJson` becomes `new TrayInfo(state, detail, notes)`. Compute `notes` once, right after deserializing:

```csharp
            var notes = status.TryGetProperty("warnings", out var warnings) &&
                        warnings.ValueKind == JsonValueKind.Array
                ? warnings.EnumerateArray()
                    .Where(w => !(w.TryGetProperty("acknowledged", out var a) && a.ValueKind == JsonValueKind.True))
                    .Select(NoteLabel).ToList()
                : new List<string>();
```

In the `catch (JsonException)` branch, return `new TrayInfo(TrayState.Unreachable, null, [])`.

Add:

```csharp
    /// <summary>A tooltip-sized label for one note.</summary>
    public static string NoteLabel(JsonElement warning)
    {
        var target = warning.TryGetProperty("targetName", out var t) && t.ValueKind == JsonValueKind.String
            ? t.GetString() : null;
        var kind = warning.TryGetProperty("kind", out var k) ? k.GetString() : null;
        return kind switch
        {
            "HopBypassed" when target is not null => $"{target} isn't answering (traffic passes it)",
            "NeverAnswered" when target is not null => $"{target} never answers its probe",
            "UnprovableUp" => "Can't prove the internet is up beyond your ISP",
            _ => "ConnectionWatcher note",
        };
    }

    /// <summary>NotifyIcon.Text: state, evidence, then one line per note,
    /// truncated to the 127-character limit.</summary>
    public static string Tooltip(TrayInfo info)
    {
        var text = Describe(info.State);
        if (info.Detail is { Length: > 0 })
            text += $"\n{info.Detail}";
        foreach (var note in info.Notes)
            text += $"\n🔧 {note}";
        const int max = 127;
        return text.Length <= max ? text : text[..(max - 1)] + "…";
    }
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test Tests/ConWatch.Tests -c Debug --filter "FullyQualifiedName~TrayStatusTests"`
Expected: all PASS.

- [ ] **Step 6: Tray program: overlay, tooltip, menu item**

In `Programs/ConWatchTray/Program.cs`:

1. Fields. Replace `Dictionary<TrayState, Icon> _icons` with `Dictionary<(TrayState, bool), Icon> _icons`, and add:

```csharp
    private IReadOnlyList<string> _notes = [];
    private ToolStripItem _notesItem = null!;
```

2. Menu. Right after the bold "Open ConnectionWatcher" item:

```csharp
        _notesItem = menu.Items.Add("ConnectionWatcher notes", null, (_, _) => Open("/#notes"));
        _notesItem.Visible = false;
```

3. `RefreshAsync`. The two `new TrayInfo(TrayState.Unreachable, null)` become `new TrayInfo(TrayState.Unreachable, null, [])`. Replace the `if (next.State != _state) { … }` block with:

```csharp
        bool stateChanged = next.State != _state;
        bool notesChanged = !next.Notes.SequenceEqual(_notes);
        if (stateChanged || notesChanged)
        {
            var previous = _state;
            _state = next.State;
            _notes = next.Notes;
            _icon.Icon = IconFor(next.State, badge: next.Notes.Count > 0);
            _icon.Text = TrayStatus.Tooltip(next);
            _notesItem.Text = $"ConnectionWatcher notes ({next.Notes.Count})";
            _notesItem.Visible = next.Notes.Count > 0;
            if (stateChanged)
                MaybeToast(previous, next); // notes never toast (spec D3)
        }
```

4. The constructor's initial `Icon = IconFor(TrayState.Unreachable)` becomes `IconFor(TrayState.Unreachable, badge: false)`.

5. `IconFor(TrayState state, bool badge)`: cache on `(state, badge)`. After the `if (slash) { … }` block, still inside the `using (var g …)`, add:

```csharp
            if (badge)
            {
                // Subtle note marker: a small neutral dot, bottom-right, ringed
                // so it reads on light and dark taskbars. Never a state colour.
                const float d = 9f;
                var box = new RectangleF(size - d - 0.5f, size - d - 0.5f, d, d);
                using var ring = new SolidBrush(Color.White);
                using var fill = new SolidBrush(Color.FromArgb(96, 112, 140));
                g.FillEllipse(ring, RectangleF.Inflate(box, 1.2f, 1.2f));
                g.FillEllipse(fill, box);
            }
```

and store under `_icons[(state, badge)]`.

6. Delete the now-unused `Truncate` helper only if nothing else uses it (`MaybeToast` does, so keep it).

- [ ] **Step 7: Build the tray**

Run: `dotnet build Programs/ConWatchTray -c Debug`
Expected: builds with no warnings. The tray has no automated UI test; Task 7 covers the manual check.

- [ ] **Step 8: Commit**

```bash
git add Libraries/ConInfrastructure/Tray Programs/ConWatchTray Tests/ConWatch.Tests/TrayStatusTests.cs
git commit -m "feat(tray): subtle note dot, tooltip lines and a notes menu item; never a toast"
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

- [ ] **Step 2: D3 audit**

Run: `grep -rn "ModelWarning\|ModelCondition\|warnings" Libraries/ConNotify Libraries/ConPersistence/Report Programs/ConWatchTray/Program.cs`
Expected: no hits in `ConNotify` (webhooks, MQTT) or `Report`. In the tray, the only hits are the notes item, the tooltip and the icon badge. Nothing feeds `MaybeToast`.

- [ ] **Step 3: Browser check of the dashboard and badge**

Start the app with a config in which a hop's probe fails while what lies beyond it answers. The simplest is a local run whose Router target points at an unused LAN address, with real anchors parented under it. Then:
- `/` shows the "ConnectionWatcher notes" panel with a `HopBypassed` note naming that target. The verdict banner stays Up.
- The header wrench shows `1`. Clicking it lands on `/#notes`.
- Acknowledge: the button disappears within about 1 s, the note shows "acknowledged" and the wrench hides.
- Light and dark themes both render the badge and panel readably.
- With the admin gate on and logged out, neither the wrench nor the panel shows, while `/api/v1/status` still lists the note.

Take a screenshot of the panel and the badge for the PR.

- [ ] **Step 4: Tray (Mark, manual)**

Mark verifies against the same server:
- The dot appears on the tray icon, with no colour change.
- The tooltip lists the note.
- "ConnectionWatcher notes (1)" opens `/#notes`.
- No toast appears.
- Acknowledging removes the dot within 5 s.

- [ ] **Step 5: Hand back**

Report the branch, commit range and test results. Merging is Mark's decision. Never push.
