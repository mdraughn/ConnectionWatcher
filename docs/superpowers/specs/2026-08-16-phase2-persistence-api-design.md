# ConnectionWatcher Phase 2 — Persistence + API Design

**Date:** 2026-08-16
**Status:** Approved design, pending implementation plan
**Prerequisite:** Phase 1 engine (see `2026-08-14-connectionwatcher-engine-design.md`), merged to master.

## 1. Purpose

Give the engine memory and a queryable interface: every state transition is
recorded with its evidence, per-minute statistics power future charts, the
monitoring configuration moves into the database with full CRUD, and a REST
API exposes all of it. This is the phase that turns the engine into a service.

## 2. Decisions made during design review

| Topic | Decision |
|---|---|
| App project | **New `Programs/ConWatch`** ASP.NET Core app (net10.0, cross-platform) hosting engine + SQLite + API. **Delete `Programs/ConDiag` and `Core/ConHost`** (dead stub + its only consumer; also removes the vulnerable Microsoft.OpenApi 2.3.0 references). |
| Data access | **EF Core + Microsoft.EntityFrameworkCore.Sqlite, code-first migrations**, applied automatically at startup (`Database.Migrate()`). |
| Persistence layering | **New `ConPersistence` class library** (Approach A): DbContext, entities, migrations, repositories, background services. Engine stays EF-free; persistence subscribes to the engine's Rx streams. |
| Config apply | **Engine rebuild on config change**: CRUD write → validate → save → debounced (~2 s) graceful engine restart inside the process. Seconds-long monitoring gap accepted; in-memory ping history resets; DB history untouched. |
| Retention | Transitions **forever**. Minute rollups **90 days** → hourly; hourly **13 months** → daily; daily **forever**. All knobs configurable. |
| API style | **Minimal APIs** under `/api/v1`, OpenAPI document always on, Swagger UI in development. **No auth** (LAN appliance; revisit in Phase 4). |
| Simulator | ConWatch references real ICMP only (`ConInfrastructure`). The WPF simulator stays in ConTinker (Windows-only dev harness). Engine-level integration tests use the existing `FakePingTransceiverFactory`. |
| Verdict layer | **Included in Phase 2** (added at design review): a `VerdictCoordinator` classifies overall internet health — Healthy / SiteIncident / InternetWeather / LinkDegraded / LinkDown — from existing states; its transitions are recorded like all others. |

## 3. Projects

| Project | Role |
|---|---|
| `Programs/ConWatch` *(new)* | Production app: WatcherHostService (engine), persistence services, REST API |
| `ConPersistence` *(new)* | EF Core DbContext + entities + migrations, repositories, TransitionRecorder, RollupCollector, RetentionService, ConfigService |
| `Tests/ConPersistence.Tests` *(new)* | Unit tests for persistence logic (SQLite in-memory) |
| `Tests/ConWatch.Tests` *(new)* | Integration tests via WebApplicationFactory + temp SQLite + FakePingTransceiverFactory |
| `Programs/ConDiag`, `Core/ConHost` | **Deleted** |
| ConEngine | Two small additions (stable IDs, `PingSampleSource`) — see §5 |

## 4. Database schema

SQLite file at a configurable path (default `data/conwatch.db`; the `data/`
directory is the container volume mount in Phase 4).

### Config tables (source of truth after first boot)

- **Profiles**: `Id` GUID PK, `Name` unique (case-insensitive), all discipline
  and analysis knobs (intervals/timeouts stored as seconds, REAL). `Internal`
  and `External` seeded as ordinary editable rows.
- **Targets**: `Id` GUID PK, `Name` unique, `Address`, `ParentId` nullable
  self-FK, `ProfileId` FK → Profiles, `Enabled`.
- **Pools**: `Id` GUID PK, `Name` unique, `ParentId` nullable FK → Targets.
- **PoolMembers**: composite PK (`PoolId`, `TargetId`), `MemberIndex`
  (member order drives phase stagger).

### Data tables

- **Transitions**: `Id` INTEGER PK autoincrement, `Timestamp` (UTC),
  `SubjectId` GUID, `SubjectName`, `SubjectKind` (Target|Pool|Verdict),
  `FromState`, `ToState`, `Trigger`, `SuccessRate?`, `SampleSize?`,
  `BurstLossRate?`, `AncestorId?`, `Detail?`, `Verdict?`, `AvgRtt`.
  Indexes: (`SubjectId`,`Timestamp`), (`Timestamp`). Never pruned.
- **Rollups**: `Id` INTEGER PK, `TargetId` GUID, `PeriodStart` (UTC),
  `Resolution` (Minute|Hour|Day), `PingCount`, `SuccessCount`, `RttAvg`,
  `RttMin`, `RttMax`. Unique index (`TargetId`,`Resolution`,`PeriodStart`).
  Downsampling: sum counts, min of mins, max of maxes, count-weighted average.
  Pools are not rolled up (no pings; their timeline derives from transitions).

### Persistence settings (appsettings `Persistence` section)

`DbPath` (default `data/conwatch.db`), `MinuteRetentionDays` (90),
`HourlyRetentionMonths` (13), `RetentionRunHourUtc` (03), `RebuildDebounceSeconds` (2).

## 5. ConEngine changes (small, targeted)

1. **Stable IDs**: `TargetConfig`, `PoolConfig` gain optional `Guid? Id`;
   `TargetGraph.FromOptions` uses the provided Id when present, generating one
   only as fallback (ConTinker's file config keeps working unchanged).
   Transition history thereby stays attached to its subject across engine
   rebuilds and app restarts.
2. **`WatcherEngine.PingSampleSource`**: public
   `IObservable<(Guid TargetId, PingResult Result)>`, emitted at the existing
   routing step where results are matched to monitors. Feeds the RollupCollector.
3. **`VerdictCoordinator`** (new, `ConEngine.Topology`, same pattern as
   PoolCoordinator): classifies overall internet health from existing states.
   - **Anchor set**: the members of a configured pool (`Verdict:AnchorPool`,
     e.g. "Internet"). Verdict is disabled with a startup warning if the
     setting is absent or names no existing pool.
   - **Rules**, evaluated on every relevant transition:
     1. Any *ancestor* of the anchor set is `Down` → **LinkDown** (deepest
        Down ancestor named as culprit). Ancestor `Faltering` → **LinkDegraded**.
     2. Otherwise count anchors in `Down`/`Faltering`/`Recovering`
        ("degraded"): 0 → **Healthy**; 1..(threshold−1) → **SiteIncident**
        (names listed); ≥ `WeatherThreshold` (default 2) → **InternetWeather**.
     3. Anchors still `Initializing`/`Starting` are excluded from counts;
        before any anchor is determinable the verdict is **Unknown**.
   - **Emission**: verdict changes emit a `TargetTransition` on the merged
     stream with a synthetic subject (fixed GUID, name "Internet"),
     `Trigger = VerdictChanged` (new enum member), `Reason.Verdict` (new
     optional field) carrying the verdict label, and `Detail` the evidence
     (e.g. "InternetWeather: 3 of 8 anchors degraded (Google=Down, ...)").
     `From`/`To` map the verdict onto `HostState` for stream compatibility:
     Healthy/SiteIncident→Responding, InternetWeather/LinkDegraded→Faltering,
     LinkDown→Down, Unknown→Starting. A transition is emitted whenever the
     verdict *label* changes, even if the mapped HostState is unchanged
     (e.g. Healthy→SiteIncident is Responding→Responding).
   - Config: appsettings section `Verdict { AnchorPool, WeatherThreshold }`
     (system-level tuning, not part of the CRUD'd monitoring config).

## 6. Runtime services (ConPersistence, hosted by ConWatch)

- **TransitionRecorder** — subscribes `TransitionSource` → bounded channel →
  background batch writes. The engine never blocks on the database. On
  overflow (unreachable at home scale), oldest events are dropped with a log.
- **RollupCollector** — subscribes `PingSampleSource`; per target keeps one
  in-memory minute bucket (count, successes, RTT min/max/sum); flushes buckets
  whose minute has closed; final flush on shutdown. Partial minutes around a
  rebuild lose at most seconds of data — acceptable.
- **RetentionService** — at startup and daily at `RetentionRunHourUtc`:
  minute rows older than `MinuteRetentionDays` are aggregated into hourly rows
  and deleted in one transaction; hourly older than `HourlyRetentionMonths`
  likewise into daily. Idempotent (unique index makes re-aggregation an upsert);
  crash mid-run is harmless. Incremental vacuum afterwards.
- **ConfigService** — every CRUD mutation materializes the would-be full
  config, converts it to `WatcherOptions`, and runs the existing
  `WatcherOptionsValidator`. Invalid → nothing saved, validator messages
  returned (API surfaces them as 400). Valid → saved, then a debounced rebuild
  signal to WatcherHostService. Deletes are refused (409) while the entity is
  referenced: a target with children, a pool member, a profile in use.
  Export: one JSON document (profiles, targets, pools, by name references +
  ids). Import: validate whole document, then replace all config tables in one
  transaction, then rebuild.
- **WatcherHostService** (in ConWatch) — startup: migrate DB → seed if config
  empty (from appsettings `Watcher` section when present, else just the two
  built-in profiles) → load config → build `WatcherOptions` with stable IDs →
  validate → start engine → attach recorder + collector. `RebuildAsync()`:
  stop engine, rebuild from DB, restart, reattach.

## 7. REST API (`/api/v1`, minimal APIs)

| Route | Behavior |
|---|---|
| `GET /status` | Current state of all targets and pools from the live engine (state, success rate, avg RTT, sample size, resolved address) plus the current **verdict** with its evidence |
| `GET /targets` · `GET /targets/{id}` | List / fetch config |
| `POST /targets` · `PUT /targets/{id}` · `DELETE /targets/{id}` | CRUD; 400 with validator messages, 404 unknown id, 409 delete-while-referenced |
| `/profiles`, `/pools` | Same CRUD pattern |
| `GET /transitions` | Query params `subjectId?`, `from?`, `to?`, `limit` (default 100, max 1000), `before?` (keyset cursor = last seen transition id); newest first |
| `GET /rollups/{targetId}` | Params `from`, `to`, `resolution` = `minute\|hour\|day\|auto`; `auto` picks the finest resolution fully stored for the range |
| `GET /config/export` | Whole config as JSON |
| `POST /config/import` | Validate-then-replace atomically; 400 with messages if invalid |
| `GET /health` | 200 when DB writable and engine running; body includes uptime, target count, engine state |

OpenAPI document at `/openapi/v1.json`; Swagger UI mapped in Development only.
No authentication in Phase 2 (LAN-only appliance; Phase 4 revisits exposure).

## 8. Error handling

- Startup with corrupt/unwritable DB → fatal, clear log message (appliance
  shows failed container rather than silently monitoring nothing).
- Recorder/collector DB errors → logged, retried with backoff; engine unaffected.
- Engine rebuild failure after a config write (should be impossible given
  pre-validation) → old engine kept running, error logged and surfaced via
  `/health`.
- All timestamps stored and served as UTC (ISO 8601 with `Z`).

## 9. Testing

- **ConEngine.Tests** additions: VerdictCoordinator rules — link-down beats
  weather, single anchor down = SiteIncident, threshold crossing to
  InternetWeather, degraded-ancestor = LinkDegraded, Unknown during startup,
  label-change emission with unchanged mapped state.
- **ConPersistence.Tests** (unit, SQLite in-memory): rollup bucket math and
  minute-boundary flushing (FakeTimeProvider), weighted downsampling
  arithmetic, retention boundary selection and idempotence, ConfigService
  validation/rejection paths, delete-while-referenced rules, export/import
  round-trip.
- **ConWatch.Tests** (integration, WebApplicationFactory + temp SQLite file +
  FakePingTransceiverFactory + fast profiles): boot app → seed via API →
  targets go Responding → force an outage → transition rows queryable with
  evidence → rollups appear → config edit (add target) applies without
  restart → invalid config (cycle) → 400 with message → export/import
  round-trip preserves ids and history linkage.

## 10. Exit criteria

1. ConWatch runs locally against the real home network (ICMP), configured
   entirely through the API.
2. Adding a target via Swagger starts monitoring within seconds, no restart.
3. An induced outage produces queryable transition rows with evidence.
4. Minute rollups accumulate; a synthetic aged dataset downsamples correctly
   on the retention pass.
5. Export → wipe → import restores identical config (same GUIDs).
6. With intermediates healthy, taking one simulated anchor down yields a
   SiteIncident verdict; taking a second down flips it to InternetWeather;
   taking the router down flips it to LinkDown — each recorded as a
   transition row with evidence.
7. Both new test suites and the existing ConEngine.Tests are green.

## 11. Out of scope for Phase 2

- Web UI, charts, SignalR, traceroute assist (Phase 3)
- Docker packaging, MQTT/webhooks/Home Assistant (Phase 4)
- Authentication/TLS (Phase 4 decision)
- Raw ping storage (never, by design)
