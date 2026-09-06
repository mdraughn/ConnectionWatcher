# Fast Detection & Glitches — Design

**Date:** 2026-09-06
**Status:** Approved in chat (interview 2026-09-06)

## Purpose

Catch and record transient connectivity problems — seconds-scale blips and
the recurring late-night variety — that today pass invisibly because sampling
is lazy while everything looks healthy. Two halves:

1. **Fast detection**: accelerate sampling the moment anything looks
   suspicious, so short events yield enough evidence to classify.
2. **Glitches**: a new recorded event class *below* outage severity —
   "detected" severity and "reported" severity are decoupled. The History
   outage count stays strict; glitches get their own ledger and views.

Subjective mapping (Mark's framing): outage = "the internet is down";
glitch = "that page loaded slowly / the stream buffered" — which, per Mark,
is a lost packet + TCP retry, so **glitches are loss-based only**. Latency
(RTT) excursions are explicitly out of scope.

## Decisions (from interview)

| Question | Decision |
| --- | --- |
| Glitch classes in v1 | Loss-based only: per-target loss glitches + corroborated internet glitches. No latency baselines. |
| Recording floor | 2+ lost pings in one suspicion episode = recorded glitch; a single stray loss stays in rollups only. (The burst + sweep always trigger on the first loss regardless.) |
| Outage/glitch boundary | An episode that reaches `Down` is an outage (transitions own it; no glitch row). Everything milder that meets the floor is a glitch. No duration cutoffs. |
| Reporting | History outage semantics unchanged. New glitch ledger + **recurrence heatmap** (hour-of-day × date) to expose nightly patterns. ISP report gains a separate glitches section. |

## Part 1: Fast detection (ConEngine)

### Suspicion burst (per target)

`PingDiscipline` currently bursts only on state change (Faltering/Down/
Recovering). Add: a **failed ping while the target is Responding or
Starting** also starts the burst window (same `_burstStartedAt` mechanics,
same `BurstMaxDuration` cap). A success while bursting does NOT extend the
window; state-change bursts behave as today.

Effect with default External profile: Faltering in ~11s (was ~30s), Down in
~30s (was ~60s).

### Burst timeout

While the burst window is active, the effective ping timeout is
`min(profile.PingTimeout, 1s)` — hardcoded, no new profile field, no
migration. Rationale: real replies arrive in <150ms; against a dead target
the 3s timeout dominates burst cost. Steady-state timeout semantics are
unchanged.

### Corroboration sweep (cross-target)

New `SuspicionCoordinator` in the engine:

- Subscribes to the ping sample stream.
- Trigger: a failed sample from a target currently `Responding`/`Starting`
  whose subject is an **anchor-pool member or trunk target** (trunk = the
  anchor pool's parent chain to the root).
- Action: command `TriggerBurst()` (new method on `IPingCadence`; starts the
  burst window without a state change) on **all anchor-pool members + the
  trunk chain**. Each target's own discipline handles cadence and cap.
- Cooldown: no new sweep within **30s** of the last sweep trigger (prevents
  sweep-loss → sweep loops). Per-target burst caps still apply.
- Politeness: a sweep is ~6 targets at 1/s for ≤60s, only on suspicion;
  during a real outage the packets aren't being delivered anyway.

### Glitch episode tracking

Per-target episode state machine inside `SuspicionCoordinator` (not in
`HostAnalysisEngine` — host states are untouched):

- **Open** on the first failed sample from a `Responding`/`Starting` target.
- Accumulate: lost count, total sample count, first/last loss timestamps.
- **Close** when 5 consecutive successes arrive (recovered) — emit a
  `GlitchEvent` iff lost ≥ 2.
- **Discard** (no glitch) if the target transitions to `Down` (the outage
  ledger owns it) or `Unreachable` (parent's problem; the parent's own
  episode/outage covers it).

`GlitchEvent { TargetId, TargetName, Class, StartUtc, EndUtc, LostCount,
SampleCount, Detail }`, exposed as `WatcherEngine.GlitchSource`
(`IObservable<GlitchEvent>`).

### Internet glitches (corroboration result)

When per-target glitch episodes on **≥ Verdict.WeatherThreshold anchor
members** overlap in time (episodes joined when within ±10s of each other),
emit one additional `GlitchEvent` with `Class = Internet`,
`TargetId = VerdictCoordinator.SubjectId`, span = union of the member
episodes, `Detail` naming the affected anchors and whether any trunk target
also glitched (trunk involvement ⇒ "likely link/ISP" wording, else
"upstream"). Member glitches are still recorded individually.

## Part 2: Persistence + API (ConPersistence / ConWatch)

- New table `Glitches`: `Id` (long, PK), `TargetId`, `TargetName` (snapshot,
  same rationale as transitions), `Class` (int enum: Target=0, Internet=1),
  `StartUtc`, `EndUtc`, `LostCount`, `SampleCount`, `Detail` (nullable
  string). Index on `(StartUtc)` and `(TargetId, StartUtc)`. EF migration
  `AddGlitches`.
- `GlitchRecorder` hosted service (mirror of `TransitionRecorder`): batches
  `GlitchSource` events to the table; attached by `WatcherHostService` on
  every engine (re)start.
- Retention: glitches pruned by the existing retention run after
  `Retention.MinuteRetentionDays` (fine-grained events age with the
  fine-grained rollups; no new knob).
- API (open, read-only, like other watching surfaces):
  - `GET /api/v1/glitches?from&to&targetId&limit` → newest-first page.
  - `GET /api/v1/export/glitches?format=csv|json` beside the other exports.
- The API-surface lock contract test allowlist gains `/api/v1/glitches`
  (export prefix already covered).

## Part 3: Reporting (ConWatch UI + ISP report)

- **History page — "Glitches & recurrence" section** (React island, part of
  the history bundle):
  - **Recurrence heatmap**: grid of hour-of-day (24 rows) × last 30/60/90
    days (columns, range selector shared with the page). Cell intensity =
    glitch count; outage hours overlay in the Down color so real outages and
    glitches are distinguishable at a glance. Tooltip: date, hour, counts,
    worst event. This is the "3 AM stripe" view — months of nightly
    misbehavior reads as a vertical line.
  - Recent-glitch list: time, subject, class, lost/sample, duration, detail.
  - Timezone: rendered in browser-local time (consistent with the rest of
    History).
- **ISP report**: new "Brief disturbances (glitches)" section after
  Incidents: total count in period, count by class, and a top-hours summary
  ("41 of 63 glitches occurred 02:00–04:00 UTC") — the recurrence evidence
  in printable form. Outage/incident semantics unchanged.
- Dashboard: no change (glitches are history-grade, not live-banner-grade).

## Error handling

- SuspicionCoordinator failures must never affect ping flow or states: all
  handlers wrap in try/catch-log.
- Engine rebuild: coordinator is rebuilt with the engine (episodes in flight
  are dropped — a rebuild mid-glitch loses at most one glitch row).
- Recorder write failure: log and drop batch (same policy as transitions).

## Testing

Engine (deterministic fake time + fake pings):
1. Discipline: failure while Responding starts burst; success does not end
   it early; cap honored; burst timeout is min(timeout, 1s) only in burst.
2. Sweep: anchor loss triggers TriggerBurst on anchors + trunk; cooldown
   suppresses a second sweep within 30s; non-anchor/trunk targets don't
   trigger sweeps.
3. Episodes: 1 loss → no glitch; 2 losses + 5 recoveries → glitch with
   correct span/counts; escalation to Down → no glitch; Unreachable →
   discarded.
4. Internet glitch: overlapping episodes on ≥ threshold anchors emit one
   Internet glitch naming members; trunk involvement changes Detail.

Persistence/API: recorder writes rows; retention prunes old glitches;
glitches API paging + filters; export formats; lock contract test still
green with the new open route.

UI (Vitest): heatmap bucketing (UTC→local hour mapping, DST boundary day);
page smoke for the new section.

## Out of scope (explicit)

- Latency/RTT-based glitches and baselines (Mark: not needed now).
- New profile fields or admin knobs (constants: 30s cooldown, 5-success
  close, ±10s join, 1s burst timeout — revisit only if real data demands).
- Verdict changes: states, thresholds, and history semantics are untouched.
- Live glitch notifications (MQTT/webhook fire on transitions only; a
  glitch topic can be a follow-up if wanted).
