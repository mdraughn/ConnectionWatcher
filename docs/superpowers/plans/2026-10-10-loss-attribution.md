# Loss Attribution — Implementation Plan (Plan D of 4)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Tell real forwarding loss on our own path from a router that merely drops its own pings. Weather that our path really carries is `LinkDegraded`, blamed on the hop where forwarding loss starts. Weather that only *looks* like ours because a hop deprioritises ICMP is `InternetWeather`. A hop that keeps dropping its own probes while forwarding fine gets a quiet `ProbeOnlyLoss` admin note. Attribution never changes a target's own state.

**Architecture:**
- A new engine component, `LossWindows`, buckets every verdict-scope ping sample into clock-aligned 60 s windows and keeps the last 30 closed ones.
- A pure `LossAttribution` turns one window into a hop's own loss, forwarding loss and probe-only loss (spec corollary 3):
  - forwarding loss = the lower of its own loss and the lowest loss among the answering targets beyond it;
  - probe-only loss = own loss minus forwarding loss.
- ~~`VerdictCoordinator.Quality` uses the latest fresh window to decide `LinkDegraded` versus `InternetWeather` and reclassifies when a window closes.~~ *Removed 2026-10-12: the verdict does not read loss windows (see Revision, item 5).*
- `ModelCheckCoordinator` raises `ProbeOnlyLoss` from the last 10 usable windows.
- Plan B's ledger, status API, dashboard and tray carry the new kind with small additions.

**Tech Stack:** .NET 10, C#, System.Reactive, xunit 2.9.3, `FakeTimeProvider`; React 18 + vitest 2 (one type union); WinForms tray logic in `ConInfrastructure`.

**Spec:** `docs/superpowers/specs/2026-10-09-evidence-based-verdict-design.md`. This plan implements the rest of §4.5 (loss attribution: `LinkDegraded` versus `InternetWeather`, and "never changes a target's own state"), the `ProbeOnlyLoss` row of §4.6, decision §9.3 (on by default; ≥ 10 windows, probe-only ≥ 15 %, forwarding ≈ 0) and the §7 test bullet "probe-only loss annotation never changes state". That is rollout step 5 of §8.

## Global Constraints

- File-scoped namespaces; `Nullable` and `ImplicitUsings` enabled; match the surrounding comment density and naming.
- **Attribution never changes a target's own state** (spec §4.5) and never feeds `TopologyCoordinator`. It only chooses between `LinkDegraded` and `InternetWeather`, picks the blamed hop for `LinkDegraded`, and raises a note.
- `ProbeOnlyLoss` is a model warning, so D3 applies: never a toast, webhook, MQTT state, ISP-report count or verdict. It shows only where Plan B's notes show.
- **Thresholds (spec §9.3, made concrete):**
  - window span 60 s;
  - a target's window is *usable* with ≥ 5 samples;
  - a target beyond the hop is a *witness* in a window when it is usable and answered at least once in it;
  - `ProbeOnlyLoss` raises when, over the last 10 usable windows, average probe-only loss ≥ 15 % and average forwarding loss ≤ 2 %;
  - it clears when average probe-only loss falls below 10 % or average forwarding loss rises above 5 %.
- ~~**Attribution floor:** a path hop is blamed for weather only when its forwarding loss ≥ max(the anchors' median loss in that window, 5 %).~~ *Superseded 2026-10-12 by the veto (see Revision below).*
- Percentages in messages use `ToString("P0", CultureInfo.InvariantCulture)`, which renders as "25 %".
- Keep the verdict detail prefix `"{Verdict}: {Operator} — {Hop} is {State}"` (or `"{Verdict}: {Hop} is {State}"` without an operator). ~~The attributed `LinkDegraded` appends ` (forwarding loss NN %)`.~~ *Superseded 2026-10-12: no suffix.*
- **Never sleep-then-assert in tests:** wait on conditions. CI runners have 2 cores. Engine tests use `FakeTimeProvider` and feed samples directly.
- Test commands, per project (never the whole solution): `dotnet test Tests/ConEngine.Tests -c Debug`, `Tests/ConPersistence.Tests`, `Tests/ConWatch.Tests`; filter with `--filter "FullyQualifiedName~<Class>"`. UI: `cd Programs/ConWatch/ClientApp && npx vitest run && npm run build`. `ConWatch.Tests` has known cold-start flakes (`ProbeFieldTests`, the `SettingsApiTests` constructor, `NotifierIntegrationTests.VerdictOnly_…`). Rerun once before calling a failure real.
- Commit after every task. **Never push.** Commit messages carry no attribution lines. Work on a feature branch `loss-attribution` in a git worktree (`.worktrees/loss-attribution`). Do not edit `README.md` (the user has an uncommitted rewrite). Before merging, check `git status` in the MAIN checkout for stray edits.

## Decisions this plan makes (flag to Mark at review)

These are not in the spec.

1. **The windows are clock-aligned 60 s windows, not "burst-aligned".**
   - Why: spec §4.5 asks for samples "from the same sweep, the same time span, and a minimum sample count per target". But sweeps only start when a *healthy* anchor or trunk hop loses a ping. A router that steadily drops 25 % of its own pings sits in `Faltering`, so it triggers no further sweeps, and burst-only windows would never collect the evidence `ProbeOnlyLoss` needs.
   - Clock-aligned windows keep "same time span" and "minimum sample count". Bursts still enrich them, because burst pings land in the same window.
   - Profiles slower than one ping per 12 s (fewer than 5 samples a minute) never produce usable windows and fall back to the interim rule below.
2. ~~**When no fresh usable window exists, the verdict keeps Plan A's interim rule.** A window is fresh when it closed no more than two spans ago. The interim rule blames the deepest path hop that is Faltering, or Down with nothing answering beyond it. This happens for about the first minute after start, and on targets with slow profiles.~~ *Superseded 2026-10-12: the state rule always decides (shallowest hop); windows only veto.*
3. **The verdict reclassifies whenever a window closes.** ~~So weather attribution can change up to about 65 s after the loss starts. Expect a one-off `InternetWeather → LinkDegraded` (or the reverse) once the first window covering the trouble closes.~~ *Revised 2026-10-12: a closing window can only set or lift a veto, so expect at most a one-off `LinkDegraded → InternetWeather` when a probe-only hop is first vetoed (or the reverse when a veto lifts).*
4. **Windows are not carried across engine rebuilds or restarts.** After a config edit, `ProbeOnlyLoss` needs 10 fresh minutes to re-prove itself. ~~The ledger keeps the note meanwhile: its `ResyncGrace` grows from 6 to 12 min. A note for a deleted hop can therefore linger up to 12 min.~~ *Revised 2026-10-12: see ruling 4 below.*
5. **The `ProbeOnlyLoss` advice depends on the probe.** It says "consider a TCP probe" for an ICMP hop and "consider a different probe" for a TCP one.

### Revision 2026-10-12 (Mark, after final review)

The final review showed the per-window rule could not work: forwarding loss is min(own, lowest witness loss) and the anchors are witnesses, so forwarding ≤ the anchors' minimum ≤ their median, and "forwarding ≥ anchors' median" fired only on ties. Simulated real link loss was blamed in 3–24 % of windows, and the verdict flipped `InternetWeather ↔ LinkDegraded` 3.5–22 times an hour, each flip notifying. Mark chose option A:

1. **Verdict:** no per-window attribution and no forwarding-loss suffix. The state rule decides, blaming the **shallowest** eligible path hop (loss that starts at the router also shows at the modem). **Veto:** a hop is not eligible when the last 3 closed windows that ended within 4 spans, pooled, give `ForHop` forwarding < 5 %; once vetoed it lifts only at ≥ 10 % (or when there is no evidence). No windows → no veto.
2. **Tests** cover the veto, its hysteresis, the shallowest hop, stale windows, and stability: 15 windows of noisy real loss make one transition; 15 of noisy probe-only loss never leave weather.
3. **Model check:** a hop with a holding `ProbeOnlyLoss` does not also get the Faltering `HopBypassed` (one note, not two). Down hops keep `HopBypassed`.
4. **Ledger:** per-kind grace. `ResyncGrace` is back to 6 min for every kind but `ProbeOnlyLoss`, which gets `ProbeOnlyResyncGrace` = 15 min, so other notes for a deleted hop no longer linger.
5. **Veto removed (Mark's final ruling, 2026-10-12).** After a second review (the 3-window veto pre-vetoes healthy hops, so the onset of real loss reads `InternetWeather` and then `LinkDegraded`; and it flaps at 10–20 % real loss), Mark ruled to remove the veto. ~~Items 1 and 2 above (veto, hysteresis, stale windows, stability tests) are superseded.~~ The verdict uses the state rule with shallowest-hop blame and does not read loss windows; `WindowClosed`, `LatestFresh`, `Recent` and `Pool` are gone. Loss windows feed only the `ProbeOnlyLoss` note. A hop whose loss is only its own probe is blamed again until a statistically sound veto lands: **Plan D2** (paired probes, a sequential test calibrated by average run length).
6. **The cheap veto (2026-10-13).** The verdict never blames a path hop while that hop's `ProbeOnlyLoss` note holds (`VerdictCoordinator.UseProbeOnlyVeto`, wired in `WatcherEngine.StartAsync`): the note already proves the hop forwards fine, and banner and note cannot disagree. Until the note has proved itself (about 10 minutes after a start or rebuild) the hop is still blamed. Plan D2 is parked (`docs/superpowers/specs/2026-10-13-paired-probe-veto-design.md`).

## Review Focus

The failure modes most likely to bite, each pinned by a test in the named task:

1. **An ICMP-deprioritising router during internet weather.** The router falters on its own probes while one anchor beyond it is clean. Expect `InternetWeather`, not `LinkDegraded`. This is the false blame the interim rule makes today (Task 2).
2. **Real link loss during weather.** The router and everything beyond it lose about as much as the anchors. Expect `LinkDegraded` naming the router and its forwarding loss (Task 2).
3. **Late, interleaved samples across controller threads.** A sample for an already-closed window is dropped, never miscounted into the next window (Task 1).
4. **A steady probe-only router.** The note appears after 10 windows and not before. It clears with hysteresis, not on one clean window. The router's own state never changes because of it (Task 3).
5. ~~**Deadlock between the verdict and the window collector.**~~ *Moot 2026-10-12: the verdict no longer reads windows or subscribes to window closes.*

---

## File Structure

| File | Responsibility | Task |
|---|---|---|
| `Core/ConEngine/Topology/LossWindows.cs` (new) | `WindowCount`, `LossWindow`, `LossWindows` (collect, close, keep, publish) | 1 |
| `Core/ConEngine/Topology/LossAttribution.cs` (new) | Pure: `HopLoss`, `ForHop`, `TypicalLoss` | 1 |
| `Core/ConEngine/WatcherEngine.cs` | `Loss` property; record verdict-scope samples; hand to verdict and model check | 1, 2, 3 |
| `Core/ConEngine/Topology/VerdictCoordinator.cs` | ~~Attribution in `Quality`; reclassify on window close~~ State rule, shallowest-hop blame; no loss windows (2026-10-12) | 2 |
| `Core/ConEngine/Topology/ModelCheckCoordinator.cs` | `ProbeOnlyLoss` kind and evaluation | 3 |
| `Libraries/ConPersistence/Warnings/ModelWarningLedger.cs` | `ResyncGrace` covers the 10-window proof | 3 |
| `Libraries/ConInfrastructure/Tray/TrayStatus.cs`, `Programs/ConWatch/ClientApp/src/lib/types.ts` | Label and type for the new kind | 3 |

Tests: `Tests/ConEngine.Tests/{LossWindowsTests, LossAttributionTests, VerdictLossAttributionTests, ModelCheckCoordinatorTests, WatcherEngineTests}.cs`; `Tests/ConWatch.Tests/TrayStatusTests.cs`.

---

### Task 1: Loss windows and pure attribution

**Files:**
- Create: `Core/ConEngine/Topology/LossWindows.cs`, `Core/ConEngine/Topology/LossAttribution.cs`
- Modify: `Core/ConEngine/WatcherEngine.cs`
- Test: `Tests/ConEngine.Tests/LossWindowsTests.cs` (new), `Tests/ConEngine.Tests/LossAttributionTests.cs` (new), `Tests/ConEngine.Tests/WatcherEngineTests.cs`

**Interfaces:**
- Produces (namespace `ConEngine.Topology`):
  - `sealed record WindowCount(int Samples, int Lost)` with `double Loss`, `bool Usable`, `bool Answered`
  - `sealed record LossWindow(DateTimeOffset Start, IReadOnlyDictionary<Guid, WindowCount> Targets)` with `End`
  - `sealed class LossWindows` with:
    - constants `Span` (60 s), `CloseDelay` (5 s), `MinSamples` (5), `Keep` (30)
    - `static DateTimeOffset WindowStart(DateTimeOffset)`
    - `void Record(Guid targetId, DateTimeOffset at, bool success)`
    - `IReadOnlyList<LossWindow> Closed` (oldest first)
    - `LossWindow? LatestFresh(DateTimeOffset now)`
    - `WindowCount? Open(Guid targetId)`
    - `IObservable<LossWindow> WindowClosed`
  - `static class LossAttribution` with:
    - `const double MinAttributableLoss = 0.05`
    - `sealed record HopLoss(double Own, double Forwarding)` with `ProbeOnly`
    - `static HopLoss? ForHop(LossWindow w, Guid hop, IEnumerable<Guid> beyond)`
    - `static double? TypicalLoss(LossWindow w, IEnumerable<Guid> anchors)`
  - `WatcherEngine.Loss` (`LossWindows`, exists from construction)

- [ ] **Step 1: Write the failing collector tests**

Create `Tests/ConEngine.Tests/LossWindowsTests.cs`:

```csharp
using ConEngine.Topology;

namespace ConEngine.Tests;

public class LossWindowsTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-10-10T12:00:00Z"); // a window start
    private static readonly Guid A = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid B = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    private static void Fill(LossWindows w, Guid id, DateTimeOffset start, int samples, int lost)
    {
        for (int i = 0; i < samples; i++)
            w.Record(id, start + TimeSpan.FromSeconds(i * 50.0 / samples), success: i >= lost);
    }

    [Fact]
    public void Window_start_is_the_utc_minute()
    {
        Assert.Equal(T0, LossWindows.WindowStart(T0.AddSeconds(59.9)));
        Assert.Equal(T0.AddMinutes(1), LossWindows.WindowStart(T0.AddSeconds(60)));
    }

    [Fact]
    public void A_window_closes_once_a_sample_passes_its_end_plus_the_close_delay()
    {
        var w = new LossWindows();
        var closed = new List<LossWindow>();
        w.WindowClosed.Subscribe(closed.Add);
        Fill(w, A, T0, samples: 10, lost: 3);
        Fill(w, B, T0, samples: 6, lost: 0);

        w.Record(A, T0 + LossWindows.Span + LossWindows.CloseDelay - TimeSpan.FromMilliseconds(1), true);
        Assert.Empty(w.Closed); // still within the close delay

        w.Record(B, T0 + LossWindows.Span + LossWindows.CloseDelay, true);
        var window = Assert.Single(w.Closed);
        Assert.Same(window, Assert.Single(closed));
        Assert.Equal(T0, window.Start);
        Assert.Equal(new WindowCount(10, 3), window.Targets[A]);
        Assert.Equal(0.3, window.Targets[A].Loss, 3);
        Assert.True(window.Targets[B].Usable);
    }

    [Fact]
    public void Late_samples_for_a_closed_window_are_dropped_not_counted_in_the_next()
    {
        var w = new LossWindows();
        Fill(w, A, T0, 10, 0);
        w.Record(A, T0 + LossWindows.Span + LossWindows.CloseDelay, true); // closes T0
        w.Record(B, T0 + TimeSpan.FromSeconds(30), false);                  // late, for T0

        Assert.False(w.Closed[0].Targets.ContainsKey(B));
        Assert.Null(w.Open(B)); // and not in the open window either
    }

    [Fact]
    public void Usable_needs_the_minimum_samples_and_answered_needs_one_reply()
    {
        Assert.False(new WindowCount(LossWindows.MinSamples - 1, 0).Usable);
        Assert.True(new WindowCount(LossWindows.MinSamples, 0).Usable);
        Assert.False(new WindowCount(6, 6).Answered);
        Assert.True(new WindowCount(6, 5).Answered);
    }

    [Fact]
    public void Keeps_at_most_Keep_windows_oldest_first()
    {
        var w = new LossWindows();
        for (int i = 0; i <= LossWindows.Keep + 2; i++)
            Fill(w, A, T0.AddMinutes(i), 6, 0);
        w.Record(A, T0.AddMinutes(LossWindows.Keep + 4), true);

        Assert.Equal(LossWindows.Keep, w.Closed.Count);
        Assert.True(w.Closed[0].Start < w.Closed[^1].Start);
        Assert.Equal(T0.AddMinutes(LossWindows.Keep + 2), w.Closed[^1].Start);
    }

    [Fact]
    public void LatestFresh_ignores_a_stale_window()
    {
        var w = new LossWindows();
        Fill(w, A, T0, 6, 0);
        w.Record(A, T0 + LossWindows.Span + LossWindows.CloseDelay, true);
        var end = T0 + LossWindows.Span;

        Assert.NotNull(w.LatestFresh(end + LossWindows.Span * 2));
        Assert.Null(w.LatestFresh(end + LossWindows.Span * 2 + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void Open_reports_the_newest_open_windows_count()
    {
        var w = new LossWindows();
        Assert.Null(w.Open(A));
        Fill(w, A, T0, 3, 1);
        Assert.Equal(new WindowCount(3, 1), w.Open(A));
    }

    [Fact]
    public void Window_closed_is_published_outside_the_lock()
    {
        // A subscriber reading Closed from another thread inside OnNext would
        // deadlock if publication happened under the collector's lock.
        var w = new LossWindows();
        var reads = new List<int>();
        w.WindowClosed.Subscribe(_ =>
            reads.Add(Task.Run(() => w.Closed.Count).Wait(TimeSpan.FromSeconds(10)) ? 1 : 0));
        Fill(w, A, T0, 6, 0);
        w.Record(A, T0 + LossWindows.Span + LossWindows.CloseDelay, true);
        Assert.Equal([1], reads);
    }
}
```

- [ ] **Step 2: Write the failing attribution tests**

Create `Tests/ConEngine.Tests/LossAttributionTests.cs`:

```csharp
using ConEngine.Topology;

namespace ConEngine.Tests;

public class LossAttributionTests
{
    private static readonly Guid Hop = Guid.NewGuid(), B1 = Guid.NewGuid(), B2 = Guid.NewGuid(), B3 = Guid.NewGuid();

    private static LossWindow W(params (Guid Id, int Samples, int Lost)[] counts) =>
        new(DateTimeOffset.Parse("2026-10-10T12:00:00Z"),
            counts.ToDictionary(c => c.Id, c => new WindowCount(c.Samples, c.Lost)));

    [Fact]
    public void Forwarding_loss_is_capped_by_the_lowest_loss_beyond()
    {
        // Corollary 3: the router "loses" 40 % of its own pings, but one target
        // beyond it lost nothing, so nothing was lost in forwarding.
        var loss = LossAttribution.ForHop(W((Hop, 10, 4), (B1, 10, 0), (B2, 10, 3)), Hop, [B1, B2])!;
        Assert.Equal(0.4, loss.Own, 3);
        Assert.Equal(0.0, loss.Forwarding, 3);
        Assert.Equal(0.4, loss.ProbeOnly, 3);
    }

    [Fact]
    public void Real_forwarding_loss_shows_at_the_hop_and_everything_beyond()
    {
        var loss = LossAttribution.ForHop(W((Hop, 10, 3), (B1, 10, 3), (B2, 20, 8)), Hop, [B1, B2])!;
        Assert.Equal(0.3, loss.Forwarding, 3);
        Assert.Equal(0.0, loss.ProbeOnly, 3);
    }

    [Fact]
    public void Forwarding_loss_never_exceeds_the_hops_own_loss()
    {
        var loss = LossAttribution.ForHop(W((Hop, 10, 1), (B1, 10, 5)), Hop, [B1])!;
        Assert.Equal(0.1, loss.Forwarding, 3);
    }

    [Fact]
    public void Without_enough_samples_or_a_witness_there_is_no_answer()
    {
        Assert.Null(LossAttribution.ForHop(W((Hop, 4, 0), (B1, 10, 0)), Hop, [B1]));   // hop not usable
        Assert.Null(LossAttribution.ForHop(W((Hop, 10, 0), (B1, 4, 0)), Hop, [B1]));   // witness not usable
        Assert.Null(LossAttribution.ForHop(W((Hop, 10, 0), (B1, 10, 10)), Hop, [B1])); // never answered
        Assert.Null(LossAttribution.ForHop(W((Hop, 10, 0)), Hop, [B1]));               // no samples beyond
    }

    [Fact]
    public void Typical_loss_is_the_median_of_usable_anchors()
    {
        Assert.Equal(0.3, LossAttribution.TypicalLoss(W((B1, 10, 1), (B2, 10, 3), (B3, 10, 9)), [B1, B2, B3])!.Value, 3);
        Assert.Equal(0.2, LossAttribution.TypicalLoss(W((B1, 10, 1), (B2, 10, 3)), [B1, B2])!.Value, 3);
        Assert.Equal(0.1, LossAttribution.TypicalLoss(W((B1, 10, 1), (B2, 3, 3)), [B1, B2])!.Value, 3); // B2 unusable
        Assert.Null(LossAttribution.TypicalLoss(W((B1, 3, 0)), [B1]));
    }
}
```

- [ ] **Step 3: Write the failing engine-wiring test**

In `WatcherEngineTests.cs` (fixture: Router → Google, Cloudflare, started in `InitializeAsync` with real time; helpers `Eventually`, `State`; `_factory` is the fake pings), add `using ConEngine.Topology;` and:

```csharp
    [Fact]
    public async Task Verdict_scope_ping_samples_feed_the_loss_windows()
    {
        var google = _engine.Graph.GetByName("Google").Id;
        await Eventually(() => _engine.Loss.Open(google) is { Samples: > 0 });
    }

    [Fact]
    public void The_loss_windows_exist_before_start()
    {
        var engine = new WatcherEngine(TimeProvider.System, new FakePingTransceiverFactory(),
            new ConInfrastructure.DomainNameResolverFactory(), NullLoggerFactory.Instance,
            Microsoft.Extensions.Options.Options.Create(Options()));
        Assert.NotNull(engine.Loss);
        Assert.Empty(engine.Loss.Closed);
    }
```

Local-scope targets are excluded the same way suspicion excludes them. Add a line to `LocalScopeEngineTests` if that class already starts an engine with a Local target, using that class's own names:

```csharp
            Assert.Null(engine.Loss.Open(engine.Graph.GetByName("NAS").Id));
```

Place it after the engine has run long enough for the Router to have samples (`await Eventually(() => engine.Loss.Open(routerId) is { Samples: > 0 })` first). If that class has no started engine with a Local target, skip this line and note it in the report.

- [ ] **Step 4: Run them to verify they fail**

Run: `dotnet test Tests/ConEngine.Tests -c Debug --filter "FullyQualifiedName~LossWindowsTests|FullyQualifiedName~LossAttributionTests|FullyQualifiedName~WatcherEngineTests"`
Expected: build FAILS (`LossWindows` not found). Capture the output.

- [ ] **Step 5: Implement `LossWindows`**

Create `Core/ConEngine/Topology/LossWindows.cs`:

```csharp
using System.Collections.Concurrent;
using System.Reactive.Subjects;

namespace ConEngine.Topology;

/// <summary>One target's samples in one window.</summary>
public sealed record WindowCount(int Samples, int Lost)
{
    public double Loss => Samples == 0 ? 0 : (double)Lost / Samples;
    /// <summary>Enough samples to compare with other targets in the same window.</summary>
    public bool Usable => Samples >= LossWindows.MinSamples;
    /// <summary>Got at least one reply in the window.</summary>
    public bool Answered => Lost < Samples;
}

/// <summary>Every target's samples over the same time span.</summary>
public sealed record LossWindow(DateTimeOffset Start, IReadOnlyDictionary<Guid, WindowCount> Targets)
{
    public DateTimeOffset End => Start + LossWindows.Span;
}

/// <summary>
/// Buckets verdict-scope ping samples into clock-aligned windows so losses of
/// different targets can be compared over the same span (spec 2026-10-09
/// §4.5). Bursts land in the same windows and only add samples. A window
/// closes once a sample arrives past its end plus <see cref="CloseDelay"/>
/// (controllers run on separate threads; a little lateness is normal); a
/// sample for an already-closed window is dropped. Closed windows are
/// published outside the lock, in order.
/// </summary>
public sealed class LossWindows
{
    public static readonly TimeSpan Span = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan CloseDelay = TimeSpan.FromSeconds(5);
    public const int MinSamples = 5;
    public const int Keep = 30;

    private readonly Lock _lock = new();
    private readonly Lock _publishLock = new();
    private readonly SortedDictionary<DateTimeOffset, Dictionary<Guid, WindowCount>> _open = new();
    private readonly List<LossWindow> _closed = new();
    private readonly ConcurrentQueue<LossWindow> _outbox = new();
    private readonly Subject<LossWindow> _windowClosed = new();
    private DateTimeOffset? _closedThrough;

    public static DateTimeOffset WindowStart(DateTimeOffset at) =>
        new(at.UtcTicks - at.UtcTicks % Span.Ticks, TimeSpan.Zero);

    /// <summary>A window closed. Published outside the collector's lock.</summary>
    public IObservable<LossWindow> WindowClosed => _windowClosed;

    /// <summary>Closed windows, oldest first, at most <see cref="Keep"/>.</summary>
    public IReadOnlyList<LossWindow> Closed
    {
        get { lock (_lock) { return _closed.ToList(); } }
    }

    /// <summary>The newest closed window if it ended no more than two spans before <paramref name="now"/>.</summary>
    public LossWindow? LatestFresh(DateTimeOffset now)
    {
        lock (_lock)
        {
            return _closed.Count > 0 && now - _closed[^1].End <= Span * 2 ? _closed[^1] : null;
        }
    }

    /// <summary>This target's count in the newest open window, or null.</summary>
    public WindowCount? Open(Guid targetId)
    {
        lock (_lock)
        {
            return _open.Count > 0 && _open.Last().Value.TryGetValue(targetId, out var count) ? count : null;
        }
    }

    public void Record(Guid targetId, DateTimeOffset at, bool success)
    {
        lock (_lock)
        {
            var start = WindowStart(at);
            if (_closedThrough is { } through && start < through)
                return; // late: that window is already closed
            if (!_open.TryGetValue(start, out var counts))
                _open[start] = counts = new Dictionary<Guid, WindowCount>();
            var prior = counts.GetValueOrDefault(targetId) ?? new WindowCount(0, 0);
            counts[targetId] = new WindowCount(prior.Samples + 1, prior.Lost + (success ? 0 : 1));

            foreach (var due in _open.Keys.Where(s => s + Span + CloseDelay <= at).ToList())
            {
                var window = new LossWindow(due, _open[due]);
                _open.Remove(due);
                _closed.Add(window);
                if (_closed.Count > Keep)
                    _closed.RemoveAt(0);
                _closedThrough = window.End;
                _outbox.Enqueue(window);
            }
        }
        Drain();
    }

    /// <summary>Same discipline as the coordinators: TryEnter, so a thread that
    /// finds another draining leaves its items to that thread.</summary>
    private void Drain()
    {
        while (!_outbox.IsEmpty)
        {
            if (!_publishLock.TryEnter())
                return;
            try
            {
                while (_outbox.TryDequeue(out var window))
                    _windowClosed.OnNext(window);
            }
            finally
            {
                _publishLock.Exit();
            }
        }
    }
}
```

- [ ] **Step 6: Implement `LossAttribution`**

Create `Core/ConEngine/Topology/LossAttribution.cs`:

```csharp
namespace ConEngine.Topology;

/// <summary>
/// Spec 2026-10-09 §4.5, corollary 3: real forwarding loss at a hop shows up
/// at everything beyond it, so a hop's forwarding loss cannot exceed the
/// lowest loss seen beyond it. Loss above that figure belongs to the hop's
/// own probe (rate limiting, a busy control plane), not to the path. Pure;
/// never changes any state.
/// </summary>
public static class LossAttribution
{
    /// <summary>Below this, loss is noise, never blame.</summary>
    public const double MinAttributableLoss = 0.05;

    /// <param name="Own">The hop's own probe loss in the window.</param>
    /// <param name="Forwarding">The part of it that is real forwarding loss.</param>
    public sealed record HopLoss(double Own, double Forwarding)
    {
        /// <summary>Loss of the hop's own probe only.</summary>
        public double ProbeOnly => Own - Forwarding;
    }

    /// <summary>Null when the hop has too few samples, or nothing beyond it is a
    /// witness (usable and answered at least once).</summary>
    public static HopLoss? ForHop(LossWindow window, Guid hop, IEnumerable<Guid> beyond)
    {
        if (!window.Targets.TryGetValue(hop, out var own) || !own.Usable)
            return null;
        var witnesses = beyond
            .Select(id => window.Targets.GetValueOrDefault(id))
            .Where(c => c is { Usable: true, Answered: true })
            .Select(c => c!.Loss)
            .ToList();
        if (witnesses.Count == 0)
            return null;
        return new HopLoss(own.Loss, Math.Min(own.Loss, witnesses.Min()));
    }

    /// <summary>The anchors' median loss in the window (usable anchors only), or null.</summary>
    public static double? TypicalLoss(LossWindow window, IEnumerable<Guid> anchors)
    {
        var losses = anchors
            .Select(id => window.Targets.GetValueOrDefault(id))
            .Where(c => c is { Usable: true })
            .Select(c => c!.Loss)
            .Order()
            .ToList();
        if (losses.Count == 0)
            return null;
        int mid = losses.Count / 2;
        return losses.Count % 2 == 1 ? losses[mid] : (losses[mid - 1] + losses[mid]) / 2;
    }
}
```

- [ ] **Step 7: Wire the engine**

In `WatcherEngine`:
- Add the property next to `Suspicion`:

```csharp
    /// <summary>Verdict-scope ping samples in clock-aligned windows, for loss
    /// attribution (spec §4.5). Exists from construction.</summary>
    public LossWindows Loss { get; } = new();
```

- In the ping-routing lambda, inside the existing `if (monitor.Target.Scope != TargetScope.Local)` block (which today calls `Suspicion?.OnPingSample`), make it a block with both calls:

```csharp
                    if (monitor.Target.Scope != TargetScope.Local)
                    {
                        Suspicion?.OnPingSample(monitor.Target.Id, result);
                        Loss.Record(monitor.Target.Id, result.Timestamp, result.Success);
                    }
```

- [ ] **Step 8: Run the tests to verify they pass**

Run: `dotnet test Tests/ConEngine.Tests -c Debug`
Expected: all PASS.

- [ ] **Step 9: Commit**

```bash
git add Core/ConEngine Tests/ConEngine.Tests
git commit -m "feat(engine): clock-aligned loss windows and pure forwarding-loss attribution"
```

---

### Task 2: The verdict attributes weather from the windows

**Files:**
- Modify: `Core/ConEngine/Topology/VerdictCoordinator.cs` (constructor, `Start`, `Quality`), `Core/ConEngine/WatcherEngine.cs` (pass `Loss`)
- Test: `Tests/ConEngine.Tests/VerdictLossAttributionTests.cs` (new)

**Interfaces:**
- Consumes: `LossWindows` (`LatestFresh`, `WindowClosed`, `Span`, `CloseDelay`, `WindowStart`, `Record`), `LossAttribution.ForHop`, `LossAttribution.TypicalLoss`, `LossAttribution.MinAttributableLoss` (Task 1).
- Produces: `VerdictCoordinator(TargetGraph graph, PoolDefinition anchorPool, IReadOnlyDictionary<Guid, TargetMonitor> monitors, int weatherThreshold, TimeProvider timeProvider, ILogger<VerdictCoordinator> log, LossWindows? loss = null)`. The new parameter is last and optional, so existing tests keep the interim rule.

- [ ] **Step 1: Write the failing tests**

Create `Tests/ConEngine.Tests/VerdictLossAttributionTests.cs`:

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

/// <summary>Spec 2026-10-09 §4.5: LinkDegraded only when the loss is our path's.</summary>
public class VerdictLossAttributionTests : IDisposable
{
    private static readonly PingProfile Fast = new() { Name = "Fast", HistorySize = 20, MinUsableSample = 5 };

    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-10-10T12:00:00Z"));
    private readonly TargetGraph _graph;
    private readonly Dictionary<Guid, TargetMonitor> _monitors = new();
    private readonly TopologyCoordinator _topology;
    private readonly LossWindows _loss = new();
    private readonly VerdictCoordinator _verdict;

    public VerdictLossAttributionTests()
    {
        _graph = TargetGraph.FromOptions(new WatcherOptions
        {
            Targets =
            [
                new TargetConfig { Name = "Router", Address = "192.168.1.1", Profile = "Internal" },
                new TargetConfig { Name = "Modem", Address = "192.168.100.1", Parent = "Router", Profile = "Internal", Operator = "Comcast" },
                new TargetConfig { Name = "Google", Address = "8.8.8.8", Parent = "Modem" },
                new TargetConfig { Name = "Cloudflare", Address = "1.1.1.1", Parent = "Modem" },
                new TargetConfig { Name = "Quad9", Address = "9.9.9.9", Parent = "Modem" },
            ],
            Pools = [new PoolConfig { Name = "Internet", Members = ["Google", "Cloudflare", "Quad9"] }],
        });
        foreach (var target in _graph.Targets.Values)
        {
            var monitor = new TargetMonitor(target, Fast, new PingDiscipline(Fast, _time), _time, NullLoggerFactory.Instance);
            monitor.StartTracking(IPAddress.Parse(target.Address));
            _monitors[target.Id] = monitor;
        }
        _topology = new TopologyCoordinator(_graph, _monitors, NullLogger<TopologyCoordinator>.Instance);
        _topology.Start();
        _verdict = new VerdictCoordinator(_graph, _graph.GetPoolByName("Internet"), _monitors,
            weatherThreshold: 2, _time, NullLogger<VerdictCoordinator>.Instance, _loss);
        _verdict.Start();
    }

    public void Dispose()
    {
        _verdict.Dispose();
        _topology.Dispose();
        foreach (var m in _monitors.Values)
            m.Dispose();
    }

    private Guid Id(string name) => _graph.GetByName(name).Id;

    /// <summary>Drives a monitor's state (the state machine), not the windows.</summary>
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

    /// <summary>Records one window's samples for the given targets, then closes
    /// it with one sample past the close delay (time moves forward to it).</summary>
    private void CloseWindow(params (string Name, int Samples, int Lost)[] targets)
    {
        var start = LossWindows.WindowStart(_time.GetUtcNow());
        foreach (var (name, samples, lost) in targets)
            for (int i = 0; i < samples; i++)
                _loss.Record(Id(name), start + TimeSpan.FromSeconds(i * 50.0 / samples), success: i >= lost);
        _time.SetUtcNow(start + LossWindows.Span + LossWindows.CloseDelay);
        _loss.Record(Id("Router"), _time.GetUtcNow(), true);
    }

    private void Healthy()
    {
        foreach (var name in new[] { "Router", "Modem", "Google", "Cloudflare", "Quad9" })
            Feed(name, 5, true);
    }

    private void WeatherWithAFalteringModem()
    {
        Healthy();
        Feed("Google", 20, false);
        Feed("Cloudflare", 20, false); // two anchors down: weather level (threshold 2)
        Feed("Modem", 3, false);       // and the modem's own probe falters
    }

    [Fact]
    public void Without_a_window_the_interim_rule_still_blames_a_faltering_hop()
    {
        WeatherWithAFalteringModem();
        Assert.Equal(VerdictState.LinkDegraded, _verdict.CurrentVerdict);
    }

    [Fact]
    public void A_modem_dropping_only_its_own_pings_is_not_blamed_for_weather()
    {
        // Review Focus 1: Quad9 beyond the modem lost nothing, so the modem forwarded everything.
        WeatherWithAFalteringModem();
        CloseWindow(("Router", 12, 0), ("Modem", 12, 5), ("Google", 12, 12), ("Cloudflare", 12, 6), ("Quad9", 12, 0));

        Assert.Equal(VerdictState.InternetWeather, _verdict.CurrentVerdict);
    }

    [Fact]
    public void Real_loss_from_the_modem_onward_is_link_degraded_with_its_forwarding_loss()
    {
        // Review Focus 2.
        WeatherWithAFalteringModem();
        CloseWindow(("Router", 12, 0), ("Modem", 12, 4), ("Google", 12, 5), ("Cloudflare", 12, 4), ("Quad9", 12, 4));

        Assert.Equal(VerdictState.LinkDegraded, _verdict.CurrentVerdict);
        Assert.Equal("LinkDegraded: Comcast — Modem is Faltering (forwarding loss 33 %)", _verdict.CurrentDetail);
    }

    [Fact]
    public void Loss_below_the_anchors_typical_loss_is_not_the_paths()
    {
        WeatherWithAFalteringModem();
        // Modem forwards 1 in 12 lost (8 %); the anchors typically lose 50 %.
        CloseWindow(("Router", 12, 0), ("Modem", 12, 1), ("Google", 12, 6), ("Cloudflare", 12, 6), ("Quad9", 12, 1));
        Assert.Equal(VerdictState.InternetWeather, _verdict.CurrentVerdict);
    }

    [Fact]
    public void A_closing_window_reclassifies_without_any_state_change()
    {
        WeatherWithAFalteringModem();
        CloseWindow(("Router", 12, 0), ("Modem", 12, 5), ("Google", 12, 12), ("Cloudflare", 12, 6), ("Quad9", 12, 0));
        Assert.Equal(VerdictState.InternetWeather, _verdict.CurrentVerdict);

        var states = _monitors.ToDictionary(kv => kv.Key, kv => kv.Value.CurrentState);
        CloseWindow(("Router", 12, 0), ("Modem", 12, 4), ("Google", 12, 5), ("Cloudflare", 12, 4), ("Quad9", 12, 4));

        Assert.Equal(VerdictState.LinkDegraded, _verdict.CurrentVerdict);
        Assert.Equal(states, _monitors.ToDictionary(kv => kv.Key, kv => kv.Value.CurrentState)); // attribution never changes a state
    }

    [Fact]
    public void A_stale_window_falls_back_to_the_interim_rule()
    {
        Healthy();
        CloseWindow(("Router", 12, 0), ("Modem", 12, 5), ("Google", 12, 0), ("Cloudflare", 12, 0), ("Quad9", 12, 0));
        _time.Advance(LossWindows.Span * 3); // the window is now stale
        Feed("Google", 20, false);
        Feed("Cloudflare", 20, false);
        Feed("Modem", 3, false);
        Assert.Equal(VerdictState.LinkDegraded, _verdict.CurrentVerdict); // interim: faltering hop
    }
}
```

The expected forwarding loss in `Real_loss_…` is min(Modem 4/12, beyond min(5/12, 4/12, 4/12)) = 33 %. The median anchor loss is 4/12 = 33 % ≥ the 5 % floor, and 33 % ≥ 33 %, so Modem is blamed. Router (0 % own loss) is never blamed.

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test Tests/ConEngine.Tests -c Debug --filter "FullyQualifiedName~VerdictLossAttributionTests"`
Expected: build FAILS (the constructor takes no `LossWindows`).

- [ ] **Step 3: Implement**

In `VerdictCoordinator`:
- Add the field `private readonly LossWindows? _loss;` and the last constructor parameter `LossWindows? loss = null`, assigned in the constructor.
- In `Start()`, after subscribing to the monitors:

```csharp
        // A closed window can change attribution with no monitor transition.
        if (_loss is not null)
            _subscriptions.Add(_loss.WindowClosed.Subscribe(_ => Recalculate()));
```

- In `Quality`, replace the block from `// A Down hop that something beyond answers through is bypassed …` through `if (losingHop.Monitor is not null) return Wording(…);` with:

```csharp
        // Is the weather our own path's? (spec §4.5) From the latest fresh
        // window: the deepest path hop whose FORWARDING loss — its own loss,
        // capped by the lowest loss among the witnesses beyond it — is at least
        // what the anchors typically lose. A hop that only drops its own
        // probes forwards fine and is never blamed.
        if (AttributeLoss() is { } attributed)
        {
            if (attributed.Hop is { } hop)
                return Wording(VerdictState.LinkDegraded, hop,
                    $"{hop.Target.Name} is {states[hop.Target.Id]} (forwarding loss " +
                    $"{attributed.Forwarding.ToString("P0", CultureInfo.InvariantCulture)})");
        }
        else
        {
            // No fresh usable window yet (first minute, slow profiles): the
            // interim state rule. A Down hop that something beyond answers
            // through is bypassed (its probe lies): never blamed (M1).
            … keep the existing `losingHop` computation and its `return Wording(…)` unchanged …
        }
```

- Add the helper below `Quality`:

```csharp
    /// <summary>Null: no fresh usable window, so use the interim rule. Hop null:
    /// the window shows no path hop to blame (internet weather).</summary>
    private (TargetMonitor? Hop, double Forwarding)? AttributeLoss()
    {
        if (_loss?.LatestFresh(_time.GetUtcNow()) is not { } window ||
            LossAttribution.TypicalLoss(window, _anchors.Select(a => a.Target.Id)) is not { } typical)
            return null;
        var floor = Math.Max(typical, LossAttribution.MinAttributableLoss);
        foreach (var (hop, _) in _ancestors.OrderByDescending(a => a.Depth))
        {
            if (LossAttribution.ForHop(window, hop.Target.Id, _descendants[hop.Target.Id]) is { } loss &&
                loss.Forwarding >= floor)
                return (hop, loss.Forwarding);
        }
        return (null, 0);
    }
```

- Add `using System.Globalization;`.
- Update the `Quality` doc comment: "Anchor evidence decides quality. Weather is our link's only when a path hop's forwarding loss explains it (spec §4.5); until a fresh window exists, the interim state rule decides."

In `WatcherEngine.StartAsync`, pass `Loss` as the last argument of `new VerdictCoordinator(...)`.

- [ ] **Step 4: Run the tests**

Run: `dotnet test Tests/ConEngine.Tests -c Debug`
Expected: all PASS. The existing `VerdictCoordinatorTests` build their coordinator without `LossWindows` and keep passing on the interim rule.

- [ ] **Step 5: Commit**

```bash
git add Core/ConEngine Tests/ConEngine.Tests
git commit -m "feat(verdict): weather is our link's only when a path hop's forwarding loss explains it"
```

---

### Task 3: `ProbeOnlyLoss` model warning

**Files:**
- Modify: `Core/ConEngine/Topology/ModelCheckCoordinator.cs`, `Core/ConEngine/WatcherEngine.cs` (pass `Loss`), `Libraries/ConPersistence/Warnings/ModelWarningLedger.cs` (`ResyncGrace`), `Libraries/ConInfrastructure/Tray/TrayStatus.cs` (`NoteLabel`), `Programs/ConWatch/ClientApp/src/lib/types.ts` (`ModelWarning.kind`)
- Test: `Tests/ConEngine.Tests/ModelCheckCoordinatorTests.cs`, `Tests/ConWatch.Tests/TrayStatusTests.cs`

**Interfaces:**
- Consumes: `LossWindows`, `LossAttribution.ForHop` (Task 1).
- Produces:
  - `ModelWarningKind.ProbeOnlyLoss` (appended last), key `probe-only-loss:{id:D}`
  - constants `ProbeOnlyWindows = 10`, `ProbeOnlyRaise = 0.15`, `ProbeOnlyClear = 0.10`, `ForwardingRaise = 0.02`, `ForwardingClear = 0.05`, `ProbeOnlyProveTime` (= `LossWindows.Span * ProbeOnlyWindows`)
  - `ModelCheckCoordinator(..., ILogger<ModelCheckCoordinator> log, LossWindows? loss = null)`
  - `ModelWarningLedger.ResyncGrace` = max(`NeverAnsweredAfter`, `ProbeOnlyProveTime`) + 2 min (12 min)
  - tray label "{target} drops its own pings (forwards fine)"

- [ ] **Step 1: Write the failing coordinator tests**

In `ModelCheckCoordinatorTests.cs` (fixture: Router → Inside (`tcp:80`) → External → Google, Cloudflare; `_time`, `_monitors`, `Id`, `Feed`, `AllHealthy`, `_changes`), add a `LossWindows` the coordinator can use. Change `StartCheck` to take it:

```csharp
    private readonly LossWindows _loss = new();

    private void StartCheck(bool unprovableUp = false)
    {
        _check = new ModelCheckCoordinator(_graph, _monitors, unprovableUp, _time,
            NullLogger<ModelCheckCoordinator>.Instance, _loss);
        _check.ChangeSource.Subscribe(_changes.Add);
        _check.Start();
    }

    /// <summary>One closed window: Inside loses <paramref name="insideLost"/> of 12,
    /// External loses <paramref name="beyondLost"/> of 12, the rest nothing.</summary>
    private void CloseWindow(int insideLost, int beyondLost = 0)
    {
        var start = LossWindows.WindowStart(_time.GetUtcNow());
        void Fill(string name, int lost)
        {
            for (int i = 0; i < 12; i++)
                _loss.Record(Id(name), start + TimeSpan.FromSeconds(i * 4), success: i >= lost);
        }
        Fill("Router", 0);
        Fill("Inside", insideLost);
        Fill("External", beyondLost);
        Fill("Google", beyondLost);
        Fill("Cloudflare", beyondLost);
        _time.SetUtcNow(start + LossWindows.Span + LossWindows.CloseDelay);
        _loss.Record(Id("Router"), _time.GetUtcNow(), true);
        _time.Advance(ModelCheckCoordinator.EvaluateEvery); // let the periodic evaluation run
    }
```

Existing tests call `StartCheck()` unchanged; with no samples recorded the new check never fires for them. Then add:

```csharp
    [Fact]
    public void A_hop_dropping_only_its_own_pings_is_a_note_after_ten_windows_and_not_before()
    {
        StartCheck();
        AllHealthy();
        for (int i = 0; i < ModelCheckCoordinator.ProbeOnlyWindows - 1; i++)
            CloseWindow(insideLost: 3); // 25 % of its own probes, 0 % beyond
        Assert.DoesNotContain(_check.Active, c => c.Kind == ModelWarningKind.ProbeOnlyLoss);

        CloseWindow(insideLost: 3);
        var note = Assert.Single(_check.Active, c => c.Kind == ModelWarningKind.ProbeOnlyLoss);
        Assert.Equal($"probe-only-loss:{Id("Inside"):D}", note.Key);
        Assert.Equal("Inside drops some of its own pings but forwards fine (loss 25 % · forwarding 0 % " +
            "over the last 10 minutes) — common router behaviour; consider a different probe.", note.Message);
    }

    [Fact]
    public void Real_forwarding_loss_is_never_probe_only()
    {
        StartCheck();
        AllHealthy();
        for (int i = 0; i < ModelCheckCoordinator.ProbeOnlyWindows; i++)
            CloseWindow(insideLost: 3, beyondLost: 3); // everything beyond loses as much
        Assert.DoesNotContain(_check.Active, c => c.Kind == ModelWarningKind.ProbeOnlyLoss);
    }

    [Fact]
    public void The_note_clears_with_hysteresis_not_on_one_clean_window()
    {
        StartCheck();
        AllHealthy();
        for (int i = 0; i < ModelCheckCoordinator.ProbeOnlyWindows; i++)
            CloseWindow(insideLost: 3);
        Assert.Contains(_check.Active, c => c.Kind == ModelWarningKind.ProbeOnlyLoss);

        CloseWindow(insideLost: 0); // average now 22.5 %: still above the 10 % clear line
        Assert.Contains(_check.Active, c => c.Kind == ModelWarningKind.ProbeOnlyLoss);

        for (int i = 0; i < 6; i++)
            CloseWindow(insideLost: 0); // average now 7.5 %
        Assert.DoesNotContain(_check.Active, c => c.Kind == ModelWarningKind.ProbeOnlyLoss);
    }

    [Fact]
    public void Probe_only_loss_never_changes_any_targets_state()
    {
        StartCheck();
        AllHealthy();
        var before = _monitors.ToDictionary(kv => kv.Key, kv => kv.Value.CurrentState);
        for (int i = 0; i < ModelCheckCoordinator.ProbeOnlyWindows; i++)
            CloseWindow(insideLost: 3);
        Assert.Contains(_check.Active, c => c.Kind == ModelWarningKind.ProbeOnlyLoss);
        Assert.Equal(before, _monitors.ToDictionary(kv => kv.Key, kv => kv.Value.CurrentState));
    }

    [Fact]
    public void An_icmp_hop_is_advised_to_try_tcp()
    {
        StartCheck();
        AllHealthy();
        for (int i = 0; i < ModelCheckCoordinator.ProbeOnlyWindows; i++)
        {
            // Router (ICMP) is the hop this time: it loses 3 of 12, everything beyond nothing.
            var start = LossWindows.WindowStart(_time.GetUtcNow());
            foreach (var name in new[] { "Router", "Inside", "External", "Google", "Cloudflare" })
                for (int s = 0; s < 12; s++)
                    _loss.Record(Id(name), start + TimeSpan.FromSeconds(s * 4), success: name != "Router" || s >= 3);
            _time.SetUtcNow(start + LossWindows.Span + LossWindows.CloseDelay);
            _loss.Record(Id("Inside"), _time.GetUtcNow(), true);
            _time.Advance(ModelCheckCoordinator.EvaluateEvery);
        }
        var note = Assert.Single(_check.Active, c => c.Kind == ModelWarningKind.ProbeOnlyLoss);
        Assert.EndsWith("consider a TCP probe.", note.Message);
    }
```

In `TrayStatusTests.cs`, add:

```csharp
    [Fact]
    public void Probe_only_loss_has_a_short_label()
    {
        var info = TrayStatus.FromStatusJson(With("""
            [{"key":"p","kind":"ProbeOnlyLoss","targetName":"Router","message":"m","since":"x","acknowledged":false}]
            """));
        Assert.Equal(["Router drops its own pings (forwards fine)"], info.Notes);
    }
```

- [ ] **Step 2: Run them to verify they fail**

Run:
```bash
dotnet test Tests/ConEngine.Tests -c Debug --filter "FullyQualifiedName~ModelCheckCoordinatorTests"
dotnet test Tests/ConWatch.Tests -c Debug --filter "FullyQualifiedName~TrayStatusTests"
```
Expected: build FAILS (`ProbeOnlyLoss`, the `loss` parameter), and the tray label test FAILS ("ConnectionWatcher note").

- [ ] **Step 3: Implement the check**

In `ModelCheckCoordinator.cs`:
- Append `ProbeOnlyLoss` to `ModelWarningKind` with the doc comment `/// <summary>A hop keeps dropping its own probes while forwarding fine (spec §4.5, §4.6).</summary>`.
- In `ModelCondition.KeyFor`, add `ModelWarningKind.ProbeOnlyLoss => "probe-only-loss",`.
- Add the constants:

```csharp
    /// <summary>ProbeOnlyLoss needs this many usable windows (spec §9.3).</summary>
    public const int ProbeOnlyWindows = 10;
    public const double ProbeOnlyRaise = 0.15;
    public const double ProbeOnlyClear = 0.10;
    /// <summary>"Forwarding loss about 0" to raise, and the most it may grow to stay raised.</summary>
    public const double ForwardingRaise = 0.02;
    public const double ForwardingClear = 0.05;
    /// <summary>How long a ProbeOnlyLoss takes to prove itself from nothing.</summary>
    public static readonly TimeSpan ProbeOnlyProveTime = LossWindows.Span * ProbeOnlyWindows;
```

- Add the field `private readonly LossWindows? _loss;` and the last constructor parameter `LossWindows? loss = null`.
- In `Evaluate`, at the top of the `foreach (var (id, beyond) in _beyond)` loop body, right after `var state = states[id];` and the `_falteringSince` bookkeeping, and before the `if (state is not (HostState.Down or HostState.Faltering)) continue;`:

```csharp
                // A hop that keeps dropping its own probes while forwarding fine
                // (spec §4.5 corollary 3, §9.3). A Down hop is HopBypassed's business.
                if (_loss is not null && state is not (HostState.Down or HostState.Unreachable))
                    CheckProbeOnlyLoss(found, _monitors[id], beyond);
```

- Add the helper:

```csharp
    private void CheckProbeOnlyLoss(Dictionary<string, ModelCondition> found, TargetMonitor hop, List<Guid> beyond)
    {
        var losses = _loss!.Closed
            .Select(w => LossAttribution.ForHop(w, hop.Target.Id, beyond))
            .OfType<LossAttribution.HopLoss>()
            .TakeLast(ProbeOnlyWindows)
            .ToList();
        if (losses.Count < ProbeOnlyWindows)
            return;
        double own = losses.Average(l => l.Own);
        double forwarding = losses.Average(l => l.Forwarding);
        double probeOnly = own - forwarding;
        bool active = _active.ContainsKey(ModelCondition.KeyFor(ModelWarningKind.ProbeOnlyLoss, hop.Target.Id));
        bool holds = active
            ? probeOnly >= ProbeOnlyClear && forwarding <= ForwardingClear
            : probeOnly >= ProbeOnlyRaise && forwarding <= ForwardingRaise;
        if (!holds)
            return;
        ProbeSpec.TryParse(hop.Target.Probe, out var probe);
        var advice = probe.Kind == ProbeKind.Tcp ? "consider a different probe" : "consider a TCP probe";
        Add(found, ModelWarningKind.ProbeOnlyLoss, hop.Target,
            $"{hop.Target.Name} drops some of its own pings but forwards fine (loss {Pct(own)} · " +
            $"forwarding {Pct(forwarding)} over the last {ProbeOnlyWindows} minutes) — " +
            $"common router behaviour; {advice}.");
    }

    private static string Pct(double value) => value.ToString("P0", CultureInfo.InvariantCulture);
```

- Add `using System.Globalization;`. If `_beyond`'s value type is not `List<Guid>`, match it. The message says "minutes" because `Span` is 60 s; if `Span` ever changes, derive the wording from `ProbeOnlyProveTime`.

In `WatcherEngine.StartAsync`, pass `Loss` as the last argument of `new ModelCheckCoordinator(...)`.

- [ ] **Step 4: The ledger grace, tray label and TS type**

`ModelWarningLedger.ResyncGrace`:

```csharp
    /// <summary>Long enough for a new engine to re-raise every kind: the slowest
    /// are NeverAnswered (5 min) and ProbeOnlyLoss (10 windows).</summary>
    public static readonly TimeSpan ResyncGrace =
        (ModelCheckCoordinator.ProbeOnlyProveTime > ModelCheckCoordinator.NeverAnsweredAfter
            ? ModelCheckCoordinator.ProbeOnlyProveTime : ModelCheckCoordinator.NeverAnsweredAfter)
        + TimeSpan.FromMinutes(2);
```

`TrayStatus.NoteLabel`: add the arm `"ProbeOnlyLoss" when target is not null => $"{target} drops its own pings (forwards fine)",` before the fallback.

`ClientApp/src/lib/types.ts`: the `ModelWarning.kind` union gains `"ProbeOnlyLoss"`.

- [ ] **Step 5: Run the tests**

Run:
```bash
dotnet test Tests/ConEngine.Tests -c Debug
dotnet test Tests/ConPersistence.Tests -c Debug
dotnet test Tests/ConWatch.Tests -c Debug
cd Programs/ConWatch/ClientApp && npx vitest run && npm run build
```
Expected: all PASS. The ledger tests use `ResyncGrace` symbolically. If one hard-codes "4 minutes is below ResyncGrace", it still holds.

- [ ] **Step 6: Commit**

```bash
git add Core/ConEngine Libraries Programs/ConWatch/ClientApp/src Tests
git commit -m "feat(model-check): ProbeOnlyLoss note for a hop that drops its own pings but forwards fine"
```

---

### Task 4: Verification

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

- [ ] **Step 2: Containment audit**

Run: `grep -rn "LossWindows\|LossAttribution\|ProbeOnly" Core Libraries Programs --include=*.cs | grep -v /obj/`
Expected hits:
- `ConEngine`: `LossWindows`, `LossAttribution`, `WatcherEngine`, `VerdictCoordinator`, `ModelCheckCoordinator`
- `ModelWarningLedger`
- `TrayStatus`

None in `TopologyCoordinator`, `HostAnalysisEngine`, `TargetMonitor`, `ConNotify` or the ISP report.

- [ ] **Step 3: Hand back**

Report the branch, commit range and test results. The real-world check is for Mark: on the NAS instance, an ICMP-deprioritising hop should show `ProbeOnlyLoss` after about 10 minutes. Merging is Mark's decision. Never push.
