# Warm restart — design (DECIDED)

**Problem.** Every restart (deploy, reboot) and every engine rebuild (config
change) cold-starts the engine: all targets march through Starting for
~100 s (MinUsableSample × normal interval), the verdict resets, MQTT/HA see a
gap, and the timelines collect restart noise. Mark: "having the watch
re-measure on every update is getting annoying."

**Decision.** Options 2+1 from the discussion: **seed the presumed state**
from the last known world, and **burst-confirm it immediately**. No sample
persistence (option 3 / Redis rejected as overkill — the hard part is resume
semantics, not storage). Redis-based blue-green handoff noted in BACKLOG.

## Behavior

- **Seeding** initializes a subject's state silently — no transition is
  emitted. Fresh pings then confirm or correct it:
  - While seeded and below MinUsableSample: no threshold transitions can fire
    (no hair-trigger Faltering off the first lost ping after boot).
  - At MinUsableSample: classify the fresh sample exactly like a Starting
    exit (Responding / Faltering / Down). Same state → **no transition, the
    timeline is continuous across the restart**. Different → one honest
    transition from the seeded state.
  - A seeded **Down** that proves alive exits via **Recovering** (not straight
    to Responding), because TopologyCoordinator releases parked children only
    on Down → Recovering/Responding.
  - A seeded **Unreachable** target stays parked (no pings) until its parent's
    confirmation releases it — same as live semantics. Seed builders must only
    seed Unreachable when an ancestor is seeded Down; otherwise skip that
    target (cold start it).
- **Warm-up burst.** When the engine starts with a seed, every monitor bursts
  at activation (1 s cadence), so confirmation lands in ~MinUsableSample
  seconds. Seeded Faltering/Down states burst automatically through the
  existing discipline; Responding/Starting get an explicit TriggerBurst.
  Burst length is the existing BurstMaxDuration — no new cadence machinery.
- **Pools and verdict** recalculate only on member transitions, so a fully
  confirmed warm start would leave them stuck at Initializing/Unknown. Both
  get a silent Seed(); VerdictCoordinator additionally runs one initial
  Recalculate() on Start when seeded (emits only if the seeded monitors
  contradict the seeded verdict).
- **Honest labeling.** A seeded-but-unconfirmed state is `assumed`: exposed on
  HostStatus/TargetStatus, in the status snapshot, and shown in the dashboard
  tree (dimmed dot + tooltip). Cleared at confirmation.

## Seed sources

- **Process start** — from the database, gated by freshness:
  - `lastAlive` = max(latest transition timestamp, latest minute-rollup period
    end). If `now − lastAlive` exceeds **Engine.WarmRestartMaxGapMinutes**
    (setting, default 10; ≤ 0 disables seeding entirely), cold start.
  - Per subject: the latest transition row. Skip HistoryReset rows (address
    changed — different data history), Initializing/Starting states, and
    unparseable states. Verdict state parses from the row's Verdict name.
- **Engine rebuild** (config change) — from the outgoing engine's live states,
  no gap check. Target seeds carry the address; WatcherEngine only applies a
  seed whose address matches current config, so a repointed target cold-starts.

## Non-goals

- No persistence of sample windows (rejected option 3).
- No burst-on-Release for children after a parent recovers mid-run (existing
  behavior, unchanged).
- No MQTT changes: retained states stay valid precisely because states are
  continuous across the restart.
