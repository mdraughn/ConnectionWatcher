# Test-safety-net branch: what it found, and what is left for Mark

Companion to `2026-10-18-test-coverage-audit.md`, written when the plan `../plans/2026-10-18-test-safety-net.md` finished on branch `test-safety-net` (50 commits on 930c3f4).

## What the branch delivered

**Tasks 1–27** add characterization tests that pin current behaviour at stable seams. Every new test was shown to go red under a named mutation, recorded as `Bites:` lines in the commits. From Task 4 on, each task reviewer independently re-ran one mutation.

**Seams added**, all behaviour-identical:
- `PingTransceiverIcmp.FromReply`
- `WatcherHostService.RetryDelays`
- `CliApp.StripOutputOption` / `OutputOption`
- `ConInfrastructure.Tray.TrayModel`
- client `historyData.ts` (`weightedRtt`, `lossPercent`)
- `MetricsCounters.Record`

**CI** now runs ConNotify.Tests. The Docker MQTT tests fail instead of skipping when `CI=true`.

**Tasks 28–36** are your rulings D1–D10, each done red then green:
- **D1:** clear-history and factory reset delete glitches; the no-op vacuum is gone.
- **D2:** the header pip reads red "DNS down" during DnsDown.
- **D3:** History `resolution=auto` follows the Settings-table retention (one shared `RetentionWindows.EffectiveAsync`).
- **D4:** rates are double, so 9/10 meets 90 % and 19/20 meets 95 %.
- **D5:** Healthy counts only answering anchors and names unreachable ones ("Healthy: 2 anchors responding (Quad9 unreachable behind ModemB)").
- **D6:** `/api/v1/transitions?opening=true` returns each subject's latest row before `from`, and History uses it.
- **D7:** both nested-anchor rules are pinned.
- **D8:** transition avgRtt averages answered pings only; 0 when none answered.
- **D9:** the verdict's metric series is `name="verdict"`. "verdict" is a reserved target and pool name. The dashboard trail fetches by subject id.
- **D10:** the tray scout session and TraySettings live in tested libraries. A corrupt `tray.json` is moved aside to `tray.json.corrupt`.

**Verification:**
- Three full-solution runs pinned to 2 cores at 53104b0 completed every test.
- One NT2 MQTT flake was then fixed in the final wave, and the fixed class passed 3/3 on 2 cores.
- The engine tests also pass on Linux (dotnet/sdk:10.0 container).
- The tray builds clean.
- Build output: only the pre-existing CS8631 plus NU1903.

## Product defects found (not fixed: pinned with `// AUDIT:`; each needs your ruling)

1. **MQTT wipes Home Assistant target entities on every config rebuild** (the most serious).
   - **Cause:** `WatcherHostService.cs:212-216` attaches observers before `engine.StartAsync`. `MqttNotifier.Attach` posts a Refresh, and `RefreshAllAsync` (`MqttNotifier.cs:231-251`) sees no monitors yet, so it publishes empty retained config, state and attributes for every known target.
   - **What survives:** verdict, internet and dns are not wiped.
   - **Recovery:** the entities come back only when that target next transitions or MQTT reconnects. A warm rebuild emits no transitions, so a stable link stays wiped.
   - **Scope:** every rebuild that adds no fresh target, including delete, disable, rename, parent, profile, critical or scope edits, pool edits and setting edits.
   - **Fix direction:** refresh after StartAsync, or don't clear until the engine has started.
   - **Pin:** NotifierIntegrationTests ~571.
2. **A failed clone leaves monitoring switched off.** `CloneService.CloneAsync` (`CloneService.cs:68-82`) writes `Engine.Enabled=false`, `Clone.SourceUrl` and `Clone.IncludeLocalDevices` before importing, and never restores them when the import fails (400 import-failed). Take-over (`:154-164`) imports first and is fine. Pin: CloneTests (CF19).
3. **A throwing observer Detach leaves the rebuild lock held for good.**
   - **Cause:** the finally block at `WatcherHostService.cs:302-304` calls `DetachObservers()` before `_rebuildLock.Release()`.
   - **What breaks:** every later rebuild blocks, `/admin/engine`, factory reset and clone hang instead of answering 500, and `StopAsync` blocks.
   - **Also:** `DetachObservers` stops at the first throw.
   - **Status:** latent today, because all observers only dispose Rx subscriptions.
   - **Fix:** release the lock in a nested finally, and wrap each observer in its own try/catch.
4. **ISP report and History can still disagree** when no row exists before the window but the first in-range row starts from Down. The report counts 0 outages; History counts 1. This refines D6. Pin: IspReportBuilderTests rows 7 and 10.
5. **ISP report: a null-verdict marker row splits one outage into two episodes**, and the second loses its evidence text. Totals are right. Pin: IspReportBuilderTests (PS15).
6. **History truncation with opening rows.** History asks for `limit=1000` and never pages. Above 1000 in-range rows (a flapping device over 30 days), the opening row seeds the range start across a gap it never saw. This existed before D6 in another form; the fix is to page, or drop opening rows when `nextBefore` is set.

## Questions for you (pinned as current behaviour)

- **Pool seed:** a pool seed alone is not silent. With cold members the pool emits Responding→Starting at once. Should a seeded pool hold until its members determine? (EngineSeedTests)
- **Internet glitch span:** it covers only already-closed member episodes; an open contributor's later losses fall outside it. (SuspicionCoordinatorTests)
- **v0.10 import:** importing a v0.10 export keeps "External" as a custom profile (its targets keep pointing at it). Only the DB migration renames it. Should import rename it too? (ConfigRoundTripTests)
- **Warm-restart gap setting:** `Engine.WarmRestartMaxGapMinutes` can't be set through `PUT /api/v1/settings` ("Unknown setting"), so neither the API nor the UI can change it. (RebuildCarryOverTests)
- **InternetWeather detail:** its "N of M anchors degraded" denominator still counts Unreachable anchors. Should it follow D5? (VerdictCoordinator.cs:550-552)
- **MCP timestamps:** `get_glitches` ends timestamps in `Z`; REST and `get_transitions` use `+00:00`. Both are UTC.
- **Client/server shapes:** Tests/contracts/README.md lists 9 discrepancies: server keys the client types don't declare, `| string` unions that defeat exhaustiveness, and the private `InitialData`.
- **Private-address predicates:** `::ffff:10.0.0.1` (IPv4-mapped) is not private in any of the three. (ProbeSpecTests)
- **Factory reset:** it also deletes `Retention.LastRunUtc`, which nothing recreates until the job next runs. Probably fine.
- **Going public:** `GET /api/v1/auth/status` returns the admin-password reset file path to anonymous callers.

## Things that change what you see

- **Grafana:**
  - The verdict's transitions now count under `conwatch_transitions_total{name="verdict"}`. Existing history stays under `name="Internet"` (merged with the pool's).
  - `conwatch_target_rtt_seconds` and the status RTT now show answered-ping latency, so they read higher during lossy periods (D8).
- **Upgrade:** a target or pool named "verdict" (any case) now fails validation, so that database boots ConfigInvalid until it is renamed.
- **Old transition rows:** pre-upgrade avgRtt values now serialise with float-widening digits (e.g. 12.300000190734863) in REST, MCP and CSV. The UI rounds.
- **Tray:** the plain-HTTP scout dialog now names the server host. The password dialog shows the URL without a trailing slash.
- **README:** add a sentence each for the `name="verdict"` series and the RTT semantics before going public.

## Manual tray checklist additions

- One poll cycle: a toast on Healthy→LinkDown, none on a notes-only change.
- Scout with a wrong password three times stops. Remember saves only after success. The plain-HTTP warning is asked once per server.

## Notes for the refactor session

- **Pinned behaviour:** the rule catalogue (`2026-10-18-test-audit-rule-catalogue.md`) plus these tests are the net. A red test mid-refactor is the net working: decide the behaviour, don't edit the test to match.
- **Harness:** reuse `ConEngine.Tests.Fakes.EngineHarness`, `TestWait`, `TimerCountingTime`, `HostTestWait` and `ContractFiles`.
  - `Transitions` and `Glitches` reads return snapshot copies.
  - The timer-creation-counting helpers would break on a refactor to PeriodicTimer: that shows up as a red test, never a silently passing one.
- **Golden contracts:** they live in `Tests/contracts/` and regenerate only with `UPDATE_CONTRACTS=1`. A settle timeout during regeneration now throws instead of writing a partial file.
- **Behaviour protected by redundant guards:** the following are protected by more than one guard, so removing any single guard keeps tests green:
  - engine stop silence (three guards);
  - the re-point burst;
  - `No_retry_after_stop`.
- **Environment lesson:** a helper agent once left a background loop that force-killed `testhost.exe` after 75 s, which looked like test-host crashes. Never leave watchdogs running.
