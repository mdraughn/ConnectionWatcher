# Warm restart — implementation plan

Spec: ../specs/2026-09-11-warm-restart-design.md

1. **ConEngine seeding core**
   - `EngineSeed` (+ `TargetSeed(State, Address)`) record types.
   - `HostAnalysisEngine`: `Seed(HostState)` (silent, Initializing-only,
     sets TrackingStarted), `Assumed` flag on HostStatus, seeded branch in
     Recalculate (no transitions below MinUsableSample; Starting-style
     classification at MinUsableSample; seeded Down exits via Recovering),
     seeded flag cleared by MarkUnreachable/Release.
   - `HostTracker.Seed` passthrough; `TargetMonitor.Seed` (pre-tracker state +
     discipline notify, applied to the tracker at StartTracking), `IsAssumed`.
   - `PoolCoordinator.Seed` (silent). `VerdictCoordinator.Seed` (silent) +
     initial Recalculate in Start when seeded.
   - `WatcherEngine.StartAsync(ct, EngineSeed? seed = null)`: apply target
     seeds (address must match), pool/verdict seeds; TriggerBurst in Activate
     when warm.
2. **ConEngine.Tests**: seed confirmation (no transition), correction (one
   transition from seeded state), no hair-trigger below MinUsableSample,
   seeded Down → Recovering, seeded Unreachable parks/releases; pool seed
   silent; verdict seed consistent/contradicted.
3. **ConPersistence**: `WarmStartQueries.LastAliveUtcAsync` +
   `LatestTransitionsPerSubjectAsync` (max-id-per-subject); SettingKeys
   `Engine.WarmRestartMaxGapMinutes`.
4. **ConWatch**: WatcherHostService builds the seed (DB on start with gap
   gate; live-engine capture on rebuild); seeds default setting 10;
   StatusSnapshotBuilder exposes `assumed`; dashboard tree dims assumed dots.
5. **ConWatch.Tests**: restart-with-same-DB integration — state carries over
   with no new transitions; contradicted seed emits one honest correction.
6. Client build, full suites, preview restart demo, docs, merge to master
   (never push).
