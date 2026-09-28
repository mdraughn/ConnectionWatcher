# Follow-ups from the 2026-09-18 review fixes (input to plan B)

Branch `review-2026-09-18` implemented `2026-09-18-review-fixes.md` (the correctness and durability half of `../reviews/2026-09-18-codebase-review.md`). This file collects what the per-task and final reviews deferred, triaged by the final whole-branch review. Nothing here blocks merge.

## Structure and dedupe (review steps 12–16, unchanged)

Done on plan-b (2026-09-25).

~~S2 `WarmStartSeeder` + `SettingsDefaults` + `RetentionWindows`; S3 one trunk computation (`TopologyNames`) for History and Targets (owner decision: union of ancestor chains); S4 pure `HostTracker`, drop `StatusSource`; S6 persistence folder moves; S7 `SnapshotImporter` on DI; S9 atomic setup; S11 delete the root `bin/obj` shells; section 3 dedupe (`TargetRow.CopyFrom`, one profile mapping pair, `TransitionDto`, `ApiResults.Error`, snapshot records, streaming exports, `GatewayDiscovery` in parallel, dead members).~~

## Deferred from the review (stated reasons)

- ~~E3's publish-outside-lock outbox for `PoolCoordinator`/`VerdictCoordinator` (needs a `TryEnter` drain; no deadlock exists today).~~ Done on plan-b (2026-09-25).
- ~~P4's one-snapshot read for `ConfigStore.LoadWatcherOptionsAsync` (needs `SqliteConnection.BeginTransaction(deferred: true)` threaded through EF).~~ Done on plan-b (2026-09-25).

## Next-plan items from the branch reviews

Engine
- `PingResult` positional rebuild in `PingController.SendMessage` → a record `with` (needs init-only properties).
- Timeout status strings ("TimedOut"/"Timeout"), the ICMP `IPStatus.TimedOut` path, and resolver mid-flight cancellation have no assertions; `DomainNameResolverDnsTests.Unknown_host` and `ProbeTestEndpointTests.Unresolvable_hostname` depend on live DNS for `.invalid` (fake one).
- DNS follower: the catch around `Activate`/`RepointAsync` has no reachable trigger and no test; a non-shutdown `TaskCanceledException` would still exit the follower silently.
- Persistent bad subscriber: one `LogError` per message at the ping rate; throttle.
- `EngineHealth.Classify`: no test for a configured-but-unresolved hostname; the seeded `ignoreUnknown` verdict path no longer overwrites `Detail` (correct) but has no assertion.
- `Inbox` (MQTT): document that a `Reconfigure` is droppable only when no reader is live and every no-reader path re-reads settings.

Host
- `SettingsStore` should own the master switch (set from the written values before raising the event) so `WatcherHostService.OnConfigChanged` no longer does a guarded synchronous read; ~~`EngineEnabledAsync()` is still called per request by `ConWatchMcpTools` and `Index.cshtml.cs`.~~ Done on plan-b (2026-09-25): `EngineEnabledAsync()` is now private to `WatcherHostService` (grepped — nothing else called it).
- `StatusBroadcaster`: an abandoned send keeps `_sendGate` (pre-existing hub-wedge exposure); `Dispose` disposes the gate while a push may hold it; the "transition" hub message is not identity-gated; no client test for `statusStore.disabled`; five `Detach()` bodies are the same two lines with inconsistent locking (serialized by `_rebuildLock`; comment or share).
- `StartAsync`'s boot start runs outside `_rebuildLock` (an explicit `RebuildAsync` during boot could interleave).
- Tests with fixed delays: `MasterEnableTests` 500 ms pairs, `PingPumpResilienceTests` 200 ms then strict `>`.
- History page: with no in-range transition the page cannot seed a pre-window outage (the report can); the transitions API should return the latest pre-window row. `incidents.ts` `verdictWindows` doc comment should say an open verdict window at the range start is not attributed.
- REST additions to put in release notes: `/status` and snapshots carry `enabled`; `/health` carries `stalled`, `silenceSeconds`, `startFailure` and the `EngineStalled`/`ConfigInvalid` statuses; `/targets/probe-test` carries `resolutionError`; JSON export and snapshot timestamps end in `Z`; `/admin/engine` answers 500 on a failed rebuild.
- Upgrade note: a stored profile with `Faltering >= Responding` or `Down >= Recovering` now fails validation at boot; `/health` says `ConfigInvalid` with the message; fix it on the Profiles page.

Persistence
- `"Minute"` resolution is hardcoded in `RollupCollector`'s lookup and defaulted in `RollupMerge.ToRow` (shared const); `var merged = _pending;` alias; `FailingOnceDbFactory` is `FailingNTimesDbFactory(1)`.
- `itemDropped` logs one warning per evicted event with no rate limit.
- `ConPersistence.csproj` has `Microsoft.EntityFrameworkCore.Design` as `PrivateAssets=all`, so `dotnet ef ... --startup-project Programs/ConWatch` fails; use the project-only form (`DesignTimeFactory`) or fix the reference.

## Full-suite flakiness (not attributed to the branch)

Ten full `ConWatch.Tests` runs (five on the branch head, five on an earlier branch commit) each showed at most one failure, four different tests, none repeating: `WarmRestartTests.Restart_resumes_states_without_new_transitions`, `SetupApiTests.Apply_is_additive_on_rerun`, `PageSmokeTests.Page_renders(/Admin)`, and one more; SQLite pool symptoms in some. The controller's own full run on the branch head was clean. To measure properly: three full runs on `8c8eeb2` versus the merge commit.

## Open after plan B

- The weather-threshold fallback exists in two places: `TopologyNames`/`SettingsDefaults.WeatherThreshold` and the engine's `VerdictOptions`.
- ~~`/export/snapshot` reads config under a snapshot, but its measurement tables are read separately (not part of that same snapshot).~~ Done 2026-09-28: `SnapshotExporter` reads settings, config and measurements inside one read snapshot.
- ~~A root-level (parentless) anchor never appears in a Targets pipeline chain.~~ Done 2026-09-28: shown as "reached directly"; parent names also match ignoring case there.
- `WarmStartSeeder.FromEngine` and the empty-DB path have no direct test, and the max-gap `>` boundary is unpinned.
- `WarmStartSeederTests` re-implements `FileDbFactory` inline instead of sharing one.

## Wire changes on plan-b (for the release notes)

- Webhook/MQTT/MCP transition timestamps are now `+00:00` (were `Z`); MCP `get_transitions` rows use `from`/`to`.
- `/status` 503 body is `{ "errors": [...] }`.
- MCP `get_current_status` returns the dashboard's shape, and targets carry `operator`.
- Factory reset seeds all seven settings.
- The setup wizard applies all-or-nothing.
- Targets in `/status` and SignalR carry `operator`.
