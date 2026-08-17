# Phase 2 Persistence + API Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement the Phase 2 spec (`docs/superpowers/specs/2026-08-16-phase2-persistence-api-design.md`): ConWatch app + ConPersistence library, SQLite via EF Core, transition/rollup recording with retention, config-in-DB with CRUD + engine rebuild, verdict layer, REST API.

**Architecture:** Engine stays EF-free; persistence subscribes to engine Rx streams. New `Programs/ConWatch` (ASP.NET Core, minimal APIs) composes everything. `ConPersistence` holds DbContext, migrations, repositories, background services.

**Tech Stack:** .NET 10, EF Core 10 (Sqlite + Design), Minimal APIs + built-in OpenAPI, Swashbuckle SwaggerUI (dev only), xUnit, FakeTimeProvider, WebApplicationFactory.

## Global Constraints

- **NEVER `git push`.** Work on new branch `phase2` off `master`; commit per task.
- PowerShell on Windows 11; `dotnet build ConnectionWatcher.sln` must stay green after every task.
- All timestamps stored as `DateTime` (UTC) columns suffixed `Utc`; served as ISO 8601 `Z`.
- Retention defaults: minute 90 days, hourly 13 months, daily forever; transitions never pruned.
- Verdict rules per spec §5.3: Down-ancestor → LinkDown (deepest named); Faltering-ancestor → LinkDegraded; else degraded anchors (Down/Faltering/Recovering) 0 → Healthy, 1..threshold−1 → SiteIncident, ≥threshold → InternetWeather; undetermined anchors (Initializing/Starting) excluded; no determined anchors → Unknown.
- RTT statistics count **successful pings only**; rows with zero successes carry RttAvg/Min/Max = 0 and are excluded from downsample min/max/avg merging.
- TDD: failing test → implement → pass → commit. Existing 55 ConEngine tests must stay green throughout.

---

### Task 1: Branch, scaffold projects, delete dead code

**Files:**
- Create: `ConPersistence/ConPersistence.csproj` (classlib), `Programs/ConWatch/ConWatch.csproj` (web), `Tests/ConPersistence.Tests/`, `Tests/ConWatch.Tests/` (xunit)
- Delete: `Programs/ConDiag/`, `Core/ConHost/`

**Interfaces:** produces the four projects wired into the solution; ConWatch references ConEngine, ConInfrastructure, ConPersistence; ConPersistence references ConCore, ConEngine (for `WatcherOptions`/events); ConWatch.Tests references ConWatch + ConEngine.Tests (reuses `FakePingTransceiverFactory`) + `Microsoft.AspNetCore.Mvc.Testing`; ConPersistence.Tests references ConPersistence + TimeProvider.Testing.

- [ ] **Step 1: Branch and scaffold**

```powershell
git checkout -b phase2
dotnet new classlib -o ConPersistence -n ConPersistence
dotnet new web -o Programs/ConWatch -n ConWatch
dotnet new xunit -o Tests/ConPersistence.Tests -n ConPersistence.Tests
dotnet new xunit -o Tests/ConWatch.Tests -n ConWatch.Tests
dotnet sln ConnectionWatcher.sln add ConPersistence/ConPersistence.csproj Programs/ConWatch/ConWatch.csproj Tests/ConPersistence.Tests/ConPersistence.Tests.csproj Tests/ConWatch.Tests/ConWatch.Tests.csproj
dotnet add ConPersistence reference Core/ConCore/ConCore.csproj Core/ConEngine/ConEngine.csproj
dotnet add ConPersistence package Microsoft.EntityFrameworkCore.Sqlite
dotnet add ConPersistence package Microsoft.EntityFrameworkCore.Design
dotnet add Programs/ConWatch reference Core/ConEngine/ConEngine.csproj ConInfrastructure/ConInfrastructure.csproj ConPersistence/ConPersistence.csproj
dotnet add Programs/ConWatch package Swashbuckle.AspNetCore.SwaggerUI
dotnet add Tests/ConPersistence.Tests reference ConPersistence/ConPersistence.csproj
dotnet add Tests/ConPersistence.Tests package Microsoft.Extensions.TimeProvider.Testing
dotnet add Tests/ConWatch.Tests reference Programs/ConWatch/ConWatch.csproj Tests/ConEngine.Tests/ConEngine.Tests.csproj
dotnet add Tests/ConWatch.Tests package Microsoft.AspNetCore.Mvc.Testing
```

Delete template files `Class1.cs`/`UnitTest1.cs` from the new projects. Ensure all four csprojs target `net10.0`.

- [ ] **Step 2: Delete dead projects**

```powershell
dotnet sln ConnectionWatcher.sln remove Programs/ConDiag/ConDiag.csproj Core/ConHost/ConHost.csproj
Remove-Item -Recurse -Force Programs/ConDiag, Core/ConHost
```

- [ ] **Step 3: Verify** — `dotnet build ConnectionWatcher.sln` (0 errors; NU1903 warnings gone) and `dotnet test Tests/ConEngine.Tests` (55 pass). Dismiss the pending "vulnerable Microsoft.OpenApi" task chip (superseded by deletion).

- [ ] **Step 4: Commit** — `git add -A; git commit -m "chore: scaffold ConWatch/ConPersistence, remove ConDiag/ConHost"`

---

### Task 2: ConEngine additions — stable IDs, subject kind, PingSampleSource, LastStatus

**Files:**
- Modify: `Core/ConEngine/Configuration/WatcherOptions.cs`, `Core/ConEngine/Topology/TargetGraph.cs`, `Core/ConEngine/Events/TargetTransition.cs`, `Core/ConEngine/Topology/PoolCoordinator.cs`, `Core/ConEngine/Tracking/TargetMonitor.cs`, `Core/ConEngine/WatcherEngine.cs`
- Test: `Tests/ConEngine.Tests/EngineExtensionTests.cs`

**Interfaces:**
- `TargetConfig` and `PoolConfig` gain `public Guid? Id { get; set; }`; `TargetGraph.FromOptions` uses `config.Id ?? Guid.NewGuid()`.
- `enum TransitionSubjectKind { Target, Pool, Verdict }`; `TargetTransition` gains `public TransitionSubjectKind Kind { get; init; } = TransitionSubjectKind.Target;` — `PoolCoordinator` sets `Kind = TransitionSubjectKind.Pool`.
- `TargetMonitor` gains `public TargetStatus? LastStatus { get; private set; }` (set inside the existing status subscription in `StartTracking`).
- `WatcherEngine` gains `public IObservable<(Guid TargetId, PingResult Result)> PingSampleSource` — a `Subject` fed in the routing subscription right where results reach a monitor.

- [ ] **Step 1: Failing tests** (`EngineExtensionTests.cs`):

```csharp
using System.Net;
using ConEngine.Configuration;
using ConEngine.Events;
using ConEngine.Tests.Fakes;
using ConEngine.Topology;
using ConEngine.Tracking;
using Microsoft.Extensions.Logging.Abstractions;

namespace ConEngine.Tests;

public class EngineExtensionTests
{
    [Fact]
    public void TargetGraph_preserves_provided_ids()
    {
        var routerId = Guid.NewGuid();
        var g = TargetGraph.FromOptions(new WatcherOptions
        {
            Targets = [new TargetConfig { Id = routerId, Name = "Router", Address = "10.0.0.1", Profile = "Internal" }],
        });
        Assert.Equal(routerId, g.GetByName("Router").Id);
    }

    [Fact]
    public async Task Engine_emits_ping_samples_with_target_ids()
    {
        var options = new WatcherOptions
        {
            Profiles = { ["Fast"] = new PingProfileConfig { NormalIntervalSeconds = 0.02, MinUsableSample = 5, HistorySize = 20 } },
            Targets = [new TargetConfig { Name = "Router", Address = "10.0.0.9", Profile = "Fast" }],
        };
        var engine = new WatcherEngine(TimeProvider.System, new FakePingTransceiverFactory(),
            new ConInfrastructure.DomainNameResolverFactory(), NullLoggerFactory.Instance,
            Microsoft.Extensions.Options.Options.Create(options));
        var samples = new List<(Guid, ConCore.PingResult)>();
        engine.PingSampleSource.Subscribe(s => { lock (samples) samples.Add(s); });
        using var cts = new CancellationTokenSource();
        await engine.StartAsync(cts.Token);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline) { lock (samples) if (samples.Count >= 3) break; await Task.Delay(20); }
        cts.Cancel(); await engine.StopAsync();
        lock (samples)
        {
            Assert.True(samples.Count >= 3);
            Assert.All(samples, s => Assert.Equal(engine.Graph.GetByName("Router").Id, s.Item1));
        }
    }

    [Fact]
    public void Pool_transitions_carry_pool_kind()
    {
        // PoolCoordinatorTests already drive a pool transition; here just check the flag.
        var t = new TargetTransition
        {
            TargetId = Guid.NewGuid(), TargetName = "x", From = HostState.Initializing,
            To = HostState.Starting, Timestamp = DateTimeOffset.UtcNow,
            Reason = new TransitionReason { Trigger = TransitionTrigger.PoolMembersChanged },
        };
        Assert.Equal(TransitionSubjectKind.Target, t.Kind); // default
    }
}
```

Also extend `PoolCoordinatorTests.Pool_down_when_all_members_down_with_detail` with `Assert.Equal(TransitionSubjectKind.Pool, t.Kind);`.

- [ ] **Step 2: Verify failure** — compile errors on `Id`, `PingSampleSource`, `Kind`.

- [ ] **Step 3: Implement** —
  - `WatcherOptions.cs`: add `public Guid? Id { get; set; }` to `TargetConfig` and `PoolConfig`.
  - `TargetGraph.FromOptions`: `var idsByName = options.Targets.ToDictionary(t => t.Name, t => t.Id ?? Guid.NewGuid(), ...)`; pools: `var id = p.Id ?? Guid.NewGuid();`.
  - `TargetTransition.cs`: add the enum + `Kind` property as specified above.
  - `PoolCoordinator.Recalculate`: add `Kind = TransitionSubjectKind.Pool` to the emitted transition.
  - `TargetMonitor`: in `StartTracking`'s status subscription, also `LastStatus = <the TargetStatus being pushed>`; property `public TargetStatus? LastStatus { get; private set; }`.
  - `WatcherEngine`: field `private readonly Subject<(Guid, PingResult)> _pingSampleSubject = new();`, property `public IObservable<(Guid TargetId, PingResult Result)> PingSampleSource => _pingSampleSubject;` and inside the routing subscription, after `monitor.RecordPingResult(result);` add `_pingSampleSubject.OnNext((monitor.Target.Id, result));`.

- [ ] **Step 4: Verify** — `dotnet test Tests/ConEngine.Tests` all green (58+).

- [ ] **Step 5: Commit** — `git add -A; git commit -m "feat(engine): stable ids, subject kind, PingSampleSource, LastStatus"`

---

### Task 3: VerdictCoordinator

**Files:**
- Create: `Core/ConEngine/Topology/VerdictCoordinator.cs`, `Core/ConEngine/Configuration/VerdictOptions.cs`
- Modify: `Core/ConEngine/Events/TargetTransition.cs` (add `VerdictChanged` trigger + `Verdict` reason field), `Core/ConEngine/WatcherEngine.cs` (wire it), `ConTinker/Program.cs` + `ConTinker/appsettings.json` (demo)
- Test: `Tests/ConEngine.Tests/VerdictCoordinatorTests.cs`

**Interfaces:**

```csharp
public enum VerdictState { Unknown, Healthy, SiteIncident, InternetWeather, LinkDegraded, LinkDown }

public class VerdictOptions
{
    public const string SectionName = "Verdict";
    public string? AnchorPool { get; set; }          // null => verdict disabled
    public int WeatherThreshold { get; set; } = 2;
}

public class VerdictCoordinator : IDisposable
{
    public static readonly Guid SubjectId = Guid.Parse("6E7E12E7-0000-4000-8000-000000000001");
    public VerdictCoordinator(TargetGraph graph, PoolDefinition anchorPool,
        IReadOnlyDictionary<Guid, TargetMonitor> monitors, int weatherThreshold,
        TimeProvider timeProvider, ILogger<VerdictCoordinator> log);
    public VerdictState CurrentVerdict { get; }       // starts Unknown
    public string? CurrentDetail { get; }
    public IObservable<TargetTransition> TransitionSource { get; }
    public void Start();
}
```

`TransitionTrigger` gains `VerdictChanged`; `TransitionReason` gains `public string? Verdict { get; init; }`. `WatcherEngine` ctor gains a final parameter `IOptions<VerdictOptions>? verdictOptions = null`; exposes `public VerdictCoordinator? Verdict { get; }`. HostState mapping: Unknown→Starting, Healthy/SiteIncident→Responding, InternetWeather/LinkDegraded→Faltering, LinkDown→Down. Emit whenever the **label** changes.

- [ ] **Step 1: Failing tests** (`VerdictCoordinatorTests.cs`) — build the Phase 1 test topology (Router→Modem→{G,C,Q} + pool "Internet" of the three), monitors with `FakeTimeProvider`, coordinator with threshold 2; helpers `Feed(name,count,ok)` as in TopologyCoordinatorTests (plus a TopologyCoordinator so Unreachable cascades work):

```csharp
[Fact] public void Starts_unknown_then_healthy() { /* verdict Unknown; feed all 5 targets 5 ok pings -> Healthy, mapped transition Starting->Responding with Trigger VerdictChanged, Reason.Verdict "Healthy" */ }
[Fact] public void One_anchor_down_is_site_incident() { /* all healthy; Google 20 fails -> SiteIncident; Detail contains "Google" */ }
[Fact] public void Two_anchors_down_is_internet_weather() { /* + Cloudflare 20 fails -> InternetWeather; emitted transition From==Responding To==Faltering */ }
[Fact] public void Ancestor_down_beats_weather() { /* Modem 20 fails -> LinkDown; Detail contains "Modem"; anchors are Unreachable but verdict is LinkDown not weather */ }
[Fact] public void Ancestor_faltering_is_link_degraded() { /* Modem 3 fails (Faltering) -> LinkDegraded */ }
[Fact] public void Recovering_anchor_still_counts_degraded() { /* Google Down then 3 ok pings (Recovering) with Cloudflare Down -> still InternetWeather */ }
[Fact] public void Label_change_with_same_mapped_state_still_emits() { /* Healthy -> SiteIncident emits Responding->Responding row */ }
```

Write these fully (same Feed/Make patterns as existing coordinator tests — copy the fixture style from `TopologyCoordinatorTests`, adding the pool and coordinator).

- [ ] **Step 2: Verify failure** (compile errors).

- [ ] **Step 3: Implement** `VerdictCoordinator`:

```csharp
using System.Reactive.Subjects;
using ConCore.Targets;
using ConEngine.Events;
using ConEngine.Tracking;
using Microsoft.Extensions.Logging;

namespace ConEngine.Topology;

/// <summary>
/// Classifies overall internet health from anchor-pool member states and
/// their ancestors: Healthy / SiteIncident / InternetWeather / LinkDegraded / LinkDown.
/// </summary>
public class VerdictCoordinator : IDisposable
{
    /// <summary>Fixed synthetic subject id for the "Internet" verdict stream.</summary>
    public static readonly Guid SubjectId = Guid.Parse("6E7E12E7-0000-4000-8000-000000000001");

    private readonly TargetGraph _graph;
    private readonly IReadOnlyList<TargetMonitor> _anchors;
    private readonly IReadOnlyList<(TargetMonitor Monitor, int Depth)> _ancestors;
    private readonly IReadOnlyDictionary<Guid, TargetMonitor> _monitors;
    private readonly int _weatherThreshold;
    private readonly TimeProvider _time;
    private readonly ILogger<VerdictCoordinator> _log;
    private readonly Lock _lock = new();
    private readonly List<IDisposable> _subscriptions = new();
    private readonly ReplaySubject<TargetTransition> _transitionSubject = new(bufferSize: 1);

    public VerdictState CurrentVerdict { get; private set; } = VerdictState.Unknown;
    public string? CurrentDetail { get; private set; }
    public IObservable<TargetTransition> TransitionSource => _transitionSubject;

    public VerdictCoordinator(TargetGraph graph, PoolDefinition anchorPool,
        IReadOnlyDictionary<Guid, TargetMonitor> monitors, int weatherThreshold,
        TimeProvider timeProvider, ILogger<VerdictCoordinator> log)
    {
        _graph = graph;
        _monitors = monitors;
        _weatherThreshold = weatherThreshold;
        _time = timeProvider;
        _log = log;
        _anchors = anchorPool.MemberTargetIds
            .Where(monitors.ContainsKey).Select(id => monitors[id]).ToList();

        // Union of ancestors of all anchors, with depth (root = 0).
        var seen = new Dictionary<Guid, int>();
        foreach (var anchor in _anchors)
        {
            var chain = new List<Guid>();
            var parent = _graph.Targets[anchor.Target.Id].ParentId;
            while (parent is { } pid) { chain.Add(pid); parent = _graph.Targets[pid].ParentId; }
            chain.Reverse(); // root first => index == depth
            for (int depth = 0; depth < chain.Count; depth++) seen[chain[depth]] = depth;
        }
        _ancestors = seen.Where(kv => monitors.ContainsKey(kv.Key))
            .Select(kv => (monitors[kv.Key], kv.Value)).ToList();
    }

    public void Start()
    {
        foreach (var m in _anchors.Concat(_ancestors.Select(a => a.Monitor)).Distinct())
            _subscriptions.Add(m.TransitionSource.Subscribe(_ => Recalculate()));
    }

    private void Recalculate()
    {
        lock (_lock)
        {
            var (verdict, detail) = Classify();
            if (verdict == CurrentVerdict) return;
            var from = Map(CurrentVerdict);
            CurrentVerdict = verdict;
            CurrentDetail = detail;
            var transition = new TargetTransition
            {
                TargetId = SubjectId, TargetName = "Internet",
                Kind = TransitionSubjectKind.Verdict,
                From = from, To = Map(verdict),
                Timestamp = _time.GetUtcNow(),
                Reason = new TransitionReason
                {
                    Trigger = TransitionTrigger.VerdictChanged,
                    Verdict = verdict.ToString(), Detail = detail,
                },
            };
            _log.LogInformation("Verdict: {Transition}", transition);
            _transitionSubject.OnNext(transition);
        }
    }

    private (VerdictState, string) Classify()
    {
        var downAncestor = _ancestors.Where(a => a.Monitor.CurrentState == HostState.Down)
            .OrderByDescending(a => a.Depth).FirstOrDefault();
        if (downAncestor.Monitor is not null)
            return (VerdictState.LinkDown, $"LinkDown: {downAncestor.Monitor.Target.Name} is Down");

        var faltAncestor = _ancestors.Where(a => a.Monitor.CurrentState == HostState.Faltering)
            .OrderByDescending(a => a.Depth).FirstOrDefault();
        if (faltAncestor.Monitor is not null)
            return (VerdictState.LinkDegraded, $"LinkDegraded: {faltAncestor.Monitor.Target.Name} is Faltering");

        var determined = _anchors.Where(a => a.CurrentState
            is not (HostState.Initializing or HostState.Starting)).ToList();
        if (determined.Count == 0)
            return (VerdictState.Unknown, "No anchors determined yet");

        var degraded = determined.Where(a => a.CurrentState
            is HostState.Down or HostState.Faltering or HostState.Recovering).ToList();
        string List() => string.Join(", ", degraded.Select(a => $"{a.Target.Name}={a.CurrentState}"));

        if (degraded.Count == 0)
            return (VerdictState.Healthy, $"Healthy: {determined.Count} anchors responding");
        if (degraded.Count < _weatherThreshold)
            return (VerdictState.SiteIncident, $"SiteIncident: {List()}");
        return (VerdictState.InternetWeather,
            $"InternetWeather: {degraded.Count} of {determined.Count} anchors degraded ({List()})");
    }

    private static HostState Map(VerdictState v) => v switch
    {
        VerdictState.Healthy or VerdictState.SiteIncident => HostState.Responding,
        VerdictState.InternetWeather or VerdictState.LinkDegraded => HostState.Faltering,
        VerdictState.LinkDown => HostState.Down,
        _ => HostState.Starting,
    };

    public void Dispose()
    {
        foreach (var s in _subscriptions) s.Dispose();
        _subscriptions.Clear();
        _transitionSubject.OnCompleted();
        GC.SuppressFinalize(this);
    }
}
```

`VerdictOptions` as shown in Interfaces (plus `VerdictState` in the same file or `Events`). Wire in `WatcherEngine.StartAsync` after pools: if `verdictOptions?.Value.AnchorPool` names an existing pool (case-insensitive via `Graph.GetPoolByName` in try/catch → warn + skip), construct, subscribe its `TransitionSource` into `_transitionSubject`, `Start()`; expose `public VerdictCoordinator? Verdict { get; private set; }`; dispose in `StopAsync`. ConTinker: bind `VerdictOptions` in Program.cs and add `"Verdict": { "AnchorPool": "Internet" }` to appsettings.json.

- [ ] **Step 4: Verify** — verdict tests green; full ConEngine suite green.
- [ ] **Step 5: Commit** — `git commit -am "feat(engine): verdict layer — site incident vs internet weather vs link down"`

---

### Task 4: ConPersistence — entities, DbContext, migration

**Files:**
- Create: `ConPersistence/ConWatchDbContext.cs`, `ConPersistence/Entities/ConfigRows.cs`, `ConPersistence/Entities/DataRows.cs`, `ConPersistence/DesignTimeFactory.cs`, `ConPersistence/PersistenceOptions.cs`, migration via CLI
- Test: `Tests/ConPersistence.Tests/DbContextTests.cs`

**Interfaces:**

```csharp
// Entities/ConfigRows.cs
public class ProfileRow { public Guid Id; public string Name = ""; public double NormalIntervalSeconds = 10; public double PingTimeoutSeconds = 3; public double BurstIntervalSeconds = 1; public double BurstMaxDurationSeconds = 60; public int HistorySize = 100; public int MinUsableSample = 10; public double RespondingThreshold = .95; public double FalteringThreshold = .75; public double DownThreshold = .10; public double RecoveringThreshold = .25; }   // (as { get; set; } properties)
public class TargetRow { public Guid Id; public string Name = ""; public string Address = ""; public Guid? ParentId; public Guid ProfileId; public bool Enabled = true; }
public class PoolRow { public Guid Id; public string Name = ""; public Guid? ParentId; public List<PoolMemberRow> Members = new(); }
public class PoolMemberRow { public Guid PoolId; public Guid TargetId; public int MemberIndex; }

// Entities/DataRows.cs
public class TransitionRow { public long Id; public DateTime TimestampUtc; public Guid SubjectId; public string SubjectName = ""; public string SubjectKind = "Target"; public string FromState = ""; public string ToState = ""; public string Trigger = ""; public double? SuccessRate; public int? SampleSize; public double? BurstLossRate; public Guid? AncestorId; public string? Detail; public string? Verdict; public float AvgRtt; }
public class RollupRow { public long Id; public Guid TargetId; public DateTime PeriodStartUtc; public string Resolution = "Minute"; public int PingCount; public int SuccessCount; public double RttAvg; public long RttMin; public long RttMax; }

public class ConWatchDbContext(DbContextOptions<ConWatchDbContext> options) : DbContext(options)
{ /* DbSets: Profiles, Targets, Pools, PoolMembers, Transitions, Rollups */ }

public class PersistenceOptions { public const string SectionName = "Persistence";
    public string DbPath { get; set; } = "data/conwatch.db";
    public int MinuteRetentionDays { get; set; } = 90;
    public int HourlyRetentionMonths { get; set; } = 13;
    public int RetentionRunHourUtc { get; set; } = 3;
    public double RebuildDebounceSeconds { get; set; } = 2; }
```

Model config in `OnModelCreating`: unique indexes on `ProfileRow.Name`, `TargetRow.Name`, `PoolRow.Name` (collate NOCASE), composite PK (`PoolId`,`TargetId`) on PoolMembers with FK cascade from PoolRow, indexes `(SubjectId, TimestampUtc)` and `(TimestampUtc)` on Transitions, unique `(TargetId, Resolution, PeriodStartUtc)` on Rollups. `DesignTimeFactory : IDesignTimeDbContextFactory<ConWatchDbContext>` using `Data Source=design.db`.

- [ ] **Step 1: Failing test** (`DbContextTests.cs`): open SQLite in-memory connection (`Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:")`, keep open), `UseSqlite(connection)`, `Database.EnsureCreated()`, insert one row per table with FK links, read back; second test asserts unique-name violation throws `DbUpdateException`.
- [ ] **Step 2: Verify failure** (types missing).
- [ ] **Step 3: Implement** entities/context/factory/options exactly per Interfaces (properties, not fields). Then generate the migration:

```powershell
dotnet new tool-manifest --force:$false; if (-not (Test-Path .config/dotnet-tools.json)) { dotnet new tool-manifest }
dotnet tool install dotnet-ef
dotnet ef migrations add InitialCreate --project ConPersistence
```

- [ ] **Step 4: Verify** — tests green; `dotnet build ConnectionWatcher.sln` green; migration files present under `ConPersistence/Migrations/`.
- [ ] **Step 5: Commit** — `git add -A; git commit -m "feat(persistence): EF Core schema + InitialCreate migration"`

---

### Task 5: ConfigStore — load, seed, export mapping

**Files:**
- Create: `ConPersistence/ConfigStore.cs`, `ConPersistence/Dto/ConfigDtos.cs`
- Test: `Tests/ConPersistence.Tests/ConfigStoreTests.cs`

**Interfaces:**

```csharp
// Dto/ConfigDtos.cs — name-referenced, id-preserving; used by API CRUD and export/import
public record ProfileDto { public Guid? Id { get; init; } public required string Name { get; init; }
    /* the ten numeric knobs, same names/defaults as PingProfileConfig */ }
public record TargetDto { public Guid? Id { get; init; } public required string Name { get; init; }
    public required string Address { get; init; } public string? Parent { get; init; }
    public string Profile { get; init; } = "External"; public bool Enabled { get; init; } = true; }
public record PoolDto { public Guid? Id { get; init; } public required string Name { get; init; }
    public List<string> Members { get; init; } = new(); public string? Parent { get; init; } }
public record ConfigDocument { public List<ProfileDto> Profiles { get; init; } = new();
    public List<TargetDto> Targets { get; init; } = new(); public List<PoolDto> Pools { get; init; } = new(); }

public class ConfigStore(IDbContextFactory<ConWatchDbContext> dbFactory)
{
    public Task<bool> IsEmptyAsync();                       // no profile rows
    public Task SeedAsync(WatcherOptions? fromAppSettings); // builtins + optional import of file config
    public Task<WatcherOptions> LoadWatcherOptionsAsync();  // rows -> options with Ids + name refs
    public Task<ConfigDocument> ExportAsync();
}
```

Seeding: always insert `Internal`/`External` profile rows from `PingProfile.Internal/.External` values; if `fromAppSettings` is non-null and passes `WatcherOptionsValidator`, also insert its custom profiles, targets, pools (new GUIDs, names resolved to ids); if invalid, log-and-skip (builtins only). `LoadWatcherOptionsAsync` puts **every** profile row into `WatcherOptions.Profiles` (so edited builtins override the compiled presets — `TargetGraph.FromOptions` merge order already guarantees this).

- [ ] **Step 1: Failing tests**: (a) seed empty DB with null → exactly 2 profiles, load gives empty targets and profiles dict containing Internal/External; (b) seed with a valid home config (Phase 1 `ValidHome()` fixture from `WatcherOptionsValidatorTests`, copied local) → load round-trips names, parent links, pool member order, and populated `Id`s; (c) load→`TargetGraph.FromOptions` twice yields identical ids; (d) `ExportAsync` contains the same names/refs.
- [ ] **Step 2: Verify failure.** Step 3: Implement (straightforward mapping; member order by `MemberIndex`).
- [ ] **Step 4: Verify green.** Step 5: Commit `feat(persistence): ConfigStore load/seed/export`.

---

### Task 6: ConfigService — CRUD, validation, delete rules, import

**Files:**
- Create: `ConPersistence/ConfigService.cs`
- Test: `Tests/ConPersistence.Tests/ConfigServiceTests.cs`

**Interfaces:**

```csharp
public enum ConfigErrorKind { None, Validation, NotFound, Conflict }
public record ConfigResult(bool Success, ConfigErrorKind Kind = ConfigErrorKind.None,
    IReadOnlyList<string>? Errors = null, Guid? Id = null)
{ public static ConfigResult Ok(Guid? id = null) => new(true, Id: id); }

public class ConfigService(IDbContextFactory<ConWatchDbContext> dbFactory,
    ConfigStore store, ILogger<ConfigService> log)
{
    public event Action? ConfigChanged;                     // host debounces + rebuilds
    public Task<ConfigResult> UpsertProfileAsync(ProfileDto dto);   // Id null => create
    public Task<ConfigResult> DeleteProfileAsync(Guid id);          // Conflict if referenced
    public Task<ConfigResult> UpsertTargetAsync(TargetDto dto);
    public Task<ConfigResult> DeleteTargetAsync(Guid id);           // Conflict: children/pool member/pool parent
    public Task<ConfigResult> UpsertPoolAsync(PoolDto dto);
    public Task<ConfigResult> DeletePoolAsync(Guid id);
    public Task<ConfigResult> ImportAsync(ConfigDocument doc);      // validate whole doc, wipe+insert, one tx
}
```

Every mutation runs inside a transaction: apply → `SaveChanges` → `store.LoadWatcherOptionsAsync()` (same context state) → `new WatcherOptionsValidator().Validate(null, options)` → commit + fire `ConfigChanged`, or rollback + `Validation` errors. Name refs (`Parent`, `Profile`, pool `Members`) resolve to rows before save; unresolved → `Validation` error listing the name (the validator would catch it anyway, but resolving first avoids FK exceptions). Unknown `Id` on PUT/DELETE → `NotFound`. Delete conflicts → `Conflict` with a message naming the referrers.

- [ ] **Step 1: Failing tests** (in-memory SQLite, seeded home config): create target under Modem → success + `ConfigChanged` fired once; create target with parent cycle attempt (reparent Router under Google) → Validation error mentions "cycle", DB unchanged; delete Modem → Conflict (children + pool parent); delete profile in use → Conflict; delete unreferenced target → ok; upsert pool with unknown member → Validation; import of exported document into a fresh DB → identical export (ids preserved); import invalid doc → Validation, DB unchanged.
- [ ] **Steps 2–5:** verify failure, implement, verify green, commit `feat(persistence): ConfigService CRUD with validation and rebuild signal`.

---

### Task 7: RollupAccumulator (pure math)

**Files:**
- Create: `ConPersistence/Rollups/RollupAccumulator.cs`
- Test: `Tests/ConPersistence.Tests/RollupAccumulatorTests.cs`

**Interfaces:**

```csharp
public record RollupBucket(Guid TargetId, DateTime PeriodStartUtc)
{ public int PingCount; public int SuccessCount; public long RttSum; public long RttMin = long.MaxValue; public long RttMax;
  public double RttAvg => SuccessCount > 0 ? (double)RttSum / SuccessCount : 0;
  public long EffectiveMin => SuccessCount > 0 ? RttMin : 0; }

public class RollupAccumulator
{
    public void AddSample(Guid targetId, PingResult result);       // bucket keyed by UTC minute of result.Timestamp
    public List<RollupBucket> TakeClosed(DateTime nowUtc);         // buckets whose minute < current minute
    public List<RollupBucket> TakeAll();                           // shutdown flush
}
```

RTT sums/min/max update **only on success**; failures increment `PingCount` only.

- [ ] **Step 1: Failing tests**: samples within one minute aggregate (counts, min/max/avg over successes only); samples spanning minutes produce separate buckets; `TakeClosed(now)` returns only earlier minutes and removes them; all-failure bucket has avg/min 0; `TakeAll` empties.
- [ ] **Steps 2–5:** implement, green, commit `feat(persistence): rollup minute-bucket accumulator`.

---

### Task 8: TransitionRecorder + RollupCollector services

**Files:**
- Create: `ConPersistence/Services/TransitionRecorder.cs`, `ConPersistence/Services/RollupCollector.cs`
- Test: `Tests/ConPersistence.Tests/RecorderCollectorTests.cs`

**Interfaces:**

```csharp
public class TransitionRecorder(IDbContextFactory<ConWatchDbContext> dbFactory,
    ILogger<TransitionRecorder> log) : BackgroundService
{
    public void Attach(IObservable<TargetTransition> source);   // disposes prior subscription
    // bounded Channel(1000, DropOldest); ExecuteAsync drains in batches (<=100 or 500ms) -> TransitionRow inserts
    public Task FlushAsync(CancellationToken ct = default);     // test hook: drain channel now
}

public class RollupCollector(IDbContextFactory<ConWatchDbContext> dbFactory,
    TimeProvider timeProvider, ILogger<RollupCollector> log) : BackgroundService
{
    public void Attach(IObservable<(Guid TargetId, PingResult Result)> source);
    // accumulator + 15s TimeProvider timer -> flush TakeClosed as RollupRow(Minute) upserts;
    // StopAsync flushes TakeAll
    public Task FlushClosedAsync(CancellationToken ct = default); // test hook
}
```

Row mapping: `TransitionRow { TimestampUtc = t.Timestamp.UtcDateTime, SubjectKind = t.Kind.ToString(), FromState/ToState/Trigger = enum ToString(), Verdict = t.Reason.Verdict, ... }`. Upsert for rollups: try insert; on unique violation merge (sum counts, min/max, weighted avg) — factor the merge into `static RollupMerge.Combine(RollupRow into, RollupRow add)` for reuse by retention.

- [ ] **Step 1: Failing tests** (temp-file SQLite + real migrations via `Database.Migrate()`): recorder attached to a `Subject` receives 5 transitions incl. one Pool + one Verdict kind → 5 rows with correct kinds/verdict column after `FlushAsync`; collector fed 130 samples across 3 minutes with `FakeTimeProvider` → advancing time and calling `FlushClosedAsync` writes 2 closed minute rows, third arrives after another advance; duplicate-flush merge keeps counts correct.
- [ ] **Steps 2–5:** implement, green, commit `feat(persistence): transition recorder + rollup collector`.

---

### Task 9: RetentionPass + RetentionService

**Files:**
- Create: `ConPersistence/Rollups/RetentionPass.cs`, `ConPersistence/Services/RetentionService.cs`
- Test: `Tests/ConPersistence.Tests/RetentionTests.cs`

**Interfaces:**

```csharp
public class RetentionPass(ILogger log)
{
    // minute->hour for rows older than minuteRetention; hour->day older than hourlyRetention.
    // Per (target, coarse period): aggregate via RollupMerge, upsert coarse row, delete fine rows — one tx per batch.
    public Task<int> RunAsync(ConWatchDbContext db, DateTime nowUtc, PersistenceOptions options, CancellationToken ct);
}

public class RetentionService(IDbContextFactory<ConWatchDbContext> dbFactory,
    IOptions<PersistenceOptions> options, TimeProvider timeProvider,
    ILogger<RetentionService> log) : BackgroundService
{ /* run once at startup, then daily at RetentionRunHourUtc via TimeProvider delays */ }
```

- [ ] **Step 1: Failing tests**: synthesize 3 days of minute rows for 2 targets around the 90-day boundary → `RunAsync` converts only rows older than boundary into hourly rows with exact weighted math (assert one hand-computed hour), deletes converted minute rows, leaves newer ones; running twice is a no-op (idempotence); hour→day path with 14-month-old hourly rows; transitions table untouched.
- [ ] **Steps 2–5:** implement, green, commit `feat(persistence): retention downsampling pass + scheduler`.

---

### Task 10: ConWatch host — Program, WatcherHostService, health

**Files:**
- Create: `Programs/ConWatch/Program.cs` (replace template), `Programs/ConWatch/WatcherHostService.cs`, `Programs/ConWatch/appsettings.json`
- Test: `Tests/ConWatch.Tests/ConWatchFactory.cs`, `Tests/ConWatch.Tests/HostSmokeTests.cs`

**Interfaces:**

```csharp
public class WatcherHostService(IDbContextFactory<ConWatchDbContext> dbFactory, ConfigStore store,
    ConfigService configService, IPingTransceiverFactory pingFactory,
    IDomainNameResolverFactory resolverFactory, TimeProvider timeProvider,
    ILoggerFactory loggerFactory, IOptions<VerdictOptions> verdictOptions,
    IOptions<PersistenceOptions> persistenceOptions, IConfiguration configuration,
    TransitionRecorder recorder, RollupCollector collector) : IHostedService
{
    public WatcherEngine? Engine { get; }      // null until started / during rebuild
    public bool EngineRunning { get; }
    public Task RebuildAsync();                // stop -> reload config -> new engine -> reattach
    // StartAsync: Migrate() -> SeedAsync(appsettings "Watcher" section if bindable) -> StartEngineAsync()
    // ConfigChanged event -> debounce RebuildDebounceSeconds -> RebuildAsync
}
```

`Program.cs`: DI registrations exactly as spec §3/§6 (DbContextFactory with `Data Source={DbPath}` after `Directory.CreateDirectory`, singletons + hosted services for recorder/collector/retention/WatcherHostService, `AddOpenApi()`; `MapOpenApi()`; SwaggerUI at `/swagger` in Development pointing at `/openapi/v1.json`); `GET /api/v1/health` returns 200 `{ status, engineRunning, targetCount, uptime }` or 503 when engine not running; ends with `public partial class Program {}`. `ConWatchFactory : WebApplicationFactory<Program>` uses a temp-file DB, `RebuildDebounceSeconds=0`, `Verdict:AnchorPool=Internet`, and swaps in a shared `FakePingTransceiverFactory` instance exposed as a property; `DisposeAsync` deletes the temp DB (ignore IO errors).

- [ ] **Step 1: Failing tests**: factory boots; `GET /api/v1/health` → 200 with `engineRunning: true`, `targetCount: 0` (empty seed); DB file exists with Profiles == 2 rows.
- [ ] **Steps 2–5:** implement, green (`dotnet test Tests/ConWatch.Tests`), commit `feat(conwatch): app host, WatcherHostService, health endpoint`.

---

### Task 11: Config + status API endpoints

**Files:**
- Create: `Programs/ConWatch/Api/ConfigEndpoints.cs`, `Programs/ConWatch/Api/StatusEndpoints.cs`
- Test: `Tests/ConWatch.Tests/ConfigApiTests.cs`

**Interfaces:** `static void MapConfig(RouteGroupBuilder g)` / `MapStatus(...)` mapped from Program under `/api/v1`. Routes per spec §7: GET/POST list+create, GET/PUT/DELETE by id for `targets`, `profiles`, `pools` (DTOs from Task 5); `ConfigResult` mapping — Validation→400 `{errors}`, NotFound→404, Conflict→409; `GET /config/export` → `ConfigDocument`; `POST /config/import` → 400 or 204. `GET /status` → `{ verdict: { state, detail } | null, targets: [{ id, name, address, state, successRate, avgRtt, sampleSize }], pools: [{ id, name, state }] }` — from `WatcherHostService.Engine` (503 if engine down); target stats from `monitor.LastStatus`.

- [ ] **Step 1: Failing tests** (through `ConWatchFactory` over HTTP): POST fast profile + Router/Google/Cloudflare targets + Internet pool → all 201 with ids; GET /status eventually shows Router `Responding` and verdict `Healthy` (engine rebuilt on config change — proves the debounce path); POST target with unknown parent → 400 with message; DELETE Router → 409; export → import into a second factory → identical GET /targets.
- [ ] **Steps 2–5:** implement, green, commit `feat(conwatch): config CRUD, status, export/import endpoints`.

---

### Task 12: History API — transitions + rollups

**Files:**
- Create: `Programs/ConWatch/Api/HistoryEndpoints.cs`, `ConPersistence/HistoryQueries.cs`
- Test: `Tests/ConWatch.Tests/HistoryApiTests.cs` + unit tests in `Tests/ConPersistence.Tests/HistoryQueryTests.cs`

**Interfaces:**

```csharp
public static class HistoryQueries
{
    // newest first, keyset: WHERE Id < before ORDER BY Id DESC LIMIT limit (limit clamp 1..1000, default 100)
    public static Task<List<TransitionRow>> TransitionsAsync(ConWatchDbContext db,
        Guid? subjectId, DateTime? fromUtc, DateTime? toUtc, int limit, long? before);
    // resolution: "minute"|"hour"|"day"|"auto"; auto = finest whose retention window contains fromUtc
    public static string ResolveResolution(string requested, DateTime fromUtc, DateTime nowUtc, PersistenceOptions o);
    public static Task<List<RollupRow>> RollupsAsync(ConWatchDbContext db,
        Guid targetId, DateTime fromUtc, DateTime toUtc, string resolution);
}
```

Endpoints: `GET /transitions?subjectId&from&to&limit&before` → rows mapped to JSON (camelCase, timestamps ISO-Z, `nextBefore` = last row id when a full page was returned); `GET /rollups/{targetId}?from&to&resolution=auto` → `{ resolution, points: [{ periodStart, pingCount, successCount, rttAvg, rttMin, rttMax }] }`; 400 on missing/invalid range.

- [ ] **Step 1: Failing tests**: unit — `ResolveResolution` boundaries (89d→minute, 91d→hour, 14mo→day); keyset paging returns disjoint ordered pages; integration — after the Task 11 scenario plus a forced outage (fake sets Router down, wait for Down, restore), `GET /transitions` returns rows incl. a `ParentDown` reason for a descendant and verdict rows with `verdict` populated; `GET /rollups/{router}?resolution=minute` non-empty after ≥60s-equivalent of samples (use collector `FlushClosedAsync` via a test-only wait on rows instead of real minutes — assert ≥1 row exists after forcing a flush through elapsed fake time is not available here, so: keep profile interval 0.02s, wait for the *next real minute boundary* max 70s **only if needed**; simpler and preferred: call `POST /api/v1/debug/flush-rollups` mapped **in Development only** that invokes `RollupCollector.FlushClosedAsync` after a `TakeAll`-style flush of the current partial minute — implement as `FlushAllForTestsAsync()` on the collector).
- [ ] **Steps 2–5:** implement (`FlushAllForTestsAsync` included), green, commit `feat(conwatch): transitions + rollups query API`.

---

### Task 13: End-to-end scenarios, ConTinker check, wrap-up

**Files:**
- Test: `Tests/ConWatch.Tests/EndToEndTests.cs`
- Modify: `README.md` (one paragraph: what ConWatch is, how to run, where the DB lives)

- [ ] **Step 1: End-to-end test** (single factory, fast profiles): seed home config via API → all Responding, verdict Healthy → kill Google (fake) → verdict SiteIncident recorded in DB → kill Cloudflare too → InternetWeather → kill Router → LinkDown + descendants' ParentDown transitions in DB → restore all → verdict Healthy again; assert the full verdict sequence appears in `GET /transitions?subjectId=<verdict-guid>` in order.
- [ ] **Step 2:** full solution: `dotnet build ConnectionWatcher.sln` + all four test projects green.
- [ ] **Step 3:** run ConWatch manually 30s against the real LAN (real ICMP, seeded router+8.8.8.8 via API), confirm `/status` and Swagger UI locally. Document in README.
- [ ] **Step 4: Commit** `feat(conwatch): end-to-end verdict scenarios + docs`.

---

## Plan Self-Review (completed)

- **Spec coverage:** projects/deletions (T1), stable ids + PingSampleSource + kind (T2), verdict (T3), schema/migrations (T4), seed/load/export (T5), CRUD+validation+rebuild signal (T6), rollup math (T7), recorder/collector (T8), retention (T9), host+health (T10), CRUD/status/export API (T11), transitions/rollups API incl. auto resolution (T12), exit-criteria scenarios (T13). Retention config knobs in T4 `PersistenceOptions`.
- **Placeholder scan:** clean — abbreviated entity listings state exact members; test steps name exact assertions.
- **Type consistency:** DTO names (Task 5) reused in T6/T11; `RollupMerge.Combine` defined T8, used T9; `FlushAllForTestsAsync` defined T12 where consumed; `ConfigResult` kinds map to HTTP in T11.
