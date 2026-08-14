# ConnectionWatcher — Architecture & Phase 1 Engine Design

**Date:** 2026-08-14
**Status:** Approved design, pending implementation plan
**Scope:** Overall architecture for ConnectionWatcher plus detailed design for Phase 1 (the monitoring engine). Phases 2–4 get their own specs when they begin.

## 1. Purpose

ConnectionWatcher is a home internet connectivity monitor. It is not a site-uptime
watchdog for servers Mark is responsible for; it answers the question *"is my
internet down, and what kind of down is it?"* from inside the house.

It does this by pinging a hierarchy of targets — LAN devices, the router, the cable
modem, upstream ISP (Comcast/Xfinity) infrastructure, and external anchors such as
Google and Cloudflare — and by recording every up/down transition with the evidence
that caused it.

Key behavioral requirements:

- **Hierarchy-aware:** if the router is unreachable, everything beyond it is
  *unreachable*, not independently probed and falsely marked down.
- **Polite to externals:** steady-state external pings are slow (~10 s); internal
  pings may be fast (~1 s). No target is ever hammered indefinitely.
- **Smart about flaps:** a couple of lost pings never mark a target down; sustained
  degradation does. Degradation itself (rising loss rate) is a detectable state.
- **Burst on trouble:** when a target falters or goes down, ping it faster for a
  bounded window to measure loss rate and catch recovery quickly.
- **Historical record:** every state transition is kept, with a structured reason,
  and is queryable via API and (later) a web UI with charts.

## 2. Decisions made during design review

| Topic | Decision |
|---|---|
| Deployment | **One container**, one ASP.NET Core process (engine + API + web UI), **SQLite** on a mounted volume. Builds for linux-x64 (Synology Container Manager) and win-x64 (local dev). No message queue; the engine's Rx streams are the in-process event bus. |
| Configuration home | Targets, profiles, pools, hierarchy live **in the database**, edited via web UI/API CRUD (Phase 2+). `appsettings.json` keeps only infrastructure settings. JSON export/import for backup/seeding. In Phase 1 (no DB yet) config is read from `appsettings.json` in the same shape. |
| Hierarchy model | **Single-parent tree.** Descendants of a Down target become `Unreachable` (a distinct state) and are not pinged until released. |
| Ping discipline | **Per-target profiles with burst mode, AND rotation pools, both in Phase 1.** |
| Persistence (Phase 2) | **Transitions + periodic rollups** (per-minute success rate and RTT min/avg/max, downsampled with age). Raw pings are never stored. |
| Web UI (Phase 3) | **Razor Pages base with JS islands** where interactivity is needed; **SignalR** for live updates; **TanStack charts** (React islands, small Vite build). |
| Alerting (Phase 4) | **Webhooks and MQTT**, aimed at Home Assistant; HA integration via MQTT discovery later. Engine publishes transition events on an internal stream that notifiers subscribe to — designed now, delivered in Phase 4. |
| Upstream hop discovery (Phase 3) | **Traceroute assist in the UI:** run a traceroute from the app, show hops, one-click "add as target" with parent pre-filled. One-time tool, not continuous. |
| Engine evolution | **Approach A: evolve in place.** Keep PingManager/PingController/HostTracker/HostAnalysisEngine; add Target model, per-target `PingDiscipline`, `TopologyCoordinator`, `PoolCoordinator`. No central-scheduler rewrite. |

## 3. Phase roadmap

1. **Phase 1 — Engine** *(this spec, detailed below)*: Target model, hierarchy,
   disciplines and bursts, pools, transition events with reasons. Config from
   `appsettings.json`. Driven by ConTinker against the simulator; unit-tested.
2. **Phase 2 — Persistence + API**: EF Core + SQLite; transition log and minute
   rollups with downsampling; config moves to the DB; REST API with CRUD for
   targets/profiles/pools and queries for history.
3. **Phase 3 — Web UI**: live dashboard (SignalR), config editors, TanStack
   charts, traceroute assist.
4. **Phase 4 — Packaging + notifications**: Dockerfile and Synology deployment,
   webhook + MQTT publishers, Home Assistant MQTT discovery.

## 4. Project layout

| Project | Role |
|---|---|
| `Core/ConCore` | Contracts and primitives: `IPingTransceiver`, `PingResult`, new Target domain types |
| `Core/ConEngine` | The engine: disciplines, topology, pools, analysis state machine, transition events |
| `ConInfrastructure` | Real implementations: ICMP transceiver, DNS resolver, (later) traceroute |
| `ConTestingInfrastructure` | WPF interactive ping simulator (test rig; unchanged) |
| `ConTinker` | Console dev harness that drives the engine in Phase 1 |
| `Programs/ConDiag` | Grows into the production web app (API + UI) in Phases 2–3 |
| `Core/ConHost` | Shared hosting glue |
| `ConEngine.Tests` *(new)* | xUnit test suite for the engine |

## 5. Phase 1 domain model

### Target

The engine's unit of work (replaces raw `IPAddress` keying).

- `Id` — stable GUID
- `Name` — display name ("Living-room AP", "Google DNS")
- `Address` — IP literal or hostname. Hostnames are DNS-resolved at watch-start
  and re-resolved after repeated ping failures; first IPv4 address wins in Phase 1.
- `ParentId` — optional; forms the reachability tree (Google → Modem → Router)
- `ProfileId` — the ping profile governing this target
- `Enabled` — disabled targets are configured but not pinged

### PingProfile

Named, reusable discipline settings:

- `NormalInterval` (e.g. 1 s internal, 10 s external), `PingTimeout`
- `BurstInterval` (e.g. 1 s), `BurstMaxDuration` (e.g. 60 s)
- Analysis knobs (existing): `HistorySize`, `MinUsableSample`,
  `RespondingThreshold`, `FalteringThreshold`, `DownThreshold`, `RecoveringThreshold`
- Two shipped presets: `Internal`, `External`; users may define more.

### Pool

A virtual aggregate target ("Internet" = {Google, Cloudflare, Quad9}).

- `Id`, `Name`, `MemberTargetIds`, optional `ParentId` (a pool may sit in the tree)
- Never pinged itself. State derivation:
  - any member `Responding` → pool `Responding`
  - all members `Down` or `Unreachable` → pool `Down`
  - otherwise → pool `Faltering`
- Members' ping schedules are phase-staggered (member *k* of *n* offset by
  *k·interval/n*): a 3-member pool at 15 s intervals yields a data point every
  5 s while each site sees one ping per 15 s.

### States

Existing machine plus one new state:

`Initializing → Starting → Responding ⇄ Faltering → Down ⇄ Recovering → Responding`
plus **`Unreachable`**: entered when an ancestor goes `Down` (pinging pauses);
exited to `Starting` when the ancestor recovers (history is stale, so the target
re-proves itself).

### TargetTransition

The event everything revolves around:

- `TargetId`, `From`, `To`, `Timestamp`
- `Reason` — structured: trigger kind (`ThresholdCrossed`, `ParentDown`,
  `ParentRecovered`, `ResolutionFailed`, `AllMembersDown`, …) plus the numbers
  behind it (success rate, sample size, burst loss rate, the ancestor involved)
- Stats snapshot (success rate, average RTT, sample size)

Phase 1 emits these to the log and an Rx stream. Phase 2 persists them; Phase 4
pushes them to MQTT/webhooks. Example rendering:
*"Faltering → Down; success 8% over 25 pings in 30 s; parent Router still Responding."*

## 6. Phase 1 runtime behavior

### PingDiscipline (per target)

Each enabled target keeps its own `PingController` polling loop, but the fixed
1 s delay is replaced by asking the target's `PingDiscipline` for the next ping
time, based on current state:

- **Normal cadence** (`NormalInterval`): `Responding`, `Starting`, and long-term `Down`
- **Burst cadence** (`BurstInterval`): `Faltering`, `Recovering`, and the first
  `BurstMaxDuration` after entering `Down`. After the cap, a still-down target
  falls back to normal cadence (no hammering through a long outage). Entering a
  burst-eligible state restarts the burst window.
- **Hold** (no pinging): `Unreachable`

During a burst the discipline tracks the window's success ratio; that loss rate
is attached to any transition emitted during/at the end of the burst.

### TopologyCoordinator

Subscribes to all state-change streams; owns the parent tree.

- Parent enters `Down` → all descendants (transitive) are commanded to
  `Unreachable`; their loops park; each gets a transition with reason
  `ParentDown(ancestorId)`.
- Parent leaves `Down` (`Recovering` or `Responding`) → descendants are released
  to `Starting` with fresh history and resume pinging.
- Root targets (no parent — typically the router) are always pinged.

### PoolCoordinator (per pool)

Assigns member phase offsets, recomputes pool state on any member change, emits
pool transitions on the same `TargetTransition` stream (e.g. reason `AllMembersDown`).

### Politeness invariants

- Externals are never pinged faster than their profile's `BurstInterval`, even in burst.
- Bursts are always time-capped.
- `Unreachable` targets are not pinged at all.
- Pool members share load via stagger.
- Internal LAN targets may ping at 1 s indefinitely by design.

## 7. Error handling

- **DNS failure:** hostname target that fails to resolve enters `Starting` with a
  `ResolutionFailed` transition reason; retries with backoff (30 s doubling,
  capped at 5 min). Never crashes the engine.
- **Config validation at load** (extends the existing `IValidateOptions` +
  `ValidateOnStart` pattern): profile references exist; parent references exist
  and form a tree (cycle detection rejects config with a clear message); pool
  members exist and are not themselves pools. Bad config fails fast at startup.
- **Fault isolation:** an unhandled failure in one target's loop (transceiver
  exception, closed channel) is logged, the target enters a faulted condition,
  and its loop is restarted; other targets are unaffected.

## 8. Testing

There is currently no test project; Phase 1 adds `ConEngine.Tests` (xUnit).
`TimeProvider` is already injected throughout the engine, so tests use
`FakeTimeProvider` (Microsoft.Extensions.TimeProvider.Testing) for deterministic,
fast simulation of long scenarios.

Core suites:

- Discipline cadence per state, burst cap expiry, burst-window restart
- Topology cascade: parent down → transitive `Unreachable` → release on recovery
- Pool state derivation and stagger arithmetic
- Analysis state-machine threshold transitions
- Config validation: cycles, dangling references, pool-of-pools rejection

The WPF simulator remains the interactive rig. A ConTinker configuration modeling
the real house — Router → Modem → Comcast hop → {Google, Cloudflare, Quad9} pool,
plus a couple of LAN devices — is the standing demo scenario.

## 9. Phase 1 exit criteria

Running ConTinker against the simulator:

1. Kill the simulated router → every downstream target transitions to
   `Unreachable` with reason `ParentDown`, and pinging stops for them.
2. Restore the router → the cascade recovers (descendants re-prove via `Starting`).
3. Degrade an external target's loss rate → `Faltering` triggers a burst, and the
   resulting transition record contains the measured loss rate.
4. Take one pool member down → pool state rides through (stays `Responding` while
   any member responds).

All transitions appear in the log with structured reasons, and the `ConEngine.Tests`
suite is green.

## 10. Out of scope for Phase 1

- Any persistence (Phase 2)
- REST API endpoints (Phase 2)
- Web UI, charts, traceroute assist (Phase 3)
- Docker packaging, webhooks, MQTT, Home Assistant (Phase 4)
- IPv6 preference/multi-address targets, global cross-target rate governor,
  multi-parent (DAG) topologies — possible later evolutions, deliberately excluded now.
