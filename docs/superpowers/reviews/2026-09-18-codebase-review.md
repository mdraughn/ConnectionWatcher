# ConnectionWatcher codebase once-over (2026-09-18)

Read-only review. No code was changed. Commit reviewed: `6e8bc13` (master, clean tree; nobody else was committing during the review).

Baseline: `dotnet test ConnectionWatcher.sln`, one run, nothing excluded, zero compiler warnings in the build output.

| Project | Passed | Failed | Skipped |
|---|---|---|---|
| ConEngine.Tests | 157 | 0 | 0 |
| ConWatch.Tests | 169 | 0 | 0 |
| ConPersistence.Tests | 79 | 0 | 0 |
| ConNotify.Tests | 29 | 0 | 0 |
| **Total** | **434** | 0 | 0 |

Scope: this is the second once-over. The first (2026-09-16) produced `docs/superpowers/plans/2026-09-17-cleanup-and-concurrency.md`; every task in it is checked off and merged (`bf827ad`..`6e8bc13`). Nothing from that plan is re-reported. Where a fix from it left a gap, the finding says "follow-on to <id>".

Read line by line by me: all of `Core/ConEngine` and `Core/ConCore` (2,878 + 396 lines); the two transceivers and the DNS resolver in `Libraries/ConInfrastructure`; `WatcherHostService.cs`, `Program.cs`, `Startup/ConWatchServices.cs`, `StatusBroadcaster.cs`, `StatusSnapshotBuilder.cs`, `Metrics/MetricsCounters.cs`, `RuntimeHealth.cs` in `Programs/ConWatch`; `Services/ChannelRecorder.cs`, `Services/RollupCollector.cs`, `Rollups/RollupAccumulator.cs`, `WarmStartQueries.cs` in `Libraries/ConPersistence`. Delegated: the rest of `Libraries/ConPersistence` to one reviewer; the rest of `Programs/ConWatch` plus `ConNotify` and `ConInfrastructure` to another. Every delegated claim below was re-opened at the cited lines before it was kept; one delegated claim was dropped because its premise was wrong (see P4), and the delegated line numbers for `TopologyNames.cs` were corrected.

Finding IDs: E = engine, P = persistence, H = host/API/integrations, S = structure. Severity: HIGH = data loss, deadlock, or a headline feature not working as described; MEDIUM = wrong under realistic conditions or slow shutdown/rebuild; LOW = latent or cosmetic.

---

## 1. Concurrency and correctness

### H1. MqttNotifier orphans a waiter on every loop turn: settings changes are swallowed and the first transition after a reconnect is lost (HIGH)

Evidence: `Libraries/ConNotify/MqttNotifier.cs:107-119`. Each iteration of the connected-session loop creates a fresh `_queue.Reader.ReadAsync(stoppingToken)` **and** a fresh `_wake.WaitAsync(stoppingToken)`, then `Task.WhenAny`s them. The loser is never awaited again but stays registered: an abandoned `SemaphoreSlim.WaitAsync` stays in the semaphore's FIFO waiter list, and an abandoned `ReadAsync` stays in the channel's blocked-reader list. `_wake` is a `SemaphoreSlim(0)` (`:23`); `_queue` is a bounded channel (`:21-22`).

Interleaving 1 (deterministic): every session starts with a `Refresh` already queued by `Attach` (`:43`), so on the first turn `readTask` wins and `wakeTask #1` is orphaned. Each transition processed afterwards orphans one more. `SettingsStore.SetManyAsync` raises `NotifierSettingsChanged` for `Mqtt.*`, `Webhook.*`, `Heartbeat.*` **and `Engine.*`** keys (`Libraries/ConPersistence/SettingsStore.cs:121-127`), and the handler does one `_wake.Release()` (`MqttNotifier.cs:49`). `Release` completes the **oldest** waiter, which is an orphan. Result: after N items in a session, the next N settings pokes do nothing. Changing the broker host, credentials, topic, or toggling `Engine.Enabled` from the Admin page or the take-over choreography does not end the session; the notifier keeps publishing under the old settings. For a take-over source this is the case the design forbids: the demoted node stays `online` in Home Assistant with its last states (see H2 for the rest of that picture).

Interleaving 2: when `wakeTask` does win (after enough pokes), the pending `ReadAsync` is orphaned; the outer loop starts a new session and a new `ReadAsync`. The next `TargetTransition` written by the engine is handed to the **older** blocked reader, which nobody awaits, so that transition is never published to MQTT.

`HeartbeatNotifier.cs:24-50` already uses the correct pattern (create the wait once, recreate only after it completes).

Why tests miss it: `NotifierIntegrationTests` drive one session and never change a setting after traffic has flowed.

**Proposed change (small):** delete the semaphore. `NotifierSettingsChanged` writes a `Reconfigure` record into `_queue` (it already carries `Refresh | TargetTransition`, `:19`). The session loop becomes a single `ReadAsync`; the outer disabled-wait reads the same channel and ignores everything but `Reconfigure`. Test: connect, deliver two transitions, change `Mqtt.Host`, assert the session ends within a second; then deliver one transition after the reconnect and assert it arrives.

### E1. The ping result pump has no per-message guard; one subscriber exception ends result processing for the life of the engine while `/health` keeps saying Healthy (MEDIUM, latent)

Evidence: `Core/ConEngine/Network/PingManager.cs:122-145`. The `try` wraps the whole `await foreach`; the `catch` logs once and the `finally` completes the subject. Every subscriber runs inline on this thread: the engine's routing lambda at `WatcherEngine.cs:188-201` calls `Discipline.OnPingResult`, `monitor.RecordPingResult` (which drains the outbox and so runs the topology, pool and verdict coordinators and every host observer), `_pingSampleSubject.OnNext` (RollupCollector) and `Suspicion.OnPingSample`. Only `SuspicionCoordinator.OnPingSample` (`SuspicionCoordinator.cs:87-100`) guards itself. If anything else throws, the exception unwinds through `OnNext` into the pump, the loop exits, and the finally completes the subject. The controllers do not know: they keep `TryWrite`-ing into the **unbounded** channel created at `PingManager.cs:43`, so memory grows until restart. Meanwhile `Program.cs:73-90` reports Healthy because `host.Engine` is non-null, and `MetricsEndpoint.cs` likewise. The dashboard's activity LEDs stop flickering and transitions stop; nothing else complains.

Today no subscriber throws under normal input (host observers are `TryWrite` / `AddOrUpdate` / fire-and-forget with their own catch, `StatusBroadcaster.cs:31`, `MetricsCounters.cs:35-38`, `ChannelRecorder.cs:32-38`, `RollupCollector.cs:27-29`, `MqttNotifier.cs:39-41`, `WebhookNotifier.cs:27-29`; engine coordinators only index dictionaries built at start), which is why this is "latent". It is the single point where one future bug in any observer becomes silent total failure. The same shape exists once more in `WatcherEngine.cs:219`: the DNS follower is `_ = Task.Run(...)` with no catch around `Activate`/`RepointAsync`, so a throw there (for example `ObjectDisposedException` from `GetOrCreateController` after stop, `PingManager.cs:61`) faults an unobserved task and the hostname target silently stops following DNS.

Why tests miss it: `PingManagerTests.cs` has one test (stop within grace). No test subscribes a throwing observer and asserts a later result still arrives; no test asserts `/health` against a stalled engine.

**Proposed change (small):** in `ProcessMessages`, move the `try/catch` inside the loop body (`catch (Exception ex) when (ex is not OperationCanceledException)` logs and continues); record a last-result stamp (`Interlocked.Exchange` on a `long`) that `WatcherEngine` exposes; `/health` returns 503 `EngineStalled` when at least one monitor is active and the stamp is older than ~3× the longest configured `NormalInterval` plus timeout, and `/metrics` exposes the age as a gauge. Wrap the DNS follower body in a catch that logs and keeps the loop alive. Tests: an engine with a `PingSampleSource` subscriber that throws on the first sample, asserting the second sample reaches a second subscriber; a `ConWatch.Tests` health test with a stale stamp.

### E2. Cancellation is reported as a lost ping. A DNS re-point injects one spurious loss into the target's history and, for an anchor or trunk hop, into the suspicion sweep (MEDIUM)

Evidence:
- `Libraries/ConInfrastructure/PingTransceiverIcmp.cs:62-65` and `TcpConnectTransceiver.cs:40-43`: an `OperationCanceledException` from the engine token is converted into `new PingResult(ip, false, 0, "Cancelled")`, a failed sample.
- `Core/ConEngine/Network/PingController.cs:80-82`: the loop awaits `PingAsync`, then calls `SendMessage(reply)` **before** re-checking `_cancellationToken`; the `catch (OperationCanceledException)` at `:84` is dead against the real transceivers because they never throw it.
- `WatcherEngine.cs:254-260` (`RepointAsync`) stops the old controller (`StopControllerAsync` cancels its local CTS, `PingManager.cs:76-87`) while the result channel stays open, so the "Cancelled" sample is written, pumped, and routed by target id to the monitor that is about to be re-pointed.

Interleaving: hostname target whose current address is still up but has left the answer set (record rotation, the case the DNS follower exists for). `RepointAsync` cancels the controller; if a probe is in flight (a 3 s timeout on a 10 s cadence leaves a small window when the host answers, a near-certain one when it does not), the transceiver returns a loss. On the pump thread: `Discipline.OnPingResult(false)` starts a suspicion burst on a Responding target (`PingDiscipline.cs:107-117`); `TargetMonitor.RecordPingResult` drops the sample into the history so the next classification runs over a window with one manufactured loss; `SuspicionCoordinator.ProcessLocked` (`SuspicionCoordinator.cs:112-116`) sees "loss from a healthy anchor/trunk" and fires a **corroboration sweep**: every anchor and trunk target bursts for up to `BurstMaxDuration`, and a glitch episode opens (`:130-134`). One more real loss inside the coalesce window and a glitch row is recorded against a target that never lost a packet. The same path runs at engine stop and rebuild (`_cts.Cancel()` at `WatcherEngine.cs:342`), but there it races the pump's own cancellation and usually loses; at a re-point it is deterministic.

The DNS resolver has the same shape: `DomainNameResolverDns.cs:69-80` returns `Success=false, addresses=[]` with status "Cancelled" instead of throwing, so `WatcherEngine.cs:287` (`catch (OperationCanceledException) when ...`) is also dead, and a hostname target still resolving at shutdown publishes a `ResolutionFailed` transition ("No IPv4 address for 'x'") on its way out (`:284-285`). Related: `result.Success` and `result.Status` are never read at `:282-283`, so "Host not found" and "Temporary DNS failure" both surface to the user as "No IPv4 address".

Why tests miss it: every fake transceiver either returns synchronously (`Tests/ConEngine.Tests/Fakes/FakePingTransceiverFactory.cs:39-46`, so a probe is never in flight when cancellation lands) or throws `OperationCanceledException` (`PingManagerTests.cs:21-25`), which is the behavior the controller was written for and the production transceivers do not have. `DnsRefreshTests` asserts the end state, not the absence of a loss sample or a sweep.

**Proposed change (tiny):** make the transceivers and the resolver rethrow on the engine's own token (keep converting a *timeout* into a failed sample; that is a real loss). Then `PingController.cs:84` and `WatcherEngine.cs:287` are live again and the spurious sample never exists. Belt and braces: in `PingController.PollingLoop`, `if (_cancellationToken.IsCancellationRequested) break;` between the await and `SendMessage`. Tests: a transceiver fake whose `PingAsync` blocks until cancelled and then returns "Cancelled"; stop the controller mid-probe; assert nothing was written to the channel. In `DnsRefreshTests`, assert the fake's loss count does not change across the re-point.

### P1. A recorder batch whose write failed is retried only when the *next event* arrives, not "in 5s" (MEDIUM; follow-on to 2026-09-16 P1)

Evidence: `Libraries/ConPersistence/Services/ChannelRecorder.cs:45-56` and `:78-86`. `FlushAsync` drains the channel into `batch` before writing; on failure the items live in `_pending` and the channel is now empty, so after the 5 s delay the loop returns to `WaitToReadAsync` (`:45`) and blocks. The log line at `:53` ("retrying in 5s") describes a retry that only happens on the next event or at shutdown. Transitions are sparse (hours apart on a quiet link), so one transient write failure during an outage burst leaves that burst invisible to History, the report, MCP and warm-start seeding for hours. In archive mode (`WatcherHostService.cs:119-120`) nothing new ever arrives, so a failed flush sits until process stop. Nothing is lost (the shutdown flush at `:58-63` writes it), but "recorded" and "queryable" diverge for an unbounded time.

Why tests miss it: `FailingOnceDbFactory` tests call `FlushAsync` twice by hand; none runs the background loop through a failure with no second event.

**Proposed change (tiny):** after the delay in the catch, retry `FlushAsync` while `_pending is not null` before returning to `WaitToReadAsync`. Test: background loop, failing-once factory, one event, assert the row lands within the retry delay with no second event.

### P2. Two pending rollup buckets with the same key poison the collector until restart (MEDIUM; follow-on to 2026-09-16 P1)

Evidence: `Services/RollupCollector.cs:71-100`, `Rollups/RollupAccumulator.cs:31-49`, unique index at `ConWatchDbContext.cs:63`. After a failed write, bucket (T, M) stays in `_pending`. Ping results ride a channel and the pump (`WatcherEngine.cs:188-196`), so a reply stamped M:59.9 can be consumed after the 15 s tick that already closed minute M; `AddSample` then creates a **fresh** (T, M) bucket (`:37-38`), and the next `TakeClosed` appends it to `_pending`. In `WriteAsync` the per-bucket `SingleOrDefaultAsync` (`:84-86`) queries the database and cannot see the un-saved `Add` of the first (T, M), so both are `Add`ed; `SaveChangesAsync` hits the UNIQUE index; `_pending` is never cleared (`:94` is not reached) and grows every cycle; no rollup is written again until restart, where `_pending` is lost. The comment at `:66-69` describes the merge only for the case where the first bucket was committed in an earlier flush. Two rare events must coincide (a failed write and a late sample), but the blast radius is total and the only symptom is a repeating error log.

Why tests miss it: the `merges_duplicates` test covers the committed-first case only.

**Proposed change (small):** coalesce `_pending` by `(TargetId, PeriodStartUtc)` before writing (merge buckets with `RollupMerge`), or keep an `addedThisFlush` dictionary the way `GlitchRecorder.cs:22-29` does; cap `_pending` with a log-and-drop after N consecutive failures. This is also the natural place to replace the per-bucket `SingleOrDefaultAsync` with one query by `PeriodStartUtc >= min pending` matched in memory (see section 3). Test: failing-once factory, then a sample for the already-closed minute, then a flush; assert one row with the merged counts.

### P3. The ISP report's per-target rows ignore an outage already open at the window start (MEDIUM)

Evidence: `Report/IspReportBuilder.cs:163-165` loads transitions `<= toUtc`; `:195-197` filters each target to rows `> effectiveFrom`; `ComputeIncidents` (`:222-266`) starts with `open = null`. A target that went Down before `fromUtc` and recovered inside the window contributes 0 outages and 0 s; one Down for the whole window shows OutageCount 0. The link-level episodes in the same report do carry the pre-window state in (`before` at `:87-89`), so one report can say "link down all week" in the episodes and "0 outages" in the target table. The comment at `:221` says the function twins the History page's semantics, which may make this intended for the page; within one printed evidence document the two numbers still disagree.

**Proposed change (small):** the query already has no lower bound, so seed `open` from the latest row `<= effectiveFrom` per target (Down → outage at `effectiveFrom`, Unreachable → unreach), mirroring `BuildEpisodesAsync`. Test: one Down transition before the window and one Responding inside it; assert one outage of the clipped length. Owner decision: whether the History page should get the same seed.

### H2. After a disable (archive) rebuild every observer keeps the dead engine; the broadcaster keeps pushing "live" snapshots from it each second (MEDIUM)

Evidence: `WatcherHostService.cs:299-309` sets `Engine = null` and stops the engine, then `StartEngineAsync` returns at `:115-124` in archive mode without touching the observers. `StatusBroadcaster.SendSnapshotAsync` (`StatusBroadcaster.cs:86-100`) reads `_engine`, which is still the stopped engine, and `StatusSnapshotBuilder.Build` (`StatusSnapshotBuilder.cs:11-45`) happily reads its monitors' last states (disposed monitors remain readable), so every second all SignalR clients receive `statusChanged` with the old states. `/api/v1/status` correctly reports `Enabled=false`, but a dashboard tab opened before the disable was rendered live and keeps receiving live-looking pushes until reloaded. `MqttNotifier._engine`, `MetricsCounters._subscriptions` and the three recorders also hold the stopped engine's subjects, rooting the whole old engine graph until the next enable. Combined with H1 (the disable poke is swallowed), a demoted take-over source looks alive in HA and in every open browser.

Why tests miss it: no test disables the engine and then reads the next broadcast.

**Proposed change (small):** give observers a `Detach()` (see S1) and call it in the archive path and right after `engine.StopAsync()` in `RebuildAsync`; `StatusBroadcaster` sends one final snapshot with `Enabled=false` when its engine goes null so open tabs flip to archive mode. Test: attach, disable, assert the next broadcast is the disabled snapshot and no further live snapshot arrives.

### H3. The JSON exports and the snapshot serialize offset-less `DateTime`s straight from EF (MEDIUM)

Evidence: `Api/ExportEndpoints.cs:30`, `:52`, `:97` return the raw `TransitionRow`/`RollupRow`/`GlitchRow` lists via `Results.Json(rows)`; `BuildSnapshotAsync` (`:122-124`) does the same for the snapshot document. `ConWatchDbContext.OnModelCreating` has no value converter (no `HasConversion`/`SpecifyKind` anywhere under `Libraries/ConPersistence` except `CsvExport.cs:74`), so every `DateTime` comes back `Kind=Unspecified` and `System.Text.Json` emits `2026-09-18T10:00:00` with no `Z`. Every other surface re-stamps UTC by hand: `HistoryEndpoints.cs:24`, `GlitchEndpoints.cs:25-26`, `Mcp/ConWatchMcpTools.cs:98`, `Report.cshtml`, and the CSV path stamps `Z`. The JSON export is the one surface that hands a consumer (Grafana, a script, the clone importer on another node) timestamps most parsers read as local; the project's own rule is "UTC end to end".

**Proposed change (tiny):** one `ValueConverter<DateTime, DateTime>` (`v => v`, `v => DateTime.SpecifyKind(v, Utc)`) applied to all `DateTime` properties in `OnModelCreating`; storage format unchanged, no migration; then the five hand-stamps can go. Test: `/export/transitions?format=json` contains `Z`.

### E3. Pool, verdict and the status builders read each monitor's state several times per classification, and the coordinators publish while holding their lock (LOW)

Evidence: `PoolCoordinator.cs:60-100` snapshots `states` at `:64` then builds `detail` by re-reading `m.CurrentState` at `:82`, and calls `OnNext` at `:98` inside `lock (_lock)`. `VerdictCoordinator.cs:109-141` publishes at `:139` under `_lock`, and `Classify()` (`:143-199`) reads `CurrentState` in four passes plus once more in `List()`. `StatusSnapshotBuilder.cs:16-36`, `MetricsEndpoint.cs` and `ConWatchMcpTools.cs` read `CurrentVerdict`, `CurrentDetail`, `CurrentOperator` (three auto-properties set under the coordinator lock, read without it) and `CurrentState`, `IsAssumed`, `LastStatus` as separate reads. Monitor transitions now arrive on two threads (ping pump; DNS thread via `ReportResolutionFailure`/`StartTracking`).

I built the lock-order table for every publisher (below). There is **no deadlock**: the only lock held across a publish that can reach another monitor is a monitor's `_publishLock`, and the only subscriber that calls into other monitors (`TopologyCoordinator`) walks strictly downward in a tree, so no two threads can wait on each other's `_publishLock`. What remains is the second-read problem: between the classification and the detail string a state can change, and the detail contradicts the transition it explains ("Faltering: A=Responding, B=Responding"); a snapshot can pair `LinkDown` with the previous detail. Rare, self-healing next tick.

**Proposed change (small):** snapshot `(monitor, state)` once at the top of `Recalculate`/`Classify`; have `VerdictCoordinator` and `TargetMonitor` publish one immutable snapshot record swapped as a single reference under the existing lock; return the transition from the locked section and publish after the lock, the shape `TargetMonitor` now uses.

Lock-order table (publishers and what runs inline):

| Publisher (lock held at publish) | Threads | Inline subscribers | Locks they take |
|---|---|---|---|
| `TargetMonitor.DrainOutbox` (`_publishLock` only; `_lock` released) | ping pump; DNS refresh (`ReportResolutionFailure`, `StartTracking`); any thread inside another monitor's publish (topology) | engine merge subject → host observers; engine DNS-wake lambda; TopologyCoordinator; PoolCoordinator; VerdictCoordinator | descendants' `_lock` then `_publishLock` (topology); pool `_lock` → member `_lock` (read); verdict `_lock` → member `_lock` (read); discipline `_lock` (TriggerBurst) |
| `PoolCoordinator.Recalculate` (pool `_lock`) | same (inline) | engine merge subject → host observers, DNS-wake lambda | none beyond `ConcurrentDictionary` |
| `VerdictCoordinator.Recalculate` (verdict `_lock`) | same | same | none |
| `SuspicionCoordinator.CloseEpisode` (suspicion `_lock`, inside its own try/catch) | ping pump | engine glitch subject → GlitchRecorder, MetricsCounters | none |
| `PingManager.ProcessMessages` (no lock) | ping pump | engine routing lambda | monitor `_lock`, discipline `_lock`, suspicion `_lock`, RollupAccumulator `_lock` |

Threads that enter the graph: ping pump (1); per-controller probe loops (write only); per-hostname DNS refresh tasks; host request threads (read `CurrentState`, `LastStatus`, `Verdict.Current*` without holding anything); StatusBroadcaster's 1 s timer (reads); RollupCollector's flush timer (own lock only); WatcherHostService rebuild (calls `StopAsync`, never a publish).

### H4. `Engine.Enabled` changes rebuild the engine twice, and a failed rebuild answers 204 (LOW)

Evidence: `SettingsStore.cs:117-120` raises `SettingsChanged` for any `Engine.*` key, which schedules a debounced rebuild (`WatcherHostService.cs:321-345`); `AdminEndpoints.cs:32-36`, `CloneService.cs:57-72` and `:143-147` then call `host.RebuildAsync()` directly. Each toggle stops and starts the engine now and again `RebuildDebounceSeconds` later, with two MQTT refreshes, two "Engine started" lines and a second warm-seed carry. Separately, `RebuildAsync` turns every failure into a log line (`:311-314`); on the take-over source a failed `recorder.FlushAsync()`/`collector.FlushAllAsync()` (`:119-121`) still yields 204 from `/admin/engine`, so the peer fetches a snapshot missing the unflushed tail while the log on a node nobody is watching says "rebuild failed".

**Proposed change (tiny):** `RebuildAsync` cancels any pending debounce at the top (`lock (_debounceLock) { _debounceCts?.Cancel(); }`, safe because callers await `SetManyAsync` first) and returns `bool`; the debounced `Task.Run` logs, direct callers surface a 500 so `CloneService.TakeOverAsync` aborts before disabling anything it cannot recover.

### H5. Shutdown budget: the engine's worst case runs before the recorders' final flush and the container gets 10 s (LOW)

Evidence: `WatcherHostService.cs:100` → `WatcherEngine.cs:354` (`manager.StopAsync(5 s)`) → `PingManager.cs:107-114` (5 s controllers, then up to 5 s pump drain). Hosted services stop in reverse order, so the recorders' final `FlushAsync(CancellationToken.None)` (`ChannelRecorder.cs:58-63`, `RollupCollector.cs:49-53`) runs after the engine, broadcaster and notifiers; `MqttNotifier.cs:128-133` publishes `offline` and disconnects with `CancellationToken.None` (how long MQTTnet blocks against a black-holed broker: unverified). `HostOptions.ShutdownTimeout` is not set (default 30 s); `deploy/docker/compose.yaml` sets no `stop_grace_period` (Docker default 10 s); the Dockerfile has no `STOPSIGNAL`. Normally the engine stops in milliseconds (E2's fix keeps it that way), but any wedged controller or a black-holed broker eats the whole grace and SIGKILL lands before the last minute of rollups is written.

**Proposed change (tiny):** `stop_grace_period: 30s` in compose (and the Synology notes); `HostOptions.ShutdownTimeout` set explicitly below it; engine grace cut from 5+5 to 2+2 s; a 2 s CTS around the MQTT goodbye.

### E4. Sweep cooldown constant is dead: the discipline's 60 s guard overrides the coordinator's 30 s (LOW)

Evidence: `SuspicionCoordinator.cs:19` `SweepCooldown = 30 s` gates `Sweep()` at `:159`; each `Discipline.TriggerBurst()` then hits `PingDiscipline.cs:135` `SuspicionGuard = 60 s` and returns without bursting when the last burst on that target started under 60 s ago. A second sweep 30-59 s after the first does nothing on any target that took part in the first one. Tests assert `TriggerBurst` was *called*, not that a burst *started*.

**Proposed change (tiny):** one constant, owned by the discipline and read by the coordinator, or delete `SweepCooldown`. Owner decision: which number is intended.

### E5. Ping timestamps come from the wall clock, not the engine's `TimeProvider` (LOW)

Evidence: `Core/ConCore/PingResult.cs:20` `Timestamp = timestamp ?? DateTimeOffset.UtcNow`; neither production transceiver passes a timestamp. Downstream is `TimeProvider`-driven: `HostAnalysisEngine` stamps threshold transitions with `LastUpdated` (`HostAnalysisEngine.cs:104-105, 170-171`), and `TargetMonitor.QueueOutcomeLocked` computes `BurstLossRate` by comparing history timestamps to `Discipline.BurstStartedAt` (`TargetMonitor.cs:253-261`), which is `TimeProvider` time. In production the clocks agree; under `FakeTimeProvider` the burst-loss window spans two clocks, which is why `BurstLossRate` is never asserted in the warm-start or discipline tests.

**Proposed change (tiny):** stamp results in `PingController.SendMessage` from the engine's `TimeProvider` (the controller can receive it alongside the cadence). Test: with a fake clock, a transition raised during a burst carries a `BurstLossRate`.

### E6. Profile validation does not check that thresholds are ordered (LOW)

Evidence: `WatcherOptionsValidator.cs:93-111` checks each threshold is in [0, 1] and `MinUsableSample <= HistorySize`, but not `DownThreshold < RecoveringThreshold` or `FalteringThreshold < RespondingThreshold`. With `Responding = 0.5, Faltering = 0.75` the state machine (`HostAnalysisEngine.cs:144-149`) flips Responding→Faltering→Responding on every sample between the two. The Profiles page can save this.

**Proposed change (tiny):** two ordering checks, one test each.

### P4. Any `DbUpdateException` in a config mutation reads as "duplicate name or address"; whole-config reads are not one snapshot (LOW)

Evidence: `ConfigService.cs:283-289` maps every `DbUpdateException` to a Validation failure with a uniqueness message; a foreign-key violation or a full disk gets the same text. (The delegated reviewer also argued a write conflict would land here via `SQLITE_BUSY_SNAPSHOT`; that premise is wrong: Microsoft.Data.Sqlite's default transaction is `BEGIN IMMEDIATE`, so writers queue behind the 30 s busy timeout instead. Dropped.) Separately, `ConfigStore.LoadWatcherOptionsAsync` (`:134-153`) issues three reads outside a transaction, each its own WAL snapshot; `WriteDocumentAsync` deletes and re-inserts every row and mints new ids for a document without ids, so an import committing between the profile read and the target read makes `profileNamesById[t.ProfileId]` throw `KeyNotFoundException`; from `StartEngineAsync:126` that aborts the rebuild ("Engine rebuild failed", no engine until the next config change). Two writes within milliseconds, so rare.

**Proposed change (tiny):** map to Validation only when `ex.InnerException is SqliteException { SqliteExtendedErrorCode: 2067 }`; wrap the three reads (and `ExportAsync:161-162`) in one `BeginTransactionAsync`.

### P5. `PRAGMA incremental_vacuum` after the nightly pass is a no-op (LOW)

Evidence: `Services/RetentionService.cs:47-49` runs it when `converted > 0`; incremental vacuum only works when `auto_vacuum=INCREMENTAL` was set before the tables were created, and nothing in the repo sets `auto_vacuum` (grep is empty). The file never shrinks after downsampling; only `DataPruner`'s full VACUUM does. The `converted` bookkeeping in `RetentionPass` exists only to feed this pragma.

**Proposed change (tiny):** either `PRAGMA auto_vacuum=INCREMENTAL` in `DatabaseStartup.PrepareAsync` before `MigrateAsync` (new files; existing files need one VACUUM), or delete the pragma and the `converted` counter.

### P6. Rollup buckets are taken out of the accumulator before the flush lock is acquired (LOW)

Evidence: `RollupCollector.cs:58-59`, `:62-63` evaluate `TakeClosed()`/`TakeAll()` as argument expressions, then `WriteAsync` waits on `_flushLock.WaitAsync(ct)` at `:73`. If `ct` fires while waiting (host stop while a take-over `FlushAllAsync` holds the lock), the `OperationCanceledException` propagates and those buckets are in neither the accumulator nor `_pending`; the shutdown `FlushAllAsync(None)` takes only what is left. Narrow window; silent loss of up to one flush of closed minutes.

**Proposed change (tiny):** take inside the lock (pass the take as a delegate), or wait on the lock with `CancellationToken.None` and pass `ct` only to the DB work.

### P7. Recorder channel drops are silent, and the pumps end on any `OperationCanceledException` (LOW)

Evidence: `ChannelRecorder.cs:16-17` uses `BoundedChannelFullMode.DropOldest`; with that mode `TryWrite` evicts and returns `true`, returning `false` only for a completed channel, so the "channel full; dropped" warning at `:35-36` can never fire. A long DB outage discards transitions with no log line (DropOldest is still the right policy: the newest row is what warm start seeds from). `ChannelRecorder.cs:58`, `RollupCollector.cs:49`, `RetentionService.cs:56-59` catch `OperationCanceledException` unfiltered, so an OCE unrelated to `stoppingToken` silently terminates a `BackgroundService` while the host runs on.

**Proposed change (tiny):** the `Channel.CreateBounded(options, itemDropped: ...)` overload; `when (stoppingToken.IsCancellationRequested)` on the outer catches.

### H6. Smaller host items (LOW)

- **Debounce CTS at stop.** Re-checked 2026-09-18 while planning: `OnConfigChanged:326-327` disposes the previous source on every change, winner or not, so at most one is outstanding; only `StopAsync:90` leaves one behind. Fixed there in the follow-up plan's Task 4.2.
- **Admin gate.** `AdminGate.cs:43-49` delays 1 s per failed attempt on the request's own thread, so parallel attempts are bounded only by PBKDF2 cost; `/auth/set-password` (`AuthEndpoints.cs:48-57`) re-signs the caller but every other browser's cookie stays valid. Fine for a LAN tool; it is the only lock on the take-over channel, which is open to the network. Fix: a global `SemaphoreSlim(1)` around failed verifications; a "password generation" claim checked in `OnValidatePrincipal`.
- **`/health`, `/metrics`, `/status` hit the database per call** for `Engine.Enabled` (`WatcherHostService.cs:110-111` opens a context and `FindAsync`s), with the Docker HEALTHCHECK every 10 s and the tray every 5 s. The host service already decided enabled/disabled in `StartEngineAsync:115`. Fix: a `bool EngineEnabled` set there.
- **Broadcaster ordering.** `StatusBroadcaster.cs:86-100` gates under the lock but sends outside it; a cadence tick and a transition-triggered send 250 ms apart can deliver the older snapshot last. Fix: a `seq` in the payload the client ignores if older, or serialize sends.
- **`HistoryModel` reads `host.Engine` twice** (`Pages/History.cshtml.cs:20`, `:27`); a rebuild between them skips both branches and serializes an empty subject list. Harmless; folds into S3.
- **`StartAsync` has no rollback.** `WatcherHostService.cs:146-153` attaches observers, then `engine.StartAsync`. `WatcherEngine.StartAsync` (`:95-224`) is synchronous with no cleanup path; every throw point I could find is before any child is live (`TargetGraph.FromOptions` at `:101`, validated input), so today this is theoretical. If a later change adds a throw after `:187` (pump started), a half-built engine keeps pinging with `Engine == null`. Fix: `try { attach; await engine.StartAsync } catch { await engine.StopAsync(); detach; throw; }`.

Engine lifecycle otherwise checked and found correct after the 2026-09-17 fixes: `_appStopping` is the real token; `_stopped` is set under the rebuild semaphore; `RebuildAsync` is serialized and refuses after stop; `Attach` only runs under `_rebuildLock` after the old engine stopped; `StopAsync` is idempotent. Note for the registration comment at `ConWatchServices.cs:147-152`: the engine stops before the recorders because its token is `ApplicationStopping` (which fires before any `StopAsync`), not because it is registered last; both are true, only one is the reason.

---

## 2. Structure and reorganization

### S1. One `IEngineObserver { Attach(WatcherEngine); Detach(); }` injected as `IEnumerable<>` — `Programs/ConWatch/WatcherHostService.cs:17-36, 146-152`
`WatcherHostService` has 19 constructor parameters, seven of which exist only to be attached at `:146-152`, with two `Attach` shapes: `Attach(IObservable<T>)` (`ChannelRecorder<TargetTransition>`, `ChannelRecorder<GlitchEvent>`, `RollupCollector`) and `Attach(WatcherEngine)` (`StatusBroadcaster`, `MetricsCounters`, `MqttNotifier`, `WebhookNotifier`). `ConPersistence` already references `ConEngine.Events`, so an interface declared in `ConEngine` works for all seven; each picks its own stream off the engine inside `Attach`. Registration order becomes attach order next to the hosted-service list in `ConWatchServices.cs:154-166`, so the "recorders first" rule lives in one list. `Detach()` is what H2 needs. The archive path also flushes the three recorders (`:119-121`), so keep those references or add `IFlushable`; the count drops to about 14.

### S2. Warm-start policy out of the host service — `WatcherHostService.cs:159-284`
`BuildSeedFromDbAsync`, `DropUnjustifiedUnreachable`, `BuildSeedFromEngine` (~125 lines) are pure policy over `WarmStartQueries` and config, with no lifecycle state; the settings-default table at `:57-78` is a third concern. A `WarmStartSeeder(dbFactory, store, settingsStore, timeProvider, log)` with `FromDbAsync()` and `FromEngine(engine)` leaves the host service at ~150 lines of choreography, which is where the concurrency lives. Pair with S8's `SettingsDefaults`.

### S3. Trunk/anchor roles computed three times with two different rules
`Pages/History.cshtml.cs:31-41` (engine graph by `ParentId`, `TreeWalk.AncestorsOf`), `TopologyNames.cs:32-39` (stored config by name, `AncestorsOf`), and `Pages/Config/Targets.cshtml.cs:32-63` (stored config, but the trunk leaf is `pool.Parent ?? unique shared parent` walked with `ChainToRoot`, a **different** rule from `AncestorsOf(members)`; with two anchors under different parents the Targets pipeline shows no trunk while History shows both chains). `IndexModel` already uses `TopologyNames` even with an engine running (`Index.cshtml.cs:69`). One home: `HistoryModel` uses `TopologyNames` in both modes (117 → ~45 lines); `TargetsModel` uses it too, or `TopologyNames` grows the branches computation, so the pipeline view and the history ribbon agree on what "trunk" means.

### S4. `HostTracker` is a locked wrapper around a pure engine, publishing to nobody — `Core/ConEngine/Tracking/HostTracker.cs`
Every call into `HostTracker` arrives under `TargetMonitor._lock` (`TargetMonitor.cs:127, 131, 157, 168, 189`), and `HostTracker` takes its own `_lock` again (double locking, checklist item 10; the 2026-09-17 tidy removed one layer, this is the other). Its two subjects (`:24-25`, `:57-60`) are published at `:153-160` **while the monitor lock is held** (the one remaining publish-under-lock in the engine) to no production subscriber: `HostStatusSource` has no reference outside its file, `HostStateChangeSource` is referenced only by `HostAnalysisEngineTests`. `TargetMonitor.StatusSource` (`:68`) likewise has no subscriber anywhere; `LastStatus` is the polling surface, yet every sample allocates a `TargetStatus` and enqueues a closure for it (`:241-246`). Proposed: make `HostTracker` pure like `HostAnalysisEngine` (history buffer + engine, no lock, no subjects, returns `AnalysisOutcome`), owned and locked by `TargetMonitor`; drop `StatusSource` and its per-sample publish; keep `LastStatus`. Tests that used the tracker subjects switch to the returned outcome.

### S5. `MqttNotifier`'s wake path replaced by the channel it already has — `Libraries/ConNotify/MqttNotifier.cs:21-23, 43-49, 107-119`
The session loop needs three inputs: refresh, transition, reconfigure. Two already travel in `_queue`; the third goes through `_wake` and the `WhenAny` that causes H1. Writing a `Reconfigure` record from the `NotifierSettingsChanged` handler makes the session loop one `ReadAsync`, the outer disabled-wait a read that ignores everything but `Reconfigure`, and deletes the semaphore, both wake tasks, and the `Attach`-time `Release()` at `:49` (redundant with the queued `Refresh`).

### S6. `ConPersistence` folders no longer match responsibilities
`Services/SnapshotImporter.cs` is in namespace `ConPersistence`, not `.Services`; `Rollups/RetentionPass.cs` prunes glitches and transitions (`:26-50`) but lives under Rollups; config CRUD, settings, and read queries sit at the root; `Services/` mixes three recorders with a static `DataPruner` and `RetentionService`. Move-only: `Config/` (ConfigService, ConfigStore, ConfigHealth, ConfigMapping, ConfigDtos, ConfigRows), `Settings/`, `Recording/` (ChannelRecorder, the two recorders, RollupCollector, RollupAccumulator, RollupMerge), `Retention/` (RetentionPass, RetentionService, DataPruner), `Queries/` (HistoryQueries, WarmStartQueries), `Snapshot/`; keep `Report/` and `Export/`.

### S7. `SnapshotImporter` bypasses the DI singletons — `Services/SnapshotImporter.cs:37-38`
It news up its own `ConfigStore`/`ConfigService`, so the singleton's `ConfigChanged` never fires on import and `CloneService.cs:70` compensates with an explicit `host.RebuildAsync()` (which is also how H4's double rebuild arises). Accept a `ConfigService` for the in-process path; keep the static file path for the CLI/test route.

### S8. Settings defaults live in two diverging lists — `WatcherHostService.cs:67-77` vs `Api/AdminEndpoints.cs:150-155`
Startup seeds seven keys from options; factory reset seeds four from literals (`"2"`, `"90"`, `"13"`, `"3"`), omitting `TransitionRetentionMonths`, `WarmRestartMaxGapMinutes` and `AnchorPool`; the warm-restart literal `10` appears at `:74`, `:160`, `:170`. Nothing breaks today because `GetIntAsync` has fallbacks. One `SettingsDefaults.For(verdictOptions, persistenceOptions)` called from both. Also `PersistenceOptions.RebuildDebounceSeconds` (`PersistenceOptions.cs:24-25`) is a host concern read only at `WatcherHostService.cs:331`, and `RetentionService.EffectiveOptionsAsync` (`:22-34`) rebuilds the whole options object to override four ints, so any new option field silently reverts to its default during the nightly pass: give `RetentionPass.RunAsync` a `RetentionWindows(MinuteDays, HourlyMonths, TransitionMonths)` record instead.

### S9. `SetupService.ApplyAsync` is N separate transactions — `Programs/ConWatch/SetupService.cs:31-126`
Up to 2 + anchors + 1 upserts, each its own transaction and `ConfigChanged`; a failure at anchor 3 leaves router, WAN and two anchors committed without the pool. Build the additive `ConfigDocument` in memory and commit through one `MutateAsync`-shaped call, so the wizard is atomic and raises one event.

### S10. `/health` inline in `Program.cs:73-90`
The only endpoint not in an `Api/*Endpoints.cs` file; it belongs in `StatusEndpoints` next to `/status`, and it gains the stall check from E1 there.

### S11. Leftover directories
`ConInfrastructure/`, `ConNotify/`, `ConPersistence/` at the repo root still exist as ignored `bin/obj` shells from before the `Libraries/` move (`git status --ignored`), alongside `.superpowers/`, `dist/`, `staging/`. Delete the three shells; harmless but confusing in an IDE.

---

## 3. Simplification and duplication

**ConEngine / ConCore**
- Dead or test-only public members (script + grep, verified): `CircularBuffer.GetLatest` (unused), `HostAnalysisEngine.LastUpdated/NumSuccess/NumFailed` public setters (internal class; fine to leave), `HostTracker.HostStatusSource` (unused), `HostStateChangeSource`, `HistoryCount`, `MinUsableSample` (test-only), `TargetMonitor.StatusSource` (unused; see S4), `PingDiscipline.IsParked` (test-only), `TargetGraph.GetByName/GetPoolByName/GetChildren` (test-only; keep as test helpers via `InternalsVisibleTo` or leave), `PingController.Stop()` (only called by `StopAsync`; make private), `WatcherEngine.DefaultDnsRefreshInterval` (public static, internal use). `DomainResolutionResult` carries `IpAddresses`, `AddressCount`, `FirstAddress`, `IPv6Addresses`, `ToDetailedString` with no callers (the engine reads only `IPv4Addresses`); the record can be `(IReadOnlyList<IPAddress> Addresses, string? Status)` plus the IPv4 filter.
- `WatcherOptionsValidator.cs:73-91` has its own name-keyed cycle walk; `TreeWalk.ChainToRoot` is already cycle-safe and depth-capped and is what every other walker uses (2026-09-17 S7).
- `WatcherEngine.cs` (360 lines) holds the DNS follower (`:226-337`, ~110 lines); a `DnsFollower` class owning `ResolveAndActivateAsync`, `RepointAsync` and `_dnsRefreshRequests` would leave the engine as pure wiring. Optional; do it with E1's catch and E2's fix if the file is open anyway.
- Abandoned `Task.Delay` timers: `PingDiscipline.cs:172-178` and `WatcherEngine.cs:330` leave the losing delay running until it elapses (bounded by the token). Not a bug; a `using var timeout = new CancellationTokenSource()` linked to `ct` and cancelled on the wake path frees the timer.

**ConInfrastructure**
- `DomainNameResolverDns.cs` is 174 lines for a `Dns.GetHostAddressesAsync` wrapper: `Dispose(bool)` plus a finalizer (`:143-173`) with no unmanaged resources (the 2026-09-17 finalizer removal stopped at the engine), so every resolver the engine creates per hostname target (`WatcherEngine.cs:274`) and per probe test is queued for finalization; five catch blocks (`:69-137`) each build the same failure result. A `sealed` class with a no-op `Dispose` (as `TcpConnectTransceiver.cs:58`) and one catch mapping to a status string is ~40 lines. Do this together with E2.
- `GatewayDiscovery.cs:35-50` pings up to six candidates sequentially at 1 s each; `Task.WhenAll` makes the wizard's gateway step 1 s instead of 5-6 s.

**ConPersistence**
- `TargetRow` field assignment written three times: `ConfigService.cs:128-135`, `:162-172`, `ConfigStore.cs:96-103`; a new column goes in three places plus `ConfigMapping`. One `TargetRow.CopyFrom(TargetDto, profileId, parentId)` next to `ProfileRow.CopyFrom` (`ConfigMapping.cs:93`). While there: `Address.Trim()` happens in the write paths but not in `TargetDto.ToConfig()` (`ConfigMapping.cs:121-126`), which is what the validator sees on import; `Name` is never trimmed anywhere.
- The 10-field profile list appears five times (`ConfigMapping.cs:34, 49, 64, 78, 93`) and the defaults three times (`ConfigDtos.cs`, `ConfigRows.cs`, `PingProfileConfig`). One row↔DTO pair, `ProfileRow.ToConfig()` as `ToDto().ToConfig()`, defaults on `PingProfileConfig` only.
- `Dto/Snapshot.cs:18-20` serializes the EF entity classes as the wire format; any column change silently changes the snapshot JSON while `SchemaVersion` stays 1. Explicit snapshot records, or a test that pins the serialized shape.
- N+1 leftovers, none hot: `ConfigStore.WriteDocumentAsync:107-108` one SELECT per parented target for rows it just added; `ConfigService.UpsertPoolAsync:204-210` one per member name; `RollupCollector.WriteAsync:84-86` one per pending bucket per flush (fold into P2's fix); `RetentionService.EffectiveOptionsAsync:26-33` four contexts for four settings.
- `RetentionPass.DownsampleAsync:58-61, 86` loads every eligible fine row *tracked* into one context and `SaveChanges` per group, so a first run after upgrade or a long-disabled retention is roughly quadratic in `DetectChanges`. Page by target, `AsNoTracking` the source, `ExecuteDeleteAsync` inside the per-group transaction.
- Unbounded result sets: `IspReportBuilder.cs:163-165` loads every transition of every target since the beginning of time (only `<= toUtc`) to find the latest HistoryReset; `HistoryQueries.RollupsAsync:57-63` has no cap (90 days of minute rows = 129,600 per target). One grouped query for the latest reset per target plus rows in range plus the row `<= fromUtc` (P3); clamp `RollupsAsync` like `TransitionsAsync`.
- Dead: `RollupBucket.RttSum` public (used internally only), `SnapshotImporter.ImportToFileAsync`, `RollupCollector.FlushClosedAsync` and `DataPruner.PruneResult` are test-only.
- Misleading text to fix with the findings above: `ChannelRecorder.cs:35-36` (P7), `:53` (P1), `RollupCollector.cs:66-69` (P2), `RetentionService.cs:47-49` (P5); `RetentionPass` "converted" mixes downsampled rollups with pruned rows.
- No test asserts the migrations match the model (tests use `EnsureCreated`, `TestDbFactory.cs:22`); one `db.Database.HasPendingModelChanges()` assertion catches a forgotten migration.

**ConWatch / ConNotify**
- The transition DTO is hand-built four times with three timestamp encodings: `Builders.cs:123-138` (string `FFF'Z'`), `StatusBroadcaster.cs:40-54` (`DateTimeOffset`), `HistoryEndpoints.cs:21-29` (`SpecifyKind` → `DateTimeOffset`), `ConWatchMcpTools.cs:96-101` (`SpecifyKind`). `ConWatchMcpTools.GetCurrentStatus` re-derives `StatusSnapshotBuilder.Build` with renamed fields; `MetricsEndpoint.cs` hard-codes `"Internet"` as the verdict subject's name. One `TransitionDto.From(TargetTransition)` and one row projection; `StatusSnapshotBuilder` gains `Operator`; the verdict name becomes a constant next to `VerdictCoordinator.SubjectId`.
- Error-body shape: `StatusEndpoints.cs:21` returns `{ error }` (singular) while every other 4xx/5xx is `{ errors: [...] }` or `{ errors, code }` (`SetupEndpoints.cs:40-47`, whose private `ToHttp` duplicates `ApiResults`). One `ApiResults.Error(status, code, messages)`.
- Exports materialize every row and then one string: `ExportEndpoints.cs:26, 48, 93` (`ToListAsync` over the whole filter) and `:135-139` (`string.Join` + `GetBytes`); 90 days of minute rollups for ten targets is ~1.3 M rows held twice. Stream CSV lines to the response body; JSON via `Results.Json` over an `IAsyncEnumerable`.
- `MetricsCounters.cs` and `StatusBroadcaster.cs` both keep a `List<IDisposable>`/`IDisposable` plus a lock for one subscription; S1 removes both.

---

## 4. What is in good shape

- **The monitor publish path.** `TargetMonitor`'s outbox (`_lock` for state, `_publishLock` for delivery order, nothing held that a subscriber can reach) is the reason the lock-order table above closes with no cycle. Do not touch it while doing E3; make the coordinators look like it.
- **`PingDiscipline`.** Phase-locked slots, the burst wake race (`WaitForNextPingAsync:168-180`), the park/release pair, and the suspicion guard are all correct under both clocks; `PingDisciplineTests` covers the timing edges properly.
- **Per-target controllers and id-based routing.** `PingManager` is small, idempotent on stop, bounded on every phase, and `RoutedPingResult` makes the address a detail. E2 is a transceiver problem, not a routing one.
- **Warm start end to end.** `EngineSeed` → `TargetMonitor.Seed` → `HostAnalysisEngine.Seed` with the "no threshold transition below MinUsableSample" rule and the Down→Recovering exit is coherent and well-tested (`WarmStartTests`, 322 lines).
- **Host lifecycle after the 2026-09-17 fixes.** Real stopping token, serialized rebuild, guarded debounce, hosted-service order stated next to the registrations, every singleton on `IDbContextFactory`.
- **Recorders decouple the engine from the database.** Bounded `DropOldest` channels, one base class, the pending-batch rule, serialized flushes; P1/P2 are edges of a sound design, not holes in it.
- **Time handling at the edges.** One `TimeRange.ParseUtc` for every query string, JSON body and MCP argument; no `DateTime.Now`/`ToLocalTime` anywhere; retention and bucket boundaries are UTC and `TimeProvider`-driven. H3 is the one export edge that missed the rule.
- **Auth and secrets.** PBKDF2 with a random salt, `FixedTimeEquals`, fail-closed on a corrupt file, one `VerifyOrDelayAsync`, one page filter, `LogSafe` and `RedactedSettings` keep secrets out of logs and snapshots.
- **Tests are honest.** Real app per fixture in its own temp directory, no shared mutable statics, failure-mode tests (failing-once factories, duplicate-period merge, retention idempotency) rather than happy paths only.

---

## 5. Suggested sequencing

Each step is one small PR with its own failing-first test. Safety first, then durability, then structure, then dedupe.

1. **H1 + S5**: `MqttNotifier` reconfigure-via-channel; delete the semaphore. (small)
2. **E2**: transceivers and resolver rethrow on the engine token; controller checks the token before `SendMessage`; engine reads `result.Success`. Fold in the `DomainNameResolverDns` slimming (section 3). (tiny + tiny)
3. **E1 + S10**: per-message guard in the pump, last-result stamp, `/health` stall check moved to `StatusEndpoints`, DNS follower catch. (small)
4. **P1, P7**: recorder retries its pending batch after the delay; `itemDropped` logging; filtered outer catches. (tiny)
5. **P2 + the rollup N+1**: coalesce `_pending` by key, one lookup query per flush, failure cap. (small)
6. **H2 + S1**: `IEngineObserver` with `Detach()`; archive path and post-stop detach; final disabled snapshot. (moderate; touches seven observers, but each change is mechanical)
7. **H3 + S9-adjacent**: UTC value converter on the DbContext; drop the five hand-stamps. (tiny)
8. **P3**: seed the report's per-target incidents from the pre-window row. (small)
9. **H4, H5, H6**: cancel pending debounce inside `RebuildAsync`, return a result, 500 on take-over failure; compose `stop_grace_period`, explicit `ShutdownTimeout`, shorter engine grace; dispose the winning CTS. (tiny each)
10. **E3**: coordinators snapshot states once and publish outside the lock; verdict/monitor snapshot records for the builders. (small)
11. **E4, E5, E6, P4, P5, P6**: the one-line fixes, batched. (tiny)
12. **S2, S8**: `WarmStartSeeder`, `SettingsDefaults`, `RetentionWindows`. (moderate, pure moves)
13. **S3**: one trunk computation (`TopologyNames`) for History and Targets. (small; needs decision 3)
14. **S4**: pure `HostTracker`, drop `StatusSource`. (small)
15. **S6, S7, S9, S11**: persistence folder moves, `SnapshotImporter` on DI, atomic setup, delete the root shells. (moves)
16. **Section 3 dedupe**: `TargetRow.CopyFrom`, one profile mapping pair, `TransitionDto`, `ApiResults.Error`, snapshot records, streaming exports, `GatewayDiscovery` parallel, dead members. (many tiny PRs)

**Status 2026-09-18 (later the same day):** steps 1–9 and 11 landed in full and step 10 in part (the snapshot-once half; the publish-outside-lock outbox is deferred) on branch `review-2026-09-18` via `plans/2026-09-18-review-fixes.md`; E1 additionally gained a `ConfigInvalid` health status for a stored config the stricter validator now rejects. Steps 12–16 and the deferred items are listed in `plans/2026-09-18-review-fixes-followups.md`.

Decisions needed from the owner before starting:

1. **E4**: is the intended sweep cadence 30 s or 60 s? The coordinator says one, the discipline enforces the other. **DECIDED 2026-09-18: 60 s. Delete `SweepCooldown`; the discipline's guard is the only rule.**
2. **P3**: should the History page also seed an open outage from the pre-window row, or is "starts fresh at the window" intended there and only the report should differ? **DECIDED 2026-09-18: seed both. One rule everywhere: an outage open at the window start counts, clipped to the start. Report target rows seed from the latest row `<= effectiveFrom`; the page's `incidents.ts` verdict windows and incident counters take a pre-window state (the ribbon already does via the first transition's `from`).**
3. **S3**: which trunk rule is right when anchors do not share a parent: the union of all ancestor chains (History, `TopologyNames`) or the chain above the pool's parent (Targets pipeline)? **DECIDED 2026-09-18: union of all ancestor chains everywhere (the engine's rule). `TopologyNames` is the one source of trunk/anchor names; the Targets pipeline learns to draw more than one chain. Revisit the drawing when dual-WAN failover groups land.**
4. **P5**: turn on `auto_vacuum=INCREMENTAL` for new databases (existing ones need one VACUUM to convert), or drop the pragma and rely on the Data page's VACUUM? **DECIDED 2026-09-18: drop the pragma and the `converted` bookkeeping. Space reclamation stays on the Data page's full VACUUM.**
5. **H5**: where does the shutdown budget go: raise the container grace to 30 s, or accept 10 s and shorten the engine's grace to fit under it? **DECIDED 2026-09-18: raise and fit. `stop_grace_period: 30s` in compose plus a Synology note; `HostOptions.ShutdownTimeout` = 25 s; engine grace 2 + 2 s; MQTT goodbye bounded by a 2 s token.**
6. **Section 3, `DomainResolutionResult`**: shrink the record to what the engine reads, or keep the IPv6/detail members for a future traceroute or wizard use? **DECIDED 2026-09-18: keep the record's shape; fix the consumers. The engine's DNS follower and the Targets Test button read `Success`/`Status` and surface the real reason ("Host not found", "Temporary DNS failure") instead of "No IPv4 address". The resolver still gets E2's rethrow-on-token and the finalizer removal; no `ConCore` shape change.**
7. **Report location**: this file lives in the session scratchpad like the 2026-09-16 one; if the follow-up plan will cite it by id again, it may be worth committing under `docs/superpowers/reviews/`. **DECIDED 2026-09-18: committed as `docs/superpowers/reviews/2026-09-18-codebase-review.md`. The 2026-09-16 report was chat-only and is not recoverable; its decisions live in `plans/2026-09-17-cleanup-and-concurrency.md`.**

All seven decisions are recorded above; the follow-up plan can start from them.
