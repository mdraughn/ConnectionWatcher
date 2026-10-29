# Refactor 5: moves and dead code Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Carry out survey items O1–O8 and X1–X5 as behavior-preserving moves, renames and deletions on branch `refactor-5-moves-and-dead-code`, one commit per task.

**Architecture:** Five groups. A: repo-root housekeeping (O7). B: dead code (X1–X4). C: test-only members become internal behind `InternalsVisibleTo` (X5, ruling 5). D: misplaced code moves to where it belongs (O1–O6, ruling 6). E: the `Core/ConEngine/Topology/` folder split, namespace unchanged (O8, ruling 7). Every task pins current behavior with a named test (existing or new) before it moves or deletes anything; no wire shape changes.

**Tech Stack:** .NET 10, ASP.NET Core Razor Pages + minimal APIs, EF Core 10.0.13 on SQLite, System.Reactive 6.1.0, xUnit 2.9.3, React/TypeScript with vitest (happy-dom).

**Spec:** `docs/superpowers/reviews/2026-10-22-refactor-survey.md` — the O and X tables and the Rulings section (rulings 5, 6, 7). Locations come from the reviewer reports behind it; everything below was re-checked against master `1e0c820`.

## Global Constraints

- Never push. Branch `refactor-5-moves-and-dead-code`, cut from master after refactor plans 1–4 are merged. Nothing here merges into `master` without Mark.
- Runs last. Plans 1–4 edit some of the files this plan moves or trims. Each task starts with a "check what changed" step: if an earlier plan already did the item, mark the step done and say so in the commit message; if it reshaped the code, apply the same decision to the new shape.
- Behavior-preserving: every HTTP/JSON shape, SignalR message, MCP output, MQTT/webhook payload, metric, CLI output and the database schema stay as they are. No EF model change, no migration.
- After every task: `dotnet build ConnectionWatcher.sln -c Debug` has 0 errors and no warning beyond the pre-existing four (CA1416 in `WindowsFolderLockdownTests`, CA2022 ×2, CS8631 ×1); `dotnet test ConnectionWatcher.sln -c Debug` has 0 failed; from `Programs/ConWatch/ClientApp`, `npx vitest run` passes and `npx tsc --noEmit` is clean.
- No new NuGet or npm packages, no new projects, no `global using`.
- Moves are `git mv` so history follows the file; file content changes in a move commit are limited to `namespace`, `using` and fully-qualified-name lines.
- Conventional commit prefixes (`refactor:`, `chore:`, `test:`), no attribution or co-author lines, one commit per task.
- UTC end to end; no `DateTime.Now`, no `new DateTimeOffset(dt)` on a value of unknown Kind. (No task here touches time code; listed so a move does not "tidy" a conversion.)
- Tests wait for conditions, never sleep-then-assert (CI runners have 2 cores).
- Run commands from the repo root in PowerShell. Locate code by symbol; line numbers have drifted.
- Known flaky (followups file): `WarmRestartTests.Restart_resumes_states_without_new_transitions`, `SetupApiTests.Apply_is_additive_on_rerun`, `PageSmokeTests.Page_renders(/Admin)`. One failure of one of these: rerun that project once.

## Review Focus

1. A Razor page whose page model is deleted (`/Setup`, `/Traceroute`) must still redirect a locked-out browser to `/?admin=1`. Test: existing `AdminLockTests` (its URL loop includes both), run in Task 4 before and after.
2. `GET /api/v1/config/health` must keep `page` and `pageName` on every warning after `HealthWarning.Page` moves into the host. Test: existing `DnsPageTests` asserts both keys (Task 11).
3. A hostname target that goes Down must still trigger an immediate re-resolve after the follower leaves `WatcherEngine`. Test: existing `DnsRefreshTests` (Task 10).
4. A target in two pools must keep the slot of the first pool by name after the stagger moves. Test: existing `PoolStaggerTests` (Task 9, retargeted).
5. A malformed `testdata/nas-snapshot.json` path after the script moves to `tools/` would silently skip the real-data suite. Test: Task 1 step 3 checks the resolved output path.

---

## Decisions made at plan time

| Item | Survey said | Found / decided |
|---|---|---|
| O4 direction | Folder ↔ namespace mismatch | Folders follow the namespaces (`Dns/` → `NameResolution/`), not the reverse: a namespace segment named `Dns` inside `ConWatch.*` or `ConInfrastructure.*` would shadow `System.Net.Dns` for every file in those namespaces, which is why the namespaces were named `NameResolution` in the first place. |
| O4 scan | Two mismatches named | A full scan of product code found exactly those two (`Programs/ConWatch/Dns`, `Libraries/ConInfrastructure/Dns`) plus `Startup/ConWatchServices.cs` in `namespace ConWatch`. That one stays: it holds `IServiceCollection` extension methods, and keeping them in the root namespace is the usual convention (no `using` at the call site). |
| O4 resolver classes | Only transceivers to `Probes/` | `DomainNameResolverDns.cs` and `DomainNameResolverFactory.cs` also leave the library root, into `NameResolution/`; otherwise they are the only loose files left at the root. |
| O3 `HealthWarning.Page` | Move route mapping into the host | The record stays in ConPersistence (it is what `ConfigHealth` returns); the route mapping becomes host extension methods `Page()`/`PageName()`. `GET /config/health` projects to `{ code, message, page, pageName }` in that order, so the JSON is unchanged. If plan 3's `HealthReport` owns that JSON by then, the projection goes there. |
| O3 `AddConWatchDiscovery` | Discovery/LAN-scan/DoH registrations scattered | `AddConWatchDiscovery` takes the gateway discovery, WAN-address probe, `DiscoveryStore`, `LanScanner`, `IDiscoveryScanner` and `DiscoverySearchService`. The DoH detector joins the other DNS registrations in `AddConWatchEngine`. `IspReportBuilder` stays in persistence (it is a persistence query). None of these is a hosted service, so order is irrelevant. |
| O5 | Move `ScoutClient` into `Tray/` | Ruling 6. Namespace becomes `ConInfrastructure.Tray`. The note lives in a three-line `Libraries/ConInfrastructure/Tray/README.md`. |
| O6 | Move `IEngineObserver` to the recorders that honor it | A new `EngineChannelRecorder<TEvent> : ChannelRecorder<TEvent>, IEngineObserver` carries `SourceOf` and `Attach(WatcherEngine)`; `TransitionRecorder` and `GlitchRecorder` derive from it; `ModelWarningRecorder` stays on `ChannelRecorder` and loses its throwing `SourceOf`. |
| O7 `APM/` | Delete the empty tree | Confirmed empty (four nested empty directories, untracked). Plan B left it as a possible placeholder; nothing in the repo, CI or docs references it, and git cannot track an empty directory, so it is local clutter only. Deleted; no commit results. |
| O7 extra | — | `Core/ConEngine/Examples/` is also an empty folder, kept alive only by `<Folder Include="Examples\" />` in `ConEngine.csproj`. Both go in Task 1. |
| O7 `.idea/` | Untrack | Four files under `.idea/.idea.ConnectionWatcher/.idea/` are tracked and `.gitignore` has no `.idea` rule. `git rm -r --cached` plus a `.idea/` ignore line; local files stay. (`.vscode/` is untracked today; left alone, see owner questions.) |
| O8 | Split `Topology/` | Ruling 7: folders `Topology/`, `Verdict/`, `Diagnostics/`, all keeping `namespace ConEngine.Topology`; the README Development table gets one sentence saying so. |
| X1 `HostAnalysisEngine` | Remove `LastUpdated/NumSuccess/NumFailed` | `LastUpdated` is write-only: deleted. `NumSuccess`/`NumFailed` are read inside `Recalculate` only: they become locals there. |
| X1 `CliApp.Guarded` | Remove the redundant catch | Pinned first with a new `GuardedTests` case, since nothing tests that path today. |
| X3 `DataModel` ≡ `SettingsModel` | Merge | Razor binds by type (`@model ConWatch.Pages.DataModel`), so both classes stay and share an abstract `SettingsPageModel` base; no `.cshtml` changes. |
| X3 empty page models | Delete `SetupModel`, `TracerouteModel` | The admin lock is applied by route convention (`AddPageApplicationModelConvention` in `ConWatchServices`), not by model type, so a page without a model is still locked. `AdminLockTests` proves it; if it fails, keep the empty models and record that. |
| X3 `CloneService` pass-throughs | Delete | The `WithoutLocalDevices` predicate and its doc move to `SnapshotFilter.WithoutLocalDevices`; the two test classes call `SnapshotFilter` and move to `ConPersistence.Tests`. |
| X4 dark tokens | Duplicated in `site.css` | Plain CSS cannot share one declaration block between `[data-theme="dark"]` and a `prefers-color-scheme` media query; removing either changes behavior. Kept, with a vitest guard that fails when the two blocks drift. |
| X4 `unacknowledgedCount`, `LanSummary.down` | Test-only TS | TS has no `internal`; both have no production reader, so they are deleted with their test assertions. |
| X5 IVT targets | ConCore, ConEngine, ConPersistence, ConNotify | ConCore has no test project (its tests live in `ConEngine.Tests`), and no ConCore or ConNotify member becomes internal in this plan, so neither gets an `InternalsVisibleTo` line (add one when a member needs it). ConEngine gets three (its internals are used by `ConEngine.Tests`, `ConPersistence.Tests` and `ConWatch.Tests`); ConPersistence gets two (`ConPersistence.Tests`, `ConWatch.Tests`). |
| X5 survey errors | `ConfigStore.LoadWatcherOptionsAsync(db)` internal | Wrong: `WarmStartSeeder` and `WatcherHostService` (ConWatch) call it. Stays public. `SnapshotImporter.SimpleFactory` is already private. |
| X5 `IsLocalSubject` | Test-only | Becomes internal per ruling 5 (it is not dead in tests). |
| X5 re-wraps | Delete | `DnsLookupName.TryFromJson` two-argument overload and `TracerouteRunner.RunAsync(host, ct)` only forward to another overload: deleted; tests call the remaining overload. `ITrayAutostart.IsInstalled` is read only by tests through the fake: dropped from the interface and the two real implementations; the fake keeps its own property. |

---

## File structure

| Path | After this plan | Task |
|---|---|---|
| `tools/Get-NasSnapshot.ps1` | Moved from the root; writes `..\testdata\nas-snapshot.json` | 1 |
| `Programs/ConWatch/Pages/SettingsPageModel.cs` | Shared base of `DataModel` and `SettingsModel` (new) | 4 |
| `Programs/ConWatch/ClientApp/src/lib/siteCss.test.ts` | Guard: the two dark-token blocks are identical (new) | 5 |
| `Core/ConEngine/Topology/VerdictTypes.cs` | `VerdictState`, `DnsBlameKind`, `DnsBlame` (new; to `Verdict/` in Task 16) | 7 |
| `Core/ConEngine/Topology/ModelConditions.cs` | `ModelWarningKind`, `ModelCondition`, `ModelConditionChange` (new; to `Diagnostics/` in Task 16) | 7 |
| `Libraries/ConPersistence/Settings/SettingKeys.cs` | `SettingKeys` (new) | 7 |
| `Libraries/ConPersistence/Config/ConfigResult.cs` | `ConfigResult`, `ConfigErrorKind` (new) | 7 |
| `Libraries/ConPersistence/Discoveries/DiscoveryStatuses.cs` | `DiscoveryStatuses` (new) | 7 |
| `Core/ConEngine/Scheduling/PoolStagger.cs` | Pool positions + phase offset (new) | 9 |
| `Core/ConEngine/Network/HostnameFollower.cs` | Hostname resolve/refresh loop (new) | 10 |
| `Programs/ConWatch/Health/` | `EngineHealth`, `RuntimeHealth`, `CloneHealth`, new `HealthWarningLinks` (`namespace ConWatch.Health`) | 11 |
| `Libraries/ConInfrastructure/NameResolution/`, `Probes/` | Renamed/new folders | 13 |
| `Libraries/ConNotify/MqttPayloads.cs`, `WebhookPayload.cs` | Split of `Builders.cs` | 13 |
| `Libraries/ConPersistence/Recording/EngineChannelRecorder.cs` | Engine-fed recorder base (new) | 15 |
| `Core/ConEngine/Verdict/`, `Core/ConEngine/Diagnostics/` | Split of `Topology/` | 16 |

---

## Group A: housekeeping

### Task 1: O7, repo-root leftovers

**Files:**
- Delete (untracked, local): `APM/` (empty directory tree)
- Delete: `Core/ConEngine/Examples/` (empty) and its `<ItemGroup><Folder Include="Examples\" /></ItemGroup>` in `Core/ConEngine/ConEngine.csproj`
- Move: `Get-NasSnapshot.ps1` → `tools/Get-NasSnapshot.ps1`
- Modify: `testdata/README.md:3`, the doc comment in `Tests/ConWatch.Tests/RealDataSnapshotTests.cs` (both say `./Get-NasSnapshot.ps1`), `.gitignore`
- Untrack: `.idea/`

**Interfaces:** none.

- [ ] **Step 1: Confirm the empty folders are empty**

```powershell
(Get-ChildItem -Force -Recurse -File APM, Core/ConEngine/Examples | Measure-Object).Count
```
Expected: `0`. Anything listed: stop and report it.

- [ ] **Step 2: Delete them and the csproj `Folder` item; untrack `.idea/`**

```powershell
Remove-Item -Recurse APM, Core/ConEngine/Examples
git rm -r --cached .idea
```
Add `.idea/` to `.gitignore` in the "User-specific files" block.

- [ ] **Step 3: Move the script and fix its output path**

`git mv Get-NasSnapshot.ps1 tools/Get-NasSnapshot.ps1`. In the script, `$out` becomes `Join-Path $PSScriptRoot "..\testdata\nas-snapshot.json"`. Update both references to read `.\tools\Get-NasSnapshot.ps1`. Check the path resolves to the repo's `testdata`:
```powershell
pwsh -NoProfile -Command '$PSScriptRoot = (Resolve-Path tools).Path; [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\testdata\nas-snapshot.json"))'
```
Expected: `...\ConnectionWatcher\testdata\nas-snapshot.json`.

- [ ] **Step 4: Build, test, commit**

Build and test per Global Constraints (`RealDataSnapshotTests` still runs or skips exactly as before). `git status --short` shows the four `.idea` deletions, the move, and the three edits.
```bash
git commit -m "chore: move Get-NasSnapshot to tools/, untrack .idea, drop the empty Examples folder item"
```

---

## Group B: dead code

### Task 2: X1, engine, infrastructure and CLI dead members

**Files:**
- Modify: `Core/ConEngine/Tracking/CircularBuffer.cs` (delete `Capacity`, `ToArray`)
- Modify: `Core/ConEngine/Network/PingController.cs` (delete `public Task? Task => _pingTask;`)
- Modify: `Core/ConEngine/Tracking/HostAnalysisEngine.cs` (delete `LastUpdated`; `NumSuccess`/`NumFailed` become locals of `Recalculate`, and the two other reset statements drop them)
- Modify: `Libraries/ConInfrastructure/Traceroute/IcmpTtlProbe.cs` (delete the `IPStatus.TimedOut` arm, identical to `_`)
- Modify: `Programs/ConWatchCli/CliApp.cs` (delete the `catch (PlatformNotSupportedException)` in `Guarded`)
- Test: `Tests/ConWatchCli.Tests/GuardedTests.cs`

**Interfaces:** none produced. Plan 2 (D10 `IcmpEcho`) may have rewritten `IcmpTtlProbe`; if its switch is gone, skip that bullet.

- [ ] **Step 1: Prove each member is dead**

```powershell
rg -n --type cs "\.Capacity\b|\.ToArray\(\)" Core/ConEngine Libraries Programs Tests | rg -i "buffer"
rg -n --type cs "\bLastUpdated\b|\bNumSuccess\b|\bNumFailed\b" Core Libraries Programs Tests
rg -n --type cs "\.Task\b" Core/ConEngine Programs Tests | rg -i "controller"
```
Expected: the first and third print nothing; the second prints only lines inside `HostAnalysisEngine.cs`.

- [ ] **Step 2: Pin `Guarded`'s mapping for `PlatformNotSupportedException`**

```csharp
[Fact]
public async Task Platform_not_supported_maps_to_service_control_failed()
{
    // build CliServices the way the other cases in this file do
    var code = await CliApp.Guarded(s, () => throw new PlatformNotSupportedException("no services here"));
    Assert.Equal(ExitCodes.ServiceControlFailed, code);
    Assert.Contains("no services here", err.ToString());
}
```
Run `dotnet test Tests/ConWatchCli.Tests -c Debug --filter GuardedTests`. Expected: PASS (it pins today's behavior).

- [ ] **Step 3: Delete the members listed under Files**

- [ ] **Step 4: Build, test, commit**

Full build and test. Expected: `HostAnalysisEngineTests`, `GuardedTests`, `TracerouteRunnerTests` pass.
```bash
git commit -m "refactor: delete dead engine members, the duplicate TimedOut arm and the redundant Guarded catch"
```

---

### Task 3: X2, persistence dead members and parameters

**Files:**
- Modify: `Libraries/ConPersistence/Export/CsvExport.cs` (delete `GlitchLines`; `TransitionLines`/`RollupLines` are handled in Task 6)
- Modify: `Libraries/ConPersistence/Recording/RollupMerge.cs` (`ToRow(RollupBucket bucket)`: drop the `resolution` parameter; `Resolution = RollupResolution.Minute`)
- Modify: `Libraries/ConPersistence/Config/ConfigMapping.cs` (`ToDto(this PingProfile p)`: drop `Guid? id = null`; the `Id = id` initializer goes, `ProfileDto.Id` defaults to null)
- Modify: `Libraries/ConPersistence/Discoveries/DiscoveryStore.cs` (delete `public const int MaxBatch`; uses become `DiscoveryLimits.MaxBatch`)
- Modify callers: `Programs/ConWatch/Discoveries/DiscoverySearchService.cs`, `Tests/ConPersistence.Tests/DiscoveryStoreTests.cs`, `Tests/ConWatch.Tests/DiscoverySearchServiceTests.cs`

**Interfaces:** `RollupMerge.ToRow(RollupBucket)`, `ConfigMapping.ToDto(this PingProfile)`.

- [ ] **Step 1: Prove each is unused**

```powershell
rg -n --type cs "GlitchLines|ToRow\(bucket," Core Libraries Programs Tests
rg -n --type cs "ToDto\([^)]" Libraries/ConPersistence Programs Tests | rg -v "ToDto\(\)"
```
Expected: only the declarations print.

- [ ] **Step 2: Make the edits; the existing tests are the pin**

`CsvExportTests`, `RecorderCollectorTests`, `ProfileMappingTests`, `DiscoveryStoreTests`, `DiscoverySearchServiceTests` cover every touched path.

- [ ] **Step 3: Build, test, commit**

```bash
git commit -m "refactor: drop never-passed parameters, GlitchLines and the MaxBatch alias"
```

---

### Task 4: X3, host and tray dead code

**Files:**
- Delete: `Programs/ConWatch/Pages/Setup.cshtml.cs`, `Programs/ConWatch/Pages/Traceroute.cshtml.cs`; remove the `@model` line from `Setup.cshtml` and `Traceroute.cshtml`
- Create: `Programs/ConWatch/Pages/SettingsPageModel.cs`
- Modify: `Programs/ConWatch/Pages/Data.cshtml.cs`, `Settings.cshtml.cs`
- Modify: `Programs/ConWatch/Clone/CloneService.cs` (delete `WithoutLocalDevices`, `WithoutSystemManaged`; call `SnapshotFilter` directly)
- Modify: `Libraries/ConPersistence/Snapshots/SnapshotFilter.cs` (add `WithoutLocalDevices`)
- Move: `Tests/ConWatch.Tests/CloneWithoutLocalDevicesTests.cs`, `CloneWithoutSystemManagedTests.cs` → `Tests/ConPersistence.Tests/` (renamed `SnapshotFilterLocalDevicesTests`, `SnapshotFilterSystemManagedTests`; namespace and calls updated)
- Modify: `Programs/ConWatchTray/ScoutPasswordStore.cs` (constructor takes no arguments; uses `ConfigPaths.Default()`)
- Modify: `Programs/ConWatch/Auth/AdminLockPageFilter.cs` (doc comment)

**Interfaces:**
- Produces: `public abstract class SettingsPageModel(SettingsStore settingsStore) : PageModel` with `Dictionary<string, string> Settings { get; private set; }`, `Task OnGetAsync()`, `string Setting(string key, string fallback = "")`. `DataModel(SettingsStore s) : SettingsPageModel(s)` has no body; `SettingsModel(SettingsStore s, AdminGate gate) : SettingsPageModel(s)` keeps `PasswordSet`, `EngineEnabled`, `CloneSource` exactly as plan 3 left them.
- Produces: `public static Snapshot SnapshotFilter.WithoutLocalDevices(Snapshot snapshot)`, carrying the doc comment from `CloneService.WithoutLocalDevices`.

- [ ] **Step 1: Run the pins before any change**

`dotnet test Tests/ConWatch.Tests -c Debug --filter "AdminLockTests|PageSmokeTests|CloneWithout"`. Expected: PASS. (`AdminLockTests` loops over `/Setup` and `/Traceroute`.)

- [ ] **Step 2: Delete the empty page models and run `AdminLockTests` again**

Expected: PASS. If a locked `/Setup` or `/Traceroute` no longer redirects, restore the two models, note it in the commit message and continue.

- [ ] **Step 3: Add `SettingsPageModel` and rebase `DataModel`/`SettingsModel` on it**

- [ ] **Step 4: Move the clone filters into `SnapshotFilter` and the two test classes to ConPersistence.Tests**

Plan 3 (D13) may have merged the clone and take-over prep in `CloneService`; replace whatever calls the pass-throughs there.

- [ ] **Step 5: `ScoutPasswordStore()` and the filter comment**

`AdminLockPageFilter`'s summary should say: applied by convention in `ConWatchServices` to every page under `/Config` plus Settings, Data, Setup and Traceroute.

- [ ] **Step 6: Build, test, commit**

The tray builds only on Windows; `dotnet build ConnectionWatcher.sln` covers it here.
```bash
git commit -m "refactor: share one settings page-model base, drop empty page models and clone filter pass-throughs"
```

---

### Task 5: X4, client dead code and CSS

**Files (under `Programs/ConWatch/`):**
- Modify: `ClientApp/src/components/marks.tsx` (delete `export { RAMP };`)
- Modify: `ClientApp/src/components/TimeChart.tsx` (delete `lossAxis`, `ChartSeries.secondaryAxisId` and the `loss` axis branch; `secondaryAxes` keeps the line axis)
- Modify: `ClientApp/src/components/Tree.tsx` (`Branch` loses its `targets` prop; the call site in `Tree` too)
- Modify: `ClientApp/src/components/TargetRow.tsx` (`RowParts` loses `successRate` and `led`; both always render)
- Modify: `ClientApp/src/components/RibbonBoard.tsx` (`orderRows` no longer exported)
- Modify: `ClientApp/src/components/NotesPanel.tsx` + `NotesPanel.test.tsx` (delete `unacknowledgedCount` and its test case)
- Modify: `ClientApp/src/lib/scope.ts` (`lan.ts` before plan 4) + its test (delete `LanSummary.down`)
- Modify: `wwwroot/css/site.css` (drop `.cw-mono` from the mono selector list; delete `.verdict-banner.siteincident` and `.verdict-banner.internetweather`)
- Create: `ClientApp/src/lib/siteCss.test.ts`

**Interfaces:** none.

- [ ] **Step 1: Re-prove each item against the post-plan-4 tree**

```powershell
cd Programs/ConWatch/ClientApp
rg -n "lossAxis|secondaryAxisId|export \{ RAMP|unacknowledgedCount|orderRows" src
rg -n "\.down\b" src
rg -n "cw-mono|siteincident|internetweather" src ../Pages ../wwwroot/lib
```
For the banner classes, find what the slab function returns now (plan 4 C3 moved the verdict tables to `lib/verdicts.ts`): delete a `.verdict-banner.<x>` rule only if `<x>` is not among its outputs. Anything still in use: leave it and note it in the commit message.

- [ ] **Step 2: Write the dark-token guard**

```ts
import { readFileSync } from "fs";
import { resolve } from "path";
import { describe, expect, it } from "vitest";

describe("site.css", () => {
  it("keeps the explicit and the OS-preference dark token blocks identical", () => {
    const css = readFileSync(resolve(__dirname, "../../../wwwroot/css/site.css"), "utf8");
    const explicit = /\[data-theme="dark"\]\s*\{([^}]*)\}/.exec(css)![1];
    const os = /:root:not\(\[data-theme="light"\]\):not\(\[data-theme="dark"\]\)\s*\{([^}]*)\}/.exec(css)![1];
    const norm = (s: string) => s.split(";").map((d) => d.trim()).filter(Boolean);
    expect(norm(os)).toEqual(norm(explicit));
  });
});
```
Run `npx vitest run src/lib/siteCss.test.ts`. Expected: PASS. Add a comment above the media-query block: `/* Same tokens as [data-theme="dark"] above; siteCss.test.ts fails if they drift. */`

- [ ] **Step 3: Make the deletions**

- [ ] **Step 4: Verify and commit**

`npx tsc --noEmit`, `npx vitest run`, `npm run build` (from ClientApp). Expected: clean, all pass, build succeeds.
```bash
git commit -m "refactor(client): delete dead exports, props and CSS; guard the duplicated dark tokens"
```

---

## Group C: test-only members (ruling 5)

### Task 6: X5, internal + InternalsVisibleTo

**Files:**
- Modify: `Core/ConEngine/ConEngine.csproj` — add `<InternalsVisibleTo Include="ConEngine.Tests" />`, `"ConPersistence.Tests"`, `"ConWatch.Tests"` (same `ItemGroup` style as `ConInfrastructure.csproj`)
- Modify: `Libraries/ConPersistence/ConPersistence.csproj` — `"ConPersistence.Tests"`, `"ConWatch.Tests"`
- Modify: the members in the table below
- Move: `Core/ConEngine/Scheduling/FixedIntervalCadence.cs` → `Tests/ConEngine.Tests/Fakes/FixedIntervalCadence.cs` (namespace `ConEngine.Tests.Fakes`; its three test files gain the `using`)

| Member | Today used by | Becomes |
|---|---|---|
| `WatcherEngine.IsLocalSubject` | tests only | `internal` |
| `LossWindows.Open` | tests only | `internal` |
| `LossAttribution.HopLoss.ProbeOnly` | tests only | `internal` |
| `TargetGraph.GetByName`, `GetPoolByName`, `GetChildren` | tests only (ConEngine, ConPersistence, ConWatch test projects) | `internal` |
| `VerdictCoordinator.CurrentDetail` | tests only | `internal` |
| `FixedIntervalCadence` | tests only | moved into the test project |
| `DnsMessage.TryReadQuestionName` | tests + `FakeDnsServer` | `internal` (IVT exists) |
| `OuiTable.Count` | tests only | `internal` (IVT exists) |
| `CsvExport.TransitionLines`, `RollupLines` | tests only | `internal` |
| `SnapshotImporter.ImportToFileAsync` | `ConWatch.Tests` only | `internal` |
| `WatcherHostService.EngineRunning` | tests only | `internal` (IVT exists) |
| `AdminGate.Verify` | own class + tests | `internal` |
| `DnsLookupName.IsValidName` | own file | `private` |
| `TraySettings.OverrideUrl`, `HasOverride` | own class | `private` |
| `SystemResolverReconciler.ProfileName`, `NameFor`; `SetupService.PoolName`; `SystemResolverService.Interval` | own class | `private` (skip any plan 2 D12 already moved or made shared) |
| `DnsLookupName.TryFromJson(json, out names)` | re-wraps the 3-argument overload | deleted; tests use `out _` for `badName` |
| `TracerouteRunner.RunAsync(host, ct)` | re-wraps `RunAsync(host, lookupOperators: false, ct)` | deleted; tests pass `lookupOperators: false` |
| `ITrayAutostart.IsInstalled` | tests, through the fake | removed from the interface, `WindowsTrayAutostart` and the `file` class in CLI `Program.cs`; the fake in `Tests/ConWatchCli.Tests/Fakes.cs` keeps a plain `IsInstalled` property |

**Interfaces:** none produced. Tests that read these members keep compiling through `InternalsVisibleTo`.

- [ ] **Step 1: Re-prove "test-only" for each row**

For each member: `rg -n --type cs "\bName\b" Core Libraries Programs` must show only its declaration (plus same-class uses for the `private` rows). A row that now has a product caller (plans 1–4 may have added one) stays public; note it in the commit message.

- [ ] **Step 2: Add the InternalsVisibleTo lines, then change visibility row by row**

- [ ] **Step 3: Build and test**

Expected: no new warnings (a `private` member nothing uses would warn: then it is dead, delete it), 0 failed.

- [ ] **Step 4: Commit**

```bash
git commit -m "refactor: test-only members become internal behind InternalsVisibleTo; delete re-wrapping overloads"
```

---

## Group D: misplaced code

### Task 7: O1, shared C# types get their own files

**Files (moves of declarations; namespaces unchanged):**

| Type(s) | From | To |
|---|---|---|
| `VerdictState`, `DnsBlameKind`, `DnsBlame` | `Core/ConEngine/Topology/VerdictCoordinator.cs` | `Core/ConEngine/Topology/VerdictTypes.cs` |
| `ModelWarningKind`, `ModelCondition`, `ModelConditionChange` | `Core/ConEngine/Topology/ModelCheckCoordinator.cs` | `Core/ConEngine/Topology/ModelConditions.cs` |
| `SettingKeys` | `Libraries/ConPersistence/Settings/SettingsStore.cs` | `Libraries/ConPersistence/Settings/SettingKeys.cs` |
| `ConfigResult`, `ConfigErrorKind` | `Libraries/ConPersistence/Config/ConfigService.cs` | `Libraries/ConPersistence/Config/ConfigResult.cs` |
| `DiscoveryStatuses` | `Libraries/ConPersistence/Discoveries/DiscoveryStore.cs` | `Libraries/ConPersistence/Discoveries/DiscoveryStatuses.cs` |

**Interfaces:** unchanged type names and namespaces; no caller edits.

- [ ] **Step 1: Check what plan 2 did.** If D1 already put `VerdictState` (with `VerdictStateExtensions`) in its own file, or plan 3 moved `SettingKeys`, skip those rows.
- [ ] **Step 2: Cut each declaration (with its doc comment and `[JsonConverter]` attributes) into the new file.** No other text changes.
- [ ] **Step 3: Build, test, commit**

```bash
git commit -m "refactor: shared engine and persistence types move out of the classes that happened to declare them"
```

---

### Task 8: O1, client contract types — done in R4

R4 Task 5 moves `TargetScope`, `GlitchItem` and `InitialData` into `types.ts` and closes the contracts AUDIT line. Nothing to do here; if R4 left any of them behind, move it as part of Task 7's commit.

---

### Task 9: O2, pool stagger into `Scheduling/PoolStagger`

**Files:**
- Create: `Core/ConEngine/Scheduling/PoolStagger.cs`
- Modify: `Core/ConEngine/WatcherEngine.cs` (delete `PoolPositions`; `StartAsync` calls `PoolStagger`)
- Modify: `Core/ConEngine/Topology/PoolCoordinator.cs` (delete `PhaseOffset`)
- Modify: `Tests/ConEngine.Tests/PoolStaggerTests.cs` (call `PoolStagger.Positions`); move the `PhaseOffset` asserts from `PoolCoordinatorTests` into it as `Phase_offset_spreads_members_across_the_interval`

**Interfaces:**
- Produces: `public static class PoolStagger` (namespace `ConEngine.Scheduling`) with
  - `public static Dictionary<Guid, (int Index, int Count)> Positions(IEnumerable<PoolDefinition> pools)` (body and doc from `WatcherEngine.PoolPositions`)
  - `public static TimeSpan PhaseOffset(int memberIndex, int memberCount, TimeSpan interval)` (from `PoolCoordinator`)

- [ ] **Step 1: Run the pins.** `dotnet test Tests/ConEngine.Tests -c Debug --filter "PoolStaggerTests|PoolCoordinatorTests"`. Expected: PASS.
- [ ] **Step 2: Create `PoolStagger`, retarget the tests, delete the two old members.**
- [ ] **Step 3: Build, test, commit**

```bash
git commit -m "refactor(engine): pool phase staggering lives in Scheduling/PoolStagger"
```

---

### Task 10: O2, hostname follower out of `WatcherEngine`

**Files:**
- Create: `Core/ConEngine/Network/HostnameFollower.cs`
- Modify: `Core/ConEngine/WatcherEngine.cs` (delete `_dnsRefreshRequests` and `ResolveAndActivateAsync`; keep `Activate`, `RepointAsync`, `StartController`)
- Test: existing `Tests/ConEngine.Tests/DnsRefreshTests.cs` (five tests through `WatcherEngine`'s `dnsRefreshInterval` parameter; unchanged)

**Interfaces:**
- Produces: `internal sealed class HostnameFollower` (namespace `ConEngine.Network`):
  - `HostnameFollower(IDomainNameResolverFactory resolverFactory, TimeProvider time, TimeSpan refreshInterval, ILogger log, Action<TargetMonitor, IPAddress> activate, Func<TargetMonitor, IPAddress, Task> repoint)`
  - `Task FollowAsync(TargetMonitor monitor, string hostname, CancellationToken ct)`: the body of today's `ResolveAndActivateAsync`, with its doc comment, calling `activate`/`repoint` where it called `Activate`/`RepointAsync`
  - `void RequestRefresh(Guid targetId)`: completes the waiting request for that target, if any (today's `TrySetResult` in the Down-transition subscription)
- `WatcherEngine` keeps its constructor signature (`dnsRefreshInterval` parameter and the 5-minute default). It builds one follower per `StartAsync`; the Down-transition subscription calls `RequestRefresh`, and the activation loop calls `Task.Run(() => follower.FollowAsync(...), ct)`.

- [ ] **Step 1: Run the pin.** `dotnet test Tests/ConEngine.Tests -c Debug --filter DnsRefreshTests`. Expected: PASS.
- [ ] **Step 2: Move the loop and the request map into `HostnameFollower`.** The ordering comment ("Adopt the answer only once the call it feeds has returned") moves with the code, unchanged.
- [ ] **Step 3: Run `DnsRefreshTests` three times.** Expected: PASS each time (timing-sensitive; a single pass is not enough).
- [ ] **Step 4: Full build, test, commit**

```bash
git commit -m "refactor(engine): the hostname follower leaves WatcherEngine for Network/HostnameFollower"
```

---

### Task 11: O3, host `Health/` folder and the warning page links

**Files:**
- Move (namespace becomes `ConWatch.Health`): `Programs/ConWatch/EngineHealth.cs`, `RuntimeHealth.cs`, `Clone/CloneHealth.cs` → `Programs/ConWatch/Health/`; plan 3's `HealthReport` too, if it is not there already
- Create: `Programs/ConWatch/Health/HealthWarningLinks.cs`
- Modify: `Libraries/ConPersistence/Config/ConfigHealth.cs` (delete `HealthWarning.Page`, `PageName`; the record becomes `public record HealthWarning(string Code, string Message);`)
- Modify: `Programs/ConWatch/Api/SettingsEndpoints.cs` (or plan 3's `HealthReport`): `/config/health` projects each warning to `new { w.Code, w.Message, Page = w.Page(), PageName = w.PageName() }`
- Modify: `Pages/Shared/_HealthBanner.cshtml`, `Pages/Index.cshtml`, `Pages/Index.cshtml.cs` (`.Page` → `.Page()`, `.PageName` → `.PageName()`, `@using ConWatch.Health`)
- Move test: `Tests/ConPersistence.Tests/HealthWarningPageTests.cs` → `Tests/ConWatch.Tests/HealthWarningPageTests.cs` (calls `w.Page()`/`w.PageName()`)

**Interfaces:**
- Produces: `public static class HealthWarningLinks` with `public static string Page(this HealthWarning w)` and `public static string PageName(this HealthWarning w)`; bodies are today's two property bodies.

- [ ] **Step 1: Run the pins.** `DnsPageTests` (asserts JSON `page`/`pageName`) and `HealthWarningPageTests`. Expected: PASS.
- [ ] **Step 2: `git mv` the three health files; fix namespaces and `using`s.**
- [ ] **Step 3: Move the page mapping into `HealthWarningLinks` and update the five call sites.**
- [ ] **Step 4: Build, test, commit**

Expected: `DnsPageTests` and the moved `HealthWarningPageTests` pass; JSON key order is unchanged.
```bash
git commit -m "refactor(host): health code in Health/, and the warning-to-page mapping leaves persistence"
```

---

### Task 12: O3, policy and registrations to their owners

**Files:**
- Modify: `Core/ConCore/ProbeSpec.cs` (`ProbePolicy` gains `CandidatePorts`)
- Modify: `Programs/ConWatch/Api/ProbeTestEndpoint.cs` (calls it)
- Modify: `Libraries/ConPersistence/Config/ConfigStore.cs` (gains `HasUserTargetsAsync`)
- Modify: `Programs/ConWatch/Pages/Index.cshtml.cs` (calls it instead of the inline EF query)
- Modify: `Programs/ConWatch/Startup/ConWatchServices.cs`, `Programs/ConWatch/ConWatchApp.cs` (new `AddConWatchDiscovery()` between `AddConWatchPersistence()` and `AddConWatchClone()`)
- Test: `Tests/ConEngine.Tests/ProbePolicyTests.cs` (new or existing file), `Tests/ConPersistence.Tests/ConfigStoreTests.cs`, `Tests/ConWatch.Tests/ServiceRegistrationTests.cs` (new)

**Interfaces:**
- Produces: `public static IReadOnlyList<int> ProbePolicy.CandidatePorts(string address, int? requestedPort)`. Private address → `80, 443, 8080, 8443`; public → `PublicPorts`; then `requestedPort` when `IsAllowed(address, new ProbeSpec(ProbeKind.Tcp, p), out _)`; distinct, order kept.
- Produces: `public async Task<bool> ConfigStore.HasUserTargetsAsync()`, true when any target is not `SystemManaged`.
- Produces: `public static IServiceCollection AddConWatchDiscovery(this IServiceCollection services)` (registrations per the Decisions table).

- [ ] **Step 1: Write the pins**

```csharp
[Fact]
public void Candidate_ports_follow_the_politeness_policy()
{
    Assert.Equal([80, 443, 8080, 8443], ProbePolicy.CandidatePorts("192.168.1.1", null));
    Assert.Equal([80, 443, 8080, 8443, 22], ProbePolicy.CandidatePorts("192.168.1.1", 22));
    Assert.Equal([80, 443, 53], ProbePolicy.CandidatePorts("8.8.8.8", 8080));
    Assert.Equal([80, 443, 53], ProbePolicy.CandidatePorts("8.8.8.8", 443));
}

[Fact]
public async Task Has_user_targets_ignores_system_managed_ones() // ConfigStoreTests
{
    // empty → false; one SystemManaged target → false; plus one user target → true
}

[Fact]
public void Discovery_and_dns_services_resolve() // ServiceRegistrationTests, via ConWatchFactory.Services
{
    // GetRequiredService for IGatewayDiscovery, IWanAddressProbe, DiscoveryStore, LanScanner,
    // IDiscoveryScanner, DiscoverySearchService, IDohDetector: none throws
}
```
`Candidate_ports_follow_the_politeness_policy` fails to compile until Step 2; the other two pass now. `ProbeTestEndpointTests` and `FirstRunWithResolverTests` already pin the endpoint and the redirect.

- [ ] **Step 2: Add `CandidatePorts` and `HasUserTargetsAsync`; switch the endpoint and `Index` to them.**
- [ ] **Step 3: Move the registrations into `AddConWatchDiscovery`; the DoH detector moves into `AddConWatchEngine`.**
- [ ] **Step 4: Build, test, commit**

```bash
git commit -m "refactor(host): probe-port candidates in ProbePolicy, first-boot check in ConfigStore, AddConWatchDiscovery"
```

---

### Task 13: O4, folders match namespaces; probes together; Builders split

**Files:**

| From | To | Namespace |
|---|---|---|
| `Programs/ConWatch/Dns/*.cs` | `Programs/ConWatch/NameResolution/` | `ConWatch.NameResolution` (unchanged) |
| `Libraries/ConInfrastructure/Dns/{DnsLookupNames,DnsMessage,DohConfiguration,SystemResolverSource}.cs` | `Libraries/ConInfrastructure/NameResolution/` | `ConInfrastructure.NameResolution` (unchanged) |
| `Libraries/ConInfrastructure/{DomainNameResolverDns,DomainNameResolverFactory}.cs` | `Libraries/ConInfrastructure/NameResolution/` | `ConInfrastructure` → `ConInfrastructure.NameResolution` |
| `Libraries/ConInfrastructure/Dns/DnsQueryTransceiver.cs` | `Libraries/ConInfrastructure/Probes/` | → `ConInfrastructure.Probes` |
| `Libraries/ConInfrastructure/{PingTransceiverIcmp,PingTransceiverFactoryIcmp,TcpConnectTransceiver}.cs` | `Libraries/ConInfrastructure/Probes/` | `ConInfrastructure` → `ConInfrastructure.Probes` |
| `Libraries/ConNotify/Builders.cs` | `Libraries/ConNotify/MqttPayloads.cs` (`Slugger`, `StateMapping`, `DnsResolverEntry`, `DnsPublication`, `MqttTopics`, `HaDiscovery`) + `Libraries/ConNotify/WebhookPayload.cs` (`WebhookPayload`) | `ConNotify` (unchanged) |

Test files named after the moved classes stay where they are; they only gain `using` lines. If `ConInfrastructure.csproj` embeds a resource by path under `Dns/`, update that path.

**Interfaces:** type names unchanged; `ConInfrastructure.Probes` and `ConInfrastructure.NameResolution` gain the types listed.

- [ ] **Step 1: `git mv` per the table** (`Builders.cs`: `git mv` to `MqttPayloads.cs`, then cut `WebhookPayload` into its new file).
- [ ] **Step 2: Fix namespaces and `using`s** (`ConWatchServices.cs` names several of these types by fully qualified name).
- [ ] **Step 3: Re-run the scan; it must print nothing but `ConWatchServices.cs` (see Decisions)**

```powershell
Get-ChildItem Core,Libraries,Programs -Recurse -Filter *.cs | Where-Object FullName -notmatch '\\(obj|bin|Migrations)\\' | ForEach-Object {
  $ns = (Select-String -Path $_.FullName -Pattern '^namespace ([\w.]+)' | Select-Object -First 1).Matches.Groups[1].Value
  $rel = [IO.Path]::GetRelativePath((Get-Location), $_.DirectoryName).Split([IO.Path]::DirectorySeparatorChar)
  $expected = ($rel | Select-Object -Skip 1) -join '.'
  if ($ns -and $ns -ne $expected) { "$($_.FullName): $ns (folder $expected)" } }
```
Expected after Task 16: the `Verdict/` and `Diagnostics/` files are the sanctioned exception as well.

- [ ] **Step 4: Build, test, commit**

```bash
git commit -m "refactor: folders follow their namespaces; transceivers in Probes/; split ConNotify Builders.cs"
```

---

### Task 14: O5, `ScoutClient` into `Tray/` (ruling 6)

**Files:**
- Move: `Libraries/ConInfrastructure/Discovery/ScoutClient.cs` → `Libraries/ConInfrastructure/Tray/ScoutClient.cs`, namespace `ConInfrastructure.Discovery` → `ConInfrastructure.Tray` (`ScoutItem`, `ScoutFailure`, `ScoutResult`, `ScoutClient` move with it)
- Create: `Libraries/ConInfrastructure/Tray/README.md`:

```markdown
# Tray logic

Everything here is used only by `Programs/ConWatchTray`. It lives in this library, not in the
WinForms project, so it builds and is tested on Linux CI (`Tests/ConWatch.Tests`); the tray project
itself holds only the Windows UI shell.
```
- Modify: `Libraries/ConInfrastructure/Tray/ScoutSession.cs`, `Programs/ConWatchTray/Program.cs`, `ScoutRunner.cs`, `Tests/ConWatch.Tests/ScoutClientTests.cs`, `ScoutSessionTests.cs` (`using` lines)

**Interfaces:** type names unchanged; namespace `ConInfrastructure.Tray`.

- [ ] **Step 1: `git mv`, fix the namespace and `using`s, add the README.**
- [ ] **Step 2: Build and test.** Expected: `ScoutClientTests`, `ScoutSessionTests` pass; the tray builds.
- [ ] **Step 3: Commit**

```bash
git commit -m "refactor(tray): ScoutClient joins the rest of the tray logic in ConInfrastructure/Tray"
```

---

### Task 15: O6, `IEngineObserver` only on recorders that honor it

**Files:**
- Create: `Libraries/ConPersistence/Recording/EngineChannelRecorder.cs`
- Modify: `Libraries/ConPersistence/Recording/ChannelRecorder.cs` (drops `IEngineObserver`, `SourceOf` and `Attach(WatcherEngine)`; keeps `Attach(IObservable<TEvent>)` and `Detach()`)
- Modify: `Libraries/ConPersistence/Recording/TransitionRecorder.cs`, `GlitchRecorder.cs` (derive from `EngineChannelRecorder<TEvent>`)
- Modify: `Libraries/ConPersistence/Warnings/ModelWarningRecorder.cs` (delete the throwing `SourceOf`)
- Test: `Tests/ConPersistence.Tests/RecorderCollectorTests.cs` (or the existing recorder test file)

**Interfaces:**
- Produces: `public abstract class EngineChannelRecorder<TEvent> : ChannelRecorder<TEvent>, IEngineObserver` with `protected abstract IObservable<TEvent> SourceOf(WatcherEngine engine);` and `public void Attach(WatcherEngine engine) => Attach(SourceOf(engine));`. The constructor forwards `(ILogger log, int capacity = 1000, int batchSize = 500, TimeSpan? retryDelay = null)`.
- If plan 2's `SubscriptionSlot` now holds `ChannelRecorder`'s subscription, it stays in `ChannelRecorder`.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public void Only_engine_fed_recorders_are_engine_observers()
{
    Assert.True(typeof(IEngineObserver).IsAssignableFrom(typeof(TransitionRecorder)));
    Assert.True(typeof(IEngineObserver).IsAssignableFrom(typeof(GlitchRecorder)));
    Assert.False(typeof(IEngineObserver).IsAssignableFrom(typeof(ModelWarningRecorder)));
}
```
Run it. Expected: FAIL on the last assert.

- [ ] **Step 2: Introduce `EngineChannelRecorder` and rebase the three recorders.** `ConWatchServices`' `IEngineObserver` registrations are unchanged (they never included `ModelWarningRecorder`).
- [ ] **Step 3: Build and test.** Expected: PASS; `ObserverDetachTests` and the recorder tests pass.
- [ ] **Step 4: Commit**

```bash
git commit -m "refactor(persistence): only engine-fed recorders implement IEngineObserver"
```

---

## Group E: `Topology/` folders (ruling 7)

### Task 16: O8, split `Core/ConEngine/Topology/` into three folders, one namespace

**Files (`git mv` only; every file keeps `namespace ConEngine.Topology`):**

| Folder | Files |
|---|---|
| `Core/ConEngine/Topology/` (stays) | `TargetGraph.cs`, `TopologyCoordinator.cs`, `PoolCoordinator.cs` |
| `Core/ConEngine/Verdict/` | `VerdictCoordinator.cs`, `VerdictTypes.cs` (Task 7) |
| `Core/ConEngine/Diagnostics/` | `ModelCheckCoordinator.cs`, `ModelConditions.cs` (Task 7), `SuspicionCoordinator.cs`, `LossWindows.cs`, `LossAttribution.cs` |

Any file plans 1–4 added under `Topology/` goes by the same rule: graph/pools stay in `Topology/`, the verdict's code goes to `Verdict/`, model checks, suspicion and loss statistics go to `Diagnostics/`.

- Modify: `README.md`, Development table, the `Core/ConEngine` row gains: `` `Topology/`, `Verdict/` and `Diagnostics/` share `namespace ConEngine.Topology` by design (one namespace, three folders). ``

**Interfaces:** none (no namespace change).

- [ ] **Step 1: `git mv` per the table; add the README sentence.**
- [ ] **Step 2: Build and test.** Expected: no source edits needed beyond the README; 0 failed.
- [ ] **Step 3: Re-run Task 13's scan.** Expected: `ConWatchServices.cs` plus the `Verdict/` and `Diagnostics/` files, nothing else.
- [ ] **Step 4: Commit**

```bash
git commit -m "refactor(engine): split Topology/ into Topology, Verdict and Diagnostics folders (one namespace)"
```

---

## Owner questions (not planned)

- **X6:** `DomainResolutionResult.AddressCount`, `FirstAddress`, `IPv6Addresses`, `ToDetailedString` are still unused. You kept them in September (decision 6); the survey asks whether to revisit. No task until you rule.
- **`.vscode/`:** untracked in the working tree today. Ignore it like `.idea/`, commit shared settings from it, or leave it as is?

## Wire changes

None.
