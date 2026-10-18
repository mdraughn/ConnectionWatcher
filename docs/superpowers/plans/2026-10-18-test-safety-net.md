# Test Safety Net Before the Refactor: Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close the gaps found by the 2026-10-18 test audit so the coming refactor (DRY, coupling, vibe-code cleanup) cannot change engine, configuration, or contract behaviour without a test going red.

**Architecture:** Almost every task adds **characterization tests** that pin today's behaviour at a stable seam: a component's public API, the engine built with fake transceivers and fake time, HTTP through `ConWatchFactory`, DB contents, and wire payloads. No test reaches into private state. Because these tests pin existing behaviour, they pass when first written, so each one must **prove it bites**: apply the named mutation to the product code, watch the new test fail, revert. Five tasks need a small, named product seam. Phase 6 holds the bug fixes Mark ruled on (D1–D10, 2026-10-18), done with normal red-green TDD.

**Tech Stack:** .NET 10, xUnit 2.9, Microsoft.Extensions.TimeProvider.Testing (FakeTimeProvider), EF Core + SQLite, ASP.NET Core WebApplicationFactory, Vitest 2 + happy-dom, GitHub Actions.

**Spec:** `docs/superpowers/reviews/2026-10-18-test-coverage-audit.md`. Finding ids (EV, ET, PR, CF, PS, HA, NT, CL, TR) and decisions (D1–D10) refer to it. The design rules each test pins are listed with ids in `docs/superpowers/reviews/2026-10-18-test-audit-rule-catalogue.md`. Master is at `b847038`.

## Global Constraints

- **Pin, don't change.** Outside the tasks marked **Seam** and Phase 6, no file under `Core/`, `Libraries/` or `Programs/` changes. If a pinning test reveals behaviour that looks wrong, pin it as it is, add a `// AUDIT:` comment naming the question, and report it. Don't fix it in the same task.
- **Prove it bites.** Every new test, or each Theory as a whole, is run once against the mutation the task names (or an equivalent one-line break of the same rule). It must go red, and the product file is then restored with `git checkout -- <file>`. Record the mutation in the commit body, as `Bites: <file> <one-line change>`. A test that stays green under its mutation is not finished.
- **Timing rule** (CI runners have 2 cores): never "sleep N ms, then assert that something happened". Wait for the condition with a generous limit (10–30 s). Fixed windows are allowed only for absence checks, after a positive condition has been reached. With FakeTimeProvider, wait until the woken loop has armed its timer before `Advance` (use the shared `TimerCountingTime`, Task 1).
- **Public seams only.** No reflection on private members, no `InternalsVisibleTo` additions beyond the projects that already have it (ConInfrastructure → ConEngine.Tests; ConWatch → ConWatch.Tests). Tests assert outcomes, not call sequences.
- **Assert the meaning, not the exact wording,** of user-facing messages. Use `Contains` on the subject name (target, pool or profile) and the key word. Wire-format strings (enum names, topics, JSON keys) are asserted exactly.
- **Golden fixtures never auto-update.** A mismatch fails and prints the new content. Regenerate only when `UPDATE_CONTRACTS=1` is set.
- **Addresses** in fixtures use documentation ranges (192.0.2.0/24, 198.51.100.0/24, 203.0.113.0/24, 2001:db8::/32) or loopback. No personal endpoints.
- **Repo rules:** commit per task, never push, no attribution lines, output pristine apart from the NU1903 and pre-existing CS8631/CA2022/CA1416 warnings in untouched files.
- **Test commands:**
  - `dotnet test Tests/<Project>.Tests --filter "FullyQualifiedName~<Class>"`
  - Whole suite: `dotnet test ConnectionWatcher.sln`
  - Client: `npm --prefix Programs/ConWatch/ClientApp run test` and `npx --prefix Programs/ConWatch/ClientApp tsc --noEmit -p Programs/ConWatch/ClientApp`

## Review Focus

1. **Vacuous pins.** A test whose setup races ahead of the scenario passes without exercising it. That is how `RebuildRetryTests` went bad (CF1). The prove-it-bites step catches this. Reviewers check that each commit body has its `Bites:` line, and re-run one mutation per task at random.
2. **Flakes on a starved runner.** New engine and host tests run real loops. Each wait must be condition-based with a generous limit. Reviewers run the touched classes pinned to 2 cores (`start /affinity 0x3 dotnet test …`) once per phase.
3. **Golden fixtures edited to make a red test green.** Any change under `Tests/contracts/` in a later commit needs a sentence in that commit body saying which contract changed and why.
4. **Docker absent.** MQTT tests skip locally without Docker, but must fail in CI (Task 2). Reviewers confirm one CI run shows them executed, not skipped.
5. **A pin that freezes a bug.** Where an `// AUDIT:` comment marks behaviour pinned pending a ruling (D1–D10), the Phase 6 task for that ruling must flip the assertion. Reviewers grep `AUDIT:` at the end and match each one to a Phase 6 task or a recorded "keep" ruling.

---

## File Structure

**Create (tests)**
- `Tests/ConEngine.Tests/Fakes/TestWait.cs`: the shared `Eventually` plus `TimerCountingTime`.
- `Tests/ConEngine.Tests/Fakes/EngineHarness.cs`: builds a real `WatcherEngine` over the fake factory from a compact topology description.
- `Tests/ConEngine.Tests/EngineSeedTests.cs`, `EngineWiringTests.cs`, `DesignConstantsTests.cs`, `TreeWalkTests.cs`, `PingManagerGuardTests.cs`
- `Tests/ConWatch.Tests/RebuildCarryOverTests.cs`, `ConfigRoundTripTests.cs`, `VocabularyTests.cs`, `ContractShapeTests.cs`, `SurfaceClassificationTests.cs`, `AdminEraseTests.cs`, `HostTestWait.cs`
- `Tests/ConPersistence.Tests/MigrationUpgradeTests.cs`
- `Tests/contracts/*.json`: golden key-set fixtures, shared by .NET and Vitest.
- `Tests/contracts/old-export-v0.10.json`, `Tests/contracts/snapshot-2026-09.json`: sanitized compatibility fixtures.
- `Programs/ConWatch/ClientApp/src/lib/contracts.test.ts`, `src/lib/siteShell.test.ts`, `src/lib/story.test.ts`

**Modify (tests):** the existing test classes named in each task.

**Modify (product, seams only):** `.github/workflows/ci.yml` (Task 2); `Libraries/ConInfrastructure/PingTransceiverIcmp.cs` (Task 13); `Programs/ConWatch/WatcherHostService.cs` (Task 17); `Programs/ConWatchCli/Program.cs`, `CliApp.cs` and `Windows/WindowsElevation.cs` (Task 29); `Programs/ConWatchTray/Program.cs` plus new `Libraries/ConInfrastructure/Tray/TrayModel.cs` (Task 30). Phase 6: as each task says.

---

## Phase 0: Gates and shared helpers

### Task 1: Shared waiting helpers and the engine harness

**Files:**
- Create: `Tests/ConEngine.Tests/Fakes/TestWait.cs`, `Tests/ConEngine.Tests/Fakes/EngineHarness.cs`
- Modify: `Tests/ConEngine.Tests/Fakes/FakePingTransceiverFactory.cs`, `Tests/ConEngine.Tests/PingDisciplineTests.cs` (use the moved `TimerCountingTime`)
- Create: `Tests/ConWatch.Tests/HostTestWait.cs` (moves `ConfigApiTests.Eventually` and `MasterEnableTests.SettledRebuildCountAsync` here; the old call sites delegate)

**Interfaces (Produces):**
- `static class TestWait { Task Eventually(Func<bool> cond, int timeoutMs = 15000, string? what = null); Task Eventually(Func<Task<bool>> cond, int timeoutMs = 30000, string? what = null); }`. On timeout it throws `Xunit.Sdk.XunitException` with `what`.
- `sealed class TimerCountingTime : FakeTimeProvider { int ActiveTimers { get; } Task WaitForTimersAsync(int count, int timeoutMs = 10000); }`: moved verbatim from PingDisciplineTests, then made public.
- `FakePingTransceiverFactory` gains:
  - `void Script(IPAddress ip, params bool[] results)`: the next N results from that IP follow the script, then the `SetUp` state applies.
  - `void DropEveryNth(IPAddress ip, int n)`: every n-th result fails (0 turns it off).
  - `int PingCount(IPAddress ip)`.
  - `void SetRefused(IPAddress ip, bool refused)`: a failed-looking reply with Success=true and Message "Refused".
  - Existing members unchanged.
- `sealed class EngineHarness : IAsyncDisposable`:
  - `static EngineHarness Home(Action<HomeOptions>? tweak = null)` builds this topology:
    - Router 192.0.2.1 (Internal)
    - Modem 192.0.2.2, child of Router, Operator "Comcast"
    - Google 198.51.100.8 and Cloudflare 198.51.100.1, children of Modem, in pool "Internet"
    - Quad9 198.51.100.9, child of Modem
    - Printer 192.0.2.50 (Verdict scope, bystander)
    - NAS 192.0.2.60 (Local)
    - Resolver 192.0.2.53 (Resolver scope, SystemManaged)
  - All profiles are "Fast": interval 20 ms, MinUsableSample 5, HistorySize 10, burst 10 ms / 200 ms. AnchorPool "Internet", WeatherThreshold 2, DnsWatch on.
  - `HomeOptions` exposes those lists for edits before start.
  - Members: `WatcherEngine Engine`, `FakePingTransceiverFactory Pings`, `TimeProvider Time` (System by default; `UseFakeTime()` before start swaps in `TimerCountingTime`), `Guid Id(string name)`, `IPAddress Ip(string name)`, `Task StartAsync(EngineSeed? seed = null)`, `Task WaitStateAsync(string name, HostState state)`, `Task WaitVerdictAsync(VerdictState state)`, `List<TargetTransition> Transitions` (recorded from `TransitionSource`), `List<GlitchEvent> Glitches`.

- [ ] **Step 1:** Write `TestWait`, move `TimerCountingTime`, and make PingDisciplineTests use it. Run `dotnet test Tests/ConEngine.Tests --filter "FullyQualifiedName~PingDisciplineTests"`. Expected: PASS, same count as before.
- [ ] **Step 2:** Add the fake's new members with a test in `Tests/ConEngine.Tests/FakeFactoryTests.cs`:
  - `Script_overrides_then_falls_back`: Script(ip, false, true), then three pings give false, true, true.
  - `DropEveryNth_drops_exactly_every_nth`: n=4, 8 pings give exactly 2 failures, at positions 4 and 8.
- [ ] **Step 3:** Write `EngineHarness` with one smoke fact: `EngineHarnessTests.Home_reaches_healthy_with_every_target_responding` (start, wait for the verdict to be Healthy, then every Verdict-scope monitor is Responding). Run it. Expected: PASS within 15 s.
- [ ] **Step 4:** Create `HostTestWait` with `Eventually` and `SettledRebuildCountAsync(WatcherHostService)` (same body as in MasterEnableTests). Point the old private helpers at it. Run `dotnet test Tests/ConWatch.Tests --filter "FullyQualifiedName~MasterEnableTests|FullyQualifiedName~ConfigApiTests"`. Expected: PASS.
- [ ] **Step 5: Commit** `test: shared waiting helpers, scriptable fake transceivers, engine harness`

### Task 2: CI runs every suite; Docker tests can't silently skip there (NT1, NT7)

**Files:** Modify `.github/workflows/ci.yml` and `Tests/ConWatch.Tests/NotifierIntegrationTests.cs`.

- [ ] **Step 1:** In `ci.yml`'s `server` job, add `- run: dotnet test Tests/ConNotify.Tests -c Debug` after the ConConfig line. Update the header comment so the list of suites is accurate.
- [ ] **Step 2:** In NotifierIntegrationTests, replace the two `Skip.IfNot(DockerAvailable())` / `Skip.If(containerId.Length == 0)` gates with one helper, `RequireMosquitto()`. It calls `Assert.Fail("Docker/Mosquitto unavailable in CI")` when `Environment.GetEnvironmentVariable("CI") == "true"`, and skips otherwise.
- [ ] **Step 3:** Prove it bites (locally, Docker stopped): run with `CI=true` and expect FAIL with that message; run without it and expect skipped.
- [ ] **Step 4: Commit** `ci: run ConNotify.Tests; MQTT integration tests fail instead of skipping in CI`

---

## Phase 1: Engine (Mark's first priority)

### Task 3: Warm-start seeds are applied by the engine (EV1 / ET2, engine half)

**Files:** Create `Tests/ConEngine.Tests/EngineSeedTests.cs`.

Use `EngineHarness.Home()` with an `EngineSeed` built in the test. Ids come from the harness and the address and probe from config.

Tests:
- `A_matching_seed_is_assumed_bursts_and_confirms_silently`
  - Seed Router: Responding, same address, null probe. Right after `StartAsync`, Router's monitor is `IsAssumed`, `CurrentState == Responding` and `Discipline.IsBurstActive`.
  - Wait until `!IsAssumed`. `Transitions` holds no entry for Router.
- `A_seed_whose_address_changed_cold_starts`: seed Router at 192.0.2.99. The monitor is not assumed, and a Starting→Responding transition for Router arrives.
- `A_seed_whose_probe_changed_cold_starts`: move the existing probe-change case here. Keep the old test.
- `Pool_and_verdict_seeds_land_without_a_transition`
  - Seed pool "Internet" Responding and verdict Healthy.
  - Right after start, `PoolCoordinators[0].CurrentState == Responding` and `Engine.Verdict!.Current.State == Healthy`.
  - Wait 2 s past the first Healthy reclassification (absence window). There is no pool transition and no verdict transition.
- `A_seeded_LinkDown_verdict_bursts_the_resolvers_when_it_clears`: seed verdict LinkDown, start with everything up. Wait until the Resolver monitor's `Discipline.IsBurstActive` is true.

- [ ] **Step 1:** Write the five tests. Run `--filter EngineSeedTests`. Expected: PASS.
- [ ] **Step 2:** Prove it bites: in `WatcherEngine.StartAsync`, skip applying target seeds (comment out the `monitor.Seed(...)` call near line 202). Tests 1 and 4 must fail. Restore. Then break the address comparison (`seed.Address == …` → `true`). Test 2 must fail. Restore.
- [ ] **Step 3: Commit** `test(engine): seeds are applied, mismatches cold-start, pool and verdict seeds land`

### Task 4: Rebuild carry-over keeps state across a config edit (EV1 / CF3 / PS14, host half)

**Files:** Create `Tests/ConWatch.Tests/RebuildCarryOverTests.cs`. Modify `Tests/ConWatch.Tests/WarmStartSeederTests.cs`.

Tests:
- `FromEngine_carries_determined_states_address_and_probe` (in WarmStartSeederTests). Build a `WatcherEngine` over `FakePingTransceiverFactory` with the same Home layout (reference `ConEngine.Tests.Fakes`, which ConWatch.Tests already uses), and wait for Healthy. Then `WarmStartSeeder.FromEngine(engine)`:
  - `Verdict == Healthy`.
  - Every Responding target is present with its config `Address` and `Probe`.
  - Add a target whose IP the fake never answers. While it is still Starting it is absent from the seed.
- `FromEngine_never_seeds_an_Unknown_verdict`: take the seed before any anchor is determined. `Verdict` is null.
- `A_rename_rebuild_adds_no_transitions_and_keeps_states`
  - SeedHomeAsync, wait for steady state (reuse `WarmRestartTests.SteadyState`), flush, and count `/api/v1/transitions`.
  - PUT a rename of Cloudflare. Wait for `SettledRebuildCountAsync`.
  - Google is Responding on the first `/status` poll after the rebuild.
  - Over a 2 s absence window the transition count is unchanged and the verdict is still Healthy.
- `A_re_addressed_target_alone_cold_starts`: PUT Google to a new documentation address with `addressChange=correction`. A Starting row appears for Google only. Other targets add none.
- `Carry_over_is_off_when_the_gap_setting_is_zero`: PUT `Engine.WarmRestartMaxGapMinutes = 0`, then rename Cloudflare. Starting rows appear for every target.
- Add the four PS14 cases to WarmStartSeederTests:
  - A Pool row seeds the pool.
  - An Unknown verdict row doesn't seed.
  - A deleted target's latest row is ignored.
  - An Unreachable grandchild is kept when the Down ancestor is two levels up.

- [ ] **Step 1:** Write the tests. Run `--filter "RebuildCarryOverTests|WarmStartSeederTests"`. Expected: PASS.
- [ ] **Step 2:** Prove it bites: in `WatcherHostService` (the rebuild path near lines 264-275), pass `null` instead of the `FromEngine` seed. The rename test must fail. Restore.
- [ ] **Step 3: Commit** `test(host): a config rebuild carries engine state; address changes and gap=0 cold-start`

### Task 5: The engine's wiring of verdict, model check, DNS, scope and disabled targets (EV2, EV10, ET4, CF2 / ET8)

**Files:** Create `Tests/ConEngine.Tests/EngineWiringTests.cs`.

Tests (all on `EngineHarness.Home`):
- `Every_anchor_isp_owned_raises_unprovable_up`: give Google and Cloudflare Operator "Comcast". After start, `Engine.Verdict!.UsingIspFallback` is true and `ModelConditions` contains one `UnprovableUp`.
- `Dns_watch_splits_system_and_upstream_resolvers`: add an upstream resolver 203.0.113.53 (Resolver, not SystemManaged). `Engine.DnsWatch.System` holds only 192.0.2.53 and `Upstream` only 203.0.113.53. With `VerdictOptions.DnsWatch = false`, `DnsWatch.Enabled` is false.
- `Anchor_pool_lookup_is_case_insensitive_and_missing_means_no_verdict`: with AnchorPool "internet", `Verdict` is non-null. With "nope", it is null.
- `Resolver_notes_wait_for_a_reachable_internet`
  - Add a second system resolver.
  - With the anchors down, wait for LinkDown, then take resolver 1 down: no `ResolverPartial` within 2 s (absence window).
  - Bring the anchors up: `ResolverPartial` appears.
- `The_probe_only_veto_is_wired`:
  - `UseFakeTime()`. Modem uses `DropEveryNth(4)` and a profile whose Faltering threshold makes 75 % Faltering.
  - Inject ten closed windows into `Engine.Loss`, dated one hour ahead of the fake clock, with the Modem at 25 % own loss and 0 % beyond.
  - Take Google and Quad9 down.
  - Expect the verdict to be `InternetWeather`, not `LinkDegraded`.
  - Removing the veto must turn this into LinkDegraded naming the Modem.
- `Resolver_samples_reach_the_dns_log_before_the_monitor` (EV10): subscribe to `TransitionSource`. For a Resolver transition, capture `Engine.DnsResults.Recent(id).Count` and the monitor's `PingCount`. Assert they are equal.
- `Anchor_glitches_reach_the_engine_glitch_stream` (ET4): `Script(Google, false, true, false)`, then up. A Target glitch for Google arrives on `Glitches` with LostCount 2.
- `Local_targets_open_no_glitch_and_start_no_sweep` (ET4): `Script(NAS, false, true, false, …)`. Wait until NAS's `PingCount` is past the script. There is no NAS glitch, and no anchor's `Discipline.IsBurstActive` turned true (sampled in a 1 s absence window).
- `Disabled_targets_are_not_probed_pooled_or_watched` (CF2): disable Cloudflare and the Resolver.
  - `Monitors` lacks both. `Pings.Created` never holds their IPs after Google reaches PingCount 5.
  - Pool "Internet" has one member and is Responding.
  - `DnsWatch.System` is empty.
  - `StallAfter` ignores a 60 s profile assigned only to the disabled Cloudflare.

- [ ] **Step 1:** Write the tests. Run `--filter EngineWiringTests`. Expected: PASS. Then run the class pinned to 2 cores (Review Focus 2).
- [ ] **Step 2:** Prove it bites, restoring after each break:
  - Remove `Verdict?.UseProbeOnlyVeto(ModelCheck)` (audit mutant M2): the veto test fails.
  - Pass `false` for unprovableUp: the UnprovableUp test fails.
  - Swap the System/Upstream predicate: the split test fails.
  - Move `DnsResults.Record` after `monitor.RecordPingResult`: EV10's test fails.
  - Remove the scope check before `Suspicion.OnPingSample`: the Local test fails.
  - Drop `Where(t => t.Enabled)` at monitor creation: the disabled test fails.
- [ ] **Step 3: Commit** `test(engine): pin StartAsync wiring (veto, ISP fallback, DNS split, reachability, scope, disabled)`

### Task 6: The pump guard, stop contract, and stop grace (ET1, ET13, ET16)

**Files:** Create `Tests/ConEngine.Tests/PingManagerGuardTests.cs`. Modify `PingControllerTests.cs` and `PingManagerTests.cs`.

Tests:
- `A_throwing_result_subscriber_does_not_end_the_pump`:
  - `PingManager` with one fake controller at 10 ms. Subscribe a counter, then a thrower on `PingResultSource`.
  - Wait for at least 1 throw, then wait for the counter to grow by 5.
  - Theory variants: `InvalidOperationException`, and a foreign `OperationCanceledException`.
- `A_throw_does_not_advance_the_last_result_stamp`: the thrower throws on every message. `LastResultUtc` stays null, or unchanged across 5 throws.
- `A_throwing_transition_observer_does_not_stop_the_engine`: harness. Subscribe to `TransitionSource` with an observer that throws on its first call. After that throw, Router's `PingCount` keeps growing and `Engine.LastResultUtc` advances.
- `Stop_delivers_nothing_afterwards_and_is_idempotent` (ET16): harness at 20 ms. Run `StopAsync` and record counts. In a 500 ms absence window, `PingSampleSource` and `TransitionSource` stay unchanged. A second `StopAsync`, and `StopAsync` on a never-started engine, don't throw.
- `Stop_is_bounded_by_the_grace_when_a_probe_ignores_cancellation` (ET13): the fake awaits a never-completed TCS and ignores the token. `PingManager.StopAsync(grace: 1 s)` returns in under 5 s and doesn't throw.
- ET13 fix: in `PingControllerTests.A_probe_cancelled_mid_flight…` and `PingManagerTests.StopAsync_with_ten_hanging…`, replace the `Task.Delay(100)` and `Task.Delay(50)` with a TCS that the fake completes on entering `PingAsync`. Await it with a 10 s limit.
- ET11 flake fix: in `PingControllerTests.Timeout_provider_is_read_per_ping`, after `Volatile.Write(bursting, true)`, wait for the condition `factory.ObservedTimeouts[IpA].Contains(1000)` (10 s limit) instead of collecting a fixed count. Keep the `seen[0] == 3000` assertion.

- [ ] **Step 1:** Write and adjust. Run `--filter "PingManagerGuardTests|PingControllerTests|PingManagerTests"`. Expected: PASS.
- [ ] **Step 2:** Prove it bites: disable the per-message catch in `PingManager.ProcessMessages` (audit mutant M3: `when (false && …)`). The first and third tests fail. Restore.
- [ ] **Step 3: Commit** `test(engine): the pump's own guard, stop contract, bounded grace; de-flake timeout test`

### Task 7: PingDiscipline gates and the burst timeout policy (ET3, ET7 / PR9)

**Files:** Modify `Tests/ConEngine.Tests/PingDisciplineTests.cs` and `ProbeThreadingTests.cs`.

Tests:
- `A_loss_in_a_failing_state_never_starts_a_burst`: Theory over Down, Faltering, Recovering and Unreachable.
  - `OnStateChanged(state)`, then advance 120 s so the cap and guard expire, using the timer-armed wait.
  - `OnPingResult(false)`: `IsBurstActive` is false, and the next wait completes only after `NormalInterval`.
- `Entering_responding_ends_an_active_burst`: `OnStateChanged(Down)` then `OnStateChanged(Responding)`. `BurstStartedAt` is null and the next wait is a normal-interval wait.
- `A_loss_while_starting_starts_a_burst`.
- ET7: extend `Burst_clamps_tcp_timeout_higher_than_icmp` with:
  - A third target `Probe = "dns"` (Resolver scope): `ObservedTimeouts` contains 2000.
  - Every timeout observed before `TriggerBurst` equals the steady 5000.
  - A profile with `PingTimeoutSeconds = 0.5` keeps 500 inside a burst.

- [ ] **Step 1:** Write the tests. Run `--filter "PingDisciplineTests|ProbeThreadingTests"`. Expected: PASS.
- [ ] **Step 2:** Prove it bites:
  - Audit mutant M1 (remove the state gate in `OnPingResult`): the Theory fails.
  - Remove the clear-on-Responding branch: the second test fails.
  - Change `ProbeKind.Dns => 2000` to `1000`: the DNS cap assertion fails.
  - Restore after each.
- [ ] **Step 3: Commit** `test(engine): no bursts from failing-state losses; recovery ends a burst; DNS burst cap`

### Task 8: State-machine edges and monitor guards (ET5, ET14, ET15; ET6 pinned pending D4)

**Files:** Modify `Tests/ConEngine.Tests/HostAnalysisEngineTests.cs` and `TargetMonitorTests.cs`.

Tests:
- `Hysteresis_and_recovering_exits`: Theory with TrackerProbe, MinUsableSample 5 and default thresholds. Reach the prior state by feeding, then feed a 5-sample window.

  | Prior state | Window | Next state |
  |---|---|---|
  | Faltering | 4/5 | Faltering |
  | Faltering | 5/5 | Responding |
  | Down (unexplained) | 1/5 | Down |
  | Down | 2/5 | Recovering |
  | Recovering | 5/5 | Responding |
  | Recovering | 0/5 | Down |
  | Recovering | 3/5 | Recovering |

- `Threshold_boundaries_at_twenty_samples`: MinUsableSample 20.
  - 15/20 from Responding stays Responding.
  - 2/20 from Faltering is not Down.
  - 19/20 from Starting gives **Faltering**, pinned as current, with `// AUDIT: D4, float rate, 19/20 < 0.95` (Task 31 flips it).
- `Threshold_boundaries_at_ten_samples_with_edited_thresholds`: MinUsableSample 10, RespondingThreshold 0.90, FalteringThreshold 0.70.
  - 9/10 from Starting gives **Faltering**, pinned as current with `// AUDIT: D4` (Task 31 flips it to Responding).
  - 7/10 from Responding gives **Faltering**, pinned as current with `// AUDIT: D4` (Task 31 flips it to stays Responding).
- `Burst_loss_rate_counts_only_since_the_burst_started` (ET14): Responding on 20 successes. One loss starts the burst (FakeTimeProvider). Then feed S,F,F,S,F,F. The Faltering transition's `BurstLossRate` equals losses since the burst start divided by entries since the burst start. A Starting→Responding transition has `BurstLossRate == null`.
- ET15 facts:
  - `A_seed_after_tracking_started_is_ignored`
  - `Releasing_a_seeded_unreachable_before_tracking_bursts`
  - `One_hundred_ninety_nine_refusals_is_not_persistent`

- [ ] **Step 1:** Write the tests. Run the two classes. Expected: PASS.
- [ ] **Step 2:** Prove it bites:
  - Audit mutant M4 (Faltering→Responding at the Faltering threshold): the hysteresis Theory fails.
  - Swap the Recovering→Down arm to `< Recovering`: it fails.
  - Compute the burst rate over the window: ET14 fails.
  - Restore after each.
- [ ] **Step 3: Commit** `test(engine): state-machine hysteresis, boundaries, burst loss rate, monitor guards`

### Task 9: Verdict rules not yet pinned (EV3, EV4, EV5, EV7, EV8, EV11; EV6 pinned pending D5)

**Files:** Modify `Tests/ConEngine.Tests/VerdictDnsDownTests.cs`, `VerdictCoordinatorTests.cs` and `VerdictBlameOrderTests.cs`. Use each file's existing fixtures.

Tests:
- EV3 (VerdictDnsDownTests):
  - `DnsDown_outranks_site_incident_weather_and_degraded`: Theory with one anchor down, then two of three anchors down, then a Faltering path hop. Every resolver is Down in each case. Expect DnsDown.
  - `Resolvers_never_determined_stay_healthy_past_the_cap`: anchors up, resolvers never fed, advance 2× `AnchorWaitCap`. Healthy, `Dns == null`, no exception.
  - `A_faltering_resolver_beside_a_down_one_is_not_DnsDown`.
- EV4:
  - `No_anchor_determined_stays_unknown_past_the_cap`: every anchor unresolved, path answering, advance 3× the cap. Unknown, Detail contains "No anchors determined", no transitions.
  - `A_stuck_isp_owned_anchor_does_not_hold_the_wait`: counted anchors Down, ISP-owned anchor never fed. LinkDown with no advance needed.
- EV5 (VerdictBlameOrderTests): `Blame_takes_the_shallowest_frontier_across_branching_paths`.
  - Topology: Router → ModemA (Comcast) → {Google, Cloudflare}; Router → ModemB (Verizon) → Inner → Quad9.
  - All anchors Down, every hop answering.
  - The Detail names the hop with the smallest path index, and the result is identical with the pool order reversed.
  - Second fact, `A_down_hop_with_nothing_beyond_is_blamed_for_weather`: weather on Google and Cloudflare, ModemB Down with Quad9 Down, one more anchor answering. LinkDegraded naming ModemB, whose path index is lower than every Faltering hop's.
- EV7:
  - `Dns_blame_refreshes_without_a_transition`: DnsDown/YourResolver, upstream goes Down. Still DnsDown, `Current.Dns.Kind == NoResolver`, transition count unchanged.
  - `Link_down_frontier_refreshes_without_a_transition`: the Modem also fails. Detail names the Modem, Operator matches, no new transition.
- EV8:
  - `A_faltering_anchor_is_degraded` (SiteIncident).
  - `A_down_isp_owned_anchor_counts_for_site_incident`.
  - `One_anchor_down_plus_a_faltering_hop_stays_site_incident` (Detail lacks "Modem").
- EV11: add `Assert.Equal(HostState.X, t.To)` to the existing LinkDegraded (Faltering), LinkDown (Down) and "waiting" Unknown (Starting) transitions.
- EV6: `An_unreachable_anchor_behind_a_dead_branch`: pin the current state (Healthy) and Detail count with `// AUDIT: D5` (Task 32 flips it).

- [ ] **Step 1:** Write the tests. Run the three classes. Expected: PASS.
- [ ] **Step 2:** Prove it bites:
  - Move the `CheckDns` call after `Quality`: EV3's Theory fails.
  - Return early when `determined.Count == 0` with the cap applied: EV4 fails.
  - Pick blame from the first failing anchor: EV5 fails.
  - Skip the snapshot update when the state is unchanged: EV7 fails.
  - Define degraded as Down or Recovering only: EV8's first fact fails.
  - Restore after each.
- [ ] **Step 3: Commit** `test(engine): verdict precedence, uncapped Unknown, branching blame, live refresh, quality edges`

### Task 10: Topology, model check and suspicion edges (EV12, EV9, EV14, ET9, ET10; EV15 pinned pending D7)

**Files:** Modify `Tests/ConEngine.Tests/TopologyCoordinatorTests.cs`, `ModelCheckCoordinatorTests.cs` and `SuspicionCoordinatorTests.cs`.

Tests:
- EV12:
  - `A_deeper_cause_re_pins_silently`: Inside Down explains Google; then Router and Switch go Down. `ExplainedBy == Router` and Google gets no new transition.
  - `A_faltering_ancestor_explains_nothing`: Router Faltering, Google Down. Google is Down and `ExplainedBy` is null.
- EV9: using `CloseWindow(insideLost, beyondLost)` with 12-sample windows.
  - `Real_forwarding_loss_blocks_the_note`: 10× (6/12, 1/12) gives no note.
  - `Probe_only_raise_boundary`: 10× (2/12, 0) gives the note; 10× (1/12, 0) gives none.
  - `Forwarding_loss_clears_a_held_note`: with the note held, windows of (6/12, 1/12) clear it.
  - Assert the outcomes, not the percentages.
- EV14:
  - `A_refusing_tcp_hop_is_answered_not_never_answered`: Refused results only, child answering, past `NeverAnsweredAfter`. HopBypassed, and no NeverAnswered.
  - `Model_checks_apply_to_a_lan_switch`: a Local-scope switch is Down with an answering child. HopBypassed.
- ET9:
  - `A_trunk_hop_loss_sweeps_anchors_and_trunk`
  - `An_already_faltering_anchor_does_not_sweep`
  - `A_disabled_hop_is_walked_through`
- ET10:
  - `Episodes_ninety_seconds_apart_do_not_coalesce`
  - `Anchor_glitches_thirty_seconds_apart_are_not_an_internet_glitch`
  - In `Correlated_anchor_glitches…`, assert the span equals the union of the members.
- EV15: `A_nested_anchor_counts_as_trunk_for_sweeps`. Pin today's behaviour for an anchor parented under another anchor: (a) whether a loss on the inner anchor sweeps, and (b) whether the outer anchor counts toward "trunk involved" in the Internet-glitch wording. Observe it, then assert it. Ruling D7 says keep it, so there is no AUDIT marker. The comment states that the verdict's opposite rule (`A_tagged_anchor_nested_under_another_anchor…`) is deliberate.

- [ ] **Step 1:** Write the tests. Run the three classes. Expected: PASS.
- [ ] **Step 2:** Prove it bites:
  - Audit mutant M5 (drop `&& forwarding <= ForwardingRaise`): EV9's first fact fails.
  - Widen the root test to `!Answering`: EV12's second fact fails.
  - Drop the "prior healthy" gate: ET9's second fact fails.
  - Restore after each.
- [ ] **Step 3: Commit** `test(engine): topology re-pin, ProbeOnlyLoss limits, model-check edges, sweep triggers, glitch correlation`

### Task 11: DNS follower (ET12)

**Files:** Modify `Tests/ConEngine.Tests/DnsRefreshTests.cs`.

Tests:
- `Going_down_re_resolves_immediately`:
  - Refresh interval 1 h. Hostname target Responding on OldIp.
  - The fake resolver now answers [NewIp], and OldIp is set down.
  - `ResolvedAddress == NewIp` within 15 s. Only the Down trigger can do this.
- `Re_point_keeps_history_and_bursts`: right after the re-point, the monitor's `PingCount` is greater than 0 and not reset, and `Discipline.IsBurstActive` is true.
- `Failing_resolution_backs_off_thirty_seconds_doubling`: `UseFakeTime` plus timer-armed waits. Resolve #2 at +30 s; #3 not before +90 s.

- [ ] **Step 1:** Write the tests. Run the class. Expected: PASS.
- [ ] **Step 2:** Prove it bites: remove the Down trigger near `WatcherEngine.cs:343`. The first test fails. Restore.
- [ ] **Step 3: Commit** `test(engine): DNS follower reacts to Down, keeps history, backs off`

### Task 12: Design constants and profile defaults (EV13, PR5, CF4 part 2)

**Files:** Create `Tests/ConEngine.Tests/DesignConstantsTests.cs`. Modify `Tests/ConEngine.Tests/PingProfileTests.cs`.

Tests:
- `Design_constants_match_the_specs`: one fact, one `Assert.Equal` per constant, each with a comment citing the spec or plan section.
  - `VerdictCoordinator.AnchorWaitCap` = 30 s
  - `ModelCheckCoordinator` FalteringHold = 2 min, NeverAnsweredAfter = 5 min
  - ProbeOnly: windows 10, ProbeOnlyRaise 0.15, ProbeOnlyClear 0.10, ForwardingRaise 0.02, ForwardingClear 0.05
  - Resolver window and raise counts, NameMisses 3
  - `DnsResultLog.Keep` 20
  - `new VerdictOptions().WeatherThreshold` 2
  - The PingDiscipline guard of 60 s, through its observable effect (the existing guard test already pins it; reference that test in a comment rather than duplicating it)
- `Local_preset_and_record_defaults_are_pinned`: every field of `PingProfile.Local` (30 s / 6 / 0.80 / 0.50, NormalInterval equal to Public's) and of `new PingProfile { Name = "x" }` (3 s timeout, 100/10, 0.95/0.75/0.10/0.25).
- `PingProfileConfig_defaults_equal_PingProfile_defaults`: `new PingProfileConfig().ToProfile("x") == new PingProfile { Name = "x" }`.
- `ToProfile_maps_every_field`: a `PingProfileConfig` with ten distinct non-default values. Assert each `PingProfile` property, so a swapped Down/Recovering or BurstInterval/BurstMaxDuration shows.

- [ ] **Step 1:** Write the tests. Run them. Expected: PASS. If a constant is private, assert it through the behaviour test that already covers it and say so in the comment. Don't widen visibility.
- [ ] **Step 2:** Prove it bites: swap `DownThreshold` and `RecoveringThreshold` in `ToProfile`. `ToProfile_maps_every_field` fails. Restore.
- [ ] **Step 3: Commit** `test(engine): pin ruled design constants and profile defaults`

---

## Phase 2: Probes

### Task 13: Transceiver result classification (PR1, PR2, PR10, PR3 with a seam, PR4, PR6, PR7, PR16)

**Files:**
- Modify `Tests/ConEngine.Tests/DnsQueryTransceiverTests.cs`, `PingTransceiverIcmpTests.cs`, `TcpConnectTransceiverTests.cs` and `DomainNameResolverDnsTests.cs`.
- **Seam:** `Libraries/ConInfrastructure/PingTransceiverIcmp.cs`. Extract `internal static PingResult FromReply(IPAddress ip, IPStatus status, long rttMs)` with the same mapping as today: Success only for `IPStatus.Success`, otherwise a failure carrying the status name.

Tests:
- PR1: `A_seeded_name_answered_with_an_error_rcode_is_a_miss`: Theory over ServFail, Refused and NotImp with `Names(Google)`.
  - `!Success`.
  - Message equals `"ServFail (dns.google)"`, `"RcodeRefused (dns.google)"` or `"Rcode4 (dns.google)"`.
  - Message does not start with "Filtered".
- Plus `An_expected_address_among_others_is_Verified`.
- PR2: `Engine_cancellation_throws_instead_of_reporting_a_miss`:
  - Silent server.
  - `PingAsync(5000)` with a CTS token.
  - Wait until `server.Received == 1`, then cancel.
  - `ThrowsAnyAsync<OperationCanceledException>`.
- PR10: `Nothing_listening_on_53_is_a_prompt_miss_not_Refused`: bind and then dispose a loopback UdpClient, and probe its port with a 2000 ms timeout. `!Success`, Message is not "Refused", and Message ends with "(example.org)".
- PR3:
  - `Loopback_echo_is_a_reply`: Success, Message "Success", RTT ≥ 0.
  - `Only_success_status_is_a_reply`: Theory on `FromReply` over Success, DestinationHostUnreachable, TimedOut and TtlExpired.
- PR4: `The_factory_builds_tcp_and_icmp_transceivers`. `Create(loopback, tcp:<listener port>)` pings "Accepted". `Create(ip, ProbeSpec.Icmp)` is a `PingTransceiverIcmp`.
- PR6: `Localhost_resolves_successfully`. Tighten the unknown-host status to a member of {"Host not found", "No DNS data found", "Temporary DNS failure"}.
- PR7: `Silence_is_loss` gets a Stopwatch around `PingAsync(500)` asserting under 5 s, and Message ∈ {TimedOut, NetworkUnreachable, HostUnreachable}.
- PR16: the refusal test uses a 10 s timeout. The cap stays pinned in ProbeThreadingTests.

- [ ] **Step 1:** Write the PR3 Theory against the not-yet-existing `FromReply`. Run it: FAIL (does not compile).
- [ ] **Step 2:** Extract `FromReply` (the body moves; no behaviour change). Run all transceiver classes. Expected: PASS.
- [ ] **Step 3:** Prove it bites: in `DnsQueryTransceiver.Classify`, treat any non-NOERROR rcode on a seeded name as blocked. PR1 fails. Drop the `when` filter on the OCE catch: PR2 fails. Restore after each.
- [ ] **Step 4: Commit** `test(probes): pin transceiver classification and cancellation; extract ICMP FromReply`

### Task 14: Probe parsers, predicates and small edges (PR11, PR12, PR14, PR15, PR17)

**Files:** Modify `TracerouteRunnerTests.cs`, `ProbeSpecTests.cs`, `DnsMessageTests.cs` and `Tests/ConWatch.Tests/ScoutClientTests.cs`. Create `Tests/ConEngine.Tests/TreeWalkTests.cs`.

Tests:
- PR11: `Each_hop_is_probed_three_times_first_address_best_rtt`. A scripted probe returns (null, 192.0.2.1 @30 ms, 192.0.2.2 @12 ms) for TTL 1. Assert 3 calls per TTL, Address 192.0.2.1, RttMs 12, and `ReachedDestination` when only the 2nd probe reached. Reverse DNS stays real for now (see "Optional seams" below).
- PR12: `Private_address_predicates_by_range_edge`: Theory of (address, IsPrivate, IsPrivateOrCarrier, IsRfc1918) over:
  - 172.15.255.255, 172.16.0.0, 172.31.255.255, 172.32.0.0
  - 192.168.0.1, 192.169.0.1
  - 100.63.255.255, 100.64.0.0, 100.127.255.255, 100.128.0.0
  - 127.0.0.1, 169.254.1.1
  - ::ffff:10.0.0.1, pinned as not private, with `// AUDIT` if that surprises.
- PR14: a server under a sub-path (`/conwatch`, no trailing slash) posts to `/conwatch/api/v1/discoveries`. 200 "<html>" gives ServerError. 400 "{}" gives Rejected. 401 then login 503 gives ServerError, and the message lacks the password.
- PR15: TreeWalk:
  - A self-loop and a 2-cycle terminate.
  - A 40-deep chain stops at 32.
  - ChainToRoot is leaf-first.
  - AncestorsOf excludes members, dedupes a shared trunk and keeps first-seen order.
- PR17:
  - Two more malformed byte arrays (RDLENGTH 0xFFFF; a label length 63 with 3 bytes left) parse as false.
  - A ProbeSpec round-trip Theory over icmp, dns, tcp:1 and tcp:65535.
  - `"tcp:+80"` and `"tcp: 80"` parse to tcp:80. This is my call: pin today's leniency.

- [ ] **Step 1:** Write the tests and run them. Expected: PASS.
- [ ] **Step 2:** Prove it bites: change the RTT aggregation in `TracerouteRunner` from min to last. PR11 fails. Off-by-one the 172.16/12 upper bound: PR12 fails. Restore after each.
- [ ] **Step 3: Commit** `test(probes): traceroute aggregation, address-range edges, TreeWalk guard, scout and parse edges`

**Optional seams, not in this plan's scope unless the refactor touches the file:**
- PR8: an injectable TXT query for CymruAsnLookup.
- PR11b: an injectable reverse-DNS delegate for TracerouteRunner (LanScanner's shape).
- PR13: `UpnpWanAddressProbe.FindWanService` made `internal static`.
- CL2: pure static argument builders for `WindowsServiceManager` (`CreateArgs`, `FirewallAddArgs`, `FailureArgs`).

Each is recorded in the audit. Add the test in the same commit as the refactor that touches the file.

---

## Phase 3: Configuration → engine (Mark's second priority)

### Task 15: Validator rules and system-managed fields (CF6, CF15)

**Files:** Modify `Tests/ConEngine.Tests/WatcherOptionsValidatorTests.cs` and `Tests/ConPersistence.Tests/ConfigServiceTests.cs`.

Tests:
- `Each_rule_rejects`: one `[Theory]` over named mutations of the existing `ValidHome`. Each row asserts `Failed` and that the message contains the subject's name.
  - empty target name
  - empty address
  - duplicate pool name
  - pool named like a target
  - pool member naming a non-target
  - pool parent unknown
  - NormalIntervalSeconds = 0
  - PingTimeoutSeconds = 0
  - FalteringThreshold = -0.1
  - MinUsableSample = 0
- ConfigServiceTests:
  - `UpsertPoolAsync_with_an_unknown_member_is_a_validation_error_and_commits_nothing`
  - `..._with_an_unknown_parent_…`
  - Both assert `Kind == Validation`, rows are unchanged, and there is no ConfigChanged.
- CF15: a Theory extending `A_system_managed_target_can_be_disabled_but_not_re_addressed`.
  - Changing profile or scope gives Validation.
  - Changing name, operator or critical gives Success, with the values stored.

- [ ] **Step 1:** Write the tests and run them. Expected: PASS.
- [ ] **Step 2:** Prove it bites: delete the pool-name-equals-target-name check. Its row fails. Restore.
- [ ] **Step 3: Commit** `test(config): every validator rule has a rejecting case; system-managed field rules`

### Task 16: Config reaches the engine field by field (CF4 part 1, CF5, CF11)

**Files:** Create `Tests/ConWatch.Tests/ConfigRoundTripTests.cs` and `Tests/contracts/config-export.keys.json`. Copy `Tests/contracts/old-export-v0.10.json`, sanitized and with documentation addresses, from an export at v0.10.0. If none is at hand, write it by hand in the v0.10 shape: profile "External", no scope, critical or description keys.

Tests:
- `An_edited_builtin_profile_reaches_the_engine` (ConPersistence level, in ConfigStoreTests). Seed the built-ins, `UpsertProfileAsync` Internal with NormalIntervalSeconds = 2.5, load the options, then `TargetGraph.FromOptions(...).ProfileFor(router).NormalInterval == 2.5 s`.
- `Every_public_target_field_survives_store_export_import_and_graph` (CF5):
  - Build a `TargetDto` with a non-default value for every settable public property: bool→true, string→unique text, enum→a non-default defined value, Guid?→new.
  - Push it through `UpsertTargetAsync` → `ExportAsync` → `ImportAsync` → `LoadWatcherOptionsAsync` → `TargetGraph.FromOptions`.
  - Assert, by reflection over public properties only, that each comes back equal, and that every `TargetConfig` property has a same-named `Target` property with an equal value.
  - An explicit exception list lives in the test with a reason per entry: Id; Parent and Profile (name ↔ id); SystemManaged (read-only on the API).
  - Same pattern for `ProfileDto → PingProfile` and `PoolDto`.
- `Config_export_key_sets_are_pinned` (CF11): GET `/api/v1/config/export` after SeedHome. The property-name sets of one profile, one target, one pool and the document root equal `config-export.keys.json`. The fixture follows the golden-fixture rule.
- `A_v0_10_export_imports`: POST the old export. Targets, pools, parents and member order land; "External" becomes Public; every target has Scope Verdict.
- `A_minimal_target_post_defaults_to_public_and_enabled`.

- [ ] **Step 1:** Write the tests and run them. Expected: PASS.
- [ ] **Step 2:** Prove it bites: drop `Critical` from `ToConfig(this TargetRow)`. CF5 fails. Make `TargetGraph` use `TryAdd` for profiles. The built-in test fails. Restore after each.
- [ ] **Step 3: Commit** `test(config): built-in edits reach the engine; reflection round trip over every public field; export shape`

### Task 17: Rebuild retry, debounce, health and failure answers (CF1, CF9, CF10, CF14), with a seam

**Files:**
- Modify `Tests/ConWatch.Tests/RebuildRetryTests.cs`, `HostLifecycleTests.cs`, `AdminApiTests.cs` and `CloneTests.cs`.
- **Seam:** `Programs/ConWatch/WatcherHostService.cs`. Make the retry schedule an `internal static TimeSpan[] RetryDelays` (default 1, 5, 15, 30, 60 s). Tests set it through a factory hook and restore it in `Dispose`. My call: this is preferred over 6 s of real time.

Tests:
- Fix `Failed_rebuild_is_retried…`:
  - Await `SettledRebuildCountAsync` before arming.
  - Once the engine runs again, `RebuildCount == before + 2`.
  - No config or settings write happened in between.
- `Failures_escalate_through_the_schedule`:
  - `RetryDelays = [200 ms, 800 ms, 2 s]`. Fail twice.
  - The engine is still down 500 ms after the first failure (absence window), then running within 10 s.
  - Three rebuild attempts are recorded.
- `A_later_good_rebuild_cancels_the_pending_retry`: fail once, then make a valid write at once. Exactly one extra rebuild settles.
- `No_retry_after_stop`: fail, then dispose the factory. No rebuild is logged after stop (capturing logger).
- CF9:
  - `ConfigInvalid_clears_after_a_good_write`
  - `Booting_on_an_invalid_db_reports_ConfigInvalid_and_serves_the_api`: write the inverted profile into a DB file, then start a fresh factory on it.
- CF10: `A_failed_archive_rebuild_answers_500`. A test observer's `Detach` throws once armed. POST `/api/v1/admin/engine {enabled:false}` gives 500 with the ErrorShape body. Same for factory reset. Clone import with a failing rebuild gives 500.
- CF14: `Rapid_writes_coalesce_into_one_rebuild`. A factory option sets `RebuildDebounceSeconds = 1`. Settle, then 3 POSTs back to back. The settled count rises by exactly 1, and `/status` lists all 3.

- [ ] **Step 1:** Write the escalation test against `RetryDelays`. Run it: FAIL (does not compile).
- [ ] **Step 2:** Add the `RetryDelays` seam (the existing constant becomes this array). Run `--filter "RebuildRetryTests|HostLifecycleTests|AdminApiTests|CloneTests"`. Expected: PASS.
- [ ] **Step 3:** Prove it bites: delete the `ScheduleRetry(delay)` call. The fixed retry test **now** fails, where before this task it passed. Restore.
- [ ] **Step 4: Commit** `test(host): a real rebuild-retry test, escalation, cancel, ConfigInvalid recovery, 500 on failed rebuild, debounce`

### Task 18: Settings and wizard reaching the engine (CF7, CF8, CF12, CF13, CF16, CF17, CF18, CF19)

**Files:** Modify `Tests/ConWatch.Tests/SettingsApiTests.cs`, `DnsStatusTests.cs`, `SetupApiTests.cs`, `ConfigApiTests.cs`, `SystemResolverServiceTests.cs`, `CloneTests.cs` and `HostLifecycleTests.cs`; `Tests/ConPersistence.Tests/ConfigServiceTests.cs`; `Tests/ConConfig.Tests/ConfigFileTests.cs`.

Tests:
- CF7: `Weather_threshold_from_settings_reaches_the_verdict`. SeedHome steady. PUT `Verdict.WeatherThreshold = 1`, then `Pings.SetUp(google, false)`. The verdict becomes weather. Control: with 2 it stays SiteIncident.
- CF8: `Lookup_names_add_enabled_hostname_targets`:
  - PUT `Dns.LookupNames "[]"`, then add an enabled "example.org" target and a disabled "off.example".
  - `dns.lookupNames == ["example.org"]` and `dns.watched` is true.
  - Delete it: `watched` is false.
- CF12:
  - `Wizard_sets_the_anchor_pool_when_unset`: PUT `Verdict.AnchorPool = null` first.
  - `Wizard_leaves_an_existing_anchor_pool_alone`.
  - `Wizard_suffixes_a_colliding_router_name` ("Router 2").
- CF13:
  - ConfigServiceTests: `Deleting_the_anchor_pool_commits_and_signals`, `An_unused_custom_profile_deletes`.
  - ConfigApiTests: `Deleting_the_anchor_pool_turns_the_verdict_off_and_warns`: DELETE gives 204, status verdict becomes null, and `/config/health` contains anchor-pool-missing.
- CF16: `Import_restores_this_nodes_own_resolvers`: `Resolvers.Set("192.0.2.53")`, reconcile, POST an import. A managed target for 192.0.2.53 exists again within 15 s.
- CF17: `Watcher_section_seeds_an_empty_db_only`: `UseSetting("Watcher:Targets:0:Name", "Router")` and so on, including a Local scope and a custom profile. GET `/api/v1/targets` shows them. Restarting on the same DB with a different section changes nothing.
- CF18 (Windows-only early return, like WindowsFolderLockdownTests): `ConfigPaths.Default()` file names end with `DSoftCo\ConnectionWatcher\conwatch.json`, `…\tray.json`, and `contray-settings.json` beside it.
- CF19: `A_clone_whose_config_is_invalid_changes_nothing`: the fake source's snapshot has a parent cycle. 400 import-failed; local config and `Engine.Enabled` unchanged.

- [ ] **Step 1:** Write the tests and run the touched classes. Expected: PASS.
- [ ] **Step 2:** Prove it bites: build `VerdictOptions.WeatherThreshold` from appsettings instead of the Settings table. CF7 fails. Remove the wizard's AnchorPool write. CF12's first test fails. Restore after each.
- [ ] **Step 3: Commit** `test(config): settings and wizard reach the engine; pool delete; resolver restore; first-boot seed; paths`

---

## Phase 4: Contracts

### Task 19: Freeze the shared vocabulary (PS1 = HA6 = TR3)

**Files:** Create `Tests/ConWatch.Tests/VocabularyTests.cs` and `Tests/contracts/vocabulary.json`. Modify `Tests/ConWatch.Tests/TrayStatusTests.cs` and `Tests/ConNotify.Tests/BuildersTests.cs`.

**Interfaces (Produces):** `Tests/contracts/vocabulary.json`, shaped `{ "HostState": [...], "TransitionTrigger": [...], "TransitionSubjectKind": [...], "VerdictState": [...], "ModelWarningKind": [...], "TargetScope": [...], "DiscoveryKind": [...], "DiscoveryMethod": [...], "DiscoveryStatus": [...], "DnsBlameKind": [...], "DnsSummaryState": [...], "Markers": ["HistoryReset"], "GlitchClass": {"Target":0,"Internet":1} }`. Tasks 20 and 26 read it.

Tests:
- `Persisted_and_wire_names_are_frozen`: for each enum, `Enum.GetNames<T>()` equals the array in `vocabulary.json`. On a mismatch the message says: "persisted/wire name changed — add a data migration and update every consumer, or keep the name". `(int)GlitchClass.Target == 0` and `Internet == 1`. The `"HistoryReset"` literal used by ConfigService equals `Markers[0]`.
- TR3:
  - `Every_verdict_state_has_a_tray_state`: `foreach VerdictState`, assert the expected TrayState from a literal table. SiteIncident and InternetWeather map to Healthy.
  - `Every_warning_kind_has_a_note_label`: the label is not the generic fallback.
  - `Real_status_json_drives_the_tray`: SeedHome with the Router critical; feed the real `/api/v1/status` body to `TrayStatus.FromStatusJson`. Healthy with one Critical entry. After `Router` ping false, that entry IsDown.
- ConNotify: `Every_verdict_state_has_an_internet_payload`: `foreach VerdictState`, assert ON/OFF from a literal table. OFF only for LinkDown and DnsDown.

- [ ] **Step 1:** Write the tests, generating `vocabulary.json` once with `UPDATE_CONTRACTS=1`. Run them. Expected: PASS.
- [ ] **Step 2:** Prove it bites: rename `HostState.Unreachable` to `Explained` (IDE rename). `Persisted_and_wire_names_are_frozen` fails. Restore.
- [ ] **Step 3: Commit** `test(contracts): freeze persisted and wire vocabulary; exhaustive tray and MQTT verdict tables`

### Task 20: Client ↔ server JSON shapes (HA1, HA12 partly)

**Files:** Create `Tests/ConWatch.Tests/ContractShapeTests.cs`, `Tests/contracts/{status,transition,glitch,rollup,index-island}.keys.json`, `Programs/ConWatch/ClientApp/src/lib/contracts.test.ts` and `src/lib/story.test.ts`.

**Interfaces:**
- Consumes `Tests/contracts/vocabulary.json` (Task 19).
- Produces key-set fixtures shaped as `{ "<object path>": ["key", ...] }`. Object paths are, for example, `status`, `status.verdict`, `status.targets[]`, `status.dns`, `status.dns.blame`, `status.dns.resolvers[]`, `status.dns.upstreamResolvers[]`, `status.dns.lookupNames[]`, `status.warnings[]`, `glitch[]`, `rollup[]`, `index.island`.

Tests:
- .NET, `Server_shapes_match_the_contract_fixtures`. Seed a fully populated server: a resolver, a forced DnsDown with blame, a held model warning, a glitch, and a minute rollup. Walk the JSON from:
  - GET `/api/v1/status`
  - `/api/v1/transitions?limit=1`
  - `/api/v1/glitches`
  - `/api/v1/rollups/{id}`
  - the Index page's `initial-data` island (regex out the script body)

  Compare key sets per path. Also assert that `rollup[].periodStart` ends with `+00:00`, and that a transition's `timestamp` carries an offset.
- Vitest `contracts.test.ts`: read each fixture with `readFileSync` (as `dnsLookupNames.test.ts` already does). For each path, declare `const targetKeys = [...] as const satisfies readonly (keyof TargetStatus)[]` and assert set equality with the fixture. A missing or extra key fails, and `tsc` fails if `types.ts` drifts from the list. Read `vocabulary.json` and assert that every `VerdictState` has an `answerClassOf` result, and every `ModelWarningKind` is in the `ModelWarning.kind` union (typed list + `satisfies`).
- Vitest `story.test.ts` (HA12): stub `fetch` with transitions in which (a) one anchor goes down twice and (b) two distinct anchors go down inside a verdict window. Assert that `fetchStory`'s mark counts distinct anchors: (a) gives SiteIncident colouring, (b) gives weather.

- [ ] **Step 1:** Write the .NET test and generate the fixtures with `UPDATE_CONTRACTS=1`. Review every key against `types.ts` by hand. Any key the client declares that the server doesn't send (or the reverse) gets an `// AUDIT` note in the fixture's sibling `README.md`.
- [ ] **Step 2:** Write the Vitest tests. Run `npm --prefix Programs/ConWatch/ClientApp run test` and `tsc`. Expected: PASS.
- [ ] **Step 3:** Prove it bites: rename `StatusSnapshotBuilder`'s `lookupNames` to `names`. The .NET test fails. Regenerate the fixture: the Vitest test fails. Restore both.
- [ ] **Step 4: Commit** `test(contracts): golden server shapes checked from .NET and Vitest; story counts distinct anchors`

### Task 21: Security surface (HA4, HA7, HA11)

**Files:** Create `Tests/ConWatch.Tests/SurfaceClassificationTests.cs`. Modify `HistoryPageTests.cs`, `PageSmokeTests.cs`, `AuthApiTests.cs`, `DiscoveryApiTests.cs` and `GlitchApiTests.cs`.

Tests:
- HA4, `Every_endpoint_and_page_is_classified`:
  - Enumerate all `RouteEndpoint`s from `EndpointDataSource` and all Razor `PageActionDescriptor`s.
  - Classify each by exact (method, raw pattern) in one literal table with three buckets: open-watch, open-inline-verified (the five SkipProbe routes), and locked. An unclassified entry fails, naming it.
  - For pages: with a password set and not logged in, every page not in open-watch redirects to `/?admin=1`.
  - Keep `ApiSurfaceLockTests`' probing of API routes, and drop its `StartsWith` prefix allowlist in favour of the table.
- HA7, `A_hostile_target_name_cannot_break_out_of_a_script_island`: Theory over `/` and `/History`. Target name `x</script><script>alert(1)</script>`. The island body contains no raw `</script>` before its own close, and it parses as JSON with the name intact. Copy the `DnsPageTests` pattern.
- HA11:
  - The login `Set-Cookie` has `conwatch.admin`, `httponly` and `samesite=lax`.
  - A search held past its cap gives 504 `search-timeout`. A scanner error gives 400.
  - `export/glitches?format=json` bytes are pinned like `Transition_json_bytes_are_pinned`.

- [ ] **Step 1:** Write the tests and run them. Expected: PASS. If the classification shows an unlocked mutating route, pin it as is, mark it `// AUDIT`, and report it at once. That is a security finding, not a test gap.
- [ ] **Step 2:** Prove it bites: map a dummy `POST /api/v1/export/zz` in a test-only `TestServices` hook. The classification test fails, naming it. Switch the Index page's serializer to `UnsafeRelaxedJsonEscaping`: HA7 fails. Restore.
- [ ] **Step 3: Commit** `test(security): one classification of every endpoint and page; script-island escaping; cookie and error codes`

### Task 22: Persistence rules (PS2, PS5–PS9, PS11–PS13, PS15)

**Files:** Modify `Tests/ConWatch.Tests/WarmStartSeederTests.cs` and `Tests/ConPersistence.Tests/{GlitchRecorderTests,RecorderCollectorTests,RetentionServiceTests,RetentionTests,SnapshotImporterTests,IspReportBuilderTests}.cs`. Create `Tests/ConPersistence.Tests/MigrationUpgradeTests.cs` and `Tests/contracts/snapshot-2026-09.json` (sanitized and small: 3 targets, 1 pool, 5 transitions, profile "External", no scope, critical, systemManaged or description keys).

Tests:
- PS2:
  - `A_recent_minute_rollup_keeps_a_quiet_target_warm`: the last transition is 3 days old, and a Minute rollup started 2 min ago. The target is in the seed.
  - `Only_minute_rollups_count`: an Hour rollup only gives a null seed.
  - `A_rollup_is_alive_at_its_end`: pins the `+1 min`.
- PS5:
  - `A_coalesced_glitch_in_a_later_flush_extends_the_row`: first event, flush, coalesced event, flush. One row with summed counts and the later EndUtc.
  - `…only_the_same_targets_row`.
  - `A_coalesced_glitch_with_no_prior_row_inserts`.
- PS6, `The_collector_loop_writes_closed_minutes`: FakeTimeProvider and a timer-armed wait. Start, one sample, advance 75 s, and the Minute row exists. A FailingOnce variant writes it on the next tick.
- PS7:
  - `Stopping_drains_queued_and_failed_batches`: a factory fails until flipped. Start, OnNext, wait for 1 failed attempt, flip, StopAsync. The row is present.
  - `Bursts_larger_than_a_batch_all_land`: 1,200 events through a 500 batch, one FlushAsync, 1,200 rows.
- PS8, `The_nightly_pass_runs_at_the_configured_hour`: FakeTimeProvider at 12:00 UTC, RunHourUtc = 3. The first pass runs; advance to 03:00 the next day and `RetentionLastRunUtc` moves. A first pass that throws still lets the second run.
- PS9, `Upgrading_from_InitialCreate_keeps_data_and_defaults`:
  - A file DB migrated to `20260817145251_InitialCreate`.
  - Raw SQL inserts a profile, a target, a transition and a minute rollup.
  - `MigrateAsync`.
  - Through `ConfigStore` export: Scope Verdict, Critical false, SystemManaged false, profile renamed.
  - Through `HistoryQueries` and `WarmStartQueries`: the rows are present and their timestamps are UTC and equal.
- PS11:
  - `A_newer_schema_snapshot_is_refused_and_changes_nothing`.
  - `An_invalid_config_snapshot_is_refused_before_any_delete`.
  - `A_2026_09_snapshot_imports`: from the committed fixture. Scope Verdict, and transition ids preserved.
- PS12, `An_all_failure_minute_does_not_zero_the_hours_min_rtt`.
- PS13, `A_recorded_transition_reads_back_as_its_live_dto`: `TransitionDto.From(row) with { Id = null } == TransitionDto.From(evt) with { Dns = null }`, for a fully populated event with a non-zero offset timestamp.
- PS15:
  - A null-verdict row continues the episode.
  - Uptime uses Hour rows and Minute rows across one window.
  - LinkDegraded total is non-zero.

- [ ] **Step 1:** Write the tests and run them. Expected: PASS.
- [ ] **Step 2:** Prove it bites: drop the rollup half of `WarmStartQueries`' max. PS2 fails. Drop the DB lookup in `GlitchRecorder`. PS5 fails. Restore after each.
- [ ] **Step 3: Commit** `test(persistence): warm-start liveness, cross-flush coalescing, loops, drain, scheduler, upgrade, snapshot compatibility`

### Task 23: Report incident twin (PS4, pinned; alignment is D6)

**Files:** Modify `Tests/ConPersistence.Tests/IspReportBuilderTests.cs`.

- [ ] **Step 1:** Add `Incidents_match_the_history_page_cases`: a `[Theory]` with one row per case in `ClientApp/src/lib/historyData.test.ts` lines 27-118 (9 cases), seeded as target transitions. Assert `OutageCount`, `LongestOutage` and `UnreachableTime` for `report.Targets["Google"]`. The opening-state case is pinned to the report's current answer. Ruling D6 keeps the report's rule, and Task 33 aligns History to it.
- [ ] **Step 2:** Prove it bites: make `Unreachable → Down` not open an outage. Its row fails. Restore.
- [ ] **Step 3: Commit** `test(report): the ISP report's incident logic mirrors History's nine cases`

### Task 24: MQTT and webhook wire (NT2–NT6, NT8)

**Files:** Modify `Tests/ConWatch.Tests/NotifierIntegrationTests.cs` and `Tests/ConPersistence.Tests/SettingsStoreTests.cs`. Add a ConNotify.Tests → ConEngine.Tests project reference for the engine harness, or put NT4 in ConWatch.Tests. My call: ConWatch.Tests, so no new project reference.

Tests:
- NT2, `Retained_state_is_what_a_late_subscriber_sees` (Docker). After the app is up, connect a **late** subscriber to `conwatch/#` and `homeassistant/#`, and collect for 3 s. That collection is the retained set. Assert:
  - availability is `online`
  - `verdict/state` is a VerdictState name (from `vocabulary.json`)
  - `internet/state` is ON
  - each target's state and `attributes` equal `{"critical":false,"scope":"Verdict"}`
  - the verdict, internet and dns discovery topics are present
  - `conwatch/transitions` is **absent**

  On the live subscriber, one `conwatch/transitions` message parses with `subjectKind` and `to`. DELETE a target: a fresh late subscriber no longer receives its discovery topic.
- NT3 (same app), `Local_devices_get_sensors_too`: POST a Local target. Its state and attributes `{"critical":false,"scope":"Local"}` appear. `Pings.SetUp(ip, false)` turns its state OFF.
- NT4 (ConWatch.Tests), `Dns_publication_follows_the_engine`. On the engine harness, `DnsPublication.From(engine)`:
  - one system resolver Responding gives ("online", "ON", attributes listing it)
  - that resolver Down gives "OFF"
  - the watch off gives availability "offline"
- NT5:
  - `Engine_keys_poke_the_notifiers` (SettingsStoreTests): writing `Engine.Enabled` raises `NotifierSettingsChanged`.
  - Docker: `Engine.Enabled = false` turns availability `offline`, and true turns it `online`.
  - Heartbeat (no Docker): the first ping is seen. Disable the engine, change the URL to `/check-c`. In a 3 s absence window there is no hit. Re-enable, and `/check-c` is hit.
- NT6:
  - `Explained_failures_are_skipped_by_default`: webhook on, `Webhook.SkipExplained` unset. Down Router, Google and Cloudflare. A Router Down delivery arrives, then the verdict LinkDown delivery, and no delivery has `to == "Unreachable"`.
  - Positive control with `"false"`: the Unreachable deliveries arrive.
- NT8, `Each_webhook_url_on_its_own_line_is_called`: two hook paths separated by `"\n "` plus a blank line. Both are hit.

- [ ] **Step 1:** Write the tests and run with Docker up. Expected: PASS.
- [ ] **Step 2:** Prove it bites: publish `{base}/transitions` with retain=true. NT2 fails. Apply `IsSideSubject` in MqttNotifier's Attach. NT3 fails. Change `!= "false"` to `== "true"` in WebhookNotifier. NT6 fails. Restore after each.
- [ ] **Step 3: Commit** `test(notify): retained MQTT state, LAN sensors, dns entity, master enable, webhook defaults`

### Task 25: Host leftovers (HA5, HA8, HA9, HA10, HA12)

**Files:** Modify `Tests/ConWatch.Tests/SignalRTests.cs` (or create `StatusBroadcasterTests.cs`), `MetricsEndpointTests.cs`, `PageSmokeTests.cs` and `McpEndpointTests.cs`; `Programs/ConWatch/ClientApp/src/lib/historyData.test.ts`.

Tests:
- HA5 (unit). A recording `IHubContext<StatusHub>` fake and FakeTimeProvider, attached to an engine built as in Task 4. Assert:
  - A transition sends "transition" then "statusChanged" with no clock advance.
  - Two transitions 100 ms apart give one snapshot.
  - After Detach + `PushDisabledAsync`, a late transition from the old engine gives no `statusChanged`.
  - Server-side spacing is ≥ 250 ms on the fake clock.
  - If the broadcaster can't be built with a fake hub context without a product change, stop and report. Don't add a seam in this task.
- HA8, `Render_golden`: `MetricsEndpoint.Render` (internal static) with an engine whose target is named `a"b\c`. Assert the exact `# TYPE` family list and the escaped series line. The `transitions_total{name="Internet"}` collision is pinned with `// AUDIT: D9` (Task 35 flips it).
- HA9, `The_report_renders_its_incident_table`: seed one LinkDown pair and two glitches. GET `/Report?from=..&to=..`. One row is labelled "Line down", there is a `datetime="…+00:00"` attribute, and the glitch count sentence appears. Every `IspReport` episode DateTime has `Kind == Utc`.
- HA10: every tool in `tools/list` has `annotations.readOnlyHint == true`. `get_glitches` row keys and the timestamp suffix are pinned against seeded rows.
- HA12: move `history.tsx`'s three inline reductions (clip at HistoryReset, RTT weighted by successCount, loss % per bar) into `historyData.ts` as `clipAtReset`, `weightedRtt` and `lossPercent`. This is a client-side move with no behaviour change. Unit-test them in `historyData.test.ts`.

- [ ] **Step 1:** Write the tests and run them. Expected: PASS.
- [ ] **Step 2:** Prove it bites: remove the second `ReferenceEquals` check in StatusBroadcaster. The stale-engine assertion fails. Restore.
- [ ] **Step 3: Commit** `test(host): broadcaster timing, metrics golden, report rendering, MCP annotations, history math`

---

## Phase 5: CLI and tray

### Task 26: CLI handshake and contracts (CL1 seam, CL3, CL4)

**Files:**
- **Seam:** `Programs/ConWatchCli/Program.cs`, `CliApp.cs` and `Windows/WindowsElevation.cs`. Move `StripOutputOption` into `CliApp` as `public static (string[] Args, string? OutputFile) StripOutputOption(string[] args)`, and add `public const string OutputOption = "--__output"`, which WindowsElevation uses.
- Modify `Tests/ConWatchCli.Tests/RootCommandTests.cs`, `ServiceCommandTests.cs` and `Fakes.cs`.

Tests:
- `Strip_removes_the_pair_anywhere`.
- `A_dangling_output_flag_is_left_in_place`.
- `A_stripped_elevated_command_parses`: `CliApp.RunAsync` over the stripped `["service","start","--__output","x"]` gives no usage error.
- CL3, `Exit_codes_match_the_documented_numbers`: a Theory pinning each `ExitCodes` constant to the spec §4 table (0, 1, 2, 3, 4, 5, 130). Also add `Assert.Equal(0, code)` to `Status_reports_probe_failures_and_not_installed`.
- CL4, `Folders_are_locked_before_the_service_is_installed`: the fakes share a call log, and the lock entries precede "install".

- [ ] **Step 1:** Write the tests against `CliApp.StripOutputOption`. Run: FAIL (does not compile).
- [ ] **Step 2:** Move the function and the constant. Run `dotnet test Tests/ConWatchCli.Tests`. Expected: PASS.
- [ ] **Step 3:** Prove it bites: renumber `ExitCodes.NotInstalled`. CL3 fails. Restore.
- [ ] **Step 4: Commit** `test(cli): the elevated output handshake, exit-code numbers, lockdown order`

### Task 27: Tray reducer into a testable model (TR1, TR4; TR5 noted)

**Files:**
- **Seam:** create `Libraries/ConInfrastructure/Tray/TrayModel.cs`. Modify `Programs/ConWatchTray/Program.cs`.
- Modify `Tests/ConWatch.Tests/TrayStatusTests.cs`. Create `Tests/ConWatch.Tests/TrayModelTests.cs`.

**Interfaces (Produces):** `sealed class TrayModel { (bool Redraw, TrayToast? Toast) Apply(TrayInfo next, bool notificationsOn); }`. It holds what `Program.cs` keeps today in `_last`, `_notes`, `_criticalShown`, `_firstPoll` and `_critical`. The shell keeps HTTP, the icon and the balloon, and calls `Apply` once per poll. The HTTP-failure → Unreachable mapping stays in the shell.

Tests:
- `The_first_poll_never_toasts`.
- `Healthy_DnsDown_LinkDown_toasts_twice`: this pins the `NamesFailing` term.
- `A_notes_only_change_redraws_without_a_toast`.
- `Notifications_off_suppresses_toasts_but_still_records_criticals`.
- `A_recovery_after_reenabling_is_not_announced_as_a_failure`.
- TR4: a `verdict: null` Theory over each fallback branch, including "a Local target Down never makes the state Down".
- TR5 (deferred fix, not this task): add a comment in `TrayModel` that a malformed body is still caught in the shell.

- [ ] **Step 1:** Write the TrayModel tests. Run: FAIL (does not compile).
- [ ] **Step 2:** Move the reducer (about 40 lines, behaviour unchanged) and build the tray: `dotnet build Programs/ConWatchTray -c Debug`. Expected: build succeeds with no new warnings. Run the tests. Expected: PASS.
- [ ] **Step 3:** Prove it bites: drop the `NamesFailing` term. The DnsDown/LinkDown test fails. Restore.
- [ ] **Step 4:** Add one line to Mark's manual tray checklist (the plan's handoff note, not a file): "one poll cycle: toast on Healthy→LinkDown, none on a notes-only change".
- [ ] **Step 5: Commit** `refactor(tray): move the poll reducer into ConInfrastructure.Tray and pin it`

TR2 (scout loop) and TR6 (TraySettings) are Task 36 (ruling D10: move both).

---

## Phase 6: Ruled fixes (normal TDD; Mark ruled D1–D10 on 2026-10-18)

Each task flips the matching `// AUDIT:` pin, or adds a failing test first, then fixes.

### Task 28: Erase operations clear every measurement table (HA3 = PS10; D1, ruled 2026-10-18)

**Files:** Create `Tests/ConWatch.Tests/AdminEraseTests.cs`. Modify `Programs/ConWatch/Api/AdminEndpoints.cs`.

- [ ] **Step 1:** Write `Every_table_is_classified_and_erased_as_classified`:
  - Classify every `db.Model.GetEntityTypes()` in a literal map: config, measurement, settings or inbox. An unclassified entity fails.
  - Seed one row per table.
  - Clear-history empties every measurement table (Transitions, Rollups, ModelWarnings, **Glitches**) and keeps the config tables.
  - Factory reset leaves only the built-in profiles and seeded settings.
  - Run: FAIL on Glitches.
- [ ] **Step 2:** Add `db.Glitches.ExecuteDeleteAsync()` to both endpoints. Remove the no-op `PRAGMA incremental_vacuum` (review P5). Change the Clear history text in `Pages/Settings.cshtml` to "Deletes all transitions, rollups and glitches. Configuration and settings are kept." (ruling D1). Run: PASS.
- [ ] **Step 3: Commit** `fix(admin): clear-history and factory reset delete glitches; drop the no-op vacuum`

### Task 29: Header pip shows DnsDown (HA2; D2, ruled 2026-10-18)

**Files:** Create `Programs/ConWatch/ClientApp/src/lib/siteShell.test.ts`. Modify `Programs/ConWatch/wwwroot/lib/site-shell.js`.

- [ ] **Step 1:** Write a test that loads `site-shell.js` into happy-dom the way `dnsLookupNames.test.ts` loads `dns-lookup-names.js`:
  - A page with `#cw-pip .dot .label`; stub `fetch` to return `{verdict:{state}}`; eval; await a tick.
  - For every `VerdictState` in `Tests/contracts/vocabulary.json`, the dot's colour matches the meaning of `answerClassOf(state)`: ok → `--cw-ok`, warn → `--cw-warn`, down → `--cw-down`, unknown → `--cw-unreach`. The label is the default for that meaning ("Up", "Struggling", "Down", "Checking"), except **DnsDown → "DNS down"** (ruling D2).
  - Run: FAIL on DnsDown.
- [ ] **Step 2:** Add `DnsDown: ["var(--cw-down)", "DNS down"]` to the map. Run: PASS.
- [ ] **Step 3: Commit** `fix(shell): the header pip reads "DNS down" in red during DnsDown; every verdict is checked`

### Task 30: History auto resolution uses the effective retention (PS3; D3, ruled 2026-10-18)

**Files:** Modify `Libraries/ConPersistence/Queries/HistoryQueries.cs`, `Programs/ConWatch/Api/HistoryEndpoints.cs` and `Libraries/ConPersistence/Retention/RetentionService.cs` (extract `EffectiveAsync` to a shared `RetentionWindows.EffectiveAsync(SettingsStore, PersistenceOptions)`). Test: `Tests/ConWatch.Tests/HistoryApiTests.cs`.

- [ ] **Step 1:** Write `Auto_resolution_follows_the_settings_retention`:
  - `Retention.MinuteRetentionDays = 7` in settings.
  - Seed Hour rows 8–30 days old and Minute rows under 7 days old.
  - `GET /rollups/{id}?from=-30d&resolution=auto` returns Hour points covering the range.
  - Also: `resolution=hour|day` pass through, and `bogus` gives 400.
  - Run: FAIL.
- [ ] **Step 2:** `ResolveResolution(string requested, DateTime fromUtc, DateTime nowUtc, RetentionWindows effective)`. The endpoint passes the effective windows. Run: PASS.
- [ ] **Step 3: Commit** `fix(history): auto resolution follows the retention the Settings page applies`

### Task 31: The ≥ 95 % boundary means what it says (ET6; D4, ruled 2026-10-18)

**Files:** Modify `Core/ConEngine/Tracking/HostAnalysisEngine.cs` and `Tests/ConEngine.Tests/HostAnalysisEngineTests.cs`.

- [ ] **Step 1:** Flip the three `// AUDIT: D4` rows in `Threshold_boundaries_at_twenty_samples` and `Threshold_boundaries_at_ten_samples_with_edited_thresholds`:
  - 19/20 at 95 % gives Responding.
  - 9/10 at 90 % gives Responding.
  - 7/10 at 70 % stays Responding.

  Run: FAIL on all three.
- [ ] **Step 2:** Make `SuccessRate`, `FailureRate` and `AverageRoundtripTime` `double` in `HostAnalysisEngine`. Carry `double` through to `TargetTransition`, `TransitionDto` and the recorder if they are `float` today, so payloads show 0.9 rather than 0.8999999761581421. If a byte-pinned transition test changes (`HistoryApiTests`, `ExportApiTests`), update it deliberately and say so in the commit body: the rate's digits are the only change. The DB column type stays as it is: SQLite REAL is already double. Run the ConEngine, ConPersistence and ConWatch suites. Expected: PASS.
- [ ] **Step 3: Commit** `fix(engine): rates are double, so a threshold is met at exactly its value (9/10 meets 90 %)`

### Task 32: Healthy counts only answering anchors (EV6; D5, ruled 2026-10-18)

**Files:** Modify `Core/ConEngine/Topology/VerdictCoordinator.cs` (`Quality`) and `Tests/ConEngine.Tests/VerdictCoordinatorTests.cs`.

- [ ] **Step 1:** Flip `An_unreachable_anchor_behind_a_dead_branch` (Task 9):
  - State is Healthy.
  - Detail starts `"Healthy: 2 anchors responding"` and contains `"Quad9 unreachable behind ModemB"`.
  - With WeatherThreshold 3, the qualifier counts 2 independent anchors, not 3.

  Run: FAIL.
- [ ] **Step 2:** In `Quality`:
  - `responding` = determined anchors that are Answering.
  - `unreachable` = determined anchors in Unreachable, each named with its `ExplainedBy` target's name.
  - The Healthy detail is `$"Healthy: {responding.Count} anchors responding"`, plus `$" ({name} unreachable behind {cause}, …)"` when there are any.
  - The `qualifier` uses `CountOperators` over the non-Unreachable determined anchors.
  - SiteIncident and weather logic is unchanged.

  Run the Verdict* classes. Expected: PASS.
- [ ] **Step 3: Commit** `fix(verdict): Healthy counts only answering anchors and names unreachable ones`

### Task 33: History learns the opening state from the API (PS4; D6, ruled 2026-10-18)

**Files:** Modify `Libraries/ConPersistence/Queries/HistoryQueries.cs`, `Programs/ConWatch/Api/HistoryEndpoints.cs`, `Programs/ConWatch/ClientApp/src/history.tsx` and `src/lib/historyData.ts`. Tests: `Tests/ConWatch.Tests/HistoryApiTests.cs`, `ClientApp/src/lib/historyData.test.ts`.

**Interfaces (Produces):**
- `GET /api/v1/transitions?...&opening=true` returns the same items as today, plus, for each subject that matches the filters, its latest transition **before** `from`, placed first in the list.
- Those prepended items carry `"opening": true`. All other items omit the key. Add the `opening` key to `transition.keys.json` (Task 20) as optional.
- `incidents(transitions, rangeStartIso, rangeEndIso)` takes its opening state from an item with `opening: true` (its `to`) when present. Otherwise it falls back to `transitions[0].from` (today's rule). Opening items are not themselves counted as events.

- [ ] **Step 1:** Write the tests and run them: FAIL.
  - `Opening_rows_come_first_and_are_marked`: a target Down 3 days ago with nothing since. `?subjectId=…&from=-24h&opening=true` returns exactly one item, `opening: true`, `to: "Down"`. Without the flag it returns none.
  - Vitest: `incidents()` with only an opening Down item gives outageCount 1 and longest = the whole range.
- [ ] **Step 2:** Implement:
  - The server reuses `HistoryQueries.LatestTransitionIdsPerSubject` restricted to `< from`.
  - The client passes `opening=true` from `history.tsx` and strips opening items before drawing the ledger rows.
  - The doc comment's "Limitation:" paragraph is deleted.

  Run both suites. Expected: PASS. Task 23's report rows are unchanged; the two now agree.
- [ ] **Step 3: Commit** `fix(history): the API sends each subject's opening row so History counts outages open all range`

### Task 34: Transition avgRtt averages answered pings only (D8, ruled 2026-10-18)

**Files:** Modify `Core/ConEngine/Tracking/HostAnalysisEngine.cs` and `Tests/ConEngine.Tests/HostAnalysisEngineTests.cs`. Possibly `Tests/ConWatch.Tests/HistoryApiTests.cs` and `ExportApiTests.cs` byte pins.

- [ ] **Step 1:** Write `Average_rtt_counts_only_answered_pings`. A window of 7 successes at 20 ms and 3 failures gives `AverageRoundtripTime == 20`. A window with no successes gives 0. Run: FAIL (14 today).
- [ ] **Step 2:** Sum RTT over successful samples and divide by `NumSuccess` (0 when `NumSuccess == 0`). If a byte-pinned transition fixture changes, update it deliberately with a commit-body note. Old DB rows are not rewritten. Run the ConEngine, ConPersistence and ConWatch suites. Expected: PASS.
- [ ] **Step 3: Commit** `fix(engine): a transition's average RTT counts only pings that answered`

### Task 35: The verdict gets its own metrics name; the dashboard trail fetches by id (HA8; D9, ruled 2026-10-18)

**Files:**
- Modify `Programs/ConWatch/Metrics/MetricsCounters.cs`, `Programs/ConWatch/Api/MetricsEndpoint.cs`, `Programs/ConWatch/ClientApp/src/dashboard.tsx` and `Core/ConEngine/Configuration/WatcherOptionsValidator.cs`.
- Tests: `Tests/ConWatch.Tests/MetricsEndpointTests.cs`, `Tests/ConEngine.Tests/WatcherOptionsValidatorTests.cs`.
- README: the Grafana section's example query for the verdict, if it uses `name="Internet"`.

- [ ] **Step 1:** Write the tests and run them: FAIL.
  - Flip `Render_golden` (Task 25): pool "Internet" transitions count under `conwatch_transitions_total{name="Internet"}`; verdict transitions count under `{name="verdict"}`; the two never add up.
  - `A_target_or_pool_named_verdict_is_rejected` (case-insensitive) in the validator Theory.
- [ ] **Step 2:** Implement:
  - `MetricsCounters` keys verdict transitions (`t.Kind == TransitionSubjectKind.Verdict`) as `"verdict"`.
  - `MetricsEndpoint`'s zero-fill uses `"verdict"` for the verdict.
  - The validator reserves "verdict".
  - `dashboard.tsx` fetches `/api/v1/transitions?subjectId=${VERDICT_SUBJECT_ID}&limit=…`. The constant equals `VerdictCoordinator.SubjectId`; pin it in `contracts.test.ts` against a value the .NET contract test writes into `vocabulary.json` as `"VerdictSubjectId"`.

  Run the suites. Expected: PASS.
- [ ] **Step 3: Commit** `fix(metrics): the verdict's transitions get name="verdict"; the dashboard trail fetches by id`. The commit body says: "Grafana: panels reading the verdict as name="Internet" must switch to name="verdict"."

### Task 36: Tray scout loop and settings into testable libraries (TR2, TR6; D10, ruled 2026-10-18)

**Files:**
- Create `Libraries/ConInfrastructure/Tray/ScoutSession.cs`.
- Move `Programs/ConWatchTray/TraySettings.cs` to `Libraries/ConConfig/TraySettings.cs`.
- Modify `Programs/ConWatchTray/Program.cs`, `SettingsForm.cs` and `ScoutPasswordStore.cs` (behind an interface).
- Tests: `Tests/ConWatch.Tests/ScoutSessionTests.cs`, `Tests/ConConfig.Tests/TraySettingsTests.cs`.

**Interfaces (Produces):**
- `interface IScoutPasswordStore { string? Load(Uri server); void Save(Uri server, string password); void Forget(); }`. The DPAPI implementation stays in the tray.
- `sealed class ScoutSession { ScoutSession(ScoutClient client, IScoutPasswordStore store, Func<Uri, string?> promptPassword, Func<Uri, bool> confirmInsecure); Task<ScoutOutcome> SendAsync(IReadOnlyList<DiscoveryItem> items, CancellationToken ct); void ForgetConsent(); }`. Use `ScoutClient`'s existing item type if it is named differently.
- `TraySettings` (ConConfig) keeps today's public members. Add `static string? ApplyUrl(string typed, string installedDefault, string fallback)`: it returns null (clear the override) when `typed` equals either default, ignoring case and a trailing slash, and the trimmed URL otherwise.

- [ ] **Step 1:** Write the tests and run them: FAIL (does not compile).
  - `ScoutSessionTests`, against a locked `ConWatchFactory` server as `ScoutClientTests` does:
    - `A_wrong_password_three_times_stops_and_saves_nothing`
    - `A_password_is_saved_only_after_the_server_accepts_it`
    - `Insecure_consent_is_asked_once_per_server_and_forgotten_on_Forget`
    - `A_saved_password_is_offered_only_for_the_same_server` (trailing slash and case normalised)
  - `TraySettingsTests` over `ConfigPaths.UnderTemp`:
    - `Falls_back_to_the_legacy_file_and_migrates_on_first_save`
    - `ApplyUrl_clears_instead_of_pinning_a_default`
    - `A_corrupt_tray_json_is_moved_aside_to_corrupt` (new behaviour, ruling D10)
- [ ] **Step 2:** Move the code. Make the behaviour change: on a parse failure, `TraySettings` renames the file to `tray.json.corrupt` (the same suffix and overwrite rule as `ConfigCommands.cs:75-79`) before writing. The tray shell keeps the prompts and the dialog. Build the tray (`dotnet build Programs/ConWatchTray -c Debug`) and run both test classes. Expected: PASS.
- [ ] **Step 3:** Add to Mark's manual tray checklist: "scout with a wrong password ×3 stops; Remember saves only after success; plain-HTTP warning asked once per server".
- [ ] **Step 4: Commit** `refactor(tray): scout session and tray settings move into tested libraries; corrupt tray.json is kept aside`

---

## Handoff notes

- Phases 1 and 3 are the safety net Mark asked for. Phase 4 protects the contracts the refactor is most likely to rename. Phases 0–5 change no product behaviour; Task 13 extracts ICMP's `FromReply`, Task 17 adds `RetryDelays`, Task 26 moves `StripOutputOption`, Task 27 moves the tray reducer, and the HA12 step in Task 25 moves three client reductions into `historyData.ts`.
- After Phase 5, re-run the whole suite three times pinned to 2 cores (`start /affinity 0x3`). Any new flake is fixed before the refactor starts.
- The refactor session should keep the rule catalogue open. When a Pinned rule's test goes red mid-refactor, that is the net working. Decide on the behaviour; don't edit the test to match.
