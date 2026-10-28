# Refactor survey — 2026-10-22

Reviewed at `663ab0f`. Read-only. Goal: DRY first, then misplaced code, then dead code. No big changes.
Build: clean in product code (warnings only in test projects: CA1416 in `WindowsFolderLockdownTests`, CA2022 ×2, CS8631 ×1).
Prior pass: plan B (2026-09-25). Items it parked that still hold are marked *(plan B left out)*.

Sizes: S = under an hour or so, M = a focused task. "Ruling" = you decide a behavior question first.

## B — Drifted copies that are already bugs (fix while deduping)

| ID | What | Where |
|---|---|---|
| B1 | Pools editor offers **Resolver** targets as pool parent/members; the validator rejects them → 400 on save. Rule should be `Scope == Verdict`. | `Pages/Config/Pools.cshtml:78,90,122` vs `WatcherOptionsValidator.cs:48-58,81,87` |
| B2 | "Prune before date" skips ended model warnings that the nightly pass and Clear History both delete; Data-page stats omit Glitches and Discoveries. | `DataPruner.cs:25-34`, `RetentionPass.cs:58`, `AdminEndpoints.cs:60-70,123-151` |
| B3 | MCP `get_glitches` drops `Id`/`TargetId` and emits `DateTime` ("Z") where REST emits `DateTimeOffset` ("+00:00"). | `GlitchEndpoints.cs:28-36` vs `ConWatchMcpTools.cs:140-148` |
| B4 | `MqttNotifier` has no connect timeout (MQTTnet default); `MqttTester` sets 5 s. | `MqttNotifier.cs:153`, `MqttTester.cs:11` |
| B5 | `GatewayDiscovery` can offer a `0.0.0.0` gateway, doesn't skip tunnels, doesn't catch `NetworkInformationException` — the other two NIC walks do. | `GatewayDiscovery.cs:57-70` vs `LanScanner.cs:19-42`, `SystemResolverSource.cs:55-71` |
| B6 | `apiFetch` throws on network failure; most callers set `aria-busy` and never clear it. | `wwwroot/lib/site-config.js:4`; guarded only in `site-dns.js:29` |
| B7 | Unique-name generator ×3 disagrees: `"X 2"` vs `"X (2)"`, case-sensitive vs not, one truncates to 64 before suffixing. | `SetupService.cs:147`, `Discoveries.cshtml.cs:69`, `SystemResolverReconciler.cs:111` |
| B8 | `ConfigService.ImportAsync` lacks the unique-violation catch Edit has → case-colliding profile names give a 500. `CloneService.TakeOver` catches import failures too narrowly → bare 500 after the source is already disabled. | `ConfigService.cs:307-397`; `CloneService.cs:193-207` |
| B9 | CLI `service install` / `tray install --url` overwrite a malformed config without the `.corrupt` copy `TraySettings` promises. *(reviewer claim; not re-opened)* | `ServiceCommands.cs:36-48`, `TrayCommands.cs:42-44` vs `TraySettings.cs:63-78` |

## D — DRY consolidations (C#)

| ID | Consolidate | Into | Size |
|---|---|---|---|
| D1 | HostState groups written inline: Down-or-Unreachable ×14, Initializing-or-Starting ×7, "answering" as a static on `TopologyCoordinator`, Responding-or-Starting ×3; plus verdict groupings (`is not (LinkDown or Unknown)` ×2, tray/MQTT match verdicts by string). | `HostStateExtensions` beside `HostState`; `VerdictStateExtensions` beside `VerdictState` | S–M |
| D2 | Parent-chain/descendant walks hand-rolled in Topology, Suspicion, Verdict ×2, ConfigHealth ×2, WarmStartSeeder (none cycle-safe; caps 20 vs 32). *(plan B left out, now 7 copies)* **Ruling:** Verdict excludes an anchor nested under another anchor from the trunk; Suspicion and `TopologyNames` include it. | `TargetGraph.AncestorIds/DescendantIds` on top of `TreeWalk` | M |
| D3 | "Counted anchors / ISP operator" rule re-implemented in ConfigHealth (counts disabled targets; engine doesn't). Operator normalize ×3. Anchor-pool lookup ×2 with a hard-coded `2` threshold fallback. | ConCore static (`IspRule`, `OperatorTag.Normalize`) | M |
| D4 | Boolean settings by string compare at 18 sites, each encoding its own default. Int ranges/defaults split across `SettingsEndpoints`, Razor `min/max`, and consumer fallbacks (MQTT 1883, topic/prefix, heartbeat 5/1–1440). *(plan B left out)* | `SettingsStore.GetBoolAsync` + a `SettingRules` table in `SettingsDefaults` | M |
| D5 | Engine-observer Attach/Detach bodies ×7 *(plan B left out)*; queue-under-lock/publish-outside outbox ×3 verbatim (+ `TargetMonitor`'s blocking variant — ruling on whether that's intended); teardown lists ×4. | `SubscriptionSlot`, `OrderedOutbox<T>`, `CompositeDisposable` | S |
| D6 | The measurement-table list (prune, nightly, clear, reset, stats, snapshot). Fixes B2. | `MeasurementTables` helper in `ConPersistence.Retention` | S |
| D7 | Scope-id triple (local/resolver/known) loaded at 4 sites; glitch wire shape ×2; `Class == 1` magic ×6. Fixes B3. | `ScopeTags.LoadAsync` + `GlitchDto.From` beside `TransitionDto` | S |
| D8 | Status snapshot fetched 4 ways (archive mode differs per site); health warnings assembled 3 ways (dashboard count misses refusal + clone warnings; `clone-source-active` shows nowhere). | `StatusSource` and `HealthReport` singletons in the host | S–M |
| D9 | DNS outcome words as literals and the result line parsed twice (`DnsResultLog.Parse` vs `ModelCheck.DetailOf`, different rules); two 10/3 window constant pairs that must stay equal or `.Last()` throws. | `DnsOutcome` constants in ConCore; `DnsSummary.Judged` | S |
| D10 | ConInfrastructure network one-offs: NIC walk ×3 (B5), one-shot ICMP ×3 (differing catches, no `WaitAsync` fix), reverse DNS with timeout ×2, MAC parse ×2 (different rules), RFC 1918 test ×2. | `ConInfrastructure/Net/` (`HostInterfaces`, `IcmpEcho`, `ReverseDns`, `MacAddress`); RFC 1918 on `ProbePolicy` | S |
| D11 | CLI vs tray vs ConConfig: config read-modify-write + `.corrupt` policy (B9), legacy `url` reader ×2, server-URL trim ×~10, health probe ×2 (tray shows raw exception), identity strings ("ConnectionWatcher", "conwatch-tray", mutex). | `ConfigFile.Update`, `ConfigResolver.NormalizeServerUrl`, `HttpHealthProbe` moved to ConConfig, an identity constants class | M |
| D12 | Built-in profile names and role defaults as literals (Setup, Discoveries adopt, Targets prefill, reconciler); profile defaults restated in `Profiles.cshtml`, `PingProfile`, `WatcherOptions`. | `PingProfile.*.Name`, a `TargetRoleDefaults`, one defaults source | S |
| D13 | Small in-class dedupes: ConfigService validate→write→catch (B8) and profile/parent lookups; DiscoveryStore's three state changes; CloneService clone vs take-over prep/import (B8); export endpoint skeleton ×3 (+ export re-building `HistoryQueries` filters); `JsonSerializerOptions(Web)` ×8; `ProbeSpec` parsed at each use and rendered two ways; `TargetTransition` field list ×2; rollup arithmetic ×3. | private helpers in the owning classes | S each |

## C — DRY consolidations (client)

| ID | Consolidate | Into | Size |
|---|---|---|---|
| C1 | Four duration formatters that print the same 100 s four ways. | `lib/format.ts` (clock + prose styles) | S |
| C2 | Fetch/parse ×8, transitions URL built 5 ways, newest-first reversal ×3; three sites have no `catch` (History can hang on "loading…"). | `lib/api.ts` | M |
| C3 | Five tables keyed by verdict name (class, slab, fill, attributed, shell pip). **Ruling:** DnsDown is not in `ATTRIBUTED`, so it never reaches ledger/punchcard/strip — intended? | `lib/verdicts.ts` | M |
| C4 | Scope predicates disagree: tree/board exclude Resolvers, status bar and History "Path" picker include them. **Ruling** needed, then `lib/scope.ts` (rename `lan.ts`). | `lib/scope.ts` | S |
| C5 | Local-day/hour math ×3 (punchcard key must match in two files), `DAY_MS` ×4, ISO strings compared as strings (Z vs +00:00). | `lib/localTime.ts` | S |
| C6 | Ribbon drawing ×2, hatch pattern ×2, "unreachable behind…" text ×3, state-chip colors ×2 (mirror CSS), story-fetch effect ×3, group-by-parent ×2. | `StateRibbon`/`marks.tsx`, CSS `.statechip`, `useStory` hook | S–M |
| C7 | wwwroot JS: `apiFetch` guard + `withBusy` (B6); traceroute page bypasses `site-config.js`; retention form re-implements `wireSettingsForm`; insecure-transport retry ×2; UTC→local inline scripts ×2 that format differently (Report vs Discoveries). | `site-config.js` helpers; one `site-time.js` | S |

## O — Out of place

| ID | Move | Size |
|---|---|---|
| O1 | Shared types buried in other files: `VerdictState`/`DnsBlame` in `VerdictCoordinator.cs`; `ModelWarningKind`/`ModelCondition` in `ModelCheckCoordinator.cs`; `SettingKeys` in `SettingsStore.cs`; `ConfigResult` in `ConfigService.cs`; `DiscoveryStatuses` in `DiscoveryStore.cs`; client `GlitchItem`/`InitialData`/scope union outside `types.ts`. | S |
| O2 | Engine: hostname follower (~100 lines) out of `WatcherEngine` into `Network/HostnameFollower` *(plan B left out)*; pool stagger split between `PoolCoordinator` and `WatcherEngine` → `Scheduling/PoolStagger`. | S–M |
| O3 | Host: `EngineHealth`/`RuntimeHealth`/`CloneHealth` into `Health/`; `HealthWarning.Page` route mapping out of ConPersistence into the host; probe-port candidate policy from `ProbeTestEndpoint` into `ProbePolicy`; first-boot EF query from `Index.cshtml.cs` into `ConfigStore`; discovery/LAN-scan/DoH registrations scattered under Engine/Persistence in `ConWatchServices` → `AddConWatchDiscovery`. | S |
| O4 | Folder/namespace mismatches: `ConWatch/Dns` ↔ `ConWatch.NameResolution`, `ConInfrastructure/Dns` ↔ `ConInfrastructure.NameResolution`. Transceivers split between root and `Dns/` → `Probes/`. `ConNotify/Builders.cs` holds seven unrelated types → split MQTT/HA vs webhook. | S |
| O5 | `ConInfrastructure/Tray/*` and `Discovery/ScoutClient.cs` are tray-only. Minimal: move `ScoutClient` into `Tray/`. Fuller: a `ConTray` library (your call). | S / M |
| O6 | `ModelWarningRecorder` inherits `IEngineObserver` and throws `NotSupportedException` → move `IEngineObserver` down to the recorders that honor it. | S |
| O7 | Repo root: empty `APM/grafana/...` tree; `Get-NasSnapshot.ps1` → `tools/`; `.idea/` files tracked. | S |
| O8 | *Optional:* `Topology/` holds four concepts (graph, verdict, model checks, suspicion/loss). Split into `Topology/`, `Verdict/`, `Diagnostics/` — namespace churn across the host, your call. | M |

## X — Dead code

| ID | Remove (or make internal) |
|---|---|
| X1 | Engine: `CircularBuffer.Capacity`/`ToArray`, `PingController.Task`, `HostAnalysisEngine.LastUpdated/NumSuccess/NumFailed`, `IcmpTtlProbe` duplicate `TimedOut` arm, `CliApp.Guarded` redundant `PlatformNotSupportedException` catch. |
| X2 | Persistence: `CsvExport.GlitchLines` (unused), `RollupMerge` `resolution` param and `ConfigMapping.ToDto` `id` param (never passed), `DiscoveryStore.MaxBatch` alias. |
| X3 | Host/tray: empty `SetupModel`/`TracerouteModel`; `DataModel` ≡ `SettingsModel`; `CloneService.WithoutSystemManaged/WithoutLocalDevices` pass-throughs; `ScoutPasswordStore(ConfigPaths?)` param never passed; stale "Program.cs/Admin" comment in `AdminLockPageFilter`. |
| X4 | Client: `RAMP` re-export, `TimeChart` `lossAxis`/`secondaryAxisId`, `Branch.targets` prop, `RowParts.successRate/led` toggles, `orderRows` export; CSS `.verdict-banner.siteincident/.internetweather`, `.cw-mono`; dark-theme tokens duplicated verbatim in `site.css:31-57`. |
| X5 | Test-only public members (`WatcherEngine.IsLocalSubject`, `LossWindows.Open`, `HopLoss.ProbeOnly`, `FixedIntervalCadence`, `TargetGraph.GetByName` etc., `DnsMessage.TryReadQuestionName`, `OuiTable.Count`, `SnapshotImporter.ImportToFileAsync`, `NotesPanel.unacknowledgedCount`, …). **Ruling:** adopt `InternalsVisibleTo` repo-wide and make them internal, or leave as-is (plan B left them). |
| X6 | `DomainResolutionResult.AddressCount/FirstAddress/IPv6Addresses/ToDetailedString` — kept by your decision 6 in September; still unused. Revisit? |

## Rulings (Mark, 2026-10-28)

1. **D2 — nested anchor: never trunk.** The trunk is the anchors' ancestors minus the anchors themselves, everywhere (verdict's T4 M1 rule wins). A nested anchor still triggers sweeps and still sits in the path chain of the anchor below it, but its blips count as internet-side in `SuspicionCoordinator`, and `TopologyNames` gives it the anchor role only.
2. **D5 — keep both lock modes.** `OrderedOutbox<T>` takes a mode: *Wait* for `TargetMonitor` (callers see their transition delivered on return; tests rely on it), *Skip* (TryEnter) for the coordinators (never wait inside another delivery). The helper documents why. No behavior change.
3. **C3 — DnsDown gets its own mark in the ledger, punchcard and event strip**: a distinct "your resolver" mark, never counted as ISP time. The ISP report keeps DnsDown out of attributed episodes. This is a small feature: new mark, legend text, tests.
4. **C4 — internet scope stays "not Local"; add a named tree rule and a Resolvers group.** `TargetScope.IsTree()` (C#) and `isTreeTarget` (`lib/scope.ts`) mean `Scope == Verdict`. Used by the tree, the ribbon board, the Pools editor (fixes B1) and History's Path picker. History's picker also gets its own "Resolvers" group. The ledger and strip keep showing resolver events.
5. **X5 — internal + InternalsVisibleTo.** Add `InternalsVisibleTo` for each library's test project (ConCore, ConEngine, ConPersistence, ConNotify; ConInfrastructure and ConWatch already have it). Make test-only members internal; delete ones that are dead even in tests or only re-wrap another call.
6. **O5 — tray code: tidy in place.** Move `Discovery/ScoutClient.cs` into `ConInfrastructure/Tray/` with a note on why tray logic lives in the library (Linux-testable). No new project.
7. **O8 — Topology/: folders only, keep namespace.** Subfolders `Topology/`, `Verdict/`, `Diagnostics/` under `Core/ConEngine/`, all still `namespace ConEngine.Topology`. This is a sanctioned exception to folder = namespace (note it in a README line); O4 still fixes the unintended mismatches.

## Suggested order

B1–B9 first as their own small commits (they're bugs). Then D1 → D2 → D3, since D2 and D3 lean on D1's predicates. D4–D13 and C1–C7 are independent of each other. O and X last, mechanical, one commit per ID.
