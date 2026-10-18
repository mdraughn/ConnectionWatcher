# Test-coverage audit before the refactor (2026-10-18)

Read-only audit. No product or test code was changed. Commit audited: `b847038` (master; the tree was clean apart from an untracked `.vscode/`).

**Purpose (Mark, 2026-10-18).** A moderate refactoring session is coming: removing vibe-coding leftovers, enforcing DRY, and cutting pathological coupling. Before it starts, the tests have to be a safety net that survives it. When structure moves and behaviour stays, tests stay green. When behaviour changes silently, something goes red. This is not a coverage-percentage exercise. Trivial glue, and anything that would need ugly product refactoring to test, is out of scope.

**Priorities (Mark).** The engine comes first: the pinging and probing, and the analysis of the returns, *"a lot of design knowledge encoded in the logic … we need to avoid losing it."* Second is the configuration code that builds and controls the engine. Everything else got a lighter, contract-focused pass.

Companion files:
- `2026-10-18-test-audit-rule-catalogue.md`: about 240 engine and config design rules, one per line, each with the test that pins it or "Unpinned".
- `../plans/2026-10-18-test-safety-net.md`: the plan that closes the gaps below.

---

## 1. Bottom line

**The net is better than average, and its holes are in predictable places.** Line coverage is high:

| Assembly | Lines | Branches |
|---|---|---|
| ConEngine | 95 % | 93 % |
| ConPersistence | 97 % | 94 % |
| ConWatch (host) | 94 % | 85 % |
| ConCore | 91 % | 91 % |
| ConConfig | 92 % | 86 % |
| ConNotify | 88 % | 85 % |
| ConInfrastructure | 76 % | 74 % |
| conwatch (CLI) | 66 % | 55 % |
| ConWatchTray | no test project (Windows-only, manual checklist) | |

So "is this line run?" is the wrong question for this codebase. Almost every line runs. The holes are tests that run a line without asserting its outcome, and rules that live where no unit test looks. The same five patterns produce nearly every finding:

1. **Wiring at the engine seam.** Coordinators are unit-tested by building them by hand, so the code in `WatcherEngine.StartAsync` that connects them (cheap veto, ISP fallback → UnprovableUp, "is the internet reachable" for resolver notes, System/Upstream resolver split, disabled-target filtering, scope gating) is pinned by almost nothing. `StartAsync` is the refactor's most likely victim.
2. **Rebuild carry-over.** Every config edit rebuilds the engine. `WarmStartSeeder.FromEngine`, which keeps the verdict and target states across that rebuild, is never called by any test. Seed *application* inside the engine is pinned only negatively.
3. **Contracts pinned from one side only.** These are the persisted state/trigger/verdict names, the JSON the dashboard reads, the tray's verdict and warning names, and the MQTT topics. Each side's tests use their own hand-written strings and fixtures, so an IDE rename passes both suites.
4. **Thresholds and boundaries.** Rules are pinned at a comfortable middle value, but not at the edge or for the opposite case. Examples are hysteresis exits, ProbeOnlyLoss forwarding limits, and the burst gate in failing states.
5. **One missing CI gate.** `Tests/ConNotify.Tests` (65 tests: every MQTT topic, Home Assistant discovery document, webhook filter rule and LogSafe pin) **has never run in CI**.

**Counts** after merging duplicates across areas: **105 findings: 13 High, 51 Medium, 41 Low.** Line coverage is already high, so most findings are about assertions, not unexecuted code. **Five look like real product defects**, and a further five need Mark's ruling before a test can pin them (§4).

**The mutation spot-check backs this up** (§2.3). Removing a rule the audit called Unpinned left the engine suite green every time. Removing a rule it called Pinned turned the suite red.

### The ten things to do before the refactor starts

In order. Each is a task in the plan.

1. **Put ConNotify.Tests in CI** (NT1). One line in `ci.yml`. Also make the Docker MQTT tests fail, not skip, when `CI=true` (NT7).
2. **Pin rebuild carry-over and seed application** (EV1 = ET2 = CF3).
3. **Pin the engine's wiring** (EV2, CF2/ET8, ET4, EV10). Build a real engine with an anchor pool and assert: veto, UnprovableUp, reachability for resolver notes, resolver split, disabled targets skipped, Local/Resolver scope gating, glitch forwarding.
4. **Pin the pump's own guard** (ET1). The existing resilience tests hit a different catch block.
5. **Pin the discipline gates** (ET3). No bursts from losses in failing states; recovery ends a burst.
6. **Fix the vacuous rebuild-retry test** (CF1). The retry body never ran during the coverage run, yet the test passed.
7. **Freeze the shared vocabulary** (PS1 = HA6 = TR3). Enum member names are persisted in the DB, sent on the wire, read by the tray and the client, and used as Prometheus labels.
8. **Pin the client↔server JSON** (HA1) with golden key-set fixtures checked from both .NET and Vitest.
9. **Pin the DNS transceiver's SERVFAIL/REFUSED-on-a-seeded-name rule** (PR1). Losing it silently disables DnsDown.
10. **Pin the config→engine mapping as a whole** (CF4, CF5). An edited built-in profile must reach the engine. A reflection round-trip over the public properties must prove no mapper drops a field.

---

## 2. How the audit was done

### 2.1 Baseline

Each suite was run once, with the coverlet collector, on Mark's PC with Docker up, so the MQTT integration tests ran.

| Suite | Passed | Failed | Skipped |
|---|---|---|---|
| ConEngine.Tests | 494 | 0 | 0 |
| ConPersistence.Tests | 261 | 0 | 0 |
| ConWatch.Tests | 425 | 0 | 0 |
| ConConfig.Tests | 36 | 0 | 0 |
| ConWatchCli.Tests | 64 | 0 | 0 |
| ConNotify.Tests | 65 | 0 | 0 |
| ClientApp (Vitest) | 101 | 0 | 0 |
| **Total** | **1,446** | 0 | 0 |

The per-project coverage reports were merged as a union of hits, giving per-file line and branch coverage plus a list of uncovered line ranges. That map was used to *find* candidate holes, never as proof that a rule is asserted.

### 2.2 Area audits

Seven parallel read-only audits, each with the same brief: find untested rules, untested contracts, untested edges, thin indirect cover, brittle tests, timing-unsafe tests, and duplicated logic covered unevenly. Each needed evidence for every claim (what was grepped and read), plus a proposal for each gap: the stable seam to test at, and whether any product change is needed.

| Area | Prefix | Findings (H/M/L) |
|---|---|---|
| Engine: verdict, topology, model check, DNS verdict | EV | 15 (2/8/5) |
| Engine: tracking, detection, scheduling, pumps, warm start | ET | 16 (3/8/5) |
| Probes, network infrastructure, ConCore types | PR | 17 (1/8/8) |
| Configuration → engine | CF | 19 (4/9/6) |
| Persistence | PS | 15 (2/9/4) |
| Web host: API, MCP, metrics, SignalR, pages, React client | HA | 12 (2/5/5) |
| Notifications, CLI, tray | NT / CL / TR | 18 (1/9/8) |

Merged duplicates: EV1 = ET2 = CF3; CF2 = ET8; ET7 = PR9; HA3 = PS10; PS1 = HA6 = TR3. The headline findings were re-opened at the cited lines before being kept (§2.3).

### 2.3 Verification: spot-checks and a mutation check

Re-read at the cited lines, all confirmed:
- `AdminEndpoints.cs` clear-history and factory-reset never delete `Glitches`.
- `site-shell.js`'s pip map has no `DnsDown`.
- `HostAnalysisEngine.SuccessRate` is a `float` compared against `double` thresholds.
- `HistoryQueries.ResolveResolution` reads only `PersistenceOptions`.
- `FromEngine` and `UseProbeOnlyVeto` (engine side) have no test callers.
- `PingManager.cs:149-160` is 0 % covered.
- `ci.yml` has no ConNotify step.

**Mutation spot-check.** A full mutation-testing run (Stryker.NET 4.8 and 5.0) failed during setup on this machine (`ArgumentNullException 'path3'` in its VSTest locator). Instead, single hand mutations were applied to a scratch copy of `Core/` + `Libraries/` + `Tests/ConEngine.Tests` (the repo was not touched), and the full ConEngine suite (494 tests) was run against each:

| Mutant | Audit said | ConEngine suite |
|---|---|---|
| none (baseline) | — | **Passed** 494/494 |
| M1 `PingDiscipline`: losses burst in any state (gate removed) | ET3 Unpinned | **Passed**, confirmed unpinned |
| M2 `WatcherEngine`: `Verdict?.UseProbeOnlyVeto(ModelCheck)` removed | EV2 Unpinned | **Passed**, confirmed unpinned |
| M3 `PingManager`: per-message catch disabled | ET1 Unpinned | **Passed**, confirmed |
| M4 `HostAnalysisEngine`: Faltering→Responding at the Faltering threshold (hysteresis removed) | ET5 Unpinned | **Passed**, confirmed |
| M5 `ModelCheckCoordinator`: ProbeOnlyLoss raise ignores forwarding loss | EV9 Unpinned | **Passed**, confirmed |
| C1 control: suspicion guard 60 s → 30 s | ET R31 Pinned | **Failed** (2: `Suspicion_burst_blocked_within_60s…`, `ForceBurst_ignores_the_suspicion_guard`), confirmed pinned |
| C2 control: Responding→Faltering at the Down threshold | ET R4 Pinned | **Failed** (17 tests), confirmed pinned |

Only ConEngine.Tests was run against each mutant. The plan asks every new test to prove itself the same way: break the rule, watch the test go red, revert.

---

## 3. Findings by area

Severity means **the chance the refactor touches it × the cost of a silent break**. "Kind" uses the brief's vocabulary. Each row names the test to add; the plan gives the details. "Seam" marks the few that need a small product change to be testable.

### 3.1 Engine: verdict, topology, model check (EV)

Coverage here is 92–100 % of lines, so every hole is a missing assertion. Strongest areas: the reachability rules (one-anchor rule, ISP-owned anchors, the 30 s wait cap), DnsDown versus Healthy and LinkDown, the C1 stale-resolver logic, and the TopologyCoordinator's own-evidence Unreachable.

| Id | Sev | Gap | Test to add |
|---|---|---|---|
| **EV1** (= ET2, CF3) | High | Rebuild carry-over (`WarmStartSeeder.FromEngine`) is never called by a test. Engine seed application is pinned only negatively (`Probe_change_discards_the_seed` also passes if seeds are ignored). A refactor of `StartAsync` or of the host's warm-start policy would make every config edit cold-start: Unknown→Healthy on the verdict, Starting→Responding on every target, with notifications. | Engine: a matching seed is applied (assumed, burst, silent confirm). A changed address cold-starts. Pool and verdict seeds land. `FromEngine` skips Starting and Unknown and carries address and probe. Host: a rename rebuild adds no transitions; `WarmRestartMaxGapMinutes=0` turns the carry off. |
| **EV2** | High | Engine wiring is untested: `UseProbeOnlyVeto`, ISP fallback → UnprovableUp, the `internetReachable` lambda (LinkDown or Unknown count as unreachable), the DnsWatch System/Upstream split and its Enabled flag, the anchor-pool lookup (case-insensitive; missing → no verdict). Only one ConEngine test builds an engine with an anchor pool. **M2 confirms it.** | Engine-level tests for each; the veto test injects closed loss windows into `engine.Loss`. |
| EV3 | Med | DnsDown precedence is pinned only against Healthy, not SiteIncident, InternetWeather or LinkDegraded. Untested: the "no resolver determined" guard (removing it throws on an empty list at cold start); a Faltering resolver is not Down. | Three VerdictDnsDownTests cases. |
| EV4 | Med | "No anchors determined yet" is deliberately uncapped and pinned only as the initial value. Merging the two Unknown branches would turn unresolvable anchors into LinkDown after 30 s. A stuck ISP-owned anchor must not hold the wait. | Advance 3× the cap and stay Unknown. ISP-owned stuck anchor: LinkDown with no wait. |
| EV5 | Med | Blame with **branching** anchor paths (shallowest index across paths; LinkDegraded "Down with nothing beyond") is untested. Every fixture has one chain. | A two-WAN topology, run in both pool orders. |
| EV6 | Med | **Possible bug (ruling needed):** an Unreachable anchor counts as neither degraded nor answering, so "Healthy: 3 anchors responding" can include a dead one. | Pin after the ruling (D5). |
| EV7 | Med | Snapshot refresh without a transition (Detail, Operator, Dns blame) is unasserted. "Only update on a state change" would leave MQTT, status and MCP stale. | DnsDown YourResolver→NoResolver with the same state. LinkDown frontier moves with no transition. |
| EV8 | Med | Quality edges: a Faltering anchor is degraded; a Down ISP-owned anchor counts for SiteIncident; one anchor down plus a Faltering hop stays SiteIncident. | Three short facts. |
| EV9 | Med | ProbeOnlyLoss forwarding limits (≤ 2 % raise, > 5 % clear) are unpinned, and the note now drives the verdict veto. **M5.** | Boundary windows through `CloseWindow`. |
| EV10 | Low | Resolver results reach `DnsResultLog` *before* the monitor sees them. The DnsDown freshness check depends on this order and only the test helper mirrors it. | Engine handler asserts that the log count equals the ping count at transition time. |
| EV11 | Low | Verdict→HostState map partly pinned (LinkDegraded, LinkDown, Unknown arms). Stored rows depend on it. | Add `t.To` asserts to existing tests. |
| EV12 | Low | Topology: a silent re-pin to a new root, and "only a Down ancestor explains", are unpinned. | Two TopologyCoordinatorTests. |
| EV13 | Low | Ruled design constants (30 s cap, 2 min hold, 5 min NeverAnswered, 10 windows at 15/10/2/5 %, WeatherThreshold 2, …) are referenced by tests, never pinned. | One `Design_constants_match_the_specs` fact. |
| EV14 | Low | Model-check edges: a TCP hop that only refuses counts as answered; ProbeOnlyLoss is skipped while the hop is Down; checks apply to LAN switches. | Two facts. |
| EV15 | Med | The ancestor and trunk walk exists four times with different nested-anchor rules. Only the verdict's copy is pinned. A DRY merge picks one silently. | Pin suspicion's nested-anchor behaviour after ruling D7. |

### 3.2 Engine: tracking, detection, scheduling (ET)

Strongest areas: explanation and release on HostAnalysisEngine, seeding inside the tracker, PingDiscipline cadence and guard, the controller's cancellation and fault isolation, glitch episodes.

| Id | Sev | Gap | Test to add |
|---|---|---|---|
| **ET1** | High | The pump's own per-message guard (review E1) is 0 % covered. `PingPumpResilienceTests` throw from a `PingSampleSource` subscriber, which the engine's newer catch absorbs first. A throwing transition observer depends on PingManager's guard alone. **M3.** | A PingManager unit test with a throwing subscriber; an engine test with a throwing `TransitionSource` observer; plus "the stamp is not advanced for a throw". |
| ET2 | — | Merged into EV1. | |
| **ET3** | High | A lost ping in Down, Faltering, Recovering or Unreachable must not start a burst. Without that rule a long outage pings public anchors at 1/s indefinitely, which the 2026-08-14 spec forbids. Entering Responding must end a burst. A loss while Starting bursts. **M1 confirms it.** | Three PingDisciplineTests with FakeTimeProvider. |
| ET4 | Med | Engine glitch forwarding and the Local/Resolver scope gate on suspicion are unpinned at engine level. The scope tests assert only loss windows. | Scripted fake: an anchor glitch reaches `engine.GlitchSource`; a Local target's losses open no episode and start no sweep. |
| ET5 | Med | State-machine hysteresis and Recovering exits: Faltering returns only at 95 %, Recovering→Down, Down→Recovering on unexplained hosts. **M4.** | One table-driven Theory. |
| ET6 | Med | **Latent defect (fix ruled, D4):** the rate is a `float` compared against `double` thresholds. `(float)9/10 = 0.89999…` misses a 90 % threshold, and `(float)19/20` misses 95 %. Affected: 70 % and 90 % on a 10-sample window; 35, 45, 65 and 95 % on windows that are multiples of 20. Built-in profiles are safe; edited ones aren't. | A boundary Theory, then rates become double (plan Task 31). |
| ET7 (= PR9) | Med | Burst timeout policy: the DNS cap of 2000 ms, the steady timeout outside a burst, and min(steady, cap) are unpinned. Only the TCP and ICMP caps are. | Extend `ProbeThreadingTests.Burst_clamps…`. |
| ET8 | — | Merged into CF2. | |
| ET9 | Med | Sweep triggers: a trunk-hop loss sweeps; an already-sick anchor doesn't; disabled hops are walked through. | Three SuspicionCoordinatorTests. |
| ET10 | Low | Glitch coalescing and Internet-glitch correlation have no negative or span assertions (the 60 s gap, the 10 s slack, span = union). | Three facts. |
| ET11 | Med | **Flake risk:** `PingControllerTests.Timeout_provider_is_read_per_ping` can miss the burst switch on a starved runner. | Wait for the condition, not a fixed collect count. |
| ET12 | Med | DNS follower: "re-resolve now when the target goes Down" never executes in a test (refresh is always 50–100 ms). Re-point keeps history and bursts. Backoff 30 s → 5 min. | A refresh interval of 1 h, so only the Down trigger can re-point. |
| ET13 | Low | Two sleep-to-arm tests can pass without exercising their scenario. The stop-grace bound never waits, because the fake honours cancellation. | A TCS signalled on entering the probe; a fake that ignores cancellation. |
| ET14 | Low | `BurstLossRate` "since burst start" is indistinguishable from the window rate in its only test. | A mixed burst. |
| ET15 | Low | Small monitor guards: a late Seed after tracking is a no-op; a pre-tracker release bursts; 199 refusals is not persistent refusal. | Three facts. |
| ET16 | Low | The engine `StopAsync` contract: nothing is delivered after it returns; double or never-started stop is safe. | One fact with an absence window. |

### 3.3 Probes, infrastructure, core types (PR)

Strongest areas: DNS message parsing and the Filtered/WrongAnswer/Verified classification, TCP accept/refuse, ProbeSpec parsing and the port policy, LAN scan, neighbor tables, ScoutClient safety, DoH, hop classification.

| Id | Sev | Gap | Test to add |
|---|---|---|---|
| **PR1** | High | A **seeded** lookup name answered SERVFAIL, REFUSED or NotImp is never tested as a miss; every rcode test uses an admin name. Widening "blocked" in a tidy-up turns a dead upstream into a Filtered *hit*, and DnsDown never fires. | A Theory over the three rcodes with a seeded name, plus "an expected address mixed with others → Verified". |
| PR2 | Med | `DnsQueryTransceiver` has no engine-cancellation test (rule E2). The other three transceivers do. | Cancel while waiting on silence → throws. |
| PR3 | Med | ICMP reply classification is pinned by nothing: only `IPStatus.Success` counts; `PingException` is a failed sample, not a throw. | A loopback ping (no seam). For non-success statuses, extract a static `FromReply` (**seam**). |
| PR4 | Med | The factory's TCP mapping is unasserted. The class is misnamed `…Icmp` and likely to be renamed. | `Create(tcp:port)` against a loopback listener → "Accepted". |
| PR5 | Med | PingProfile defaults and the Local preset are unpinned, and are duplicated in `PingProfileConfig`. | Full-field asserts; `new PingProfileConfig().ToProfile("x") == new PingProfile{Name="x"}`. |
| PR6 | Med | The real resolver's success path never runs; its failure statuses are loosely pinned. | `ResolveAsync("localhost")`. |
| PR7 | Med | The TCP silence test doesn't assert the caller's timeout bounds the probe (otherwise about 21 s on Windows). | Stopwatch bound with generous slack. |
| PR8 | Med | ASN lookup: the no-Google-fallback privacy rule, cache TTLs and the name fallback are untestable without a **seam** (injectable TXT query). | Optional; decide at execution. |
| PR9 | — | Merged into ET7. | |
| PR10–PR17 | Low | DNS port-unreachable path; traceroute per-hop aggregation (and the tests do real reverse DNS); three private-address predicates with untested range edges (a merge hazard); UPnP XML parsing (**seam**); ScoutClient sub-path and odd responses; TreeWalk cycle/depth guard; a real-time limit in the TCP refusal test; small parse edges and ProbeSpec round-trip. | See the plan; each is a few facts. |

### 3.4 Configuration → engine (CF)

Strongest areas: target CRUD and its validation, address-edit semantics, normalisation on every write path, import validation, ConfigHealth, system-resolver reconcile, the setup wizard's shape, conwatch.json precedence.

| Id | Sev | Gap | Test to add |
|---|---|---|---|
| **CF1** | High | **The only rebuild-retry test can pass without the retry running.** Coverage shows the retry body (`WatcherHostService.cs:317, 319-323`) never executed while `RebuildRetryTests` passed: a queued SeedHome rebuild brings the engine back first. The 1→5→15→30→60 s escalation and "a later good rebuild or shutdown cancels the retry" are untested. | Settle before arming (move `SettledRebuildCountAsync` to a shared helper); assert rebuild count +2. Add fail-twice and cancel-on-success cases. |
| **CF2** (= ET8) | High | Disabled targets: no probing, skipped by pools, the DNS watch, the stall window and lookup names. **Nothing tests it**, and it is the main "right targets in the engine" control. | Engine level and host level. |
| CF3 | — | Merged into EV1. | |
| **CF4** | High | An edited built-in profile (e.g. Internal at 2.5 s) reaching the engine is unpinned, and so is `PingProfileConfig.ToProfile` field by field. A swapped Down/Recovering threshold would pass every mapping test. | ConfigStore → TargetGraph test; a distinct-values `ToProfile` test. |
| CF5 | Med | No completeness net. A new config field that one of the five near-identical mappers forgets is never noticed, and the DRY pass over those mappers is where that happens. | A reflection round trip over **public** properties (DTO → store → export/import → options → graph) with an explicit exception list. |
| CF6 | Med | Nine validator rules have no rejecting test: empty name or address, duplicate pool name, pool name = target name, unknown pool member, unknown pool parent, non-positive interval or timeout, negative thresholds, MinUsableSample < 1. | One Theory, one mutation of a valid home config per rule. |
| CF7 | Med | `Verdict.WeatherThreshold` reaching the engine from the Settings table is never asserted (appsettings and the setting both say 2). | Set it to 1; one anchor down → weather. |
| CF8 | Med | DNS lookup-name assembly (hostname targets added, de-duplicated, disabled and Resolver targets excluded) and "watch active with an empty list" are partly pinned. The rule exists in three copies. | Host-level status test. |
| CF9 | Med | ConfigInvalid recovery after a good write, and booting on an already-invalid DB, are untested. | Two HostLifecycleTests. |
| CF10 | Med | Review H4, second half: a failed rebuild must answer 500 on `/admin/engine`, factory reset and clone. Take-over safety depends on it. | A throwing-Detach observer. |
| CF11 | Med | The config API and export-file JSON shape are pinned only field by field. A property rename passes because the tests deserialize into the same records. The Razor JS and old backup files would break. | A golden key-set test, plus importing a checked-in v0.10-era export literal. |
| CF12 | Med | The wizard's "set AnchorPool when unset" never runs. Its assertion is satisfied by appsettings. | Clear the setting first. |
| CF13 | Med | Pool delete (including the anchor pool) and a successful profile delete are untested. | Service and API tests. |
| CF14–CF19 | Low | Rebuild debounce never runs (tests force 0 s); the system-managed frozen-field list is partly pinned; replace-all import vs this node's own resolvers; first-boot seeding from an appsettings `Watcher` section; ConConfig default paths; clone import-failure paths. | See the plan. |

### 3.5 Persistence (PS)

Strongest areas: ModelWarningLedger, DiscoveryStore (address edge cases all pinned), retention and pruning, CSV/JSON export bytes, timestamp offsets (stored and exported times carry UTC; review H3 has its regression test), review items P1, P2, P3.

| Id | Sev | Gap | Test to add |
|---|---|---|---|
| **PS1** (= HA6, TR3) | High | **Shared vocabulary.** HostState, TransitionTrigger, TransitionSubjectKind, VerdictState, ModelWarningKind, TargetScope and the Discovery enums go out by `ToString()`. They are persisted in rows, compared as string literals in about 10 C# files and the client, sent on the wire and MQTT, read by the tray, and used as Prometheus label values. An IDE rename passes every suite, while old rows stop matching (report counts zero, warm start cold-starts, carried warnings vanish). | `VocabularyTests`: `Enum.GetNames` equals frozen arrays, `GlitchClass` 0/1, `"HistoryReset"`. Written once into a fixture that Vitest also reads (HA1). Exhaustive enum tables for TrayStatus and StateMapping. |
| **PS2** | High | Warm start's "last alive = max(latest transition, latest **minute rollup** + 1 min)" is untested. On a quiet link every restart would cold-start. | Seeder tests with rollups. |
| PS3 | Med | **Likely bug (ruling needed):** History's `resolution=auto` reads appsettings retention, while the Settings page's retention (applied by RetentionService) can be shorter. With 7-day minute retention, the 30-day chart shows only the last 7 days. | After ruling D3. |
| PS4 | Med | The report's per-target incident logic (the C# twin of History's) pins 4 of the client's 9 cases, and the two pick the opening state differently. | Mirror the client cases; ruling D6 on the opening state. |
| PS5 | Med | Glitch coalescing across flushes, the production path, is untested. Only the same-batch case is. | Two flushes. |
| PS6 | Med | RollupCollector's 15 s loop never runs under test. | FakeTimeProvider + timer-armed wait. |
| PS7 | Med | Recorder shutdown drain and the full-batch continuation are unasserted. | Stop with queued events; 1,200 events through a 500 batch. |
| PS8 | Med | The nightly retention scheduler: only the startup pass is tested. | Advance to RunHourUtc the next day; a failed pass retries. |
| PS9 | Med | Migration upgrade with data: the only data-carrying test starts mid-history and checks profile names. | Migrate from `InitialCreate` with raw rows; assert defaults (Scope=Verdict, Critical=false) and that history reads back. |
| PS10 | — | Merged into HA3. | |
| PS11 | Med | Snapshot import: the failure paths (validate before delete) and old-snapshot compatibility are pinned only by a gitignored local file. | A committed, sanitized 2026-09-shape fixture plus failure cases. |
| PS12–PS15 | Low | All-failure rollup merge (RttMin must not become 0); live vs recorded transition shape (one DTO, two mappers); smaller warm-start seeding rules; smaller ISP report rules. | See the plan. |

### 3.6 Web host, API, MCP, metrics, client (HA)

Strongest areas: transitions wire row (byte-pinned), export bytes, health, MCP tool count and status keys, the admin gate, the API lock enumeration for `/api/v1`, client lib modules (incidents, encoding, verdictClock, historyData, treeLayout, statusStore).

| Id | Sev | Gap | Test to add |
|---|---|---|---|
| **HA1** | High | Nothing ties the server's JSON to `ClientApp/src/lib/types.ts`. Client tests use hand-made fixtures. Never pinned: the status `dns` block, `warnings[]` as sent, glitch item keys, rollup point keys (`periodStart` is compared as a string, so its `+00:00` format is contract), and the Index `initial-data` island. | Golden key-set fixtures under `Tests/contracts/`: the .NET test writes and compares them, and a Vitest `contracts.test.ts` checks the TypeScript key lists against them with `satisfies`. |
| **HA2** | Med | **Live bug:** the header pip (`wwwroot/lib/site-shell.js`) has no `DnsDown`, so every non-dashboard page shows grey "Checking" during DnsDown. The verdict-answer mapping exists three times (pip, dashboard slab, tray). | Vitest loads site-shell.js and checks every VerdictState from the vocabulary fixture; fix the map. |
| **HA3** (= PS10) | High | **Bug:** clear-history and factory reset never delete `Glitches`. The Settings page promises reset "deletes everything". Clear-history also keeps the review-P5 no-op `PRAGMA incremental_vacuum`. No test would catch a forgotten table. | Classify every entity type in a literal map (an unclassified table fails the test); seed every table and assert what each operation empties. Fix: ruling D1 for clear-history. |
| HA4 | Med | Lock-surface escape hatches: open prefixes matched with `StartsWith`, only `/api/v1` probed, page locks by hand-kept lists. | Enumerate all endpoints and Razor pages against one exact classification table. |
| HA5 | Med | StatusBroadcaster's 250 ms spacing, immediate send on a transition and stale-engine skip are pinned only end to end in real time. The skip never ran. | Recording hub context + FakeTimeProvider. |
| HA6 | — | Merged into PS1. | |
| HA7 | Med | JSON islands embedded with `Html.Raw`: script-breakout escaping is pinned only on the DNS page, not on the **open** dashboard or History. | A target named `x</script><script>…`. |
| HA8–HA12 | Low | /metrics label escaping, the family list, and the "Internet" pool vs verdict series collision (ruling D9); the Report page incident table and Kind=Utc; MCP readOnly annotations and the glitch row shape (a second projection); small HTTP branches (504 search-timeout, cookie attributes, glitch JSON export bytes); client `story.ts` distinct-anchor counting and the inline math in `history.tsx`. | See the plan. |

### 3.7 Notifications, CLI, tray (NT, CL, TR)

Strongest areas: the CLI (grammar, guarded and elevated paths, service commands, health probe), ScoutClient, TrayToasts, CriticalWatch, review H1 (pinned at both levels).

| Id | Sev | Gap | Test to add |
|---|---|---|---|
| **NT1** | High | **ConNotify.Tests has never run in CI.** That covers every MQTT topic and Home Assistant discovery pin, the ON/OFF mappings, the dns-sensor "unavailable, not OFF" rule, the webhook body's dns block, the whole WebhookFilter rule set, LogSafe, and the H1 unit tests. | One line in `ci.yml`. |
| NT2 | Med | The MQTT wire is barely asserted end to end: retain flags, verdict and internet state values, the transitions topic (not retained), target attributes, cleanup on target delete, reserved-slug clears. | Late subscriber in the existing Docker test (replays only retained messages). |
| NT3 | Med | "Every LAN device gets an MQTT sensor" (Mark, 2026-10-10) has no test. The MQTT and webhook observers share near-identical Attach bodies with deliberately different filters, so a DRY merge would delete those sensors. | A Local target's state and attributes in the Docker test. |
| NT4 | Med | The MQTT "Name resolution" entity is never published by any test. | `DnsPublication.From(engine)` on the engine harness. |
| NT5 | Med | Master enable for notifiers: MQTT goes offline, heartbeat stops, `Engine.*` pokes the notifiers. | SettingsStore signal; Docker availability; heartbeat absence window. |
| NT6 | Med | Webhook "skip explained" is **on by default** (absent ≠ "false"). A bool-normalising refactor flips it, and every router outage emits one webhook per target behind it. | Integration test with the key unset. |
| NT7 | Low | The Docker MQTT tests skip silently in CI if the container can't start. | Fail instead of skip when `CI=true`. |
| NT8 | Low | Multiple webhook URLs (one per line, blanks dropped). | Two hooks. |
| CL1 | Med | The elevated relaunch's `--__output` handshake is untested on both sides, in two files with two literals. The installer runs elevated, so only manual use would hit a break. | Move `StripOutputOption` and the option name into CliApp (**seam**); test. |
| CL2–CL4 | Low | Service install argument lists (optional **seam**); exit-code numbers asserted against their own constants; folder lockdown before service install, order not pinned. | See the plan. |
| TR1 | Med | The tray's poll reducer lives in WinForms code. The DnsDown↔LinkDown toast depends on one untested line (`NamesFailing` in `stateChanged`). | Move the reducer into `ConInfrastructure/Tray` (~40 lines, no behaviour change) and test it. |
| TR2 | Med | The scout send loop's security rules: a 3-prompt budget, remember-after-accept, per-server plain-HTTP consent, URL-bound saved password. | **Seam** (moderate); ruling D10. |
| TR3 | — | Merged into PS1 (exhaustive VerdictState and ModelWarningKind tables; SiteIncident and InternetWeather → Healthy). | |
| TR4–TR6 | Low | TrayStatus no-verdict fallback; malformed entries become Unreachable through a catch-all in the shell (the known deferred minor); TraySettings and the "clear instead of pin" URL rule. | See the plan. |

---

## 4. Decisions for Mark

The first five are probable defects; the rest need a ruling before the test can say what is right. Each lists my recommendation. The plan's decision-gated tasks (Phase 6) wait on these. Every other task pins current behaviour and needs no ruling.

| # | Question | Finding | Recommendation |
|---|---|---|---|
| D1 | Should **clear-history** also delete Glitches? (Factory reset obviously should; the page says "everything".) | HA3 | Yes to both; glitches are measurements. Drop the no-op `incremental_vacuum` too. |
| D2 | Header pip during **DnsDown**: show red "Down" like the dashboard and tray? | HA2 | Yes. It is drift, not design (site-shell.js predates DNS watch). |
| D3 | History `resolution=auto`: pick by the **effective** retention (Settings table), not appsettings? | PS3 | Yes: one source of truth, the one the Settings page edits. |
| D4 | Should `>= threshold` mean what it says at the exact boundary (compute the rate in double)? | ET6 | Yes. It bites edited profiles: 90 % or 70 % on the default 10-sample window, and 95 % with windows that are multiples of 20. |
| D5 | Behind a dead branch, an Unreachable **anchor**: count it as degraded (SiteIncident), or keep Healthy but stop counting it as "responding"? | EV6 | Keep Healthy (explained time carries no blame, spec §4.4), but fix the "N anchors responding" count. |
| D6 | ISP report vs History opening state: a target Down all window with no rows inside it is 1 outage in the report and 0 in History. Which is right? | PS4 | The report's way (latest row before the window), and align History. |
| D7 | Is an anchor nested under another anchor part of the **trunk** for sweeps and the "trunk involved" wording (today's suspicion behaviour) or not (the verdict's rule)? | EV15 | Keep today's behaviour and pin it; the two uses differ legitimately. |
| D8 | Average RTT on transitions counts failed samples as 0 ms, so a lossy window reports a low RTT. Intended? | ET refactor note | No. Average over successful samples only. Changes transition payloads, so decide deliberately. |
| D9 | Default anchor pool and verdict subject are both named "Internet": one `conwatch_transitions_total{name="Internet"}` series, and the dashboard trail fetch by name also returns pool rows. | HA8 | Fetch the trail by `subjectId`; key the metrics counter by subject id + kind. |
| D10 | Tray scout loop and TraySettings: move them into testable libraries (moderate seam), or keep them on the manual checklist? | TR2, TR6 | Move TR1 (small); keep TR2 manual unless the refactor touches it anyway. |

### Rulings (Mark, 2026-10-18)

- **D1:** both Clear history and Factory reset delete glitches. Clear history's page text becomes "Deletes all transitions, rollups and glitches. Configuration and settings are kept." The no-op `PRAGMA incremental_vacuum` goes.
- **D2:** during DnsDown the header shows red **"DNS down"**. This deliberately uses a more specific word than the dashboard's "Down"; the colour matches.
- **D3:** History's `resolution=auto` follows the **Settings table** retention, the same windows `RetentionService` applies, through one shared helper. Stitching resolutions was considered and not chosen.
- **D4:** fix it. Rates become `double`, so a threshold means exactly what it says. While deciding, the scope turned out wider than first reported. With the default 10-sample window, a profile edited to Responding **90 %** or Faltering **70 %** misses at exactly that rate (9/10 reads as 0.8999…). 35/45/65/95 % break with windows that are multiples of 20. 50/75/80 % and the low thresholds are safe. The built-in profiles are not affected.

- **D5:** stays **Healthy**, with an honest count. The detail names Unreachable anchors separately (e.g. "Healthy: 2 anchors responding (Quad9 unreachable behind Modem B)"). The independent-anchor confidence count uses only answering anchors.
- **D6:** `/api/v1/transitions` gains an opt-in `opening=true`. With it, each subject's latest transition before `from` comes back as well, and History uses that as the opening state. History and the report then follow one rule, and History's documented limitation goes away.
- **D7:** keep both rules. The verdict's "nested anchor is not a path hop" and suspicion's "outer anchor is trunk" are both pinned by tests, so a merged walk has to keep the difference explicit.
- **D8:** a transition's `avgRtt` averages **answered pings only**. It stays 0 when none answered, so the field stays a plain number. Old rows keep their diluted values.
- **D9:** the verdict's metrics series becomes **`name="verdict"`**. Pools and targets keep their names. The name "verdict" becomes reserved for targets and pools, so validation rejects it case-insensitively. The dashboard trail fetches by the verdict's `subjectId`. Grafana panels that read the verdict as `name="Internet"` must be edited when this lands.
- **D10:** move **both** pieces. The scout send loop goes into `ConInfrastructure/Tray`, and TraySettings plus the URL "clear instead of pin" rule go into ConConfig, each with tests. A corrupt `tray.json` is moved aside to `.corrupt`, as the CLI already does.

Smaller ones the plan settles itself, marked "my call" in it:
- ProbeSpec leniency (`tcp:+80`, `tcp: 80`): pin as accepted.
- Validation-message wording stays a contract (pinned by name of subject, not full text, in new tests).
- The CF1 retry test gets a delay seam (`RetryDelays` internal) rather than 6 s of real time.

---

## 5. Not worth covering (consolidated)

- Real raw-socket and OS adapters behind existing seams: `IcmpTtlProbe`, SSDP discovery, `GatewayDiscovery.DefaultRouteGateways`, `SystemLocalNetworks.Read`, `WindowsResolvers`, the DoH registry walk, `DomainNameResolverDns`'s Timeout branch.
- Logging-only catch blocks, dispose races, publish-lock contention guards, and an unreachable `beyond == null`. These need timer or fault seams for no behavioural gain.
- Presentational React components (their logic lives in tested lib modules), site-shell theme and drawer, dialog wiring, the Swagger UI, `StatusHub` (empty).
- Tray UI glue: icon drawing, click timing, balloons, PasswordPrompt and SettingsForm layout, ProcessTrayLauncher, autostart registry value. These stay on the manual checklist.
- `MqttTester`, the MQTT LWT on ungraceful disconnect, heartbeat interval timing, webhook non-2xx logging.
- `CircularBuffer.ToArray` (dead code: delete it rather than test it), `DesignTimeFactory`, trivial `ToString`s.
- Pool "Unreachable member counts as Down": pools no longer feed the verdict.
- Review P6 (buckets taken inside the lock): now structural.

---

## 6. Notes for the refactor session

These are not test gaps; they are things the refactor should know. Each duplicated piece gets a pinning test in the plan **before** anyone merges it.

- **Duplicated logic with deliberately different rules:**
  - The ancestor and trunk walk, four times (Verdict ×2, Topology, Suspicion, plus ModelCheck and TopologyNames).
  - ICMP echo helpers, four times, each with a different exception contract.
  - Reverse DNS with a 1 s cap, twice.
  - Private-address predicates, three times, differing on purpose.
  - The "DNS watch is active" rule, three times.
  - ISP-owned operator normalisation, twice.
  - PingProfile defaults: PingProfile, PingProfileConfig and the DB seed.
  - Five near-identical target mappers.
  - Glitch row projection, twice (REST and MCP).
  - Verdict → meaning, four times (tray, MQTT StateMapping, pip, slab).
  - The report's incident logic vs History's.
  - Retention windows, resolved in two places.
  - "Undetermined" state, declared four times.
- **Stringly-typed couplings:**
  - `"Refused"` (TCP transceiver ↔ TargetMonitor).
  - `"HistoryReset"` (not an enum member; in five files and the client).
  - State and trigger names as literals in about 10 files.
  - `--__output` in two files.
- **Load-bearing order inside `WatcherEngine.StartAsync` and the result route:** Verdict.Start before UseProbeOnlyVeto and ModelCheck.Start; DNS log before `monitor.RecordPingResult`; suspicion after it; the seeded `_lastVerdict` before Verdict.Start. A builder split must keep all of them; EV2 and EV10 tests guard them.
- **The pump has two exception guards** (PingManager's and the engine's). Keep both, or decide which owns the rule. ET1 tests PingManager's.
- **`WarmStartSeeder.FromEngine` encodes engine semantics in the host.** If it moves, take its test with it.
- **Two lock-probe tests** (`…published_outside_the_verdict_lock`, `…pool_lock`) use `Seed()` as the probe. They go vacuous if `Seed` becomes lock-free.
- **The verdict test harness is copied six times.** The plan adds one shared builder (test-only) first, which makes the new engine tests cheap.
- **`PingResult.Timestamp` defaults to the wall clock at construction,** not the engine's TimeProvider. Fine today (UTC), but worth knowing if the engine moves fully onto TimeProvider.
- **Snapshot import:** config and data are two transactions, and snapshots carry no ModelWarnings or Discoveries. Make that a conscious decision.
- **Clear-history and factory reset** are persistence logic in the API layer. A ConPersistence `HistoryClearer` beside `DataPruner` would put both destructive operations under one fixture.
