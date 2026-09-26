# Plan B: structure and dedupe Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Carry out review items S2, S3, S4, S6, S7, S8, S9, S11 and the section-3 dedupe of the 2026-09-18 once-over as behavior-preserving refactors on branch `plan-b`, each task reviewable and revertable on its own.

**Architecture:** Five groups in order. A: mechanical moves and deletions (stray folders, persistence folders/namespaces, dead members, parallel gateway probe). B: one home for each duplicated shape (target/profile mapping, the transition DTO, the error body, the status snapshot, streaming exports, pinned snapshot and migration shapes). C: policy out of the host (settings defaults, retention windows, warm-start seeder) and atomic writes (snapshot import on the DI config service, one-transaction setup wizard). D: one trunk computation and a pure host tracker. E (optional): coordinator outbox and one-snapshot config reads. Every task pins the current behavior with a test before it changes code; the few deliberate wire changes are pinned by tests that fail first and are listed under "Wire changes".

**Tech Stack:** .NET 10 (SDK 10.0.401), ASP.NET Core minimal APIs + Razor Pages + SignalR, EF Core 10.0.11 on Microsoft.Data.Sqlite, System.Reactive 6.1.0, ModelContextProtocol.AspNetCore 2.2.0, xUnit 2.9.3, Microsoft.Extensions.TimeProvider.Testing (already referenced by ConEngine.Tests and ConPersistence.Tests), vitest (ClientApp, untouched).

**Spec:** `docs/superpowers/reviews/2026-09-18-codebase-review.md` (sections S2–S11 except S1, S5, S10; the whole of section 3; section 5 decisions, in particular decision 3: the trunk is the UNION of every anchor's ancestor chain, computed in one place, `TopologyNames`). Open items come from `docs/superpowers/plans/2026-09-18-review-fixes-followups.md`.

## Global Constraints

- Never push. Commit only on branch `plan-b` (head `78bf8e8` at plan time). Nothing in this plan merges into `master`.
- Behavior-preserving: every public HTTP/JSON shape (REST under `/api/v1`, the SignalR `statusChanged`/`transition` messages, MCP tool output, the snapshot document), every MQTT topic and payload, the webhook payload, every Prometheus metric name and label, and the database schema stay as they are, unless a task says otherwise and pins the new shape with a test that fails before the change. All such changes are collected in "Wire changes (release-note lines)" at the end.
- No EF model change and no migration in any task. Task 5's test enforces this from then on.
- After every task: `dotnet build ConnectionWatcher.sln -c Debug` ends with `0 Warning(s)` and `0 Error(s)`; `dotnet test ConnectionWatcher.sln -c Debug` has 0 failed (555 passed at plan time; the count only grows); from `Programs/ConWatch/ClientApp`, `npx vitest run` passes 56.
- No new NuGet or npm packages, no new projects, no `InternalsVisibleTo`, no `global using`.
- Conventional commit prefixes (`refactor:`, `test:`, `fix:`), no attribution or co-author lines. One commit per task (Task 1 has nothing to commit).
- Do not touch `TargetMonitor`'s outbox, `_lock`/`_publishLock` ordering, or drain, except exactly as Task 19 states (it removes the per-sample status publication and nothing else).
- Run commands from the repo root in PowerShell (the default shell). Paths below use forward slashes; both shells accept them.
- UTC end to end: every timestamp a task builds or converts is tagged UTC explicitly (`DateTimeKind.Utc` / offset zero). No `ToLocalTime`, no `DateTime.Now`, no `new DateTimeOffset(dateTime)` on a value whose kind is not known to be UTC.
- Line numbers in the review are from `6e8bc13` and have drifted; locate code by symbol. The snippets in this plan were taken from `78bf8e8`.
- Known pre-existing flaky tests (followups file, not attributed to any branch): `WarmRestartTests.Restart_resumes_states_without_new_transitions`, `SetupApiTests.Apply_is_additive_on_rerun`, `PageSmokeTests.Page_renders(/Admin)`. If exactly one of these fails in a full run, rerun `dotnet test Tests/ConWatch.Tests -c Debug` once; the same test failing twice is a real failure.

## Review Focus

1. A transition row whose `TimestampUtc` has `DateTimeKind.Unspecified` (a row built in memory, or read around the value converter) must serialize as that same UTC instant, never shifted by the server's zone. Test: Task 8, `Row_timestamp_without_a_kind_is_read_as_utc`.
2. An anchor-pool setting whose case differs from the pool's name (`internet` vs `Internet`) must find the pool and give the same roles. Test: Task 18, `Trunk_is_the_union_of_every_anchor_ancestor_chain` (passes `"internet"`).
3. An export over an empty range must still be a CSV header line or `[]`, not an empty body or `null`, once rows stream. Test: Task 11, `Empty_exports_are_a_header_line_or_an_empty_array`.
4. A setup-wizard apply whose later anchor is invalid must leave no router or earlier anchor behind. Test: Task 17, `Apply_is_all_or_nothing`.
5. A snapshot row whose optional columns are null (`detail`, `successRate`, `ancestorId`, `verdict`) must still carry every key, so a consumer can tell "null" from "column gone". Test: Task 10, `Snapshot_json_shape_is_pinned` (its rows leave those columns null).

---

## Decisions made at plan time

Where the review's proposal and the code at `78bf8e8` disagree, or the review left a choice open, this plan decides as follows. Each task repeats the decision it depends on.

| Item | Review said | Found / decided |
|---|---|---|
| S6 | Move `ConfigRows` into `Config/` | Entities keep `ConPersistence.Entities`: the migrations model snapshot records entity CLR names (`"ConPersistence.Entities.TargetRow"`), and renaming the namespace would churn it for nothing. `Config/`, `Settings/`, `Recording/`, `Retention/`, `Queries/`, `Snapshots/` as proposed; `Snapshots` (plural) because a namespace named `Snapshot` would shadow the `Snapshot` type everywhere under `ConPersistence.*`. `Dto/`, `Services/`, `Rollups/` disappear. |
| Transition DTO timestamp | "one timestamp encoding" | `DateTimeOffset` at offset zero. System.Text.Json writes it as `2026-09-18T10:00:00.123+00:00` (verified with a scratch program: a UTC `DateTimeOffset` serializes with `+00:00`, a UTC `DateTime` with `Z`). `GET /api/v1/transitions` and the SignalR `transition` message already emit exactly this, so REST does not change. The webhook/MQTT payload (was a `yyyy-MM-ddTHH:mm:ss.FFFZ` string) and the MCP `get_transitions` rows (were `timestampUtc` with `Z`, `fromState`/`toState`) change; both are pinned in Task 8. |
| Transition DTO name clash | `TransitionDto.From(...)` | A record cannot have both a `From` property and a `From` method. The properties are `FromState`/`ToState` with `[JsonPropertyName("from")]`/`("to")`, so the wire keeps `from`/`to` and the factory keeps the requested name. |
| Error body | `ApiResults.Error(status, code, messages)` | One shape: `{ "errors": [...] }`, plus `"code"` only when non-null. Every existing client reads `body.errors` and, for clone/take-over, `body.code`; every `CloneOutcome` failure carries a code (all come from `CloneOutcome.Fail`), so omitting a null `code` changes no response. The one different shape, `GET /status`'s 503 `{ "error": "..." }`, becomes `{ "errors": ["Engine is not running"] }`: no client reads that body (the tray, `hub.ts` and `site-shell.js` all treat any non-2xx as "no status"; no test reads it). Pinned in Task 9. |
| Snapshot wire format | Explicit records or a shape-pinning test | Shape-pinning test. Records would add three parallel 9–15 field types plus two mappings, the exact duplication section 3 removes; the risk the review names (a column change silently changes the JSON while `SchemaVersion` stays 1) is caught by a test that fails on any key change and tells the author to decide on `SchemaVersion`. |
| Profile defaults | "defaults on `PingProfileConfig` only" | The numbers live only in `PingProfileConfig`. `ProfileDto` and `ProfileRow` keep property initializers, but they read from a private static `PingProfileConfig` instance: `POST /profiles` with a partial body must still get the defaults, which needs initializers on the DTO. |
| Target trimming | Trim consistently; "Name is never trimmed" | `Address` is trimmed in every write path and in `TargetDto.ToConfig()` (the validator then sees the stored value). `Name` stays untrimmed: names are references (`Parent`, pool `Members`), and trimming one side only would break lookups; trimming all of them changes stored configs, which is not a refactor. |
| S7 | Remove the compensating rebuild in `CloneService` if the event now fires | The event does fire once the importer uses the DI `ConfigService` (Task 16 tests it). The explicit call is kept but becomes `RebuildIfStaleAsync()`: it is also what makes the clone/take-over answer only after the node reached its state, which the debounced event rebuild cannot promise. `RebuildIfStaleAsync` supersedes the debounced rebuild for the same write instead of adding a second one (the H4 double rebuild). |
| S8 `RebuildDebounceSeconds` | A host concern, move it | Stays in `PersistenceOptions`: `Persistence:RebuildDebounceSeconds` is a public configuration key (appsettings, environment overrides, the test factory). Its only symptom, `RetentionService` rebuilding the whole options object, goes away with `RetentionWindows`. |
| S8 factory reset | One `SettingsDefaults` for both | Factory reset now seeds the same seven keys as first boot (it seeded four literals and omitted `Retention.TransitionMonths`, `Engine.WarmRestartMaxGapMinutes`, `Verdict.AnchorPool`). Pinned in Task 14. |
| S3 Targets pipeline | Union of ancestor chains everywhere; "the pipeline learns to draw more than one chain" | `TopologyNames` also returns `TrunkChains`: one root-first chain per distinct anchor parent. The Targets page draws one pipeline per chain; wherever all anchors share a parent (the wizard's layout, the tests' home layout) it draws exactly what it draws today. |
| Section 3 order | Migrations test listed last | Moved to the first task of Group B, so every later persistence edit runs under it. |

---

## File structure

| Path | Responsibility after this plan | Tasks |
|---|---|---|
| `Libraries/ConPersistence/Config/` | `ConfigService`, `ConfigStore`, `ConfigHealth`, `ConfigMapping`, `ConfigDtos` (namespace `ConPersistence.Config`) | 2, 6, 7, 16, 17, 21 |
| `Libraries/ConPersistence/Settings/` | `SettingsStore`, `SettingKeys`, new `SettingsDefaults` (`ConPersistence.Settings`) | 2, 14 |
| `Libraries/ConPersistence/Recording/` | `ChannelRecorder`, `TransitionRecorder`, `GlitchRecorder`, `RollupCollector`, `RollupAccumulator`, `RollupMerge` (`ConPersistence.Recording`) | 2, 3 |
| `Libraries/ConPersistence/Retention/` | `RetentionPass`, `RetentionService`, `DataPruner`, new `RetentionWindows` (`ConPersistence.Retention`) | 2, 14 |
| `Libraries/ConPersistence/Queries/` | `HistoryQueries`, `WarmStartQueries`, new `TransitionDto` (`ConPersistence.Queries`) | 2, 8 |
| `Libraries/ConPersistence/Snapshots/` | `Snapshot`, `SnapshotImporter` (`ConPersistence.Snapshots`) | 2, 16 |
| `Libraries/ConPersistence/Export/CsvExport.cs` | CSV header constants + per-row line functions | 11 |
| `Programs/ConWatch/Api/ApiResults.cs` | The one error body and the `ConfigResult`/`CloneOutcome` mappings | 9 |
| `Programs/ConWatch/StatusSnapshotBuilder.cs` | `StatusSnapshot` records + builder | 13 |
| `Programs/ConWatch/WarmStartSeeder.cs` | Warm-start policy (new) | 15 |
| `Programs/ConWatch/TopologyNames.cs` | The one trunk/anchor computation | 18 |
| `Core/ConEngine/Tracking/HostTracker.cs` | Pure history window + state machine | 19 |
| `Tests/ConPersistence.Tests/FileDbFactory.cs` | Migrated temp-file SQLite for concurrency tests (new) | 14, 21 |
| `Tests/ConEngine.Tests/Fakes/TrackerProbe.cs` | Drives a pure `HostTracker`, keeps returned transitions (new) | 19 |

---

## Group A: mechanical, low risk

### Task 1: S11, delete the empty root shells

**Files:**
- Delete (untracked, empty directories): `ConInfrastructure/`, `ConNotify/`, `ConPersistence/` at the repo root

**Interfaces:**
- Consumes: nothing.
- Produces: nothing.

Found at `78bf8e8`: `git status --ignored` no longer lists these three (their `bin/obj` are gone); they are empty directories, which git does not track, so this task has no commit. Also at the root: `APM/` is an untracked tree of empty folders (`APM/grafana/provisioning/datasources`) that the review did not name; it is left alone (it looks like a placeholder). `.superpowers/`, `dist/`, `staging/`, `tools/` are ignored working folders in use (brainstorm state, installer output, publish staging, code-signing tools) and are kept.

- [x] **Step 1: Confirm the three folders are empty**

```powershell
(Get-ChildItem -Force -Recurse ConInfrastructure, ConNotify, ConPersistence | Measure-Object).Count
```
Expected: `0`. If anything is listed, stop and report what it is; do not delete.

- [x] **Step 2: Delete them**

```powershell
Remove-Item ConInfrastructure, ConNotify, ConPersistence
```

- [x] **Step 3: Verify nothing referenced them**

Run: `dotnet build ConnectionWatcher.sln -c Debug`
Expected: `0 Warning(s)`, `0 Error(s)`. Run `git status --short`; expected: no output (nothing tracked changed, nothing to commit).

---

### Task 2: S6, persistence folders and namespaces follow responsibilities (move only)

**Files:** every move is a `git mv`; no file content changes except namespace, `using` and fully-qualified-name lines.

| From (`Libraries/ConPersistence/…`) | To (`Libraries/ConPersistence/…`) | New namespace |
|---|---|---|
| `ConfigService.cs` | `Config/ConfigService.cs` | `ConPersistence.Config` |
| `ConfigStore.cs` | `Config/ConfigStore.cs` | `ConPersistence.Config` |
| `ConfigHealth.cs` | `Config/ConfigHealth.cs` | `ConPersistence.Config` |
| `Dto/ConfigMapping.cs` | `Config/ConfigMapping.cs` | `ConPersistence.Config` |
| `Dto/ConfigDtos.cs` | `Config/ConfigDtos.cs` | `ConPersistence.Config` |
| `SettingsStore.cs` | `Settings/SettingsStore.cs` | `ConPersistence.Settings` |
| `Services/ChannelRecorder.cs` | `Recording/ChannelRecorder.cs` | `ConPersistence.Recording` |
| `Services/TransitionRecorder.cs` | `Recording/TransitionRecorder.cs` | `ConPersistence.Recording` |
| `Services/GlitchRecorder.cs` | `Recording/GlitchRecorder.cs` | `ConPersistence.Recording` |
| `Services/RollupCollector.cs` | `Recording/RollupCollector.cs` | `ConPersistence.Recording` |
| `Rollups/RollupAccumulator.cs` | `Recording/RollupAccumulator.cs` | `ConPersistence.Recording` |
| `Rollups/RollupMerge.cs` | `Recording/RollupMerge.cs` | `ConPersistence.Recording` |
| `Rollups/RetentionPass.cs` | `Retention/RetentionPass.cs` | `ConPersistence.Retention` |
| `Services/RetentionService.cs` | `Retention/RetentionService.cs` | `ConPersistence.Retention` |
| `Services/DataPruner.cs` | `Retention/DataPruner.cs` | `ConPersistence.Retention` |
| `HistoryQueries.cs` | `Queries/HistoryQueries.cs` | `ConPersistence.Queries` |
| `WarmStartQueries.cs` | `Queries/WarmStartQueries.cs` | `ConPersistence.Queries` |
| `Dto/Snapshot.cs` | `Snapshots/Snapshot.cs` | `ConPersistence.Snapshots` |
| `Services/SnapshotImporter.cs` | `Snapshots/SnapshotImporter.cs` | `ConPersistence.Snapshots` (was `ConPersistence`, the mismatch the review named) |

Unchanged: `ConWatchDbContext.cs`, `DatabaseStartup.cs`, `DesignTimeFactory.cs`, `PersistenceOptions.cs`, `SqliteErrors.cs` (root, `ConPersistence`); `Entities/` (`ConPersistence.Entities`; see "Decisions"); `Export/`, `Report/`, `Migrations/`. The project is SDK-style with default globs, so `ConPersistence.csproj` needs no edit. Code in a child namespace (`ConPersistence.Config` etc.) already sees root `ConPersistence` types, so only sibling namespaces need a `using`.

**Header edits inside moved files:**

| File | Edit |
|---|---|
| `Config/ConfigService.cs` | `namespace ConPersistence;` → `namespace ConPersistence.Config;`; delete `using ConPersistence.Dto;` |
| `Config/ConfigStore.cs` | same two edits |
| `Config/ConfigHealth.cs` | `namespace ConPersistence;` → `namespace ConPersistence.Config;`; add `using ConPersistence.Settings;` |
| `Config/ConfigMapping.cs` | `namespace ConPersistence.Dto;` → `namespace ConPersistence.Config;` |
| `Config/ConfigDtos.cs` | `namespace ConPersistence.Dto;` → `namespace ConPersistence.Config;` |
| `Settings/SettingsStore.cs` | `namespace ConPersistence;` → `namespace ConPersistence.Settings;` |
| `Recording/ChannelRecorder.cs`, `Recording/TransitionRecorder.cs`, `Recording/GlitchRecorder.cs` | `namespace ConPersistence.Services;` → `namespace ConPersistence.Recording;` |
| `Recording/RollupCollector.cs` | same; delete `using ConPersistence.Rollups;` |
| `Recording/RollupAccumulator.cs`, `Recording/RollupMerge.cs` | `namespace ConPersistence.Rollups;` → `namespace ConPersistence.Recording;` |
| `Retention/RetentionPass.cs` | `namespace ConPersistence.Rollups;` → `namespace ConPersistence.Retention;`; add `using ConPersistence.Queries;` and `using ConPersistence.Recording;` |
| `Retention/RetentionService.cs` | `namespace ConPersistence.Services;` → `namespace ConPersistence.Retention;`; delete `using ConPersistence.Rollups;`; add `using ConPersistence.Settings;` |
| `Retention/DataPruner.cs` | `namespace ConPersistence.Services;` → `namespace ConPersistence.Retention;`; add `using ConPersistence.Queries;` |
| `Queries/HistoryQueries.cs`, `Queries/WarmStartQueries.cs` | `namespace ConPersistence;` → `namespace ConPersistence.Queries;` |
| `Snapshots/Snapshot.cs` | `namespace ConPersistence.Dto;` → `namespace ConPersistence.Snapshots;`; add `using ConPersistence.Config;` |
| `Snapshots/SnapshotImporter.cs` | `namespace ConPersistence;` → `namespace ConPersistence.Snapshots;`; replace `using ConPersistence.Dto;` with `using ConPersistence.Config;` and `using ConPersistence.Settings;` |

**Consumer edits** (every file outside the moved set that names a moved type; found by grepping each moved type name and every `ConPersistence.Dto`/`.Services`/`.Rollups` reference at `78bf8e8`). "Dto → Config" means replace `using ConPersistence.Dto;` with `using ConPersistence.Config;`. Never delete `using ConPersistence;` (unused usings are not warnings in this repo, and several of these files still use root types).

| File | Edit |
|---|---|
| `Libraries/ConPersistence/Report/IspReportBuilder.cs` | add `using ConPersistence.Settings;` |
| `Libraries/ConNotify/HeartbeatNotifier.cs`, `MqttNotifier.cs`, `WebhookNotifier.cs` | add `using ConPersistence.Settings;` |
| `Programs/ConWatch/Api/AdminEndpoints.cs` | add `using ConPersistence.Config;`, `using ConPersistence.Settings;`; `ConPersistence.Services.DataPruner` → `ConPersistence.Retention.DataPruner` |
| `Programs/ConWatch/Api/ApiResults.cs` | add `using ConPersistence.Config;` |
| `Programs/ConWatch/Api/ConfigEndpoints.cs` | Dto → Config |
| `Programs/ConWatch/Api/ExportEndpoints.cs` | add `using ConPersistence.Config;`, `using ConPersistence.Settings;`; both `ConPersistence.Dto.Snapshot` → `ConPersistence.Snapshots.Snapshot` |
| `Programs/ConWatch/Api/GlitchEndpoints.cs` | add `using ConPersistence.Queries;` |
| `Programs/ConWatch/Api/HistoryEndpoints.cs` | add `using ConPersistence.Queries;` |
| `Programs/ConWatch/Api/SettingsEndpoints.cs` | add `using ConPersistence.Config;`, `using ConPersistence.Settings;` |
| `Programs/ConWatch/Clone/CloneHealth.cs` | add `using ConPersistence.Config;`, `using ConPersistence.Settings;` |
| `Programs/ConWatch/Clone/CloneService.cs` | replace `using ConPersistence.Dto;` with `using ConPersistence.Snapshots;`; add `using ConPersistence.Settings;` |
| `Programs/ConWatch/Mcp/ConWatchMcpTools.cs` | add `using ConPersistence.Queries;` |
| `Programs/ConWatch/Pages/Admin.cshtml.cs` | Dto → Config; add `using ConPersistence.Settings;` |
| `Programs/ConWatch/Pages/Admin.cshtml` | add `@using ConPersistence.Settings` on the line after `@using ConPersistence` |
| `Programs/ConWatch/Pages/Data.cshtml` | add `@using ConPersistence.Settings` on the line after `@using ConPersistence` |
| `Programs/ConWatch/Pages/Data.cshtml.cs` | add `using ConPersistence.Settings;` |
| `Programs/ConWatch/Pages/Config/Pools.cshtml.cs`, `Profiles.cshtml.cs` | Dto → Config |
| `Programs/ConWatch/Pages/Config/Targets.cshtml.cs` | Dto → Config; add `using ConPersistence.Settings;` |
| `Programs/ConWatch/Pages/History.cshtml.cs` | `ConPersistence.SettingsStore` → `ConPersistence.Settings.SettingsStore`; `ConPersistence.ConfigStore` → `ConPersistence.Config.ConfigStore`; all three `ConPersistence.SettingKeys` → `ConPersistence.Settings.SettingKeys` |
| `Programs/ConWatch/Pages/Index.cshtml.cs` | `ConPersistence.ConfigHealth` → `ConPersistence.Config.ConfigHealth`; `ConPersistence.SettingsStore` → `ConPersistence.Settings.SettingsStore`; `ConPersistence.ConfigStore` → `ConPersistence.Config.ConfigStore`; both `ConPersistence.SettingKeys` → `ConPersistence.Settings.SettingKeys` |
| `Programs/ConWatch/Pages/Shared/_HealthBanner.cshtml` | `ConPersistence.HealthWarning` → `ConPersistence.Config.HealthWarning` |
| `Programs/ConWatch/RuntimeHealth.cs` | add `using ConPersistence.Config;` |
| `Programs/ConWatch/SetupService.cs` | Dto → Config; add `using ConPersistence.Settings;` |
| `Programs/ConWatch/Startup/ConWatchServices.cs` | replace `using ConPersistence.Services;` with `using ConPersistence.Recording;` and `using ConPersistence.Retention;`; add `using ConPersistence.Config;`, `using ConPersistence.Settings;` |
| `Programs/ConWatch/TopologyNames.cs` | Dto → Config; add `using ConPersistence.Settings;` |
| `Programs/ConWatch/WatcherHostService.cs` | replace `using ConPersistence.Services;` with `using ConPersistence.Recording;`; add `using ConPersistence.Config;`, `using ConPersistence.Settings;`, `using ConPersistence.Queries;` |
| `Tests/ConPersistence.Tests/ConfigHealthTests.cs` | Dto → Config; add `using ConPersistence.Settings;` |
| `Tests/ConPersistence.Tests/ConfigServiceTests.cs` | Dto → Config; all eight `ConPersistence.Dto.TargetDto` → `ConPersistence.Config.TargetDto` |
| `Tests/ConPersistence.Tests/ConfigStoreTests.cs` | add `using ConPersistence.Config;` |
| `Tests/ConPersistence.Tests/DataPrunerTests.cs` | replace `using ConPersistence.Services;` with `using ConPersistence.Retention;` |
| `Tests/ConPersistence.Tests/GlitchRecorderTests.cs` | delete `using ConPersistence.Rollups;` and `using ConPersistence.Services;`; add `using ConPersistence.Recording;`, `using ConPersistence.Retention;` |
| `Tests/ConPersistence.Tests/HistoryQueryTests.cs` | add `using ConPersistence.Queries;` |
| `Tests/ConPersistence.Tests/IspReportBuilderTests.cs` | add `using ConPersistence.Config;`, `using ConPersistence.Settings;` |
| `Tests/ConPersistence.Tests/RecorderCollectorTests.cs` | replace `using ConPersistence.Services;` with `using ConPersistence.Recording;` |
| `Tests/ConPersistence.Tests/RetentionTests.cs` | replace `using ConPersistence.Rollups;` with `using ConPersistence.Retention;` |
| `Tests/ConPersistence.Tests/RollupAccumulatorTests.cs` | replace `using ConPersistence.Rollups;` with `using ConPersistence.Recording;` |
| `Tests/ConPersistence.Tests/SettingsStoreTests.cs` | add `using ConPersistence.Settings;` |
| `Tests/ConWatch.Tests/AdminApiTests.cs` | replace `using ConPersistence.Services;` with `using ConPersistence.Recording;`; `ConPersistence.Dto.TargetDto` → `ConPersistence.Config.TargetDto`; `ConPersistence.Dto.ProfileDto` → `ConPersistence.Config.ProfileDto` |
| `Tests/ConWatch.Tests/CloneTests.cs` | `ConPersistence.SettingsStore` → `ConPersistence.Settings.SettingsStore`; `ConPersistence.SettingKeys` → `ConPersistence.Settings.SettingKeys` |
| `Tests/ConWatch.Tests/ConfigApiTests.cs`, `SetupApiTests.cs` | Dto → Config |
| `Tests/ConWatch.Tests/EndToEndTests.cs`, `WarmRestartTests.cs` | replace `using ConPersistence.Services;` with `using ConPersistence.Recording;` |
| `Tests/ConWatch.Tests/HistoryApiTests.cs` | Dto → Config; replace `using ConPersistence.Services;` with `using ConPersistence.Recording;` |
| `Tests/ConWatch.Tests/OperatorFieldTests.cs`, `ProbeFieldTests.cs`, `SnapshotTests.cs`, `RealDataSnapshotTests.cs` | replace `using ConPersistence.Dto;` with `using ConPersistence.Config;` and `using ConPersistence.Snapshots;` |

**Interfaces:**
- Consumes: nothing.
- Produces: the namespaces in the tables above. Every later task uses them: `ConPersistence.Config.{ConfigService, ConfigStore, ConfigHealth, HealthWarning, ConfigResult, ConfigErrorKind, ConfigDocument, ProfileDto, TargetDto, PoolDto}` (and the internal `ConfigMapping`), `ConPersistence.Settings.{SettingsStore, SettingKeys}`, `ConPersistence.Recording.*`, `ConPersistence.Retention.*`, `ConPersistence.Queries.{HistoryQueries, WarmStartQueries}`, `ConPersistence.Snapshots.{Snapshot, SnapshotImporter}`.

- [x] **Step 1: Baseline**

Run: `dotnet build ConnectionWatcher.sln -c Debug` then `dotnet test ConnectionWatcher.sln -c Debug`
Expected: 0 warnings; 0 failed. Note the passed count; after this task it must be identical (a move adds no tests).

- [x] **Step 2: Create the folders and move the files**

```powershell
$p = "Libraries/ConPersistence"
"Config","Settings","Recording","Retention","Queries","Snapshots" | ForEach-Object { New-Item -ItemType Directory -Force "$p/$_" | Out-Null }
git mv "$p/ConfigService.cs" "$p/Config/ConfigService.cs"
git mv "$p/ConfigStore.cs" "$p/Config/ConfigStore.cs"
git mv "$p/ConfigHealth.cs" "$p/Config/ConfigHealth.cs"
git mv "$p/Dto/ConfigMapping.cs" "$p/Config/ConfigMapping.cs"
git mv "$p/Dto/ConfigDtos.cs" "$p/Config/ConfigDtos.cs"
git mv "$p/SettingsStore.cs" "$p/Settings/SettingsStore.cs"
git mv "$p/Services/ChannelRecorder.cs" "$p/Recording/ChannelRecorder.cs"
git mv "$p/Services/TransitionRecorder.cs" "$p/Recording/TransitionRecorder.cs"
git mv "$p/Services/GlitchRecorder.cs" "$p/Recording/GlitchRecorder.cs"
git mv "$p/Services/RollupCollector.cs" "$p/Recording/RollupCollector.cs"
git mv "$p/Rollups/RollupAccumulator.cs" "$p/Recording/RollupAccumulator.cs"
git mv "$p/Rollups/RollupMerge.cs" "$p/Recording/RollupMerge.cs"
git mv "$p/Rollups/RetentionPass.cs" "$p/Retention/RetentionPass.cs"
git mv "$p/Services/RetentionService.cs" "$p/Retention/RetentionService.cs"
git mv "$p/Services/DataPruner.cs" "$p/Retention/DataPruner.cs"
git mv "$p/HistoryQueries.cs" "$p/Queries/HistoryQueries.cs"
git mv "$p/WarmStartQueries.cs" "$p/Queries/WarmStartQueries.cs"
git mv "$p/Dto/Snapshot.cs" "$p/Snapshots/Snapshot.cs"
git mv "$p/Services/SnapshotImporter.cs" "$p/Snapshots/SnapshotImporter.cs"
Remove-Item "$p/Dto", "$p/Services", "$p/Rollups"   # now empty; fails loudly if not
```

- [x] **Step 3: Apply the header edits** (first table) to the 19 moved files.

- [x] **Step 4: Apply the consumer edits** (second table).

- [x] **Step 5: Build**

Run: `dotnet build ConnectionWatcher.sln -c Debug`
Expected: `0 Warning(s)`, `0 Error(s)`. If a CS0246/CS0103 names a moved type in a file the table missed, add the `using` for that type's namespace from the first table (never a `global using`).

- [x] **Step 6: Test**

Run: `dotnet test ConnectionWatcher.sln -c Debug`
Expected: 0 failed, the same passed count as Step 1.

- [x] **Step 7: Check it is a pure move**

```powershell
git add -A Libraries/ConPersistence Libraries/ConNotify Programs/ConWatch Tests/ConPersistence.Tests Tests/ConWatch.Tests
git diff --cached -M --name-status Libraries/ConPersistence
```
Expected: 19 `R0xx` lines (renames with similarity), no `D`/`A` pairs.

- [x] **Step 8: Commit**

```powershell
git commit -m "refactor: persistence folders and namespaces follow responsibilities (move only)"
```

---

### Task 3: Section 3, dead and over-exposed members

**Files:**
- Modify: `Core/ConEngine/Tracking/CircularBuffer.cs` (delete `GetLatest`)
- Modify: `Core/ConEngine/Network/PingController.cs` (`Stop()` becomes private)
- Modify: `Core/ConEngine/WatcherEngine.cs` (`DefaultDnsRefreshInterval` becomes private)
- Modify: `Libraries/ConPersistence/Recording/RollupAccumulator.cs` (`RollupBucket.RttSum` becomes internal)
- Modify: `Tests/ConEngine.Tests/PingControllerTests.cs` (the one caller of `Stop()`)

**Interfaces:**
- Consumes: Task 2's `Recording/` path.
- Produces: nothing new. Removed from the public surface: `CircularBuffer<T>.GetLatest()`, `PingController.Stop()`, `WatcherEngine.DefaultDnsRefreshInterval`, `RollupBucket.RttSum` (now `internal`).

Kept on purpose (verified by grep at `78bf8e8`): `PingDiscipline.IsParked` and `TargetGraph.GetByName/GetPoolByName/GetChildren` (test helpers used by 10+ tests; the repo has no `InternalsVisibleTo` and the review allows leaving them); `HostAnalysisEngine`'s public setters (the class is internal); `SnapshotImporter.ImportToFileAsync` (the CLI/test route S7 keeps); `RollupCollector.FlushClosedAsync` (the background loop calls it); `DataPruner.PruneResult` (it is the `/admin/prune` response body, not test-only); `HostTracker`/`TargetMonitor` members (Task 19); `DomainResolutionResult` (decision 6: keep the shape).

- [x] **Step 1: Confirm the callers**

```powershell
git grep -n -e "GetLatest" -e "DefaultDnsRefreshInterval" -e "RttSum" -e "controllerA.Stop()" -- Core Libraries Programs Tests
```
Expected: `GetLatest` only in `CircularBuffer.cs`; `DefaultDnsRefreshInterval` only in `WatcherEngine.cs`; `RttSum` only in `RollupAccumulator.cs`; `controllerA.Stop()` once in `Tests/ConEngine.Tests/PingControllerTests.cs`. (Also run `git grep -n "\.Stop()" -- Core` and confirm the only `PingController.Stop` call is inside `PingController.StopAsync`.)

- [x] **Step 2: Move the test off `Stop()`**

In `Tests/ConEngine.Tests/PingControllerTests.cs`, `Stopping_one_controller_does_not_complete_shared_channel`, replace

```csharp
        controllerA.Stop();
        await Task.Delay(100);
```
with
```csharp
        await controllerA.StopAsync(TimeSpan.FromSeconds(1));
```

Run: `dotnet test Tests/ConEngine.Tests -c Debug --filter "FullyQualifiedName~PingControllerTests"`
Expected: PASS (the test now waits for A's loop to exit instead of sleeping).

- [x] **Step 3: Narrow and delete**

`Core/ConEngine/Tracking/CircularBuffer.cs`: delete the `GetLatest()` method with its `/// <summary>The most recent item, or default when empty.</summary>` line.

`Core/ConEngine/Network/PingController.cs`:
```csharp
    /// <summary>Requests the loop to stop. Returns immediately.</summary>
    private void Stop()
```

`Core/ConEngine/WatcherEngine.cs`:
```csharp
    private static readonly TimeSpan DefaultDnsRefreshInterval = TimeSpan.FromMinutes(5);
```

`Libraries/ConPersistence/Recording/RollupAccumulator.cs`, in `RollupBucket`:
```csharp
    internal long RttSum { get; set; }
```

- [x] **Step 4: Build and test**

Run: `dotnet build ConnectionWatcher.sln -c Debug` → `0 Warning(s)`.
Run: `dotnet test ConnectionWatcher.sln -c Debug` → 0 failed.

- [x] **Step 5: Commit**

```powershell
git add Core/ConEngine Libraries/ConPersistence/Recording/RollupAccumulator.cs Tests/ConEngine.Tests/PingControllerTests.cs
git commit -m "refactor: drop or narrow dead engine and rollup members"
```

---

### Task 4: Section 3, `GatewayDiscovery` probes every candidate at once

**Files:**
- Modify: `Libraries/ConInfrastructure/Discovery/GatewayDiscovery.cs`
- Modify: `Programs/ConWatch/Startup/ConWatchServices.cs` (explicit registration)
- Create: `Tests/ConEngine.Tests/GatewayDiscoveryTests.cs` (ConEngine.Tests already references ConInfrastructure)

**Interfaces:**
- Consumes: nothing.
- Produces: `public GatewayDiscovery()` (production wiring, unchanged behavior) and `public GatewayDiscovery(Func<IEnumerable<IPAddress>> defaultRoutes, Func<IPAddress, CancellationToken, Task<long?>> probe)` (the probe returns round-trip ms, or null when the address does not answer). `IGatewayDiscovery` and `GatewayCandidate` unchanged.

Result order is part of the behavior and is kept: default-route candidates first, then reachable before unreachable, ties in insertion order (default routes, then `192.168.0.1`, `192.168.1.1`, `10.0.0.1`, `10.0.0.2`, `192.168.100.1`).

- [x] **Step 1: Write the tests**

`Tests/ConEngine.Tests/GatewayDiscoveryTests.cs`:

```csharp
using System.Net;
using ConInfrastructure.Discovery;

namespace ConEngine.Tests;

public class GatewayDiscoveryTests
{
    [Fact]
    public async Task Results_keep_default_routes_first_then_reachable_then_insertion_order()
    {
        var discovery = new GatewayDiscovery(
            defaultRoutes: () => [IPAddress.Parse("192.168.1.254")],
            probe: (ip, _) => Task.FromResult<long?>(ip.ToString() == "192.168.1.1" ? 3 : null));

        var result = await discovery.DiscoverAsync(CancellationToken.None);

        Assert.Equal(new[] { "192.168.1.254", "192.168.1.1", "192.168.0.1", "10.0.0.1", "10.0.0.2", "192.168.100.1" },
            result.Select(r => r.Address));
        Assert.Equal("default-route", result[0].Source);
        Assert.False(result[0].Reachable);
        Assert.Null(result[0].RttMs);
        Assert.True(result[1].Reachable);
        Assert.Equal(3, result[1].RttMs);
        Assert.All(result.Skip(1), r => Assert.Equal("probe", r.Source));
    }

    [Fact]
    public async Task Every_candidate_is_probed_at_once()
    {
        var inFlight = 0;
        var peak = 0;
        var gate = new object();
        var discovery = new GatewayDiscovery(
            defaultRoutes: () => [IPAddress.Parse("192.168.1.254")],
            probe: async (_, ct) =>
            {
                var now = Interlocked.Increment(ref inFlight);
                lock (gate) peak = Math.Max(peak, now);
                await Task.Delay(200, ct);
                Interlocked.Decrement(ref inFlight);
                return null;
            });

        var result = await discovery.DiscoverAsync(CancellationToken.None);

        Assert.Equal(6, result.Count);
        Assert.Equal(6, peak); // one default route + five common candidates, all in flight together
    }

    [Fact]
    public async Task A_default_route_that_is_also_a_common_candidate_is_listed_once_as_default_route()
    {
        var discovery = new GatewayDiscovery(
            defaultRoutes: () => [IPAddress.Parse("192.168.1.1")],
            probe: (_, _) => Task.FromResult<long?>(null));

        var result = await discovery.DiscoverAsync(CancellationToken.None);

        Assert.Equal(5, result.Count);
        Assert.Equal(("192.168.1.1", "default-route"), (result[0].Address, result[0].Source));
    }
}
```

- [x] **Step 2: Run to verify they fail**

Run: `dotnet test Tests/ConEngine.Tests -c Debug --filter "FullyQualifiedName~GatewayDiscoveryTests"`
Expected: build error, no `GatewayDiscovery` constructor takes two arguments.

- [x] **Step 3: Introduce the seams, still sequential**

Replace `Libraries/ConInfrastructure/Discovery/GatewayDiscovery.cs` with:

```csharp
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace ConInfrastructure.Discovery;

/// <summary>
/// Default-route gateways from the host's interfaces plus a probe of common
/// home-router addresses. Inside a container the default route may be a
/// Docker bridge — callers must present, never trust, these candidates.
/// </summary>
public class GatewayDiscovery : IGatewayDiscovery
{
    private static readonly string[] CommonCandidates =
        ["192.168.0.1", "192.168.1.1", "10.0.0.1", "10.0.0.2", "192.168.100.1"];

    private readonly Func<IEnumerable<IPAddress>> _defaultRoutes;
    private readonly Func<IPAddress, CancellationToken, Task<long?>> _probe;

    /// <summary>Production wiring: the host's default routes and a 1 s ICMP echo.</summary>
    public GatewayDiscovery() : this(DefaultRouteGateways, IcmpProbeAsync)
    {
    }

    /// <param name="defaultRoutes">IPv4 default gateways of the up, non-loopback interfaces.</param>
    /// <param name="probe">Round-trip ms when the address answers, null when it does not.</param>
    public GatewayDiscovery(Func<IEnumerable<IPAddress>> defaultRoutes,
        Func<IPAddress, CancellationToken, Task<long?>> probe)
    {
        _defaultRoutes = defaultRoutes;
        _probe = probe;
    }

    public async Task<IReadOnlyList<GatewayCandidate>> DiscoverAsync(CancellationToken ct)
    {
        var addresses = new Dictionary<string, string>(); // address -> source, in insertion order
        foreach (var gateway in _defaultRoutes())
            addresses.TryAdd(gateway.ToString(), "default-route");
        foreach (var candidate in CommonCandidates)
            addresses.TryAdd(candidate, "probe");

        var results = new List<GatewayCandidate>();
        foreach (var (address, source) in addresses)
        {
            var rtt = await _probe(IPAddress.Parse(address), ct).ConfigureAwait(false);
            results.Add(new GatewayCandidate(address, source, rtt is not null, rtt));
        }
        return Order(results);
    }

    private static List<GatewayCandidate> Order(IEnumerable<GatewayCandidate> results) => results
        .OrderByDescending(r => r.Source == "default-route")
        .ThenByDescending(r => r.Reachable)
        .ToList();

    private static IEnumerable<IPAddress> DefaultRouteGateways()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up ||
                nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;
            foreach (var gateway in nic.GetIPProperties().GatewayAddresses)
            {
                if (gateway.Address.AddressFamily == AddressFamily.InterNetwork)
                    yield return gateway.Address;
            }
        }
    }

    private static async Task<long?> IcmpProbeAsync(IPAddress address, CancellationToken ct)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(address, TimeSpan.FromSeconds(1),
                cancellationToken: ct).ConfigureAwait(false);
            return reply.Status == IPStatus.Success ? reply.RoundtripTime : (long?)null;
        }
        catch (PingException)
        {
            return null; // unreachable
        }
    }
}
```

Run: `dotnet test Tests/ConEngine.Tests -c Debug --filter "FullyQualifiedName~GatewayDiscoveryTests"`
Expected: `Results_keep_…` and `A_default_route_…` PASS; `Every_candidate_is_probed_at_once` FAILS with `Expected: 6, Actual: 1` (still sequential: the first two pin today's order, the third proves it measures concurrency).

- [x] **Step 4: Probe in parallel**

In `DiscoverAsync`, replace the block from `var results = new List<GatewayCandidate>();` through `return Order(results);` with:

```csharp
        // Every candidate at once: the wizard's gateway step takes one probe
        // timeout (1 s), not one per candidate.
        var results = await Task.WhenAll(addresses.Select(async pair =>
        {
            var rtt = await _probe(IPAddress.Parse(pair.Key), ct).ConfigureAwait(false);
            return new GatewayCandidate(pair.Key, pair.Value, rtt is not null, rtt);
        })).ConfigureAwait(false);
        return Order(results);
```

In `Programs/ConWatch/Startup/ConWatchServices.cs`, `AddConWatchEngine`, replace the `IGatewayDiscovery` line with an explicit factory, so the container never has to choose between two public constructors:

```csharp
        services.AddSingleton<ConInfrastructure.Discovery.IGatewayDiscovery>(
            _ => new ConInfrastructure.Discovery.GatewayDiscovery());
```

- [x] **Step 5: Run to verify they pass**

Run: `dotnet test Tests/ConEngine.Tests -c Debug --filter "FullyQualifiedName~GatewayDiscoveryTests"` → 3 passed.
Run: `dotnet build ConnectionWatcher.sln -c Debug` → `0 Warning(s)`; `dotnet test ConnectionWatcher.sln -c Debug` → 0 failed (`SetupApiTests` replaces the registration with `RemoveAll` + its fake, which works the same with a factory registration).

- [x] **Step 6: Commit**

```powershell
git add Libraries/ConInfrastructure/Discovery/GatewayDiscovery.cs Programs/ConWatch/Startup/ConWatchServices.cs Tests/ConEngine.Tests/GatewayDiscoveryTests.cs
git commit -m "refactor: gateway discovery probes every candidate at once"
```

---

## Group B: dedupe

### Task 5: Section 3, a test that the migrations capture the whole model

Moved to the front of Group B (the review lists it last) so every later persistence task runs under it.

**Files:**
- Create: `Tests/ConPersistence.Tests/MigrationsTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: the guard every later task relies on: any EF model change without a migration fails the suite.

- [x] **Step 1: Write the test**

`Tests/ConPersistence.Tests/MigrationsTests.cs`:

```csharp
using Microsoft.EntityFrameworkCore;

namespace ConPersistence.Tests;

/// <summary>
/// Unit tests build their schema with EnsureCreated, which never reads the
/// migrations; production runs MigrateAsync. This is the one check that the
/// two agree: a model change without a migration fails here, not on a user's
/// first start after upgrading.
/// </summary>
public class MigrationsTests
{
    [Fact]
    public void Migrations_capture_the_whole_model()
    {
        var options = new DbContextOptionsBuilder<ConWatchDbContext>()
            .UseSqlite("Data Source=:memory:").Options;
        using var db = new ConWatchDbContext(options);

        Assert.False(db.Database.HasPendingModelChanges(),
            "The EF model has changes no migration captures. Add one with " +
            "`dotnet ef migrations add <Name> --project Libraries/ConPersistence` " +
            "(the project-only form: ConPersistence's EF Design reference is PrivateAssets=all, " +
            "so --startup-project Programs/ConWatch fails).");
    }
}
```

- [x] **Step 2: Run it**

Run: `dotnet test Tests/ConPersistence.Tests -c Debug --filter "FullyQualifiedName~MigrationsTests"`
Expected: PASS. It is a characterization test: it passes on the current tree. If it FAILS on the unmodified tree, stop and report: a real missing migration is a finding for the owner, not something this plan fixes.

- [x] **Step 3: Prove it bites, then revert**

Temporarily add `public int Probe { get; set; }` to `GlitchRow` in `Libraries/ConPersistence/Entities/DataRows.cs`. Run the same filter. Expected: FAIL with the message above. Remove the property again (`git diff Libraries/ConPersistence/Entities` shows nothing).

- [x] **Step 4: Full suite and commit**

Run: `dotnet build ConnectionWatcher.sln -c Debug` → `0 Warning(s)`; `dotnet test ConnectionWatcher.sln -c Debug` → 0 failed.

```powershell
git add Tests/ConPersistence.Tests/MigrationsTests.cs
git commit -m "test: the migrations must capture the whole EF model"
```

---

### Task 6: Section 3, `TargetRow.CopyFrom` and one address normalization

**Files:**
- Modify: `Libraries/ConPersistence/Config/ConfigMapping.cs` (add `NormalizeAddress`, `TargetRow.CopyFrom`; `TargetDto.ToConfig()` trims)
- Modify: `Libraries/ConPersistence/Config/ConfigService.cs` (`UpsertTargetAsync`, `InsertParentAsync`)
- Modify: `Libraries/ConPersistence/Config/ConfigStore.cs` (`WriteDocumentAsync`: `CopyFrom`, and parents assigned in memory instead of one `SingleAsync` per parented target)
- Create: `Tests/ConPersistence.Tests/TargetWriteTests.cs`

**Interfaces:**
- Consumes: Task 2 namespaces.
- Produces (internal to ConPersistence): `ConfigMapping.NormalizeAddress(string? address) → string` and `ConfigMapping.CopyFrom(this TargetRow row, TargetDto dto, Guid profileId, Guid? parentId) → TargetRow`. Task 17's `EditDocumentAsync` writes through `WriteDocumentAsync`, so it inherits both.

Decision (see table): `Address` is trimmed everywhere, including what the import validator sees; `Name` is not trimmed. A `null` address (JSON `"address": null`) now normalizes to `""`, so the validator answers "has an empty Address" (400) where the write path used to throw a `NullReferenceException` (500).

- [x] **Step 1: Write the tests**

`Tests/ConPersistence.Tests/TargetWriteTests.cs`:

```csharp
using ConPersistence.Config;
using ConPersistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace ConPersistence.Tests;

/// <summary>Every path that writes a target row stores the same normalized
/// values, and the import validator sees the row as it will be stored.</summary>
public class TargetWriteTests : IAsyncLifetime, IDisposable
{
    private readonly TestDbFactory _factory = new();
    private readonly ConfigStore _store;
    private readonly ConfigService _service;

    public TargetWriteTests()
    {
        _store = new ConfigStore(_factory);
        _service = new ConfigService(_factory, _store);
    }

    public Task InitializeAsync() => _store.SeedAsync(null); // built-in profiles
    public Task DisposeAsync() => Task.CompletedTask;
    public void Dispose() => _factory.Dispose();

    private static TargetDto Padded(string name, string address) => new()
    {
        Name = name, Address = address, Profile = "Internal",
        Operator = "  Comcast ", Probe = " TCP:80 ",
    };

    private async Task<TargetRow> RowAsync(string name)
    {
        await using var db = _factory.CreateDbContext();
        return await db.Targets.AsNoTracking().SingleAsync(t => t.Name == name);
    }

    private static void AssertNormalized(TargetRow row, string address)
    {
        Assert.Equal(address, row.Address);
        Assert.Equal("Comcast", row.Operator);
        Assert.Equal("tcp:80", row.Probe);
    }

    [Fact]
    public async Task Upsert_stores_the_normalized_row()
    {
        Assert.True((await _service.UpsertTargetAsync(Padded("ViaUpsert", " 10.0.0.9 "))).Success);
        AssertNormalized(await RowAsync("ViaUpsert"), "10.0.0.9");
    }

    [Fact]
    public async Task Insert_parent_stores_the_normalized_row()
    {
        var child = await _service.UpsertTargetAsync(new TargetDto
        {
            Name = "Child", Address = "10.0.0.20", Profile = "Internal",
        });
        Assert.True(child.Success);

        var result = await _service.InsertParentAsync(Padded("ViaInsert", " 10.0.0.10 "), [child.Id!.Value]);

        Assert.True(result.Success);
        AssertNormalized(await RowAsync("ViaInsert"), "10.0.0.10");
        Assert.Equal(result.Id, (await RowAsync("Child")).ParentId);
    }

    [Fact]
    public async Task Import_stores_the_normalized_row_and_parents()
    {
        var result = await _service.ImportAsync(new ConfigDocument
        {
            Profiles = [new ProfileDto { Name = "Internal" }],
            Targets =
            [
                Padded("ViaImport", " 10.0.0.11 "),
                new TargetDto { Name = "Below", Address = "10.0.0.12", Profile = "Internal", Parent = "ViaImport" },
            ],
        });

        Assert.True(result.Success);
        var parent = await RowAsync("ViaImport");
        AssertNormalized(parent, "10.0.0.11");
        Assert.Equal(parent.Id, (await RowAsync("Below")).ParentId);
    }

    [Fact]
    public async Task Import_validates_addresses_the_way_they_will_be_stored()
    {
        // " 10.0.0.9 " is stored as "10.0.0.9": the validator must see the
        // duplicate, exactly as it does for the one-target CRUD path.
        var result = await _service.ImportAsync(new ConfigDocument
        {
            Profiles = [new ProfileDto { Name = "Internal" }],
            Targets =
            [
                new TargetDto { Name = "A", Address = "10.0.0.9", Profile = "Internal" },
                new TargetDto { Name = "B", Address = " 10.0.0.9 ", Profile = "Internal" },
            ],
        });

        Assert.False(result.Success);
        Assert.Contains(result.Errors!, e => e.Contains("Duplicate target address '10.0.0.9'"));
    }
}
```

- [x] **Step 2: Run to see which pin and which fail**

Run: `dotnet test Tests/ConPersistence.Tests -c Debug --filter "FullyQualifiedName~TargetWriteTests"`
Expected: the three `…stores_the_normalized_row…` tests PASS (they pin today's write paths); `Import_validates_addresses_the_way_they_will_be_stored` FAILS (`result.Success` is true: the validator saw two different strings, and two rows with the same address were stored).

- [x] **Step 3: One copy routine and one normalization**

In `Libraries/ConPersistence/Config/ConfigMapping.cs`, next to `NormalizeOperator`, add:

```csharp
    /// <summary>Addresses are stored trimmed; the validator must see the stored form.</summary>
    public static string NormalizeAddress(string? address) => (address ?? "").Trim();
```

In the `// ---- targets ----` section, add:

```csharp
    /// <summary>
    /// Copies every editable field (not the id) from a DTO onto a row,
    /// normalized the way every write path stores it. A new target column is
    /// written here and in the two ToConfig/ToDto mappings, nowhere else.
    /// </summary>
    public static TargetRow CopyFrom(this TargetRow row, TargetDto dto, Guid profileId, Guid? parentId)
    {
        row.Name = dto.Name;
        row.Address = NormalizeAddress(dto.Address);
        row.ParentId = parentId;
        row.ProfileId = profileId;
        row.Enabled = dto.Enabled;
        row.Operator = NormalizeOperator(dto.Operator);
        row.Probe = NormalizeProbe(dto.Probe);
        return row;
    }
```

and change `ToConfig(this TargetDto t)` to trim like the write paths:

```csharp
    /// <summary>Engine-options shape normalized exactly as it will be stored (the validator sees what will be stored).</summary>
    public static TargetConfig ToConfig(this TargetDto t) => new()
    {
        Id = t.Id, Name = t.Name, Address = NormalizeAddress(t.Address),
        Parent = t.Parent, Profile = t.Profile, Enabled = t.Enabled,
        Operator = t.Operator, Probe = NormalizeProbe(t.Probe),
    };
```

In `Libraries/ConPersistence/Config/ConfigService.cs`, `UpsertTargetAsync`: replace `var newAddress = dto.Address.Trim();` with `var newAddress = ConfigMapping.NormalizeAddress(dto.Address);`, and replace the seven assignments after the `else { … }` block (from `row.Name = dto.Name;` to `row.Probe = ConfigMapping.NormalizeProbe(dto.Probe);`) with:

```csharp
        row.CopyFrom(dto, profile.Id, parentId);
        return ConfigResult.Ok(row.Id);
```

`InsertParentAsync`: replace the `var row = new TargetRow { … };` initializer with:

```csharp
        var row = new TargetRow { Id = Guid.NewGuid() }.CopyFrom(newTarget, profile.Id, parents[0]);
```

In `Libraries/ConPersistence/Config/ConfigStore.cs`, `WriteDocumentAsync`: replace everything from `var targetIds = new Dictionary<string, Guid>(…);` to the end of the method with:

```csharp
        var rowsByName = new Dictionary<string, TargetRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in doc.Targets)
        {
            var row = new TargetRow { Id = t.Id ?? Guid.NewGuid() }
                .CopyFrom(t, profileIds[t.Profile], parentId: null);
            rowsByName[t.Name] = row;
            db.Targets.Add(row);
        }
        await db.SaveChangesAsync();

        // Second pass for parents (order-independent). The rows are tracked,
        // so this is an in-memory assignment, not one query per target.
        foreach (var t in doc.Targets.Where(t => t.Parent is { Length: > 0 }))
            rowsByName[t.Name].ParentId = rowsByName[t.Parent!].Id;

        foreach (var p in doc.Pools)
        {
            var poolId = p.Id ?? Guid.NewGuid();
            db.Pools.Add(new PoolRow
            {
                Id = poolId, Name = p.Name,
                ParentId = p.Parent is { Length: > 0 } parent ? rowsByName[parent].Id : null,
                Members = p.Members.Select((m, i) => new PoolMemberRow
                {
                    PoolId = poolId, TargetId = rowsByName[m].Id, MemberIndex = i,
                }).ToList(),
            });
        }
        await db.SaveChangesAsync();
```

- [x] **Step 4: Run to verify they pass**

Run: `dotnet test Tests/ConPersistence.Tests -c Debug --filter "FullyQualifiedName~TargetWriteTests"` → 4 passed.
Run: `dotnet test Tests/ConPersistence.Tests -c Debug` → 0 failed (`ConfigStoreTests` pins id-preserving round trips through `WriteDocumentAsync`).
Run: `dotnet build ConnectionWatcher.sln -c Debug` → `0 Warning(s)`; full suite → 0 failed.

- [x] **Step 5: Commit**

```powershell
git add Libraries/ConPersistence/Config Tests/ConPersistence.Tests/TargetWriteTests.cs
git commit -m "refactor: one TargetRow.CopyFrom; import validates addresses as stored"
```

---

### Task 7: Section 3, one profile row↔DTO pair; defaults only on `PingProfileConfig`

**Files:**
- Modify: `Libraries/ConPersistence/Config/ConfigDtos.cs` (`ProfileDto` initializers read `PingProfileConfig`)
- Modify: `Libraries/ConPersistence/Entities/ConfigRows.cs` (`ProfileRow` initializers read `PingProfileConfig`)
- Modify: `Libraries/ConPersistence/Config/ConfigMapping.cs` (`ProfileRow.ToConfig()` becomes `ToDto().ToConfig()`)
- Create: `Tests/ConPersistence.Tests/ProfileMappingTests.cs`

**Interfaces:**
- Consumes: Task 2 namespaces; Task 5's migrations guard (the entity change must not move the model).
- Produces: nothing new. The profile field list now appears in `ConfigMapping` as: `ProfileRow.ToDto()`, `PingProfile.ToDto()`, `ProfileDto.ToConfig()`, `ProfileRow.CopyFrom(ProfileDto)` (was five lists; `PingProfile.ToDto` maps a different source type and stays). The numbers 10/3/1/60/100/10/0.95/0.75/0.10/0.25 appear only in `Core/ConEngine/Configuration/WatcherOptions.cs` (`PingProfileConfig`).

- [x] **Step 1: Write the pinning tests**

`Tests/ConPersistence.Tests/ProfileMappingTests.cs`:

```csharp
using System.Text.Json;
using ConEngine.Configuration;
using ConPersistence.Config;
using ConPersistence.Entities;

namespace ConPersistence.Tests;

public class ProfileMappingTests : IDisposable
{
    private readonly TestDbFactory _factory = new();
    private readonly ConfigStore _store;
    private readonly ConfigService _service;

    public ProfileMappingTests()
    {
        _store = new ConfigStore(_factory);
        _service = new ConfigService(_factory, _store);
    }

    public void Dispose() => _factory.Dispose();

    private static string Json(PingProfileConfig config) => JsonSerializer.Serialize(config);

    private static PingProfileConfig ConfigOf(ProfileRow r) => new()
    {
        NormalIntervalSeconds = r.NormalIntervalSeconds, PingTimeoutSeconds = r.PingTimeoutSeconds,
        BurstIntervalSeconds = r.BurstIntervalSeconds, BurstMaxDurationSeconds = r.BurstMaxDurationSeconds,
        HistorySize = r.HistorySize, MinUsableSample = r.MinUsableSample,
        RespondingThreshold = r.RespondingThreshold, FalteringThreshold = r.FalteringThreshold,
        DownThreshold = r.DownThreshold, RecoveringThreshold = r.RecoveringThreshold,
    };

    [Fact]
    public async Task A_profile_posted_with_only_a_name_gets_the_config_defaults()
    {
        Assert.True((await _service.UpsertProfileAsync(new ProfileDto { Name = "Bare" })).Success);

        var options = await _store.LoadWatcherOptionsAsync();

        Assert.Equal(Json(new PingProfileConfig()), Json(options.Profiles["Bare"]));
    }

    [Fact]
    public void A_new_row_starts_from_the_config_defaults() =>
        Assert.Equal(Json(new PingProfileConfig()), Json(ConfigOf(new ProfileRow())));

    [Fact]
    public async Task Every_field_survives_dto_to_row_to_dto_and_config()
    {
        var dto = new ProfileDto
        {
            Name = "Tuned", NormalIntervalSeconds = 7, PingTimeoutSeconds = 2,
            BurstIntervalSeconds = 0.5, BurstMaxDurationSeconds = 30, HistorySize = 50,
            MinUsableSample = 8, RespondingThreshold = 0.9, FalteringThreshold = 0.7,
            DownThreshold = 0.05, RecoveringThreshold = 0.3,
        };
        Assert.True((await _service.UpsertProfileAsync(dto)).Success);

        var exported = (await _store.ExportAsync()).Profiles.Single(p => p.Name == "Tuned");
        Assert.Equal(dto with { Id = exported.Id }, exported);

        var config = (await _store.LoadWatcherOptionsAsync()).Profiles["Tuned"];
        Assert.Equal(Json(new PingProfileConfig
        {
            NormalIntervalSeconds = 7, PingTimeoutSeconds = 2, BurstIntervalSeconds = 0.5,
            BurstMaxDurationSeconds = 30, HistorySize = 50, MinUsableSample = 8,
            RespondingThreshold = 0.9, FalteringThreshold = 0.7, DownThreshold = 0.05,
            RecoveringThreshold = 0.3,
        }), Json(config));
    }
}
```

- [x] **Step 2: Run them on the current code**

Run: `dotnet test Tests/ConPersistence.Tests -c Debug --filter "FullyQualifiedName~ProfileMappingTests"`
Expected: 3 passed (they pin today's defaults and mappings before the lists are removed).

- [x] **Step 3: One list, one set of defaults**

`Libraries/ConPersistence/Config/ConfigDtos.cs`: add `using ConEngine.Configuration;` at the top and replace `ProfileDto` with:

```csharp
/// <summary>Ping profile for API/CRUD and export. Name-keyed; Id preserved when present.
/// A field the caller leaves out takes <see cref="PingProfileConfig"/>'s default,
/// the one place those numbers are written down.</summary>
public record ProfileDto
{
    private static readonly PingProfileConfig Defaults = new();

    public Guid? Id { get; init; }
    public required string Name { get; init; }
    public double NormalIntervalSeconds { get; init; } = Defaults.NormalIntervalSeconds;
    public double PingTimeoutSeconds { get; init; } = Defaults.PingTimeoutSeconds;
    public double BurstIntervalSeconds { get; init; } = Defaults.BurstIntervalSeconds;
    public double BurstMaxDurationSeconds { get; init; } = Defaults.BurstMaxDurationSeconds;
    public int HistorySize { get; init; } = Defaults.HistorySize;
    public int MinUsableSample { get; init; } = Defaults.MinUsableSample;
    public double RespondingThreshold { get; init; } = Defaults.RespondingThreshold;
    public double FalteringThreshold { get; init; } = Defaults.FalteringThreshold;
    public double DownThreshold { get; init; } = Defaults.DownThreshold;
    public double RecoveringThreshold { get; init; } = Defaults.RecoveringThreshold;
}
```

(A private static field is not part of a record's equality, `ToString` or JSON.)

`Libraries/ConPersistence/Entities/ConfigRows.cs`: add `using ConEngine.Configuration;` at the top and replace `ProfileRow` with:

```csharp
/// <summary>Ping profile as stored; mirrors PingProfileConfig in seconds-based units
/// and starts from its defaults.</summary>
public class ProfileRow
{
    private static readonly PingProfileConfig Defaults = new();

    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public double NormalIntervalSeconds { get; set; } = Defaults.NormalIntervalSeconds;
    public double PingTimeoutSeconds { get; set; } = Defaults.PingTimeoutSeconds;
    public double BurstIntervalSeconds { get; set; } = Defaults.BurstIntervalSeconds;
    public double BurstMaxDurationSeconds { get; set; } = Defaults.BurstMaxDurationSeconds;
    public int HistorySize { get; set; } = Defaults.HistorySize;
    public int MinUsableSample { get; set; } = Defaults.MinUsableSample;
    public double RespondingThreshold { get; set; } = Defaults.RespondingThreshold;
    public double FalteringThreshold { get; set; } = Defaults.FalteringThreshold;
    public double DownThreshold { get; set; } = Defaults.DownThreshold;
    public double RecoveringThreshold { get; set; } = Defaults.RecoveringThreshold;
}
```

(EF maps public instance properties only; the static field is invisible to the model, which Task 5's test confirms in Step 4.)

`Libraries/ConPersistence/Config/ConfigMapping.cs`: replace the whole `ToConfig(this ProfileRow p)` method with:

```csharp
    public static PingProfileConfig ToConfig(this ProfileRow p) => p.ToDto().ToConfig();
```

- [x] **Step 4: Run to verify**

Run: `dotnet test Tests/ConPersistence.Tests -c Debug --filter "FullyQualifiedName~ProfileMappingTests|FullyQualifiedName~MigrationsTests"` → 4 passed.
Run: `dotnet build ConnectionWatcher.sln -c Debug` → `0 Warning(s)`; full suite → 0 failed.

- [x] **Step 5: Commit**

```powershell
git add Libraries/ConPersistence/Config Libraries/ConPersistence/Entities/ConfigRows.cs Tests/ConPersistence.Tests/ProfileMappingTests.cs
git commit -m "refactor: one profile row/DTO mapping pair; defaults live on PingProfileConfig"
```

---

### Task 8: Section 3, `TransitionDto`: one transition shape, one timestamp encoding

**Files:**
- Create: `Libraries/ConPersistence/Queries/TransitionDto.cs`
- Modify: `Libraries/ConNotify/Builders.cs` (`WebhookPayload.ToJson`; used by both the webhook and the MQTT `<base>/transitions` topic)
- Modify: `Programs/ConWatch/StatusBroadcaster.cs` (`OnTransitionAsync`)
- Modify: `Programs/ConWatch/Api/HistoryEndpoints.cs` (`GET /transitions`)
- Modify: `Programs/ConWatch/Mcp/ConWatchMcpTools.cs` (`get_transitions`)
- Create: `Tests/ConPersistence.Tests/TransitionDtoTests.cs`
- Modify: `Tests/ConWatch.Tests/HistoryApiTests.cs`, `Tests/ConWatch.Tests/SignalRTests.cs`, `Tests/ConWatch.Tests/McpEndpointTests.cs`, `Tests/ConNotify.Tests/BuildersTests.cs`

**Interfaces:**
- Consumes: Task 2's `ConPersistence.Queries` namespace; `ConEngine.Events.TargetTransition`; `ConPersistence.Entities.TransitionRow`.
- Produces: `ConPersistence.Queries.TransitionDto` (record: `long? Id`, `DateTimeOffset Timestamp`, `Guid SubjectId`, `string SubjectName`, `string SubjectKind`, `string FromState` (JSON `from`), `string ToState` (JSON `to`), `string Trigger`, `double? SuccessRate`, `int? SampleSize`, `double? BurstLossRate`, `Guid? AncestorId`, `string? Detail`, `string? Verdict`, `float AvgRtt`), `TransitionDto.From(TargetTransition)`, `TransitionDto.From(TransitionRow)`.

What each surface emits today (read at `78bf8e8`, encoding verified with a scratch serializer run):

| Surface | Today | After |
|---|---|---|
| `GET /api/v1/transitions` items | `id, timestamp (+00:00), subjectId, subjectName, subjectKind, from, to, trigger, successRate, sampleSize, burstLossRate, ancestorId, detail, verdict, avgRtt` | identical bytes (pinned) |
| SignalR `transition` | same keys minus `id`, `ancestorId`, `avgRtt`; `timestamp` `+00:00` | gains `ancestorId`, `avgRtt`; `timestamp` unchanged (pinned) |
| Webhook body and MQTT `<base>/transitions` | same key set as SignalR; `timestamp` a string `2026-08-24T12:00:00.000Z`-style (`FFF'Z'`) | the SignalR shape; `timestamp` `2026-08-24T12:00:00.5+00:00` (full precision, `+00:00`) — deliberate, pinned |
| MCP `get_transitions` | `timestampUtc (Z), subjectName, subjectKind, fromState, toState, trigger, detail, verdict` | the REST item shape — deliberate, pinned |

`Id` is omitted when null (live messages have no log row). Timestamps are forced to offset zero on the way in: a row's `TimestampUtc` is tagged `DateTimeKind.Utc` before conversion, so a row that reaches the DTO with `Kind=Unspecified` is still read as UTC, never as server-local time (`new DateTimeOffset(unspecified)` would apply the local offset).

- [x] **Step 1: Pin the REST and SignalR shapes (these pass today)**

In `Tests/ConWatch.Tests/HistoryApiTests.cs` add `using ConPersistence;`, `using ConPersistence.Entities;`, `using Microsoft.EntityFrameworkCore;` and this test:

```csharp
    [Fact]
    public async Task Transitions_endpoint_wire_shape_is_pinned()
    {
        var dbFactory = _factory.Services.GetRequiredService<IDbContextFactory<ConWatchDbContext>>();
        long id;
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            var row = new TransitionRow
            {
                TimestampUtc = new DateTime(2026, 9, 18, 10, 0, 0, 123, DateTimeKind.Utc),
                SubjectId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                SubjectName = "Router", SubjectKind = "Target",
                FromState = "Responding", ToState = "Down", Trigger = "ThresholdCrossed",
                SuccessRate = 0.1, SampleSize = 10, AvgRtt = 12.5f,
            };
            db.Transitions.Add(row);
            await db.SaveChangesAsync();
            id = row.Id;
        }

        var body = await GetJson("/api/v1/transitions?limit=1");

        Assert.Equal(
            $$"""{"id":{{id}},"timestamp":"2026-09-18T10:00:00.123+00:00","subjectId":"11111111-1111-1111-1111-111111111111","subjectName":"Router","subjectKind":"Target","from":"Responding","to":"Down","trigger":"ThresholdCrossed","successRate":0.1,"sampleSize":10,"burstLossRate":null,"ancestorId":null,"detail":null,"verdict":null,"avgRtt":12.5}""",
            body.GetProperty("items")[0].GetRawText());
    }
```

(The test's factory has no targets, so the engine writes no transitions of its own.)

In `Tests/ConWatch.Tests/SignalRTests.cs` add:

```csharp
    [Fact]
    public async Task Transition_messages_carry_utc_offset_timestamps()
    {
        await ConfigApiTests.SeedHomeAsync(_client);
        await ConfigApiTests.Eventually(() =>
        {
            lock (_transitions)
                return Task.FromResult(_transitions.Count > 0);
        });

        lock (_transitions)
            Assert.All(_transitions, t => Assert.Matches(
                @"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(\.\d+)?\+00:00$", t.GetProperty("timestamp").GetString()));
    }
```

Run: `dotnet test Tests/ConWatch.Tests -c Debug --filter "FullyQualifiedName~Transitions_endpoint_wire_shape_is_pinned|FullyQualifiedName~Transition_messages_carry_utc_offset_timestamps"`
Expected: 2 passed.

- [x] **Step 2: Write the tests for the new type and the deliberate changes**

`Tests/ConPersistence.Tests/TransitionDtoTests.cs`:

```csharp
using System.Text.Json;
using ConEngine.Events;
using ConEngine.Tracking;
using ConPersistence.Entities;
using ConPersistence.Queries;

namespace ConPersistence.Tests;

public class TransitionDtoTests
{
    private static readonly Guid Subject = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Ancestor = Guid.Parse("33333333-3333-3333-3333-333333333333");

    [Fact]
    public void Live_transition_maps_every_field_and_normalizes_the_offset()
    {
        var t = new TargetTransition
        {
            Kind = TransitionSubjectKind.Target, TargetId = Subject, TargetName = "Modem",
            From = HostState.Responding, To = HostState.Unreachable,
            Timestamp = new DateTimeOffset(2026, 9, 18, 12, 0, 0, TimeSpan.FromHours(2)),
            Reason = new TransitionReason
            {
                Trigger = TransitionTrigger.ParentDown, AncestorId = Ancestor,
                SuccessRate = 0.5, SampleSize = 10, BurstLossRate = 0.25, Detail = "d", Verdict = null,
            },
            AverageRoundtripTime = 7.5f,
        };

        var dto = TransitionDto.From(t);

        Assert.Null(dto.Id);
        Assert.Equal(new DateTimeOffset(2026, 9, 18, 10, 0, 0, TimeSpan.Zero), dto.Timestamp);
        Assert.Equal(TimeSpan.Zero, dto.Timestamp.Offset);
        Assert.Equal(Subject, dto.SubjectId);
        Assert.Equal("Modem", dto.SubjectName);
        Assert.Equal("Target", dto.SubjectKind);
        Assert.Equal("Responding", dto.FromState);
        Assert.Equal("Unreachable", dto.ToState);
        Assert.Equal("ParentDown", dto.Trigger);
        Assert.Equal(0.5, dto.SuccessRate);
        Assert.Equal(10, dto.SampleSize);
        Assert.Equal(0.25, dto.BurstLossRate);
        Assert.Equal(Ancestor, dto.AncestorId);
        Assert.Equal("d", dto.Detail);
        Assert.Null(dto.Verdict);
        Assert.Equal(7.5f, dto.AvgRtt);
    }

    [Fact]
    public void Row_timestamp_without_a_kind_is_read_as_utc()
    {
        var row = new TransitionRow
        {
            Id = 5, SubjectId = Subject, SubjectName = "Router", SubjectKind = "Target",
            FromState = "Starting", ToState = "Responding", Trigger = "ThresholdCrossed",
            TimestampUtc = new DateTime(2026, 9, 18, 10, 0, 0, 123, DateTimeKind.Unspecified),
        };

        var dto = TransitionDto.From(row);

        Assert.Equal(5, dto.Id);
        Assert.Equal(new DateTimeOffset(2026, 9, 18, 10, 0, 0, 123, TimeSpan.Zero), dto.Timestamp);
    }

    [Fact]
    public void Live_json_has_from_and_to_and_no_id()
    {
        var json = JsonSerializer.SerializeToElement(TransitionDto.From(new TargetTransition
        {
            TargetId = Subject, TargetName = "Router", From = HostState.Starting, To = HostState.Responding,
            Timestamp = DateTimeOffset.Parse("2026-09-18T10:00:00.5Z"),
            Reason = new TransitionReason { Trigger = TransitionTrigger.ThresholdCrossed },
        }), JsonSerializerOptions.Web);

        Assert.False(json.TryGetProperty("id", out _));
        Assert.Equal("Starting", json.GetProperty("from").GetString());
        Assert.Equal("Responding", json.GetProperty("to").GetString());
        Assert.Equal("2026-09-18T10:00:00.5+00:00", json.GetProperty("timestamp").GetString());
    }
}
```

In `Tests/ConNotify.Tests/BuildersTests.cs`, class `WebhookPayloadTests`, add:

```csharp
    [Fact]
    public void Payload_is_the_transition_dto_with_a_utc_offset_timestamp()
    {
        var t = new TargetTransition
        {
            Kind = TransitionSubjectKind.Target,
            TargetId = Guid.Parse("44444444-4444-4444-4444-444444444444"),
            TargetName = "Router",
            From = HostState.Responding,
            To = HostState.Down,
            Timestamp = DateTimeOffset.Parse("2026-08-24T12:00:00.5Z"),
            Reason = new TransitionReason { Trigger = TransitionTrigger.ThresholdCrossed, SuccessRate = 0.1, SampleSize = 10 },
            AverageRoundtripTime = 12.5f,
        };

        var json = JsonSerializer.Deserialize<JsonElement>(WebhookPayload.ToJson(t));

        Assert.Equal("2026-08-24T12:00:00.5+00:00", json.GetProperty("timestamp").GetString());
        Assert.False(json.TryGetProperty("id", out _));
        Assert.Equal(12.5, json.GetProperty("avgRtt").GetDouble());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("ancestorId").ValueKind);
        Assert.Equal("Down", json.GetProperty("to").GetString());
    }
```

In `Tests/ConWatch.Tests/McpEndpointTests.cs` add:

```csharp
    [Fact]
    public async Task Transitions_tool_speaks_the_rest_transition_shape()
    {
        await ConfigApiTests.SeedHomeAsync(_client);
        await ConfigApiTests.Eventually(async () =>
        {
            var status = await _client.GetAsync("/api/v1/status");
            return status.IsSuccessStatusCode &&
                   (await status.Content.ReadAsStringAsync()).Contains("Healthy");
        });
        await _factory.Services.GetRequiredService<ConPersistence.Recording.TransitionRecorder>().FlushAsync();
        await InitializeAsync();

        var text = ToolText(await RpcAsync("tools/call", new
        {
            name = "get_transitions",
            arguments = new { limit = 10 },
        }));

        var first = JsonSerializer.Deserialize<JsonElement>(text)[0];
        foreach (var key in new[] { "id", "timestamp", "subjectId", "subjectName", "subjectKind", "from", "to", "trigger" })
            Assert.True(first.TryGetProperty(key, out _), $"missing '{key}'");
        Assert.Matches(@"\+00:00$", first.GetProperty("timestamp").GetString());
    }
```

(and `using Microsoft.Extensions.DependencyInjection;` at the top of `McpEndpointTests.cs` if it is not there.)

- [x] **Step 3: Run to verify they fail**

Run: `dotnet test ConnectionWatcher.sln -c Debug --filter "FullyQualifiedName~TransitionDtoTests|FullyQualifiedName~Payload_is_the_transition_dto|FullyQualifiedName~Transitions_tool_speaks"`
Expected: build error (`TransitionDto` does not exist). Temporarily excluding `TransitionDtoTests.cs` would show the webhook test failing on `"2026-08-24T12:00:00.500Z"` and the MCP test failing on a missing `from`; there is no need to do that.

- [x] **Step 4: Write the DTO**

`Libraries/ConPersistence/Queries/TransitionDto.cs`:

```csharp
using System.Text.Json.Serialization;
using ConEngine.Events;
using ConPersistence.Entities;

namespace ConPersistence.Queries;

/// <summary>
/// The one wire shape of a state transition, whether read back from the log
/// (GET /api/v1/transitions, the MCP get_transitions tool) or pushed live
/// (SignalR "transition", webhooks, MQTT). Timestamp is a DateTimeOffset at
/// offset zero, which System.Text.Json writes as
/// "2026-09-18T10:00:00.123+00:00" — the encoding the REST endpoint and the
/// SignalR message already used.
/// </summary>
public sealed record TransitionDto
{
    /// <summary>The log row id (the paging cursor); absent on live messages.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Id { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required Guid SubjectId { get; init; }
    public required string SubjectName { get; init; }
    public required string SubjectKind { get; init; }
    [JsonPropertyName("from")]
    public required string FromState { get; init; }
    [JsonPropertyName("to")]
    public required string ToState { get; init; }
    public required string Trigger { get; init; }
    public double? SuccessRate { get; init; }
    public int? SampleSize { get; init; }
    public double? BurstLossRate { get; init; }
    public Guid? AncestorId { get; init; }
    public string? Detail { get; init; }
    public string? Verdict { get; init; }
    public float AvgRtt { get; init; }

    public static TransitionDto From(TargetTransition t) => new()
    {
        Timestamp = t.Timestamp.ToUniversalTime(),
        SubjectId = t.TargetId,
        SubjectName = t.TargetName,
        SubjectKind = t.Kind.ToString(),
        FromState = t.From.ToString(),
        ToState = t.To.ToString(),
        Trigger = t.Reason.Trigger.ToString(),
        SuccessRate = t.Reason.SuccessRate,
        SampleSize = t.Reason.SampleSize,
        BurstLossRate = t.Reason.BurstLossRate,
        AncestorId = t.Reason.AncestorId,
        Detail = t.Reason.Detail,
        Verdict = t.Reason.Verdict,
        AvgRtt = t.AverageRoundtripTime,
    };

    public static TransitionDto From(TransitionRow r) => new()
    {
        Id = r.Id,
        // Tagged explicitly: every *Utc column is UTC by rule, and a value that
        // reaches here as Unspecified must not pick up the server's offset.
        Timestamp = new DateTimeOffset(DateTime.SpecifyKind(r.TimestampUtc, DateTimeKind.Utc)),
        SubjectId = r.SubjectId,
        SubjectName = r.SubjectName,
        SubjectKind = r.SubjectKind,
        FromState = r.FromState,
        ToState = r.ToState,
        Trigger = r.Trigger,
        SuccessRate = r.SuccessRate,
        SampleSize = r.SampleSize,
        BurstLossRate = r.BurstLossRate,
        AncestorId = r.AncestorId,
        Detail = r.Detail,
        Verdict = r.Verdict,
        AvgRtt = r.AvgRtt,
    };
}
```

(Property order is the REST order; System.Text.Json writes properties in declaration order, so the pinned REST bytes do not change.)

- [x] **Step 5: Use it everywhere**

`Libraries/ConNotify/Builders.cs`: add `using ConPersistence.Queries;` and replace the body of `WebhookPayload`:

```csharp
/// <summary>Webhook + MQTT transition-event body: the shared transition DTO.</summary>
public static class WebhookPayload
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string ToJson(TargetTransition t) =>
        JsonSerializer.Serialize(TransitionDto.From(t), Options);
}
```

`Programs/ConWatch/StatusBroadcaster.cs`: add `using ConPersistence.Queries;`; in `OnTransitionAsync` replace the `SendAsync("transition", new { … })` call with:

```csharp
            await hub.Clients.All.SendAsync("transition", TransitionDto.From(t));
```

`Programs/ConWatch/Api/HistoryEndpoints.cs`: in `GET /transitions` replace the `var items = rows.Select(r => new { … }).ToList();` statement with:

```csharp
            var items = rows.Select(r => TransitionDto.From(r)).ToList();
```

`Programs/ConWatch/Mcp/ConWatchMcpTools.cs`, `GetTransitions`: replace the `return JsonSerializer.Serialize(rows.Select(t => new { … }), Json);` statement with:

```csharp
        return JsonSerializer.Serialize(rows.Select(r => TransitionDto.From(r)), Json);
```

and in its `[Description(...)]` replace `"Times are ISO-8601 UTC."` with `"Rows have the REST /api/v1/transitions shape; times are ISO-8601 UTC (+00:00)."`. (`ConPersistence.Queries` is already imported there since Task 2.)

- [x] **Step 6: Run to verify**

Run: `dotnet test ConnectionWatcher.sln -c Debug --filter "FullyQualifiedName~TransitionDtoTests|FullyQualifiedName~WebhookPayloadTests|FullyQualifiedName~McpEndpointTests|FullyQualifiedName~Transitions_endpoint_wire_shape_is_pinned|FullyQualifiedName~Transition_messages_carry_utc_offset_timestamps|FullyQualifiedName~HistoryApiTests|FullyQualifiedName~SignalRTests|FullyQualifiedName~NotifierIntegrationTests"`
Expected: all pass (the existing `Payload_carries_the_transition` checks only the first 19 characters of the timestamp and stays green).
Run: `dotnet build ConnectionWatcher.sln -c Debug` → `0 Warning(s)`; full suite → 0 failed; `npx vitest run` in `Programs/ConWatch/ClientApp` → 56 passed (`TransitionEvent` in `lib/types.ts` already treats `id` as optional and ignores unknown keys).

- [x] **Step 7: Commit**

```powershell
git add Libraries/ConPersistence/Queries/TransitionDto.cs Libraries/ConNotify/Builders.cs Programs/ConWatch/StatusBroadcaster.cs Programs/ConWatch/Api/HistoryEndpoints.cs Programs/ConWatch/Mcp/ConWatchMcpTools.cs Tests/ConPersistence.Tests/TransitionDtoTests.cs Tests/ConWatch.Tests/HistoryApiTests.cs Tests/ConWatch.Tests/SignalRTests.cs Tests/ConWatch.Tests/McpEndpointTests.cs Tests/ConNotify.Tests/BuildersTests.cs
git commit -m "refactor: one TransitionDto for REST, SignalR, MQTT, webhooks and MCP"
```

---

### Task 9: Section 3, `ApiResults.Error`: one error body

**Files:**
- Modify: `Programs/ConWatch/Api/ApiResults.cs`
- Modify: `Programs/ConWatch/Api/SetupEndpoints.cs` (delete the private `ToHttp(CloneOutcome)`)
- Modify: `Programs/ConWatch/Api/StatusEndpoints.cs` (the 503)
- Modify: every other hand-built error body: `Programs/ConWatch/Api/AdminEndpoints.cs`, `AuthEndpoints.cs`, `ExportEndpoints.cs`, `HistoryEndpoints.cs`, `SettingsEndpoints.cs`, `TimeRange.cs`, `TracerouteEndpoints.cs`, `Programs/ConWatch/Auth/RequireAdminFilter.cs`
- Create: `Tests/ConWatch.Tests/ErrorShapeTests.cs`

**Interfaces:**
- Consumes: Task 2 namespaces; `ConWatch.Clone.CloneOutcome`.
- Produces: `ApiResults.ErrorBody(IReadOnlyList<string> Errors, string? Code = null)`; `ApiResults.Error(int status, string? code, params string[] messages) → IResult`; `ApiResults.ToHttp(this ConfigResult, Func<ConfigResult, IResult>? onSuccess = null)` (unchanged signature); new `ApiResults.ToHttp(this CloneOutcome)`.

Decision (see table): one body, `{ "errors": [...] }` plus `"code"` only when non-null. The only wire change is `GET /api/v1/status`'s 503, from `{ "error": "Engine is not running" }` to `{ "errors": ["Engine is not running"] }`.

- [x] **Step 1: Write the tests**

`Tests/ConWatch.Tests/ErrorShapeTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ConPersistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ConWatch.Tests;

/// <summary>Every error body is { "errors": [...] }, plus "code" only where a
/// client branches on it (the clone and take-over flows).</summary>
public class ErrorShapeTests : IDisposable
{
    private readonly ConWatchFactory _factory = new();
    private readonly HttpClient _client;

    public ErrorShapeTests() => _client = _factory.CreateClient();

    public void Dispose() => _factory.Dispose();

    private static async Task AssertErrorBody(HttpResponseMessage response, HttpStatusCode status,
        string? code, params string[] errors)
    {
        Assert.Equal(status, response.StatusCode);
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        string[] expectedKeys = code is null ? ["errors"] : ["code", "errors"];
        Assert.Equal(expectedKeys, body.EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.Equal(errors, body.GetProperty("errors").EnumerateArray().Select(e => e.GetString()!).ToArray());
        if (code is not null)
            Assert.Equal(code, body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Bad_request_is_errors_only() =>
        await AssertErrorBody(await _client.GetAsync("/api/v1/export/transitions?format=xml"),
            HttpStatusCode.BadRequest, null, "format must be 'csv' or 'json'.");

    [Fact]
    public async Task Config_not_found_is_errors_only()
    {
        var id = Guid.NewGuid();
        await AssertErrorBody(await _client.DeleteAsync($"/api/v1/targets/{id}"),
            HttpStatusCode.NotFound, null, $"No target with id {id}.");
    }

    [Fact]
    public async Task Clone_failure_carries_its_code() =>
        await AssertErrorBody(
            await _client.PostAsJsonAsync("/api/v1/setup/clone", new { sourceUrl = "not a url" }),
            HttpStatusCode.BadRequest, "bad-source-url",
            "The source must be an absolute http(s) URL, e.g. https://conwatch.example.com/ " +
            "or http://192.168.1.5:8080.");

    [Fact]
    public async Task Status_without_an_engine_uses_the_same_shape()
    {
        await ConfigApiTests.SeedHomeAsync(_client);
        var host = _factory.Services.GetRequiredService<WatcherHostService>();
        await ConfigApiTests.Eventually(() => Task.FromResult(host.Engine is not null));

        // Straight to the database, bypassing the validating API, so the next
        // rebuild refuses to start an engine while the node stays enabled.
        var dbFactory = _factory.Services.GetRequiredService<IDbContextFactory<ConWatchDbContext>>();
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            var profile = await db.Profiles.SingleAsync(p => p.Name == "Fast");
            profile.RespondingThreshold = 0.5;
            profile.FalteringThreshold = 0.75;
            await db.SaveChangesAsync();
        }
        Assert.False(await host.RebuildAsync());

        await AssertErrorBody(await _client.GetAsync("/api/v1/status"),
            HttpStatusCode.ServiceUnavailable, null, "Engine is not running");
    }
}
```

- [x] **Step 2: Run to verify which pass and which fail**

Run: `dotnet test Tests/ConWatch.Tests -c Debug --filter "FullyQualifiedName~ErrorShapeTests"`
Expected: `Bad_request…`, `Config_not_found…`, `Clone_failure…` PASS (they pin today's shapes); `Status_without_an_engine_uses_the_same_shape` FAILS (keys are `["error"]`).

- [x] **Step 3: One body**

Replace `Programs/ConWatch/Api/ApiResults.cs` with:

```csharp
using System.Text.Json.Serialization;
using ConPersistence.Config;
using ConWatch.Clone;

namespace ConWatch.Api;

/// <summary>The API's one error body, and the mappings from service outcomes onto HTTP.</summary>
public static class ApiResults
{
    /// <summary>{ "errors": [...] }, plus "code" when a client branches on the
    /// failure (the clone and take-over flows do).</summary>
    public sealed record ErrorBody(IReadOnlyList<string> Errors,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Code = null);

    public static IResult Error(int status, string? code, params string[] messages) =>
        Results.Json(new ErrorBody(messages, code), statusCode: status);

    public static IResult ToHttp(this ConfigResult result, Func<ConfigResult, IResult>? onSuccess = null)
    {
        if (result.Success)
            return onSuccess?.Invoke(result) ?? Results.NoContent();
        var errors = result.Errors?.ToArray() ?? [];
        return result.Kind switch
        {
            ConfigErrorKind.NotFound => Error(StatusCodes.Status404NotFound, null, errors),
            ConfigErrorKind.Conflict => Error(StatusCodes.Status409Conflict, null, errors),
            _ => Error(StatusCodes.Status400BadRequest, null, errors),
        };
    }

    /// <summary>Every failed CloneOutcome comes from CloneOutcome.Fail, which always sets a code.</summary>
    public static IResult ToHttp(this CloneOutcome outcome) =>
        outcome.Status == StatusCodes.Status200OK
            ? Results.Ok(outcome.Body)
            : Error(outcome.Status, outcome.ErrorCode, outcome.Error ?? "");
}
```

- [x] **Step 4: Replace every hand-built error body**

Each line below is the whole `return`/assignment as it reads at `78bf8e8`, then its replacement.

`Programs/ConWatch/Api/StatusEndpoints.cs`:
```csharp
            return Results.Json(new { error = "Engine is not running" }, statusCode: 503);
```
→
```csharp
            return ApiResults.Error(StatusCodes.Status503ServiceUnavailable, null, "Engine is not running");
```

`Programs/ConWatch/Api/SetupEndpoints.cs`: delete the whole private `ToHttp(CloneOutcome outcome)` method; change the two clone endpoints to call the extension:
```csharp
        api.MapPost("/setup/clone", async (CloneRequest request, CloneService clone,
            CancellationToken ct) => (await clone.CloneAsync(request.SourceUrl,
                request.SourcePassword, request.IncludeSecrets, request.AllowInsecure, ct)).ToHttp());

        api.MapPost("/setup/takeover", async (TakeoverRequest request, CloneService clone,
            CancellationToken ct) => (await clone.TakeOverAsync(request.SourcePassword,
                request.AllowInsecure, ct)).ToHttp());
```
and `return Results.BadRequest(new { errors = new[] { "A router address is required." } });` → `return ApiResults.Error(StatusCodes.Status400BadRequest, null, "A router address is required.");`

`Programs/ConWatch/Api/AdminEndpoints.cs` (two identical 500s, in `/admin/engine` and `/admin/factory-reset`):
```csharp
                return Results.Json(new { errors = new[] { "The engine could not be rebuilt; see the log." } },
                    statusCode: 500);
```
→
```csharp
                return ApiResults.Error(StatusCodes.Status500InternalServerError, null,
                    "The engine could not be rebuilt; see the log.");
```

Every remaining `Results.BadRequest(new { errors = new[] { MESSAGE } })` becomes `ApiResults.Error(StatusCodes.Status400BadRequest, null, MESSAGE)` with MESSAGE copied unchanged. At `78bf8e8` these are: `AdminEndpoints.cs` ("Host is required.", "Port must be 1–65535.", "Confirmation must be exactly 'PRUNE'.", "A cutoff date is required.", "Confirmation must be exactly 'CLEAR'.", "Confirmation must be exactly 'RESET'."); `AuthEndpoints.cs` ("No admin password is set." twice, "Password must be at least 4 characters."); `ExportEndpoints.cs` `BadFormat()` ("format must be 'csv' or 'json'."); `HistoryEndpoints.cs` ("'from' and 'to' are required.", `ex.Message`); `TimeRange.cs` (the two `problem = …` assignments, `$"'from' is not a valid timestamp: {from}"` and `$"'to' is not a valid timestamp: {to}"`); `TracerouteEndpoints.cs` ("'host' is required.", `$"Cannot resolve '{request.Host}': {ex.Message}"`).

`Programs/ConWatch/Api/SettingsEndpoints.cs`:
```csharp
                return Results.BadRequest(new { errors });
```
→
```csharp
                return ApiResults.Error(StatusCodes.Status400BadRequest, null, [.. errors]);
```

`Programs/ConWatch/Auth/RequireAdminFilter.cs`: add `using ConWatch.Api;` and
```csharp
            return Results.Json(new { errors = new[] { "Admin password required." } }, statusCode: 401);
```
→
```csharp
            return ApiResults.Error(StatusCodes.Status401Unauthorized, null, "Admin password required.");
```

Then confirm nothing hand-built is left:
```powershell
git grep -n -e "new { errors" -e "new { error " -e "errors = new\[\]" -- Programs/ConWatch
```
Expected: no output.

- [x] **Step 5: Run to verify**

Run: `dotnet test Tests/ConWatch.Tests -c Debug --filter "FullyQualifiedName~ErrorShapeTests"` → 4 passed.
Run: `dotnet build ConnectionWatcher.sln -c Debug` → `0 Warning(s)`; full suite → 0 failed (`CloneTests`, `AdminLockTests`, `SettingsApiTests`, `TimeQueryApiTests` read `errors`/`code` and stay green).

- [x] **Step 6: Commit**

```powershell
git add Programs/ConWatch/Api Programs/ConWatch/Auth/RequireAdminFilter.cs Tests/ConWatch.Tests/ErrorShapeTests.cs
git commit -m "refactor: one ApiResults.Error body; /status 503 joins the errors shape"
```

---

### Task 10: Section 3, pin the snapshot's wire shape

**Files:**
- Create: `Tests/ConPersistence.Tests/SnapshotShapeTests.cs`

**Interfaces:**
- Consumes: Task 2's `ConPersistence.Snapshots.Snapshot` and `ConPersistence.Config` DTOs.
- Produces: a guard; no production change.

Decision (see table): a shape-pinning test, not explicit snapshot records. The snapshot serializes the EF entity classes (`TransitionRow`, `RollupRow`, `GlitchRow`) directly; the test fails on any added, removed or renamed key and tells the author to decide whether `SchemaVersion` moves. It serializes with `JsonSerializerOptions.Web`, the options `Results.Ok` uses for `/export/snapshot` and `CloneService` uses to read it back.

- [x] **Step 1: Write the test**

`Tests/ConPersistence.Tests/SnapshotShapeTests.cs`:

```csharp
using System.Text.Json;
using ConPersistence.Config;
using ConPersistence.Entities;
using ConPersistence.Snapshots;

namespace ConPersistence.Tests;

/// <summary>
/// The snapshot document is a wire format (clone, take-over, real-data
/// tests) serialized straight from the EF entity classes. A column change
/// changes it silently while SchemaVersion stays 1. When this test fails,
/// update the expected keys AND decide in the same change whether
/// Snapshot.SchemaVersion (and SnapshotImporter's accepted version) must move.
/// </summary>
public class SnapshotShapeTests
{
    private static void AssertKeys(JsonElement element, params string[] expected) =>
        Assert.Equal(expected.Order(StringComparer.Ordinal).ToArray(),
            element.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray());

    [Fact]
    public void Snapshot_json_shape_is_pinned()
    {
        var now = new DateTime(2026, 9, 25, 0, 0, 0, DateTimeKind.Utc);
        var snapshot = new Snapshot
        {
            ExportedUtc = new DateTimeOffset(now),
            Config = new ConfigDocument
            {
                Profiles = [new ProfileDto { Id = Guid.NewGuid(), Name = "P" }],
                Targets = [new TargetDto { Id = Guid.NewGuid(), Name = "T", Address = "10.0.0.1", Profile = "P" }],
                Pools = [new PoolDto { Id = Guid.NewGuid(), Name = "Pool", Members = ["T"] }],
            },
            Settings = new() { ["Verdict.AnchorPool"] = "Pool" },
            RedactedSettings = ["Mqtt.Password"],
            // Optional columns left null on purpose: a null must still be a key.
            Transitions = [new TransitionRow { Id = 1, TimestampUtc = now, SubjectName = "T" }],
            Rollups = [new RollupRow { Id = 1, PeriodStartUtc = now }],
            Glitches = [new GlitchRow { Id = 1, StartUtc = now, EndUtc = now, TargetName = "T" }],
        };

        var json = JsonSerializer.SerializeToElement(snapshot, JsonSerializerOptions.Web);

        AssertKeys(json, "schemaVersion", "exportedUtc", "config", "settings", "redactedSettings",
            "transitions", "rollups", "glitches");
        AssertKeys(json.GetProperty("config"), "profiles", "targets", "pools");
        AssertKeys(json.GetProperty("config").GetProperty("profiles")[0],
            "id", "name", "normalIntervalSeconds", "pingTimeoutSeconds", "burstIntervalSeconds",
            "burstMaxDurationSeconds", "historySize", "minUsableSample", "respondingThreshold",
            "falteringThreshold", "downThreshold", "recoveringThreshold");
        AssertKeys(json.GetProperty("config").GetProperty("targets")[0],
            "id", "name", "address", "parent", "profile", "enabled", "operator", "probe");
        AssertKeys(json.GetProperty("config").GetProperty("pools")[0], "id", "name", "members", "parent");
        AssertKeys(json.GetProperty("transitions")[0],
            "id", "timestampUtc", "subjectId", "subjectName", "subjectKind", "fromState", "toState",
            "trigger", "successRate", "sampleSize", "burstLossRate", "ancestorId", "detail", "verdict", "avgRtt");
        AssertKeys(json.GetProperty("rollups")[0],
            "id", "targetId", "periodStartUtc", "resolution", "pingCount", "successCount", "rttAvg", "rttMin", "rttMax");
        AssertKeys(json.GetProperty("glitches")[0],
            "id", "targetId", "targetName", "class", "startUtc", "endUtc", "lostCount", "sampleCount", "detail");
        Assert.Equal(1, json.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("2026-09-25T00:00:00Z", json.GetProperty("transitions")[0].GetProperty("timestampUtc").GetString());
    }
}
```

- [x] **Step 2: Run it**

Run: `dotnet test Tests/ConPersistence.Tests -c Debug --filter "FullyQualifiedName~SnapshotShapeTests"`
Expected: PASS (a characterization of today's document).

- [x] **Step 3: Prove it bites, then revert**

Temporarily rename `GlitchRow.Detail` to `Details` in `Libraries/ConPersistence/Entities/DataRows.cs` and its uses the compiler reports. Run the filter: expected FAIL on the glitch keys. Revert (`git checkout -- Libraries`), rebuild.

- [x] **Step 4: Full suite and commit**

Run: `dotnet build ConnectionWatcher.sln -c Debug` → `0 Warning(s)`; `dotnet test ConnectionWatcher.sln -c Debug` → 0 failed.

```powershell
git add Tests/ConPersistence.Tests/SnapshotShapeTests.cs
git commit -m "test: pin the snapshot document's JSON shape"
```

---

### Task 11: Section 3, exports stream rows to the response

**Files:**
- Modify: `Libraries/ConPersistence/Export/CsvExport.cs` (header constants + per-row line functions; the `IEnumerable` helpers stay, built on them)
- Modify: `Programs/ConWatch/Api/ExportEndpoints.cs` (the three measurement exports)
- Modify: `Tests/ConWatch.Tests/ExportApiTests.cs`

**Interfaces:**
- Consumes: Task 2 namespaces; Task 9's `ApiResults.Error` (already used by `BadFormat()`).
- Produces: `CsvExport.TransitionHeader`, `CsvExport.RollupHeader`, `CsvExport.GlitchHeader` (`const string`), `CsvExport.TransitionLine(TransitionRow)`, `CsvExport.RollupLine(RollupRow)`, `CsvExport.GlitchLine(GlitchRow)` (`string`). `TransitionLines/RollupLines/GlitchLines(IEnumerable<…>)` keep their signatures and output (`CsvExportTests` uses them).

The bytes on the wire do not change: CSV is the header, then one line per row, each ending in `\n`, UTF-8 without a BOM, `text/csv`, same download name; JSON is the same array. What changes: the response has no `Content-Length` (chunked), rows are read from the database reader while the body is written, and the `DbContext` lives until the response ends (registered with `HttpResponse.RegisterForDisposeAsync`). `/export/snapshot` is one document and stays as it is (see "Left out").

- [x] **Step 1: Pin today's bytes (these pass today)**

In `Tests/ConWatch.Tests/ExportApiTests.cs` add:

```csharp
    private const string TransitionHeader =
        "id,timestampUtc,subjectId,subjectName,subjectKind,from,to,trigger," +
        "successRate,sampleSize,burstLossRate,ancestorId,detail,verdict,avgRtt";

    [Fact]
    public async Task Transition_csv_bytes_and_download_name_are_pinned()
    {
        var subject = Guid.Parse("11111111-1111-1111-1111-111111111111");
        long first, second;
        using (var scope = _factory.Services.CreateScope())
        {
            var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ConWatchDbContext>>();
            await using var db = await dbFactory.CreateDbContextAsync();
            var a = new TransitionRow
            {
                TimestampUtc = new DateTime(2026, 9, 18, 10, 0, 0, 123, DateTimeKind.Utc),
                SubjectId = subject, SubjectName = "Router", SubjectKind = "Target",
                FromState = "Responding", ToState = "Down", Trigger = "ThresholdCrossed",
                SuccessRate = 0.1, SampleSize = 10, AvgRtt = 12.5f, Detail = "has, comma",
            };
            var b = new TransitionRow
            {
                TimestampUtc = new DateTime(2026, 9, 18, 10, 5, 0, 250, DateTimeKind.Utc),
                SubjectId = subject, SubjectName = "Router", SubjectKind = "Target",
                FromState = "Down", ToState = "Responding", Trigger = "ThresholdCrossed",
            };
            db.Transitions.AddRange(a, b);
            await db.SaveChangesAsync();
            (first, second) = (a.Id, b.Id);
        }

        var response = await _client.GetAsync("/api/v1/export/transitions?format=csv");

        response.EnsureSuccessStatusCode();
        Assert.Equal("text/csv", response.Content.Headers.ContentType!.MediaType);
        var disposition = response.Content.Headers.ContentDisposition!;
        Assert.Matches(@"^conwatch-transitions-\d{8}-\d{6}\.csv$",
            disposition.FileNameStar ?? disposition.FileName!.Trim('"'));
        Assert.Equal(
            TransitionHeader + "\n" +
            $"{first},2026-09-18T10:00:00.123Z,{subject},Router,Target,Responding,Down,ThresholdCrossed,0.1,10,,,\"has, comma\",,12.5\n" +
            $"{second},2026-09-18T10:05:00.25Z,{subject},Router,Target,Down,Responding,ThresholdCrossed,,,,,,,0\n",
            await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Empty_exports_are_a_header_line_or_an_empty_array()
    {
        Assert.Equal(TransitionHeader + "\n",
            await _client.GetStringAsync("/api/v1/export/transitions?format=csv"));
        Assert.Equal("targetId,periodStartUtc,resolution,pingCount,successCount,rttAvg,rttMin,rttMax\n",
            await _client.GetStringAsync("/api/v1/export/rollups?format=csv"));
        Assert.Equal("id,targetId,targetName,class,startUtc,endUtc,lostCount,sampleCount,detail\n",
            await _client.GetStringAsync("/api/v1/export/glitches?format=csv"));
        Assert.Equal("[]", await _client.GetStringAsync("/api/v1/export/transitions?format=json"));
        Assert.Equal("[]", await _client.GetStringAsync("/api/v1/export/rollups?format=json"));
        Assert.Equal("[]", await _client.GetStringAsync("/api/v1/export/glitches?format=json"));
    }

    [Fact]
    public async Task Glitch_export_works_in_both_formats()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ConWatchDbContext>>();
            await using var db = await dbFactory.CreateDbContextAsync();
            db.Glitches.Add(new GlitchRow
            {
                TargetId = Guid.NewGuid(), TargetName = "Router", Class = 1,
                StartUtc = DateTime.UtcNow.AddMinutes(-2), EndUtc = DateTime.UtcNow.AddMinutes(-1),
                LostCount = 2, SampleCount = 10,
            });
            await db.SaveChangesAsync();
        }

        var csv = (await _client.GetStringAsync("/api/v1/export/glitches?format=csv")).TrimEnd('\n').Split('\n');
        Assert.Equal(2, csv.Length);
        Assert.Contains(",Router,Internet,", csv[1]);
        var json = JsonSerializer.Deserialize<JsonElement>(await _client.GetStringAsync("/api/v1/export/glitches?format=json"));
        Assert.Equal(1, json.GetArrayLength());
    }
```

(The class's factory has no targets, so the engine writes no rows of its own. The file already imports `ConPersistence`, `ConPersistence.Entities`, `Microsoft.EntityFrameworkCore`, `Microsoft.Extensions.DependencyInjection`, `System.Text.Json`.)

Run: `dotnet test Tests/ConWatch.Tests -c Debug --filter "FullyQualifiedName~ExportApiTests"`
Expected: all pass (8 tests: 5 existing, 3 new).

- [x] **Step 2: Per-row CSV lines**

In `Libraries/ConPersistence/Export/CsvExport.cs`, replace the three `…Lines` methods with:

```csharp
    public const string TransitionHeader =
        "id,timestampUtc,subjectId,subjectName,subjectKind,from,to,trigger," +
        "successRate,sampleSize,burstLossRate,ancestorId,detail,verdict,avgRtt";

    public const string RollupHeader =
        "targetId,periodStartUtc,resolution,pingCount,successCount,rttAvg,rttMin,rttMax";

    public const string GlitchHeader =
        "id,targetId,targetName,class,startUtc,endUtc,lostCount,sampleCount,detail";

    public static string TransitionLine(TransitionRow r) => Line(
        r.Id.ToString(CultureInfo.InvariantCulture),
        Iso(r.TimestampUtc),
        r.SubjectId.ToString(),
        r.SubjectName,
        r.SubjectKind,
        r.FromState,
        r.ToState,
        r.Trigger,
        Num(r.SuccessRate),
        r.SampleSize?.ToString(CultureInfo.InvariantCulture) ?? "",
        Num(r.BurstLossRate),
        r.AncestorId?.ToString() ?? "",
        r.Detail ?? "",
        r.Verdict ?? "",
        r.AvgRtt.ToString(CultureInfo.InvariantCulture));

    public static string RollupLine(RollupRow r) => Line(
        r.TargetId.ToString(),
        Iso(r.PeriodStartUtc),
        r.Resolution,
        r.PingCount.ToString(CultureInfo.InvariantCulture),
        r.SuccessCount.ToString(CultureInfo.InvariantCulture),
        r.RttAvg.ToString(CultureInfo.InvariantCulture),
        r.RttMin.ToString(CultureInfo.InvariantCulture),
        r.RttMax.ToString(CultureInfo.InvariantCulture));

    public static string GlitchLine(GlitchRow g) => Line(
        g.Id.ToString(CultureInfo.InvariantCulture),
        g.TargetId.ToString(),
        g.TargetName,
        g.Class == 1 ? "Internet" : "Target",
        Iso(g.StartUtc),
        Iso(g.EndUtc),
        g.LostCount.ToString(CultureInfo.InvariantCulture),
        g.SampleCount.ToString(CultureInfo.InvariantCulture),
        g.Detail ?? "");

    public static IEnumerable<string> TransitionLines(IEnumerable<TransitionRow> rows) =>
        rows.Select(TransitionLine).Prepend(TransitionHeader);

    public static IEnumerable<string> RollupLines(IEnumerable<RollupRow> rows) =>
        rows.Select(RollupLine).Prepend(RollupHeader);

    public static IEnumerable<string> GlitchLines(IEnumerable<GlitchRow> rows) =>
        rows.Select(GlitchLine).Prepend(GlitchHeader);
```

(`Iso`, `Num`, `Line`, `Escape` stay as they are.)

Run: `dotnet test Tests/ConPersistence.Tests -c Debug --filter "FullyQualifiedName~CsvExportTests"` → pass.

- [x] **Step 3: Stream the endpoints**

In `Programs/ConWatch/Api/ExportEndpoints.cs`: add `using ConPersistence.Entities;`; change the class summary to `/// <summary>Bulk measurement-data export: CSV or JSON, no row caps. Rows stream from the database reader to the response body; nothing is materialized.</summary>`; replace the three measurement endpoints and the `Csv` helper as below (`/export/snapshot`, `/export/snapshot/full`, `FullSnapshotRequest`, `BuildSnapshotAsync`, `IsValidFormat`, `BadFormat` stay):

```csharp
        api.MapGet("/export/transitions", async (HttpContext http,
            IDbContextFactory<ConWatchDbContext> dbFactory, TimeProvider time, string format = "csv",
            Guid? subjectId = null, string? from = null, string? to = null) =>
        {
            if (!IsValidFormat(format))
                return BadFormat();
            if (!TimeRange.TryParseRange(from, to, out var fromUtc, out var toUtc, out var problem))
                return problem!;
            var db = await OpenForResponseAsync(http, dbFactory);
            IQueryable<TransitionRow> query = db.Transitions.AsNoTracking();
            if (subjectId is { } sid) query = query.Where(t => t.SubjectId == sid);
            if (fromUtc is { } f) query = query.Where(t => t.TimestampUtc >= f);
            if (toUtc is { } u) query = query.Where(t => t.TimestampUtc <= u);
            var rows = query.OrderBy(t => t.Id).AsAsyncEnumerable();

            var stamp = time.GetUtcNow().ToString("yyyyMMdd-HHmmss");
            return IsJson(format)
                ? Results.Json(rows, contentType: "application/json")
                : Csv(CsvExport.TransitionHeader, rows, CsvExport.TransitionLine,
                    $"conwatch-transitions-{stamp}.csv");
        });

        api.MapGet("/export/rollups", async (HttpContext http,
            IDbContextFactory<ConWatchDbContext> dbFactory, TimeProvider time, string format = "csv",
            Guid? targetId = null, string? resolution = null, string? from = null, string? to = null) =>
        {
            if (!IsValidFormat(format))
                return BadFormat();
            if (!TimeRange.TryParseRange(from, to, out var fromUtc, out var toUtc, out var problem))
                return problem!;
            var db = await OpenForResponseAsync(http, dbFactory);
            IQueryable<RollupRow> query = db.Rollups.AsNoTracking();
            if (targetId is { } tid) query = query.Where(r => r.TargetId == tid);
            if (resolution is { Length: > 0 }) query = query.Where(r => r.Resolution == resolution);
            if (fromUtc is { } f) query = query.Where(r => r.PeriodStartUtc >= f);
            if (toUtc is { } u) query = query.Where(r => r.PeriodStartUtc <= u);
            var rows = query.OrderBy(r => r.TargetId).ThenBy(r => r.PeriodStartUtc).AsAsyncEnumerable();

            var stamp = time.GetUtcNow().ToString("yyyyMMdd-HHmmss");
            return IsJson(format)
                ? Results.Json(rows, contentType: "application/json")
                : Csv(CsvExport.RollupHeader, rows, CsvExport.RollupLine,
                    $"conwatch-rollups-{stamp}.csv");
        });
```

```csharp
        api.MapGet("/export/glitches", async (HttpContext http,
            IDbContextFactory<ConWatchDbContext> dbFactory, TimeProvider time, string format = "csv",
            Guid? targetId = null, string? from = null, string? to = null) =>
        {
            if (!IsValidFormat(format))
                return BadFormat();
            if (!TimeRange.TryParseRange(from, to, out var fromUtc, out var toUtc, out var problem))
                return problem!;
            var db = await OpenForResponseAsync(http, dbFactory);
            IQueryable<GlitchRow> query = db.Glitches.AsNoTracking();
            if (targetId is { } tid) query = query.Where(g => g.TargetId == tid);
            if (fromUtc is { } f) query = query.Where(g => g.StartUtc >= f);
            if (toUtc is { } u) query = query.Where(g => g.StartUtc <= u);
            var rows = query.OrderBy(g => g.Id).AsAsyncEnumerable();

            var stamp = time.GetUtcNow().ToString("yyyyMMdd-HHmmss");
            return IsJson(format)
                ? Results.Json(rows, contentType: "application/json")
                : Csv(CsvExport.GlitchHeader, rows, CsvExport.GlitchLine,
                    $"conwatch-glitches-{stamp}.csv");
        });
```

and replace the old `Csv(IEnumerable<string> lines, string fileName)` helper with:

```csharp
    /// <summary>The context outlives the handler: rows are read while the
    /// response body is written. The response disposes it when the request ends.</summary>
    private static async Task<ConWatchDbContext> OpenForResponseAsync(HttpContext http,
        IDbContextFactory<ConWatchDbContext> dbFactory)
    {
        var db = await dbFactory.CreateDbContextAsync(http.RequestAborted);
        http.Response.RegisterForDisposeAsync(db);
        return db;
    }

    private static bool IsJson(string format) =>
        format.Equals("json", StringComparison.OrdinalIgnoreCase);

    /// <summary>Header, then one line per row, each ending in '\n'; UTF-8, no BOM.</summary>
    private static IResult Csv<T>(string header, IAsyncEnumerable<T> rows, Func<T, string> line,
        string fileName) =>
        Results.Stream(async body =>
        {
            await using var writer = new StreamWriter(body,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), bufferSize: 64 * 1024, leaveOpen: true);
            await writer.WriteAsync(header);
            await writer.WriteAsync('\n');
            await foreach (var row in rows)
            {
                await writer.WriteAsync(line(row));
                await writer.WriteAsync('\n');
            }
        }, "text/csv", fileName);
```

- [x] **Step 4: Run to verify**

Run: `dotnet test Tests/ConWatch.Tests -c Debug --filter "FullyQualifiedName~ExportApiTests"` → 8 passed (same bytes, same headers).
Run: `dotnet build ConnectionWatcher.sln -c Debug` → `0 Warning(s)`; full suite → 0 failed.

- [x] **Step 5: Commit**

```powershell
git add Libraries/ConPersistence/Export/CsvExport.cs Programs/ConWatch/Api/ExportEndpoints.cs Tests/ConWatch.Tests/ExportApiTests.cs
git commit -m "refactor: measurement exports stream rows instead of materializing them"
```

---

### Task 12: Section 3, the verdict subject's name is a constant

**Files:**
- Modify: `Core/ConEngine/Topology/VerdictCoordinator.cs` (add `SubjectName`; use it)
- Modify: `Core/ConEngine/Topology/SuspicionCoordinator.cs` (the Internet-class glitch's `TargetName`)
- Modify: `Programs/ConWatch/Api/MetricsEndpoint.cs` (the zero-seeded `conwatch_transitions_total` series)
- Modify: `Tests/ConEngine.Tests/VerdictCoordinatorTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `public const string VerdictCoordinator.SubjectName = "Internet"` next to `VerdictCoordinator.SubjectId`. Task 18 uses it for the History page's `"Internet (verdict)"` label.

Not changed on purpose: the Home Assistant entity name `"Internet"` in `ConNotify/Builders.cs` (an HA display name, not the subject), the glitch class label `"Internet"` in `CsvExport`/`GlitchEndpoints`/`ConWatchMcpTools` (a class, not the subject), and `SetupService.PoolName` (the pool the wizard creates).

- [x] **Step 1: Write the test**

In `Tests/ConEngine.Tests/VerdictCoordinatorTests.cs` add:

```csharp
    [Fact]
    public void Verdict_transitions_are_named_by_the_subject_constants()
    {
        AllHealthy();

        Assert.NotEmpty(_verdictTransitions);
        Assert.All(_verdictTransitions, t =>
        {
            Assert.Equal(VerdictCoordinator.SubjectId, t.TargetId);
            Assert.Equal(VerdictCoordinator.SubjectName, t.TargetName);
        });
        // The name is on the wire: the metrics label and the MCP subject filter use it.
        Assert.Equal("Internet", VerdictCoordinator.SubjectName);
    }
```

- [x] **Step 2: Run to verify it fails**

Run: `dotnet test Tests/ConEngine.Tests -c Debug --filter "FullyQualifiedName~Verdict_transitions_are_named"`
Expected: build error, `VerdictCoordinator` has no `SubjectName`.

- [x] **Step 3: Add and use the constant**

`Core/ConEngine/Topology/VerdictCoordinator.cs`, below `SubjectId`:

```csharp
    /// <summary>The verdict subject's name in transitions, Internet-class
    /// glitches and the metrics series.</summary>
    public const string SubjectName = "Internet";
```

and in `Recalculate` replace `TargetName = "Internet",` with `TargetName = SubjectName,`.

`Core/ConEngine/Topology/SuspicionCoordinator.cs`: replace `TargetName = "Internet",` (right below `TargetId = VerdictCoordinator.SubjectId,`) with `TargetName = VerdictCoordinator.SubjectName,`.

`Programs/ConWatch/Api/MetricsEndpoint.cs`: replace `totals.TryAdd("Internet", 0); // the verdict subject's name` with `totals.TryAdd(VerdictCoordinator.SubjectName, 0);`.

- [x] **Step 4: Run to verify**

Run: `dotnet test Tests/ConEngine.Tests -c Debug --filter "FullyQualifiedName~VerdictCoordinatorTests|FullyQualifiedName~SuspicionCoordinatorTests"` → pass.
Run: `dotnet build ConnectionWatcher.sln -c Debug` → `0 Warning(s)`; full suite → 0 failed (`MetricsEndpointTests` pins the `name="Internet"` series).

- [x] **Step 5: Commit**

```powershell
git add Core/ConEngine/Topology/VerdictCoordinator.cs Core/ConEngine/Topology/SuspicionCoordinator.cs Programs/ConWatch/Api/MetricsEndpoint.cs Tests/ConEngine.Tests/VerdictCoordinatorTests.cs
git commit -m "refactor: the verdict subject's name is one constant"
```

---

### Task 13: Section 3, typed `StatusSnapshot` with `Operator`; the MCP status tool reuses it

**Files:**
- Modify: `Programs/ConWatch/StatusSnapshotBuilder.cs` (records + typed `Build`)
- Modify: `Programs/ConWatch/Api/StatusEndpoints.cs` (disabled body → `StatusSnapshot.Disabled`)
- Modify: `Programs/ConWatch/StatusBroadcaster.cs` (`DisabledSnapshot` → `StatusSnapshot.Disabled`; typed send)
- Modify: `Programs/ConWatch/Mcp/ConWatchMcpTools.cs` (`GetCurrentStatus`)
- Create: `Tests/ConWatch.Tests/StatusShapeTests.cs`
- Modify: `Tests/ConWatch.Tests/McpEndpointTests.cs`

**Interfaces:**
- Consumes: `WatcherEngine`, `WatcherHostService.EngineEnabled`.
- Produces: `ConWatch.StatusSnapshot(bool Enabled, StatusVerdict? Verdict, IReadOnlyList<StatusTarget> Targets, IReadOnlyList<StatusPool> Pools)` with `static StatusSnapshot Disabled`; `ConWatch.StatusVerdict(string State, string? Detail, string? Operator)`; `ConWatch.StatusTarget(Guid Id, string Name, string Address, string? Probe, string State, bool Assumed, double SuccessRate, double AvgRtt, int SampleSize, long PingCount, string? Operator)`; `ConWatch.StatusPool(Guid Id, string Name, string State)`; `StatusSnapshotBuilder.Build(WatcherEngine?) → StatusSnapshot?`. Task 18's `IndexModel` keeps embedding `Build(engine)`.

Wire effect: `/api/v1/status`, SignalR `statusChanged` and the dashboard's initial blob gain `operator` on each target (additive; `lib/types.ts` ignores unknown keys). The disabled body stays `{"enabled":false,"verdict":null,"targets":[],"pools":[]}` and is now one object instead of two anonymous copies. MCP `get_current_status` now reports the same target and verdict objects: `verdict.operator` (was `blamedOperator`), `avgRtt` (was `avgRttMs`), plus `id`, `sampleSize`, `pingCount`; `successRate`/`avgRtt` are `0` rather than `null` before a target's first sample. Folded in from the followups ("`EngineEnabledAsync()` is still called per request by `ConWatchMcpTools`"): `monitoringEnabled` reads `host.EngineEnabled`, which `/health`, `/status` and `/metrics` already use, instead of a database read per call.

- [x] **Step 1: Write the tests**

`Tests/ConWatch.Tests/StatusShapeTests.cs`:

```csharp
using System.Net.Http.Json;
using System.Text.Json;

namespace ConWatch.Tests;

public class StatusShapeTests : IDisposable
{
    private readonly ConWatchFactory _factory = new();
    private readonly HttpClient _client;

    public StatusShapeTests() => _client = _factory.CreateClient();

    public void Dispose() => _factory.Dispose();

    private static string[] Keys(JsonElement e) =>
        e.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray();

    [Fact]
    public async Task Live_status_shape_is_pinned()
    {
        await ConfigApiTests.SeedHomeAsync(_client);
        JsonElement status = default;
        await ConfigApiTests.Eventually(async () =>
        {
            var response = await _client.GetAsync("/api/v1/status");
            if (!response.IsSuccessStatusCode)
                return false;
            status = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
            return status.GetProperty("verdict").ValueKind == JsonValueKind.Object
                && status.GetProperty("targets").GetArrayLength() == 3
                && status.GetProperty("pools").GetArrayLength() == 1;
        });

        Assert.Equal(new[] { "enabled", "pools", "targets", "verdict" }, Keys(status));
        Assert.Equal(new[] { "detail", "operator", "state" }, Keys(status.GetProperty("verdict")));
        Assert.Equal(new[] { "address", "assumed", "avgRtt", "id", "name", "operator", "pingCount",
                "probe", "sampleSize", "state", "successRate" },
            Keys(status.GetProperty("targets")[0]));
        Assert.Equal(new[] { "id", "name", "state" }, Keys(status.GetProperty("pools")[0]));
    }

    [Fact]
    public async Task Disabled_status_body_is_pinned()
    {
        (await _client.PutAsJsonAsync("/api/v1/settings",
            new Dictionary<string, string?> { ["Engine.Enabled"] = "false" })).EnsureSuccessStatusCode();

        var body = "";
        await ConfigApiTests.Eventually(async () =>
        {
            var response = await _client.GetAsync("/api/v1/status");
            body = await response.Content.ReadAsStringAsync();
            return response.IsSuccessStatusCode && body.Contains("\"enabled\":false");
        });

        Assert.Equal("""{"enabled":false,"verdict":null,"targets":[],"pools":[]}""", body);
    }
}
```

In `Tests/ConWatch.Tests/McpEndpointTests.cs` add:

```csharp
    [Fact]
    public async Task Status_tool_reports_the_status_snapshot()
    {
        await ConfigApiTests.SeedHomeAsync(_client);
        await ConfigApiTests.Eventually(async () =>
        {
            var status = await _client.GetAsync("/api/v1/status");
            return status.IsSuccessStatusCode &&
                   (await status.Content.ReadAsStringAsync()).Contains("Healthy");
        });
        await InitializeAsync();

        var json = JsonSerializer.Deserialize<JsonElement>(ToolText(await RpcAsync("tools/call", new
        {
            name = "get_current_status",
            arguments = new { },
        })));

        Assert.True(json.GetProperty("monitoringEnabled").GetBoolean());
        Assert.True(json.GetProperty("verdict").TryGetProperty("operator", out _));
        var router = json.GetProperty("targets").EnumerateArray()
            .Single(t => t.GetProperty("name").GetString() == "Router");
        foreach (var key in new[] { "id", "address", "state", "assumed", "successRate", "avgRtt",
                     "sampleSize", "pingCount", "operator" })
            Assert.True(router.TryGetProperty(key, out _), $"missing '{key}'");
    }
```

- [x] **Step 2: Run to verify which fail**

Run: `dotnet test Tests/ConWatch.Tests -c Debug --filter "FullyQualifiedName~StatusShapeTests|FullyQualifiedName~Status_tool_reports_the_status_snapshot"`
Expected: `Disabled_status_body_is_pinned` PASSES (pins today's body); `Live_status_shape_is_pinned` FAILS (no `operator` on targets); `Status_tool_reports_the_status_snapshot` FAILS (verdict has `blamedOperator`).

- [x] **Step 3: Typed snapshot**

Replace `Programs/ConWatch/StatusSnapshotBuilder.cs` with:

```csharp
using ConEngine;

namespace ConWatch;

/// <summary>
/// What GET /api/v1/status answers, the SignalR statusChanged push carries,
/// the dashboard embeds for its first paint and the MCP status tool reports:
/// one shape. Positional records serialize in parameter order, which is the
/// order the anonymous objects they replace used.
/// </summary>
public sealed record StatusSnapshot(bool Enabled, StatusVerdict? Verdict,
    IReadOnlyList<StatusTarget> Targets, IReadOnlyList<StatusPool> Pools)
{
    /// <summary>Archive mode (monitoring disabled): no live states.</summary>
    public static StatusSnapshot Disabled { get; } = new(false, null, [], []);
}

/// <param name="Operator">The ISP blamed by a Link* verdict ("Comcast"), else null.</param>
public sealed record StatusVerdict(string State, string? Detail, string? Operator);

/// <param name="Operator">The target's operator/ISP tag, or null.</param>
public sealed record StatusTarget(Guid Id, string Name, string Address, string? Probe, string State,
    bool Assumed, double SuccessRate, double AvgRtt, int SampleSize, long PingCount, string? Operator);

public sealed record StatusPool(Guid Id, string Name, string State);

public static class StatusSnapshotBuilder
{
    public static StatusSnapshot? Build(WatcherEngine? engine)
    {
        if (engine is null)
            return null;

        var verdict = engine.Verdict?.Current is { } c
            ? new StatusVerdict(c.State.ToString(), c.Detail, c.Operator)
            : null;
        var targets = engine.Monitors.Values.Select(m => new StatusTarget(
                m.Target.Id,
                m.Target.Name,
                m.ResolvedAddress?.ToString() ?? m.Target.Address,
                m.Target.Probe,
                m.CurrentState.ToString(),
                m.IsAssumed,
                (double)(m.LastStatus?.Host.SuccessRate ?? 0f),
                (double)(m.LastStatus?.Host.AverageRoundtripTime ?? 0f),
                m.LastStatus?.Host.SampleSize ?? 0,
                m.PingCount,
                m.Target.Operator))
            .OrderBy(t => t.Name).ToList();
        var pools = engine.PoolCoordinators
            .Select(p => new StatusPool(p.Pool.Id, p.Pool.Name, p.CurrentState.ToString()))
            .OrderBy(p => p.Name).ToList();

        return new StatusSnapshot(true, verdict, targets, pools);
    }
}
```

`Programs/ConWatch/Api/StatusEndpoints.cs`, `GET /status`: replace the `if (!host.EngineEnabled) return Results.Ok(new { … });` statement with:

```csharp
            // Archive mode is a healthy answer, not an error: no live states.
            if (!host.EngineEnabled)
                return Results.Ok(StatusSnapshot.Disabled);
```

`Programs/ConWatch/StatusBroadcaster.cs`: delete the `DisabledSnapshot` field and its summary; in `PushDisabledAsync` replace `SendStatusChangedAsync(DisabledSnapshot)` with `SendStatusChangedAsync(StatusSnapshot.Disabled)`; change `private async Task SendStatusChangedAsync(object snapshot)` to `private async Task SendStatusChangedAsync(StatusSnapshot snapshot)`.

`Programs/ConWatch/Mcp/ConWatchMcpTools.cs`: replace `GetCurrentStatus`'s body (keep its attributes and `Description`) with:

```csharp
    public Task<string> GetCurrentStatus()
    {
        var snapshot = StatusSnapshotBuilder.Build(host.Engine);
        return Task.FromResult(JsonSerializer.Serialize(new
        {
            monitoringEnabled = host.EngineEnabled,
            version = BuildInfo.Version,
            builtUtc = BuildInfo.BuiltUtc,
            verdict = snapshot?.Verdict,
            targets = snapshot?.Targets,
        }, Json));
    }
```

(`Task.FromResult` keeps the tool's signature; an `async` method with no `await` would be warning CS1998.)

- [x] **Step 4: Run to verify**

Run: `dotnet test Tests/ConWatch.Tests -c Debug --filter "FullyQualifiedName~StatusShapeTests|FullyQualifiedName~McpEndpointTests|FullyQualifiedName~SignalRTests|FullyQualifiedName~MasterEnableTests|FullyQualifiedName~PageSmokeTests"` → all pass.
Run: `dotnet build ConnectionWatcher.sln -c Debug` → `0 Warning(s)`; full suite → 0 failed; `npx vitest run` (ClientApp) → 56 passed.

- [x] **Step 5: Commit**

```powershell
git add Programs/ConWatch/StatusSnapshotBuilder.cs Programs/ConWatch/Api/StatusEndpoints.cs Programs/ConWatch/StatusBroadcaster.cs Programs/ConWatch/Mcp/ConWatchMcpTools.cs Tests/ConWatch.Tests/StatusShapeTests.cs Tests/ConWatch.Tests/McpEndpointTests.cs
git commit -m "refactor: typed StatusSnapshot with operator; the MCP status tool reuses it"
```

---

## Group C: policy out of the host, atomic writes

### Task 14: S8, `SettingsDefaults` and `RetentionWindows`

**Files:**
- Create: `Libraries/ConPersistence/Settings/SettingsDefaults.cs`
- Create: `Libraries/ConPersistence/Retention/RetentionWindows.cs`
- Modify: `Libraries/ConPersistence/Retention/RetentionPass.cs` (`RunAsync` takes `RetentionWindows`)
- Modify: `Libraries/ConPersistence/Retention/RetentionService.cs` (one settings read per pass; no options rebuild)
- Modify: `Programs/ConWatch/WatcherHostService.cs` (`StartAsync` seed list; the three `10` literals)
- Modify: `Programs/ConWatch/Api/AdminEndpoints.cs` (factory reset seeds `SettingsDefaults.For`)
- Create: `Tests/ConPersistence.Tests/FileDbFactory.cs`, `Tests/ConPersistence.Tests/SettingsDefaultsTests.cs`, `Tests/ConPersistence.Tests/RetentionServiceTests.cs`
- Modify: `Tests/ConPersistence.Tests/RetentionTests.cs`, `Tests/ConPersistence.Tests/GlitchRecorderTests.cs` (new `RunAsync` argument), `Tests/ConWatch.Tests/AdminApiTests.cs`

**Interfaces:**
- Consumes: Task 2 namespaces.
- Produces: `ConPersistence.Settings.SettingsDefaults.For(VerdictOptions verdict, PersistenceOptions persistence) → Dictionary<string, string>`; `SettingsDefaults.WarmRestartMaxGapMinutes` (`const int` = 10); `SettingsDefaults.WeatherThreshold` (`static readonly int`, the compiled-in `VerdictOptions` default, 2). `ConPersistence.Retention.RetentionWindows(int MinuteDays, int HourlyMonths, int TransitionMonths)` with `static RetentionWindows From(PersistenceOptions)`. `RetentionPass.RunAsync(ConWatchDbContext db, DateTime nowUtc, RetentionWindows windows, CancellationToken ct) → Task<int>`. `Tests/ConPersistence.Tests/FileDbFactory` (used again by Task 21). Task 15 uses `WarmRestartMaxGapMinutes`; Task 18 uses `WeatherThreshold`.

Decisions (see table): factory reset now seeds the same seven keys as first boot (pinned below); `RebuildDebounceSeconds` stays in `PersistenceOptions` (public config key). `RetentionPass.RunAsync` keeps returning its count (the idempotency tests assert on it; decision 4 dropped the `incremental_vacuum` it used to feed, which is already gone), and its summary now says what the number is.

- [x] **Step 1: Write the tests**

`Tests/ConPersistence.Tests/FileDbFactory.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ConPersistence.Tests;

/// <summary>
/// A migrated SQLite file (WAL, like production) in its own temp directory,
/// for tests that need real concurrency: a background service polling while
/// the test reads, or two connections interleaving. TestDbFactory's single
/// shared in-memory connection is not safe for either.
/// </summary>
public sealed class FileDbFactory : IDbContextFactory<ConWatchDbContext>, IDisposable
{
    private readonly string? _ownedDir;
    private readonly DbContextOptions<ConWatchDbContext> _options;

    public string DbPath { get; }

    public FileDbFactory()
    {
        _ownedDir = Directory.CreateTempSubdirectory("conpersistence-test").FullName;
        DbPath = Path.Combine(_ownedDir, "test.db");
        _options = Build(DbPath, []);
    }

    private FileDbFactory(string dbPath, IInterceptor[] interceptors)
    {
        DbPath = dbPath;
        _options = Build(dbPath, interceptors);
    }

    /// <summary>A second factory over the same file, with interceptors; it does not own the file.</summary>
    public FileDbFactory With(params IInterceptor[] interceptors) => new(DbPath, interceptors);

    /// <summary>Migrate and switch to WAL, exactly as the app does at startup.</summary>
    public Task PrepareAsync() => DatabaseStartup.PrepareAsync(this, CancellationToken.None);

    public ConWatchDbContext CreateDbContext() => new(_options);

    public void Dispose()
    {
        if (_ownedDir is null)
            return;
        try
        {
            Directory.Delete(_ownedDir, recursive: true);
        }
        catch (IOException)
        {
            // Best effort, like ConWatchFactory's temp cleanup.
        }
    }

    private static DbContextOptions<ConWatchDbContext> Build(string path, IInterceptor[] interceptors)
    {
        // Pooling=False: every context really closes its file handle, so the
        // directory can be deleted and two factories really are two connections.
        var builder = new DbContextOptionsBuilder<ConWatchDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False");
        if (interceptors.Length > 0)
            builder.AddInterceptors(interceptors);
        return builder.Options;
    }
}
```

`Tests/ConPersistence.Tests/SettingsDefaultsTests.cs`:

```csharp
using ConEngine.Configuration;
using ConPersistence.Settings;

namespace ConPersistence.Tests;

public class SettingsDefaultsTests
{
    [Fact]
    public void For_lists_every_seeded_key_from_the_options()
    {
        var defaults = SettingsDefaults.For(
            new VerdictOptions { AnchorPool = "Internet", WeatherThreshold = 3 },
            new PersistenceOptions
            {
                MinuteRetentionDays = 30, HourlyRetentionMonths = 6,
                TransitionRetentionMonths = 12, RetentionRunHourUtc = 4,
            });

        Assert.Equal(new Dictionary<string, string>
        {
            [SettingKeys.WeatherThreshold] = "3",
            [SettingKeys.MinuteRetentionDays] = "30",
            [SettingKeys.HourlyRetentionMonths] = "6",
            [SettingKeys.TransitionRetentionMonths] = "12",
            [SettingKeys.RetentionRunHourUtc] = "4",
            [SettingKeys.WarmRestartMaxGapMinutes] = "10",
            [SettingKeys.AnchorPool] = "Internet",
        }, defaults);
    }

    [Fact]
    public void For_omits_the_anchor_pool_when_none_is_configured() =>
        Assert.False(SettingsDefaults.For(new VerdictOptions(), new PersistenceOptions())
            .ContainsKey(SettingKeys.AnchorPool));
}
```

`Tests/ConPersistence.Tests/RetentionServiceTests.cs` (a pin: it passes before and after the refactor):

```csharp
using ConPersistence.Entities;
using ConPersistence.Retention;
using ConPersistence.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace ConPersistence.Tests;

public class RetentionServiceTests
{
    [Fact]
    public async Task Nightly_pass_uses_the_settings_table_over_appsettings()
    {
        using var factory = new FileDbFactory();
        await factory.PrepareAsync();
        var settings = new SettingsStore(factory);
        await settings.SetManyAsync(new Dictionary<string, string?> { [SettingKeys.MinuteRetentionDays] = "1" });
        var now = new DateTimeOffset(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);
        await using (var db = factory.CreateDbContext())
        {
            db.Rollups.Add(new RollupRow
            {
                TargetId = Guid.NewGuid(), PeriodStartUtc = now.UtcDateTime.AddDays(-2).Date,
                Resolution = "Minute", PingCount = 6, SuccessCount = 6, RttAvg = 10, RttMin = 9, RttMax = 11,
            });
            await db.SaveChangesAsync();
        }

        // Appsettings say 90 days; the table says 1. Only the table's value moves this row.
        var service = new RetentionService(factory, Options.Create(new PersistenceOptions()),
            new FakeTimeProvider(now), NullLogger<RetentionService>.Instance, settings);
        await service.StartAsync(CancellationToken.None);
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (await settings.GetAsync(SettingKeys.RetentionLastRunUtc) is null && DateTime.UtcNow < deadline)
                await Task.Delay(50);

            await using var check = factory.CreateDbContext();
            Assert.Equal("Hour", (await check.Rollups.SingleAsync()).Resolution);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }
}
```

In `Tests/ConWatch.Tests/AdminApiTests.cs` add `using ConPersistence.Settings;` and:

```csharp
    [Fact]
    public async Task Factory_reset_restores_the_first_boot_settings()
    {
        string[] keys =
        [
            SettingKeys.WeatherThreshold, SettingKeys.MinuteRetentionDays, SettingKeys.HourlyRetentionMonths,
            SettingKeys.TransitionRetentionMonths, SettingKeys.RetentionRunHourUtc,
            SettingKeys.WarmRestartMaxGapMinutes, SettingKeys.AnchorPool,
        ];
        var firstBoot = await _client.GetFromJsonAsync<Dictionary<string, string>>("/api/v1/settings");

        var reset = await _client.PostAsJsonAsync("/api/v1/admin/factory-reset", new { confirm = "RESET" });
        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);

        var after = await _client.GetFromJsonAsync<Dictionary<string, string>>("/api/v1/settings");
        foreach (var key in keys)
            Assert.True(after!.TryGetValue(key, out var value) && value == firstBoot![key],
                $"{key}: first boot '{firstBoot![key]}', after reset '{value}'");
    }
```

- [x] **Step 2: Run to verify**

Run: `dotnet test Tests/ConPersistence.Tests -c Debug --filter "FullyQualifiedName~SettingsDefaultsTests|FullyQualifiedName~RetentionServiceTests"`
Expected: build error (`SettingsDefaults` does not exist). Temporarily comment out `SettingsDefaultsTests.cs` and rerun: `RetentionServiceTests` PASSES (today's behavior pinned). Restore it.
Run: `dotnet test Tests/ConWatch.Tests -c Debug --filter "FullyQualifiedName~Factory_reset_restores_the_first_boot_settings"`
Expected: FAIL naming `Retention.TransitionMonths` (factory reset does not seed it today).

- [x] **Step 3: `SettingsDefaults`**

`Libraries/ConPersistence/Settings/SettingsDefaults.cs`:

```csharp
using System.Globalization;
using ConEngine.Configuration;

namespace ConPersistence.Settings;

/// <summary>
/// The settings a node starts with. First boot seeds them for keys not yet in
/// the table (the upgrade path); factory reset seeds the same list. Values
/// come from appsettings; the table is authoritative afterwards.
/// </summary>
public static class SettingsDefaults
{
    /// <summary>Warm restart seeds only when the watcher was last alive within this many minutes.</summary>
    public const int WarmRestartMaxGapMinutes = 10;

    /// <summary>The compiled-in weather threshold: the fallback where the setting cannot be read.</summary>
    public static readonly int WeatherThreshold = new VerdictOptions().WeatherThreshold;

    public static Dictionary<string, string> For(VerdictOptions verdict, PersistenceOptions persistence)
    {
        var defaults = new Dictionary<string, string>
        {
            [SettingKeys.WeatherThreshold] = Invariant(verdict.WeatherThreshold),
            [SettingKeys.MinuteRetentionDays] = Invariant(persistence.MinuteRetentionDays),
            [SettingKeys.HourlyRetentionMonths] = Invariant(persistence.HourlyRetentionMonths),
            [SettingKeys.TransitionRetentionMonths] = Invariant(persistence.TransitionRetentionMonths),
            [SettingKeys.RetentionRunHourUtc] = Invariant(persistence.RetentionRunHourUtc),
            [SettingKeys.WarmRestartMaxGapMinutes] = Invariant(WarmRestartMaxGapMinutes),
        };
        if (verdict.AnchorPool is { Length: > 0 } anchorPool)
            defaults[SettingKeys.AnchorPool] = anchorPool;
        return defaults;
    }

    private static string Invariant(int value) => value.ToString(CultureInfo.InvariantCulture);
}
```

`Programs/ConWatch/WatcherHostService.cs`, `StartAsync`: replace the `var seedDefaults = …;`, the `if (verdictOptions.Value.AnchorPool …)` line and the `SeedMissingAsync(seedDefaults)` call with:

```csharp
        // Appsettings values fill any settings keys not yet in the DB (upgrade
        // path); the table is authoritative from then on. Factory reset seeds
        // the same list.
        await settingsStore.SeedMissingAsync(
            SettingsDefaults.For(verdictOptions.Value, persistenceOptions.Value));
```

and in `WarmRestartEnabledAsync` and `BuildSeedFromDbAsync` replace `GetIntAsync(SettingKeys.WarmRestartMaxGapMinutes, 10)` with `GetIntAsync(SettingKeys.WarmRestartMaxGapMinutes, SettingsDefaults.WarmRestartMaxGapMinutes)`.

`Programs/ConWatch/Api/AdminEndpoints.cs`: add `using ConEngine.Configuration;`; add `IOptions<VerdictOptions> verdictOptions, IOptions<PersistenceOptions> persistenceOptions` to the `/admin/factory-reset` lambda's parameters; replace its `SeedMissingAsync(new Dictionary<string, string> { … })` call with:

```csharp
            await settings.SeedMissingAsync(
                SettingsDefaults.For(verdictOptions.Value, persistenceOptions.Value)); // as first boot
```

- [x] **Step 4: `RetentionWindows`**

`Libraries/ConPersistence/Retention/RetentionWindows.cs`:

```csharp
namespace ConPersistence.Retention;

/// <summary>
/// The three ages the retention pass applies. The pass reads nothing else
/// from configuration, so a new PersistenceOptions field can no longer be
/// silently reset to its default by a pass that rebuilt the whole options object.
/// </summary>
public sealed record RetentionWindows(int MinuteDays, int HourlyMonths, int TransitionMonths)
{
    public static RetentionWindows From(PersistenceOptions options) =>
        new(options.MinuteRetentionDays, options.HourlyRetentionMonths, options.TransitionRetentionMonths);
}
```

`Libraries/ConPersistence/Retention/RetentionPass.cs`: change the signature and summary of `RunAsync` to

```csharp
    /// <summary>Runs both downsampling stages and the two prunes. Returns the
    /// rows it changed (fine rollups folded into coarser ones, plus glitches
    /// and transitions pruned): 0 on an immediate second run.</summary>
    public async Task<int> RunAsync(ConWatchDbContext db, DateTime nowUtc,
        RetentionWindows windows, CancellationToken ct)
```

and inside it replace `options.MinuteRetentionDays` with `windows.MinuteDays` (three places: the minute downsample, the glitch threshold, the glitch log line), `options.HourlyRetentionMonths` with `windows.HourlyMonths`, and `options.TransitionRetentionMonths` with `windows.TransitionMonths` (threshold and log line).

`Libraries/ConPersistence/Retention/RetentionService.cs`: replace `EffectiveOptionsAsync()` with

```csharp
    /// <summary>The settings table over appsettings, in one read per pass.</summary>
    private async Task<(RetentionWindows Windows, int RunHourUtc)> EffectiveAsync()
    {
        var o = options.Value;
        if (settingsStore is null)
            return (RetentionWindows.From(o), o.RetentionRunHourUtc);
        var all = await settingsStore.GetAllAsync().ConfigureAwait(false);
        int Get(string key, int fallback) =>
            all.TryGetValue(key, out var text) && int.TryParse(text, out var value) ? value : fallback;
        return (new RetentionWindows(
                Get(SettingKeys.MinuteRetentionDays, o.MinuteRetentionDays),
                Get(SettingKeys.HourlyRetentionMonths, o.HourlyRetentionMonths),
                Get(SettingKeys.TransitionRetentionMonths, o.TransitionRetentionMonths)),
            Get(SettingKeys.RetentionRunHourUtc, o.RetentionRunHourUtc));
    }
```

in `ExecuteAsync` replace `await EffectiveOptionsAsync().ConfigureAwait(false)` (the third `RunAsync` argument) with `(await EffectiveAsync().ConfigureAwait(false)).Windows`, and in `DelayUntilNextRunAsync` replace `(await EffectiveOptionsAsync().ConfigureAwait(false)).RetentionRunHourUtc` with `(await EffectiveAsync().ConfigureAwait(false)).RunHourUtc`.

Test call sites: in `Tests/ConPersistence.Tests/RetentionTests.cs` replace the `_options` field with

```csharp
    private readonly RetentionWindows _windows = new(MinuteDays: 90, HourlyMonths: 13, TransitionMonths: 24);
```

and every `_options` argument with `_windows` (six calls; 24 is `PersistenceOptions`' default that the old field relied on). In `Tests/ConPersistence.Tests/GlitchRecorderTests.cs` replace `new PersistenceOptions { MinuteRetentionDays = 90 }` with `RetentionWindows.From(new PersistenceOptions { MinuteRetentionDays = 90 })`.

- [x] **Step 5: Run to verify**

Run: `dotnet test Tests/ConPersistence.Tests -c Debug` → 0 failed (incl. `SettingsDefaultsTests`, `RetentionServiceTests`, `RetentionTests`, `GlitchRecorderTests`).
Run: `dotnet test Tests/ConWatch.Tests -c Debug --filter "FullyQualifiedName~AdminApiTests|FullyQualifiedName~WarmRestartTests|FullyQualifiedName~SettingsApiTests"` → pass.
Run: `dotnet build ConnectionWatcher.sln -c Debug` → `0 Warning(s)`; full suite → 0 failed.

- [x] **Step 6: Commit**

```powershell
git add Libraries/ConPersistence/Settings Libraries/ConPersistence/Retention Programs/ConWatch/WatcherHostService.cs Programs/ConWatch/Api/AdminEndpoints.cs Tests/ConPersistence.Tests Tests/ConWatch.Tests/AdminApiTests.cs
git commit -m "refactor: one SettingsDefaults list for first boot and factory reset; RetentionWindows"
```

---

### Task 15: S2, `WarmStartSeeder`: warm-start policy out of the host service

**Files:**
- Create: `Programs/ConWatch/WarmStartSeeder.cs`
- Modify: `Programs/ConWatch/WatcherHostService.cs` (delete the four seed members; inject the seeder; drop `IDbContextFactory`)
- Modify: `Programs/ConWatch/Startup/ConWatchServices.cs` (register the seeder)
- Create: `Tests/ConWatch.Tests/WarmStartSeederTests.cs`

**Interfaces:**
- Consumes: Task 14's `SettingsDefaults.WarmRestartMaxGapMinutes`; `WarmStartQueries` (Task 2 namespace `ConPersistence.Queries`).
- Produces: `ConWatch.WarmStartSeeder(IDbContextFactory<ConWatchDbContext> dbFactory, ConfigStore store, SettingsStore settingsStore, TimeProvider timeProvider, ILogger<WarmStartSeeder> log)` with `Task<bool> EnabledAsync()`, `Task<EngineSeed?> FromDbAsync()`, `static EngineSeed FromEngine(WatcherEngine engine)`. `WatcherHostService`'s constructor loses `IDbContextFactory<ConWatchDbContext> dbFactory` and gains `WarmStartSeeder seeder` (DI only; nothing constructs it by hand).

The four members move verbatim (policy unchanged): `WarmRestartEnabledAsync` → `EnabledAsync`, `BuildSeedFromDbAsync` → `FromDbAsync`, `DropUnjustifiedUnreachable` (private static), `BuildSeedFromEngine` → `FromEngine`. The only observable difference is the log category of the three warm/cold-start lines (`ConWatch.WarmStartSeeder` instead of `ConWatch.WatcherHostService`). The existing `WarmRestartTests`, `MasterEnableTests` and `CloneTests` are the end-to-end pins; the new unit tests pin each rule directly.

- [x] **Step 1: Write the tests**

`Tests/ConWatch.Tests/WarmStartSeederTests.cs`:

```csharp
using ConEngine.Topology;
using ConEngine.Tracking;
using ConPersistence;
using ConPersistence.Config;
using ConPersistence.Entities;
using ConPersistence.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ConWatch.Tests;

public sealed class WarmStartSeederTests : IAsyncLifetime
{
    private sealed class FileDb(string path) : IDbContextFactory<ConWatchDbContext>
    {
        private readonly DbContextOptions<ConWatchDbContext> _options =
            new DbContextOptionsBuilder<ConWatchDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;

        public ConWatchDbContext CreateDbContext() => new(_options);
    }

    private readonly string _dir = Directory.CreateTempSubdirectory("seeder-test").FullName;
    private FileDb _db = null!;
    private ConfigService _config = null!;
    private SettingsStore _settings = null!;
    private WarmStartSeeder _seeder = null!;

    public async Task InitializeAsync()
    {
        _db = new FileDb(Path.Combine(_dir, "seed.db"));
        await DatabaseStartup.PrepareAsync(_db, CancellationToken.None);
        var store = new ConfigStore(_db);
        await store.SeedAsync(null); // built-in profiles
        _config = new ConfigService(_db, store);
        _settings = new SettingsStore(_db);
        _seeder = new WarmStartSeeder(_db, store, _settings, TimeProvider.System,
            NullLogger<WarmStartSeeder>.Instance);
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        return Task.CompletedTask;
    }

    private async Task<Guid> AddTarget(string name, string address, string? parent = null) =>
        (await _config.UpsertTargetAsync(new TargetDto
        {
            Name = name, Address = address, Parent = parent, Profile = "Internal",
        })).Id!.Value;

    private async Task Record(Guid subject, string kind, string to, DateTime atUtc,
        string trigger = "ThresholdCrossed", string? verdict = null)
    {
        await using var db = _db.CreateDbContext();
        db.Transitions.Add(new TransitionRow
        {
            TimestampUtc = atUtc, SubjectId = subject, SubjectName = "x", SubjectKind = kind,
            FromState = "Starting", ToState = to, Trigger = trigger, Verdict = verdict,
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Recent_states_seed_but_starting_rows_and_history_resets_do_not()
    {
        var router = await AddTarget("Router", "192.168.1.1");
        var modem = await AddTarget("Modem", "192.168.100.1", "Router");
        var tv = await AddTarget("TV", "192.168.1.50", "Router");
        var now = DateTime.UtcNow;
        await Record(router, "Target", "Responding", now.AddMinutes(-2));
        await Record(modem, "Target", "Starting", now.AddMinutes(-2));
        await Record(tv, "Target", "Down", now.AddMinutes(-3));
        await Record(tv, "Target", "Starting", now.AddMinutes(-1), trigger: "HistoryReset");

        var seed = await _seeder.FromDbAsync();

        Assert.NotNull(seed);
        Assert.Equal(HostState.Responding, seed.Targets[router].State);
        Assert.Equal("192.168.1.1", seed.Targets[router].Address);
        Assert.False(seed.Targets.ContainsKey(modem)); // Starting never seeds
        Assert.False(seed.Targets.ContainsKey(tv));    // its latest row is a HistoryReset
    }

    [Fact]
    public async Task A_log_older_than_the_gap_cold_starts()
    {
        var router = await AddTarget("Router", "192.168.1.1");
        await Record(router, "Target", "Responding", DateTime.UtcNow.AddMinutes(-(SettingsDefaults.WarmRestartMaxGapMinutes + 5)));

        Assert.Null(await _seeder.FromDbAsync());
    }

    [Fact]
    public async Task Unreachable_without_a_down_ancestor_is_dropped()
    {
        var router = await AddTarget("Router", "192.168.1.1");
        var modem = await AddTarget("Modem", "192.168.100.1", "Router");
        var now = DateTime.UtcNow;
        await Record(router, "Target", "Responding", now.AddMinutes(-2));
        await Record(modem, "Target", "Unreachable", now.AddMinutes(-2));

        var seed = await _seeder.FromDbAsync();

        Assert.NotNull(seed);
        Assert.True(seed.Targets.ContainsKey(router));
        Assert.False(seed.Targets.ContainsKey(modem));
    }

    [Fact]
    public async Task Unreachable_behind_a_down_ancestor_is_kept()
    {
        var router = await AddTarget("Router", "192.168.1.1");
        var modem = await AddTarget("Modem", "192.168.100.1", "Router");
        var now = DateTime.UtcNow;
        await Record(router, "Target", "Down", now.AddMinutes(-2));
        await Record(modem, "Target", "Unreachable", now.AddMinutes(-2));

        var seed = await _seeder.FromDbAsync();

        Assert.NotNull(seed);
        Assert.Equal(HostState.Down, seed.Targets[router].State);
        Assert.Equal(HostState.Unreachable, seed.Targets[modem].State);
    }

    [Fact]
    public async Task The_latest_verdict_row_seeds_the_verdict()
    {
        await AddTarget("Router", "192.168.1.1");
        await Record(VerdictCoordinator.SubjectId, "Verdict", "Responding", DateTime.UtcNow.AddMinutes(-1),
            trigger: "VerdictChanged", verdict: "Healthy");

        var seed = await _seeder.FromDbAsync();

        Assert.NotNull(seed);
        Assert.Equal(VerdictState.Healthy, seed.Verdict);
    }

    [Fact]
    public async Task A_zero_gap_disables_all_seeding()
    {
        var router = await AddTarget("Router", "192.168.1.1");
        await Record(router, "Target", "Responding", DateTime.UtcNow.AddMinutes(-1));
        await _settings.SetManyAsync(new Dictionary<string, string?> { [SettingKeys.WarmRestartMaxGapMinutes] = "0" });

        Assert.False(await _seeder.EnabledAsync());
        Assert.Null(await _seeder.FromDbAsync());
    }
}
```

- [x] **Step 2: Run to verify they fail**

Run: `dotnet test Tests/ConWatch.Tests -c Debug --filter "FullyQualifiedName~WarmStartSeederTests"`
Expected: build error, `WarmStartSeeder` does not exist.

- [x] **Step 3: Move the policy**

`Programs/ConWatch/WarmStartSeeder.cs`:

```csharp
using ConEngine;
using ConEngine.Configuration;
using ConEngine.Topology;
using ConEngine.Tracking;
using ConPersistence;
using ConPersistence.Config;
using ConPersistence.Queries;
using ConPersistence.Settings;
using Microsoft.EntityFrameworkCore;

namespace ConWatch;

/// <summary>
/// Warm-start policy: which presumed states a new engine may start from.
/// Pure policy over the transition log, the stored config and one setting;
/// the lifecycle (when to seed, under which lock) stays in WatcherHostService.
/// Spec: docs/superpowers/specs/2026-09-11-warm-restart-design.md.
/// </summary>
public sealed class WarmStartSeeder(IDbContextFactory<ConWatchDbContext> dbFactory, ConfigStore store,
    SettingsStore settingsStore, TimeProvider timeProvider, ILogger<WarmStartSeeder> log)
{
    /// <summary>False when Engine.WarmRestartMaxGapMinutes is 0 or less: no
    /// seeding at all, neither at process start nor on a config rebuild.</summary>
    public async Task<bool> EnabledAsync() => await MaxGapMinutesAsync() > 0;

    private Task<int> MaxGapMinutesAsync() =>
        settingsStore.GetIntAsync(SettingKeys.WarmRestartMaxGapMinutes, SettingsDefaults.WarmRestartMaxGapMinutes);

    /// <summary>
    /// Presumed states from the last recorded transitions, gated by freshness:
    /// only when the watcher was last alive within WarmRestartMaxGapMinutes.
    /// HistoryReset rows (repointed targets) and undeterminable states never
    /// seed; an Unreachable seed needs an ancestor seeded Down to justify it.
    /// </summary>
    public async Task<EngineSeed?> FromDbAsync()
    {
        var maxGapMinutes = await MaxGapMinutesAsync();
        if (maxGapMinutes <= 0)
            return null;

        await using var db = await dbFactory.CreateDbContextAsync();
        var lastAlive = await WarmStartQueries.LastAliveUtcAsync(db);
        if (lastAlive is null)
            return null;
        var gap = timeProvider.GetUtcNow().UtcDateTime - lastAlive.Value;
        if (gap > TimeSpan.FromMinutes(maxGapMinutes))
        {
            log.LogInformation(
                "Cold start: watcher was last alive {Gap} ago (warm-restart window is {Max} min)",
                gap, maxGapMinutes);
            return null;
        }

        var options = await store.LoadWatcherOptionsAsync();
        var targetsById = options.Targets.Where(t => t.Id is not null)
            .ToDictionary(t => t.Id!.Value);
        var latest = await WarmStartQueries.LatestTransitionsPerSubjectAsync(db);

        var seed = new EngineSeed();
        foreach (var row in latest)
        {
            if (row.Trigger == "HistoryReset")
                continue;
            switch (row.SubjectKind)
            {
                case "Target" when targetsById.TryGetValue(row.SubjectId, out var target)
                    && Enum.TryParse<HostState>(row.ToState, out var state)
                    && state is not (HostState.Initializing or HostState.Starting):
                    seed.Targets[row.SubjectId] = new TargetSeed(state, target.Address, target.Probe);
                    break;
                case "Pool" when Enum.TryParse<HostState>(row.ToState, out var poolState)
                    && poolState is not (HostState.Initializing or HostState.Starting):
                    seed.Pools[row.SubjectId] = poolState;
                    break;
            }
        }
        var verdictRow = latest.FirstOrDefault(r => r.SubjectKind == "Verdict"
            && r.Trigger != "HistoryReset");
        if (verdictRow?.Verdict is { } verdictName &&
            Enum.TryParse<VerdictState>(verdictName, out var verdict) &&
            verdict != VerdictState.Unknown)
        {
            seed = new EngineSeed { Targets = seed.Targets, Pools = seed.Pools, Verdict = verdict };
        }

        DropUnjustifiedUnreachable(seed, targetsById);
        if (seed.IsEmpty)
            return null;
        log.LogInformation(
            "Warm restart: last alive {Gap} ago; seeding {Targets} target(s), {Pools} pool(s), verdict {Verdict}",
            gap, seed.Targets.Count, seed.Pools.Count, seed.Verdict?.ToString() ?? "unseeded");
        return seed;
    }

    /// <summary>An Unreachable presumption is only coherent behind a Down
    /// ancestor; without one, that target cold-starts instead. Config links
    /// parents by name, so the walk goes name-by-name (depth-capped).</summary>
    private static void DropUnjustifiedUnreachable(EngineSeed seed,
        IReadOnlyDictionary<Guid, TargetConfig> targetsById)
    {
        var byName = new Dictionary<string, (Guid Id, TargetConfig Config)>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var (id, config) in targetsById)
            byName[config.Name] = (id, config);

        foreach (var (id, targetSeed) in seed.Targets.ToList())
        {
            if (targetSeed.State != HostState.Unreachable)
                continue;
            var justified = false;
            var parentName = targetsById.TryGetValue(id, out var t) ? t.Parent : null;
            for (var depth = 0; depth < 20 && parentName is { Length: > 0 }; depth++)
            {
                if (!byName.TryGetValue(parentName, out var ancestor))
                    break;
                if (seed.Targets.TryGetValue(ancestor.Id, out var ancestorSeed) &&
                    ancestorSeed.State == HostState.Down)
                {
                    justified = true;
                    break;
                }
                parentName = ancestor.Config.Parent;
            }
            if (!justified)
                seed.Targets.Remove(id);
        }
    }

    /// <summary>Presumed states carried over from the outgoing engine on a
    /// config rebuild (fresh by definition — no gap gate). Addresses ride
    /// along so a repointed target cold-starts.</summary>
    public static EngineSeed FromEngine(WatcherEngine engine)
    {
        var seed = new EngineSeed
        {
            Verdict = engine.Verdict is { CurrentVerdict: not VerdictState.Unknown } v
                ? v.CurrentVerdict : null,
        };
        foreach (var (id, monitor) in engine.Monitors)
        {
            if (monitor.CurrentState is HostState.Initializing or HostState.Starting)
                continue;
            seed.Targets[id] = new TargetSeed(monitor.CurrentState, monitor.Target.Address, monitor.Target.Probe);
        }
        foreach (var pool in engine.PoolCoordinators)
        {
            if (pool.CurrentState is not (HostState.Initializing or HostState.Starting))
                seed.Pools[pool.Pool.Id] = pool.CurrentState;
        }
        return seed;
    }
}
```

`Programs/ConWatch/WatcherHostService.cs`:
- constructor: delete the `IDbContextFactory<ConWatchDbContext> dbFactory,` parameter; add `WarmStartSeeder seeder,` right after `SettingsStore settingsStore,`.
- `StartEngineAsync`: `seed ??= await BuildSeedFromDbAsync();` → `seed ??= await seeder.FromDbAsync();`
- `RebuildInternalAsync`: `if (await WarmRestartEnabledAsync()) seed = BuildSeedFromEngine(engine);` → `if (await seeder.EnabledAsync()) seed = WarmStartSeeder.FromEngine(engine);`
- delete `WarmRestartEnabledAsync`, `BuildSeedFromDbAsync`, `DropUnjustifiedUnreachable`, `BuildSeedFromEngine` with their summaries.
- delete `using ConPersistence.Queries;` and `using Microsoft.EntityFrameworkCore;` (their only users moved).
- class summary: `/// Owns the engine lifecycle: seed config at startup, build the engine from DB config, attach the observers, and rebuild (debounced) when the configuration changes. Warm-start policy lives in <see cref="WarmStartSeeder"/>.`

`Programs/ConWatch/Startup/ConWatchServices.cs`, `AddConWatchEngine`, before `services.AddSingleton<WatcherHostService>();`:

```csharp
        services.AddSingleton<WarmStartSeeder>();
```

- [x] **Step 4: Run to verify**

Run: `dotnet test Tests/ConWatch.Tests -c Debug --filter "FullyQualifiedName~WarmStartSeederTests|FullyQualifiedName~WarmRestartTests|FullyQualifiedName~MasterEnableTests|FullyQualifiedName~CloneTests"` → all pass.
Run: `dotnet build ConnectionWatcher.sln -c Debug` → `0 Warning(s)`; full suite → 0 failed.

- [x] **Step 5: Commit**

```powershell
git add Programs/ConWatch/WarmStartSeeder.cs Programs/ConWatch/WatcherHostService.cs Programs/ConWatch/Startup/ConWatchServices.cs Tests/ConWatch.Tests/WarmStartSeederTests.cs
git commit -m "refactor: warm-start policy moves from the host service into WarmStartSeeder"
```

---

### Task 16: S7, `SnapshotImporter` writes config through the DI `ConfigService`

**Files:**
- Modify: `Libraries/ConPersistence/Snapshots/SnapshotImporter.cs`
- Modify: `Programs/ConWatch/Clone/CloneService.cs`
- Create: `Tests/ConPersistence.Tests/SnapshotImporterTests.cs`

**Interfaces:**
- Consumes: Task 2's `ConPersistence.Snapshots`/`ConPersistence.Config`.
- Produces: `SnapshotImporter.ImportAsync(IDbContextFactory<ConWatchDbContext> dbFactory, ConfigService configService, Snapshot snapshot, CancellationToken ct = default)` (replaces the three-argument overload). `SnapshotImporter.ImportToFileAsync(string dbPath, Snapshot snapshot, CancellationToken ct = default)` unchanged (the CLI/test route; it builds its own `ConfigService` over the file). `CloneService`'s constructor gains `ConfigService configService` (DI only).

Decision (see table): with the DI service the singleton's `ConfigChanged` fires on import (tested below), so the host schedules its usual debounced rebuild. `CloneService` keeps a direct rebuild after each import because the clone/take-over must answer only once the node is in its new state, but calls `RebuildIfStaleAsync()` instead of `RebuildAsync()`: it services the same write as the debounced rebuild instead of running a second one (the H4 double rebuild). `RebuildIfStaleAsync` is documented as safe exactly for callers whose last write raised the event: in `CloneAsync` that is the import, in `TakeOverAsync` the `Engine.Enabled` write.

- [x] **Step 1: Write the test**

`Tests/ConPersistence.Tests/SnapshotImporterTests.cs`:

```csharp
using ConPersistence.Config;
using ConPersistence.Snapshots;

namespace ConPersistence.Tests;

public class SnapshotImporterTests
{
    [Fact]
    public async Task Import_raises_ConfigChanged_once_on_the_service_it_is_given()
    {
        using var factory = new TestDbFactory();
        var store = new ConfigStore(factory);
        var service = new ConfigService(factory, store);
        var changes = 0;
        service.ConfigChanged += () => changes++;

        await SnapshotImporter.ImportAsync(factory, service, new Snapshot
        {
            Config = new ConfigDocument
            {
                Profiles = [new ProfileDto { Name = "Internal" }],
                Targets = [new TargetDto { Name = "Router", Address = "192.168.1.1", Profile = "Internal" }],
            },
        });

        Assert.Equal(1, changes);
        Assert.Single((await store.LoadWatcherOptionsAsync()).Targets);
    }
}
```

- [x] **Step 2: Run to verify it fails**

Run: `dotnet test Tests/ConPersistence.Tests -c Debug --filter "FullyQualifiedName~SnapshotImporterTests"`
Expected: build error, no `ImportAsync` overload takes a `ConfigService`. (Against the current three-argument method the count would be 0: it builds its own `ConfigService` whose event nobody listens to — the review's finding.)

- [x] **Step 3: Pass the service in**

`Libraries/ConPersistence/Snapshots/SnapshotImporter.cs`: delete `using Microsoft.Extensions.Logging.Abstractions;`; in `ImportToFileAsync` replace `await ImportAsync(factory, snapshot, ct);` with

```csharp
        // The CLI/test route: no host is listening, so a private ConfigService is enough.
        await ImportAsync(factory, new ConfigService(factory, new ConfigStore(factory)), snapshot, ct);
```

and replace the head of `ImportAsync` down to the `result` check with:

```csharp
    /// <summary>
    /// Replace-all import. Config goes through <paramref name="configService"/>
    /// (validated, atomic, id-preserving) so its ConfigChanged reaches whoever
    /// listens to that instance — in the app, the host's rebuild.
    /// </summary>
    public static async Task ImportAsync(IDbContextFactory<ConWatchDbContext> dbFactory,
        ConfigService configService, Snapshot snapshot, CancellationToken ct = default)
    {
        if (snapshot.SchemaVersion != 1)
            throw new InvalidOperationException(
                $"Snapshot schema version {snapshot.SchemaVersion} is not supported (expected 1).");

        // Config first — the measurement rows below reference these target GUIDs.
        var result = await configService.ImportAsync(snapshot.Config);
```

(the rest of the method is unchanged).

`Programs/ConWatch/Clone/CloneService.cs`: add `ConfigService configService` to the primary constructor after `IDbContextFactory<ConWatchDbContext> dbFactory,` (and `using ConPersistence.Config;`); replace both `await SnapshotImporter.ImportAsync(dbFactory, snapshot, ct);` with `await SnapshotImporter.ImportAsync(dbFactory, configService, snapshot, ct);`; replace both `host.RebuildAsync()` with `host.RebuildIfStaleAsync()`, and change the first one's comment to `// deterministic parked-archive state before answering; supersedes the import's debounced rebuild`.

- [x] **Step 4: Run to verify**

Run: `dotnet test Tests/ConPersistence.Tests -c Debug --filter "FullyQualifiedName~SnapshotImporterTests"` → pass.
Run: `dotnet test Tests/ConWatch.Tests -c Debug --filter "FullyQualifiedName~CloneTests|FullyQualifiedName~SnapshotTests|FullyQualifiedName~RealDataSnapshotTests"` → pass (the real-data test skips without `testdata/nas-snapshot.json`).
Run: `dotnet build ConnectionWatcher.sln -c Debug` → `0 Warning(s)`; full suite → 0 failed.

- [x] **Step 5: Commit**

```powershell
git add Libraries/ConPersistence/Snapshots/SnapshotImporter.cs Programs/ConWatch/Clone/CloneService.cs Tests/ConPersistence.Tests/SnapshotImporterTests.cs
git commit -m "refactor: snapshot import writes config through the DI ConfigService"
```

---

### Task 17: S9, `SetupService.ApplyAsync` is one transaction

**Files:**
- Modify: `Libraries/ConPersistence/Config/ConfigStore.cs` (internal `ExportAsync(ConWatchDbContext)`)
- Modify: `Libraries/ConPersistence/Config/ConfigService.cs` (new `EditDocumentAsync`)
- Modify: `Programs/ConWatch/SetupService.cs`
- Modify: `Tests/ConPersistence.Tests/ConfigServiceTests.cs`, `Tests/ConWatch.Tests/SetupApiTests.cs`

**Interfaces:**
- Consumes: Task 6's `WriteDocumentAsync` (normalizes through `TargetRow.CopyFrom`).
- Produces: `ConfigService.EditDocumentAsync(Func<ConfigDocument, ConfigResult> edit) → Task<ConfigResult>`: reads the stored document, lets `edit` change it in memory, validates, writes it back; one transaction, one `ConfigChanged`, ids preserved. `internal ConfigStore.ExportAsync(ConWatchDbContext db) → Task<ConfigDocument>` (Task 21 reuses it). `SetupService.ApplyAsync` keeps its signature and success behavior.

What changes for the wizard: all or nothing (a failing anchor leaves no router or earlier anchor behind), one `ConfigChanged` and so one rebuild instead of up to four. Two failure-path details differ and are improvements: name/address lookups see targets added earlier in the same apply (the old code looked only at the pre-apply snapshot, so an anchor at the router's address became a duplicate the validator then rejected), and a uniqueness failure reports the validator's message.

- [x] **Step 1: Write the tests**

In `Tests/ConPersistence.Tests/ConfigServiceTests.cs` add:

```csharp
    [Fact]
    public async Task EditDocument_commits_once_and_keeps_untouched_ids()
    {
        var routerId = await IdOf("Router");

        var result = await _service.EditDocumentAsync(doc =>
        {
            doc.Targets.Add(new TargetDto { Name = "TV", Address = "192.168.1.50", Parent = "Router", Profile = "Internal" });
            return ConfigResult.Ok();
        });

        Assert.True(result.Success);
        Assert.Equal(1, _changeSignals);
        Assert.Equal(routerId, await IdOf("Router"));
        Assert.Contains((await _store.LoadWatcherOptionsAsync()).Targets, t => t.Name == "TV" && t.Parent == "Router");
    }

    [Fact]
    public async Task EditDocument_that_fails_validation_commits_nothing()
    {
        var before = (await _store.LoadWatcherOptionsAsync()).Targets.Count;

        var result = await _service.EditDocumentAsync(doc =>
        {
            doc.Targets.Add(new TargetDto { Name = "Good", Address = "192.168.1.60", Profile = "Internal" });
            doc.Targets.Add(new TargetDto { Name = "", Address = "", Profile = "Internal" });
            return ConfigResult.Ok();
        });

        Assert.False(result.Success);
        Assert.Equal(ConfigErrorKind.Validation, result.Kind);
        Assert.Equal(0, _changeSignals);
        Assert.Equal(before, (await _store.LoadWatcherOptionsAsync()).Targets.Count);
    }
```

In `Tests/ConWatch.Tests/SetupApiTests.cs` add `using ConPersistence.Config;` (already there after Task 2) and:

```csharp
    [Fact]
    public async Task Apply_is_all_or_nothing()
    {
        // The empty anchor address fails validation after the router and the
        // first anchor were built: nothing may be left behind.
        var response = await _client.PostAsJsonAsync("/api/v1/setup/apply",
            new SetupApplyRequest(Router, null, ["8.8.8.8", ""]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var targets = await _client.GetFromJsonAsync<List<TargetDto>>("/api/v1/targets");
        Assert.Empty(targets!);
    }

    [Fact]
    public async Task Apply_raises_one_config_change()
    {
        var config = _factory.Services.GetRequiredService<ConfigService>();
        var changes = 0;
        config.ConfigChanged += () => Interlocked.Increment(ref changes);

        var response = await _client.PostAsJsonAsync("/api/v1/setup/apply",
            new SetupApplyRequest(Router, new SetupTarget("WAN", "203.0.113.23"), ["8.8.8.8", "1.1.1.1"]));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(1, changes);
    }
```

- [x] **Step 2: Run to verify they fail**

Run: `dotnet test Tests/ConPersistence.Tests -c Debug --filter "FullyQualifiedName~EditDocument"` → build error (`EditDocumentAsync` does not exist).
Run: `dotnet test Tests/ConWatch.Tests -c Debug --filter "FullyQualifiedName~Apply_is_all_or_nothing|FullyQualifiedName~Apply_raises_one_config_change"`
Expected: both FAIL: the first finds 2 targets (router and 8.8.8.8 committed), the second counts 5 (router, WAN, two anchors, pool).

- [x] **Step 3: `EditDocumentAsync`**

`Libraries/ConPersistence/Config/ConfigStore.cs`: replace `ExportAsync()` with

```csharp
    /// <summary>Whole-config document for backup/transfer.</summary>
    public async Task<ConfigDocument> ExportAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await ExportAsync(db);
    }

    /// <summary>Same, over a caller-supplied context (used mid-transaction by ConfigService).</summary>
    internal async Task<ConfigDocument> ExportAsync(ConWatchDbContext db)
    {
        var options = await LoadWatcherOptionsAsync(db);
        var profiles = await db.Profiles.AsNoTracking().ToListAsync();
        return new ConfigDocument
        {
            Profiles = profiles.Select(p => p.ToDto()).ToList(),
            Targets = options.Targets.Select(t => t.ToDto()).ToList(),
            Pools = options.Pools.Select(p => p.ToDto()).ToList(),
        };
    }
```

`Libraries/ConPersistence/Config/ConfigService.cs`, after `ImportAsync`:

```csharp
    /// <summary>
    /// Reads the whole stored document, lets <paramref name="edit"/> change it
    /// in memory, then validates and writes it back: one transaction, one
    /// validation, one ConfigChanged. Ids in the document are preserved, so
    /// rows the edit did not touch keep their identity. A failed edit or a
    /// failed validation commits nothing.
    /// </summary>
    public async Task<ConfigResult> EditDocumentAsync(Func<ConfigDocument, ConfigResult> edit)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();

        var doc = await store.ExportAsync(db);
        var result = edit(doc);
        if (!result.Success)
            return result;

        var validation = new WatcherOptionsValidator().Validate(null, doc.ToOptions());
        if (validation.Failed)
            return ConfigResult.Fail(ConfigErrorKind.Validation, validation.Failures!.ToArray());

        try
        {
            await ConfigStore.WriteDocumentAsync(db, doc);
        }
        catch (DbUpdateException ex) when (SqliteErrors.IsUniqueViolation(ex))
        {
            _log.LogDebug(ex, "Config edit rejected by a uniqueness constraint");
            return ConfigResult.Fail(ConfigErrorKind.Validation,
                "Change violates a uniqueness constraint (duplicate name or address).");
        }

        await tx.CommitAsync();
        ConfigChanged?.Invoke();
        return result;
    }
```

(EF's `BeginTransactionAsync` on SQLite is `BEGIN IMMEDIATE`, so the read inside is serialized with every other writer.)

- [x] **Step 4: The wizard edits one document**

`Programs/ConWatch/SetupService.cs`: the primary constructor no longer needs `ConfigStore store` — change it to `SetupService(ConfigService config, SettingsStore settings, ILogger<SetupService> log)` (DI resolves it; nothing constructs it by hand). Replace `ApplyAsync` with:

```csharp
    public async Task<ConfigResult> ApplyAsync(SetupApplyRequest request)
    {
        var routerName = "";
        var poolName = PoolName;
        var anchorNames = new List<string>();

        // One read-modify-write of the whole document: the wizard is all or
        // nothing, and the engine rebuilds once.
        var result = await config.EditDocumentAsync(doc =>
        {
            string? ByAddress(string address) => doc.Targets
                .FirstOrDefault(t => t.Address.Equals(address, StringComparison.OrdinalIgnoreCase))?.Name;
            bool NameTaken(string name) => doc.Targets
                .Any(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            string Add(TargetDto target)
            {
                doc.Targets.Add(target);
                return target.Name;
            }

            // Router: reuse any target already at that address.
            routerName = ByAddress(request.Router.Address) ?? Add(new TargetDto
            {
                Name = Unique(request.Router.Name, NameTaken),
                Address = request.Router.Address,
                Profile = "Internal",
            });

            // Optional WAN address, chained under the router.
            var pathLeaf = routerName;
            if (request.OuterIp is { } outer)
            {
                pathLeaf = ByAddress(outer.Address) ?? Add(new TargetDto
                {
                    Name = Unique(outer.Name, NameTaken),
                    Address = outer.Address,
                    Parent = routerName,
                    Profile = "Internal",
                    // The WAN hop is where the ISP's service begins.
                    Operator = request.IspName,
                });
            }

            // Anchors under the path leaf.
            foreach (var address in request.AnchorAddresses.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var catalogEntry = AnchorCatalog.FirstOrDefault(a => a.Address == address);
                anchorNames.Add(ByAddress(address) ?? Add(new TargetDto
                {
                    Name = Unique(catalogEntry?.Name ?? address, NameTaken),
                    Address = address, Parent = pathLeaf, Profile = "External",
                    Operator = catalogEntry?.Operator,
                }));
            }

            // Pool: create, or union in any new members.
            var poolIndex = doc.Pools.FindIndex(p =>
                p.Name.Equals(PoolName, StringComparison.OrdinalIgnoreCase));
            var pool = poolIndex >= 0 ? doc.Pools[poolIndex] : null;
            poolName = pool?.Name ?? PoolName;
            if (anchorNames.Count > 0)
            {
                var merged = new PoolDto
                {
                    Id = pool?.Id,
                    Name = poolName,
                    Members = pool is null
                        ? [.. anchorNames]
                        : [.. pool.Members, .. anchorNames.Where(n => !pool.Members.Contains(n))],
                    Parent = pool?.Parent ?? pathLeaf,
                };
                if (poolIndex >= 0)
                    doc.Pools[poolIndex] = merged;
                else
                    doc.Pools.Add(merged);
            }
            return ConfigResult.Ok();
        });
        if (!result.Success)
            return result;

        // Verdict settings: only fill what is unset.
        if (await settings.GetAsync(SettingKeys.AnchorPool) is not { Length: > 0 })
            await settings.SetManyAsync(new Dictionary<string, string?>
            {
                [SettingKeys.AnchorPool] = poolName,
            });

        log.LogInformation("Setup applied: router {Router}, {AnchorCount} anchors",
            routerName, anchorNames.Count);
        return ConfigResult.Ok();
    }
```

(`AnchorCatalog`, `PoolName`, `Unique` are unchanged.)

- [x] **Step 5: Run to verify**

Run: `dotnet test Tests/ConPersistence.Tests -c Debug --filter "FullyQualifiedName~ConfigServiceTests"` → pass.
Run: `dotnet test Tests/ConWatch.Tests -c Debug --filter "FullyQualifiedName~SetupApiTests|FullyQualifiedName~HostLifecycleTests"` → pass (all existing wizard tests unchanged).
Run: `dotnet build ConnectionWatcher.sln -c Debug` → `0 Warning(s)`; full suite → 0 failed.

- [x] **Step 6: Commit**

```powershell
git add Libraries/ConPersistence/Config Programs/ConWatch/SetupService.cs Tests/ConPersistence.Tests/ConfigServiceTests.cs Tests/ConWatch.Tests/SetupApiTests.cs
git commit -m "refactor: the setup wizard applies in one transaction with one config change"
```

---

## Group D: one trunk rule, a pure tracker

### Task 18: S3, `TopologyNames` is the one trunk computation (History, Targets, dashboard)

**Files:**
- Modify: `Programs/ConWatch/TopologyNames.cs` (pure `Compute`, chains, pool flag, weather threshold)
- Modify: `Programs/ConWatch/Pages/History.cshtml.cs` (one path for both modes)
- Modify: `Programs/ConWatch/Pages/Config/Targets.cshtml.cs`, `Programs/ConWatch/Pages/Config/Targets.cshtml` (one pipeline per chain)
- Modify: `Programs/ConWatch/Pages/Index.cshtml.cs` (weather from `TopologyNames`; master switch from the host)
- Create: `Tests/ConWatch.Tests/TopologyNamesTests.cs`, `Tests/ConWatch.Tests/HistoryPageTests.cs`
- Modify: `Tests/ConWatch.Tests/PageSmokeTests.cs`

**Interfaces:**
- Consumes: Task 12's `VerdictCoordinator.SubjectName`; Task 14's `SettingsDefaults.WeatherThreshold`; `TreeWalk.AncestorsOf`/`ChainToRoot` (ConCore).
- Produces: `TopologyNames.Names(List<string> TrunkNames, List<string> AnchorNames, List<List<string>> TrunkChains, bool HasAnchorPool, int WeatherThreshold, ConfigDocument Config)`; `TopologyNames.Compute(ConfigDocument config, string? anchorPoolName, int weatherThreshold) → Names` (pure); `TopologyNames.FromConfigAsync(ConfigStore, SettingsStore) → Task<Names>` (unchanged signature). `TargetsModel.TrunkChains` (`List<List<TargetDto>>`) replaces `TargetsModel.Trunk`. `HistoryModel`'s constructor drops `WatcherHostService`.

Owner decision 3 (2026-09-18): the trunk is the UNION of every anchor's ancestor chain, everywhere. Today three rules exist: `HistoryModel` walks the engine graph by `ParentId` when an engine runs and uses `TopologyNames` otherwise; `TopologyNames` walks the stored config by name; `TargetsModel` walks only from `pool.Parent ?? the unique shared parent`, so with anchors under two different parents it draws no pipeline while History shows both chains. After this task all three read `TopologyNames`, which reads the stored config (the graph the engine is built from), so the ribbon's roles no longer depend on whether monitoring is on, and `HistoryModel`'s double read of `host.Engine` (review H6) disappears with the engine branch. Folded in from the followups ("`EngineEnabledAsync()` is still called per request by … `Index.cshtml.cs`"): the dashboard reads `host.EngineEnabled`.

- [x] **Step 1: Write the tests**

`Tests/ConWatch.Tests/TopologyNamesTests.cs`:

```csharp
using ConPersistence.Config;

namespace ConWatch.Tests;

public class TopologyNamesTests
{
    /// <summary>Two anchors under two different WAN hops: the case the three old rules disagreed on.</summary>
    private static ConfigDocument TwoWans() => new()
    {
        Targets =
        [
            new() { Name = "Router", Address = "192.168.1.1" },
            new() { Name = "WanA", Address = "192.168.100.1", Parent = "Router" },
            new() { Name = "WanB", Address = "192.168.200.1", Parent = "Router" },
            new() { Name = "AnchorA", Address = "8.8.8.8", Parent = "WanA" },
            new() { Name = "AnchorB", Address = "1.1.1.1", Parent = "WanB" },
        ],
        Pools = [new() { Name = "Internet", Members = ["AnchorA", "AnchorB"], Parent = "WanA" }],
    };

    [Fact]
    public void Trunk_is_the_union_of_every_anchor_ancestor_chain()
    {
        var names = TopologyNames.Compute(TwoWans(), "internet", weatherThreshold: 2); // pool lookup ignores case

        Assert.True(names.HasAnchorPool);
        Assert.Equal(new[] { "AnchorA", "AnchorB" }, names.AnchorNames);
        Assert.Equal(new[] { "WanA", "Router", "WanB" }, names.TrunkNames);
        Assert.Equal(2, names.TrunkChains.Count);
        Assert.Equal(new[] { "Router", "WanA" }, names.TrunkChains[0]);
        Assert.Equal(new[] { "Router", "WanB" }, names.TrunkChains[1]);
        Assert.Equal(2, names.WeatherThreshold);
    }

    [Fact]
    public void Anchors_sharing_a_parent_give_one_chain()
    {
        var doc = new ConfigDocument
        {
            Targets =
            [
                new() { Name = "Router", Address = "192.168.1.1" },
                new() { Name = "Google", Address = "8.8.8.8", Parent = "Router" },
                new() { Name = "Cloudflare", Address = "1.1.1.1", Parent = "Router" },
            ],
            Pools = [new() { Name = "Internet", Members = ["Google", "Cloudflare"] }],
        };

        var names = TopologyNames.Compute(doc, "Internet", weatherThreshold: 2);

        Assert.Equal(new[] { "Router" }, names.TrunkNames);
        Assert.Equal(new[] { "Router" }, Assert.Single(names.TrunkChains));
    }

    [Fact]
    public void An_unknown_anchor_pool_gives_no_roles()
    {
        var names = TopologyNames.Compute(TwoWans(), "Nope", weatherThreshold: 2);

        Assert.False(names.HasAnchorPool);
        Assert.Empty(names.TrunkNames);
        Assert.Empty(names.AnchorNames);
        Assert.Empty(names.TrunkChains);
    }
}
```

`Tests/ConWatch.Tests/HistoryPageTests.cs` (a pin: today both modes already agree for this config, so it passes before and after):

```csharp
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;

namespace ConWatch.Tests;

public class HistoryPageTests : IDisposable
{
    private readonly ConWatchFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private static string Island(string html, string id)
    {
        var match = Regex.Match(html,
            $"<script id=\"{id}\" type=\"application/json\">(.*?)</script>", RegexOptions.Singleline);
        Assert.True(match.Success, $"no '{id}' island in the page");
        return match.Groups[1].Value;
    }

    [Fact]
    public async Task Archive_mode_renders_the_same_subjects_and_roles_as_live_mode()
    {
        var client = _factory.CreateClient();
        await ConfigApiTests.SeedHomeAsync(client);
        var host = _factory.Services.GetRequiredService<WatcherHostService>();
        await ConfigApiTests.Eventually(() =>
            Task.FromResult(host.Engine is { Monitors.Count: 3, Verdict: not null }));
        var live = await client.GetStringAsync("/History");

        (await client.PutAsJsonAsync("/api/v1/settings",
            new Dictionary<string, string?> { ["Engine.Enabled"] = "false" })).EnsureSuccessStatusCode();
        await ConfigApiTests.Eventually(() => Task.FromResult(host.Engine is null));
        var archive = await client.GetStringAsync("/History");

        Assert.Equal(Island(live, "history-data"), Island(archive, "history-data"));
        Assert.Equal(Island(live, "history-topology"), Island(archive, "history-topology"));
        Assert.Contains("\"role\":\"anchor\"", Island(live, "history-data"));
        Assert.Contains("\"name\":\"Internet (verdict)\"", Island(live, "history-data"));
        Assert.Contains("\"trunkNames\":[\"Router\"]", Island(live, "history-topology"));
    }
}
```

In `Tests/ConWatch.Tests/PageSmokeTests.cs` add `using System.Net.Http.Json;`, `using System.Text.RegularExpressions;`, `using ConPersistence.Config;` and:

```csharp
    [Fact]
    public async Task Targets_pipeline_draws_one_chain_per_anchor_parent()
    {
        var client = _factory.CreateClient();
        foreach (var target in new TargetDto[]
        {
            new() { Name = "Router", Address = "192.168.1.1", Profile = "Internal" },
            new() { Name = "WanA", Address = "192.168.100.1", Parent = "Router", Profile = "Internal" },
            new() { Name = "WanB", Address = "192.168.200.1", Parent = "Router", Profile = "Internal" },
            new() { Name = "AnchorA", Address = "8.8.8.8", Parent = "WanA" },
            new() { Name = "AnchorB", Address = "1.1.1.1", Parent = "WanB" },
        })
            (await client.PostAsJsonAsync("/api/v1/targets", target)).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync("/api/v1/pools",
            new PoolDto { Name = "Internet", Members = ["AnchorA", "AnchorB"] })).EnsureSuccessStatusCode();

        var html = await client.GetStringAsync("/Config/Targets");

        Assert.Equal(2, Regex.Matches(html, "class=\"cw-pipeline\"").Count);
        Assert.Contains("<b>WanA</b>", html);
        Assert.Contains("<b>WanB</b>", html);
    }
```

- [x] **Step 2: Run to verify**

Run: `dotnet test Tests/ConWatch.Tests -c Debug --filter "FullyQualifiedName~TopologyNamesTests|FullyQualifiedName~HistoryPageTests|FullyQualifiedName~Targets_pipeline_draws_one_chain_per_anchor_parent"`
Expected: build error (`TopologyNames.Compute` does not exist). Temporarily excluding `TopologyNamesTests.cs`: `Archive_mode_renders_the_same…` PASSES (pin) and `Targets_pipeline_draws_one_chain…` FAILS (0 pipelines: the old rule finds no single leaf).

- [x] **Step 3: One computation**

Replace `Programs/ConWatch/TopologyNames.cs` with:

```csharp
using ConCore.Targets;
using ConPersistence.Config;
using ConPersistence.Settings;

namespace ConWatch;

/// <summary>
/// Trunk and anchor roles from the STORED configuration — the graph the
/// engine is built from — for the dashboard, History and the Targets
/// pipeline, whether or not an engine is running. The one rule (owner
/// decision 2026-09-18): the trunk is the UNION of every anchor's ancestor
/// chain.
/// </summary>
public static class TopologyNames
{
    /// <param name="TrunkNames">Every ancestor of every anchor, first-seen order walking each anchor upward.</param>
    /// <param name="AnchorNames">The anchor pool's members that exist, in pool order.</param>
    /// <param name="TrunkChains">One root-first chain per distinct anchor parent, in first-seen order; their union is <paramref name="TrunkNames"/>.</param>
    /// <param name="HasAnchorPool">The AnchorPool setting names an existing pool (the engine then runs a verdict).</param>
    /// <param name="WeatherThreshold">The Verdict.WeatherThreshold setting.</param>
    public sealed record Names(List<string> TrunkNames, List<string> AnchorNames,
        List<List<string>> TrunkChains, bool HasAnchorPool, int WeatherThreshold, ConfigDocument Config);

    public static async Task<Names> FromConfigAsync(ConfigStore configStore, SettingsStore settingsStore) =>
        Compute(await configStore.ExportAsync(),
            await settingsStore.GetAsync(SettingKeys.AnchorPool),
            await settingsStore.GetIntAsync(SettingKeys.WeatherThreshold, SettingsDefaults.WeatherThreshold));

    public static Names Compute(ConfigDocument config, string? anchorPoolName, int weatherThreshold)
    {
        var pool = anchorPoolName is { Length: > 0 }
            ? config.Pools.FirstOrDefault(p =>
                p.Name.Equals(anchorPoolName, StringComparison.OrdinalIgnoreCase))
            : null;
        if (pool is null)
            return new Names([], [], [], HasAnchorPool: false, weatherThreshold, config);

        var byName = config.Targets
            .GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        TargetDto? ParentOf(TargetDto t) =>
            t.Parent is { Length: > 0 } parent && byName.TryGetValue(parent, out var p) ? p : null;

        var members = pool.Members.Where(byName.ContainsKey).Select(m => byName[m]).ToList();
        var trunk = TreeWalk.AncestorsOf(members, ParentOf).Select(t => t.Name).ToList();
        var chains = members
            .Select(ParentOf).OfType<TargetDto>()
            .DistinctBy(leaf => leaf.Name, StringComparer.OrdinalIgnoreCase)
            .Select(leaf => TreeWalk.ChainToRoot(leaf, ParentOf).Select(t => t.Name).Reverse().ToList())
            .ToList();
        return new Names(trunk, members.Select(m => m.Name).ToList(), chains,
            HasAnchorPool: true, weatherThreshold, config);
    }
}
```

- [x] **Step 4: History reads it in both modes**

Replace `Programs/ConWatch/Pages/History.cshtml.cs` with:

```csharp
using System.Text.Json;
using ConEngine.Topology;
using ConPersistence.Config;
using ConPersistence.Settings;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ConWatch.Pages;

public class HistoryModel(SettingsStore settingsStore, ConfigStore configStore) : PageModel
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public string SubjectsJson { get; private set; } = "[]";
    public string TopologyJson { get; private set; } = "{}";

    /// <summary>Subjects and topology roles from the stored configuration in
    /// both modes (engine running or archive): the ribbon's roles never depend
    /// on whether monitoring is on. See <see cref="TopologyNames"/>.</summary>
    public async Task OnGetAsync()
    {
        var names = await TopologyNames.FromConfigAsync(configStore, settingsStore);
        var trunk = names.TrunkNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var anchors = names.AnchorNames.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var subjects = new List<object>();
        subjects.AddRange(names.Config.Targets
            .Where(t => t.Id is not null)
            .OrderBy(t => t.Name)
            .Select(t => new
            {
                Id = t.Id!.Value,
                Name = t.Name,
                Kind = "target",
                Parent = t.Parent,
                Role = trunk.Contains(t.Name) ? "trunk"
                    : anchors.Contains(t.Name) ? "anchor" : "other",
            }));
        subjects.AddRange(names.Config.Pools
            .Where(p => p.Id is not null)
            .OrderBy(p => p.Name)
            .Select(p => new
            {
                Id = p.Id!.Value, Name = p.Name, Kind = "pool",
                Parent = (string?)null, Role = "other",
            }));
        if (names.HasAnchorPool)
            subjects.Add(new
            {
                Id = VerdictCoordinator.SubjectId, Name = $"{VerdictCoordinator.SubjectName} (verdict)",
                Kind = "verdict", Parent = (string?)null, Role = "other",
            });

        SubjectsJson = JsonSerializer.Serialize(subjects, JsonOptions);
        TopologyJson = JsonSerializer.Serialize(new
        {
            TrunkNames = names.TrunkNames,
            AnchorNames = names.AnchorNames,
            WeatherThreshold = names.WeatherThreshold,
        }, JsonOptions);
    }
}
```

(The verdict subject used to appear when the running engine had a verdict, or in archive mode when the pool had members; both mean "the anchor-pool setting names an existing pool", since the validator rejects a pool without members.)

- [x] **Step 5: Targets draws one pipeline per chain**

In `Programs/ConWatch/Pages/Config/Targets.cshtml.cs` replace the `Trunk` property with

```csharp
    /// <summary>One root→leaf chain per distinct anchor parent (empty = no pipeline view).</summary>
    public List<List<TargetDto>> TrunkChains { get; private set; } = [];
```

and replace `OnGetAsync` and `ComputePipeline` with:

```csharp
    public async Task OnGetAsync()
    {
        var names = await TopologyNames.FromConfigAsync(store, settings);
        Config = names.Config;
        Warnings = [.. await health.EvaluateAsync(), .. RuntimeHealth.RefusalWarnings(host.Engine)];

        var byName = Config.Targets.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
        Anchors = names.AnchorNames.Select(n => byName[n]).ToList();
        TrunkChains = names.TrunkChains.Select(chain => chain.Select(n => byName[n]).ToList()).ToList();

        var trunkNames = names.TrunkNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var anchorNames = names.AnchorNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names.TrunkNames)
        {
            var branches = Config.Targets
                .Where(t => t.Parent == name && !trunkNames.Contains(t.Name) && !anchorNames.Contains(t.Name))
                .ToList();
            if (branches.Count > 0)
                BranchesByTrunkName[name] = branches;
        }
    }
```

(`using ConCore.Targets;` is no longer needed there.) In `Programs/ConWatch/Pages/Config/Targets.cshtml` replace the whole `@if (Model.Trunk.Count > 0) { … }` block with:

```cshtml
@if (Model.TrunkChains.Count > 0)
{
    <h3>Path to the internet</h3>
    @foreach (var chain in Model.TrunkChains)
    {
        <div class="cw-pipeline">
            @for (var i = 0; i < chain.Count; i++)
            {
                var stage = chain[i];
                <div class="cw-stage">
                    <b>@stage.Name</b> <small>@stage.Address</small>
                    @if (Model.BranchesByTrunkName.TryGetValue(stage.Name, out var branches))
                    {
                        <small class="cw-branches">⌐ @string.Join(", ", branches.Select(b => b.Name))</small>
                    }
                </div>
                var below = i + 1 < chain.Count
                    ? new[] { chain[i + 1].Id!.Value }
                    : Model.Anchors.Where(a => a.Parent == stage.Name).Select(a => a.Id!.Value).ToArray();
                @if (below.Length > 0)
                {
                    <div class="cw-insert">
                        <button class="outline" data-insert-children='@System.Text.Json.JsonSerializer.Serialize(below)'
                                data-insert-label="between @stage.Name and what follows">⊕ insert here</button>
                    </div>
                }
            }
            <div class="cw-stage cw-anchors">
                <b>Internet anchors</b>
                <small>@string.Join(", ", Model.Anchors.Where(a => a.Parent == chain[^1].Name).Select(a => a.Name))</small>
            </div>
        </div>
    }
}
```

(Where every anchor shares one parent, this renders the same single pipeline with the same anchor list as before.)

- [x] **Step 6: The dashboard**

In `Programs/ConWatch/Pages/Index.cshtml.cs` replace `EngineDisabled = !await host.EngineEnabledAsync();` with `EngineDisabled = !host.EngineEnabled;` and, in the `InitialDataJson` object, `WeatherThreshold = await settingsStore.GetIntAsync(ConPersistence.Settings.SettingKeys.WeatherThreshold, 2),` with `WeatherThreshold = names.WeatherThreshold,`.

- [x] **Step 7: Run to verify**

Run: `dotnet test Tests/ConWatch.Tests -c Debug --filter "FullyQualifiedName~TopologyNamesTests|FullyQualifiedName~HistoryPageTests|FullyQualifiedName~PageSmokeTests|FullyQualifiedName~MasterEnableTests"` → all pass.
Run: `dotnet build ConnectionWatcher.sln -c Debug` → `0 Warning(s)`; full suite → 0 failed; `npx vitest run` (ClientApp) → 56 passed.

- [x] **Step 8: Commit**

```powershell
git add Programs/ConWatch/TopologyNames.cs Programs/ConWatch/Pages/History.cshtml.cs Programs/ConWatch/Pages/Config/Targets.cshtml.cs Programs/ConWatch/Pages/Config/Targets.cshtml Programs/ConWatch/Pages/Index.cshtml.cs Tests/ConWatch.Tests/TopologyNamesTests.cs Tests/ConWatch.Tests/HistoryPageTests.cs Tests/ConWatch.Tests/PageSmokeTests.cs
git commit -m "refactor: TopologyNames is the one trunk rule for History, Targets and the dashboard"
```

---

### Task 19: S4, `HostTracker` is pure; `TargetMonitor` owns and locks it

**Files:**
- Modify: `Core/ConEngine/Tracking/HostTracker.cs` (no lock, no subjects, no logger)
- Modify: `Core/ConEngine/Tracking/TargetMonitor.cs` (drop `StatusSource` and its per-sample publication; construct the pure tracker)
- Create: `Tests/ConEngine.Tests/Fakes/TrackerProbe.cs`
- Modify: `Tests/ConEngine.Tests/HostAnalysisEngineTests.cs`, `Tests/ConEngine.Tests/WarmStartTests.cs` (drive the tracker through `TrackerProbe`), `Tests/ConEngine.Tests/TargetMonitorTests.cs`

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces: `HostTracker(IPAddress hostAddress, PingProfile profile, TimeProvider timeProvider)`; members `HostAddress`, `CurrentState`, `Assumed`, `HistoryCount`, `Seed(HostState) → AnalysisOutcome?`, `UpdateAddress(IPAddress)`, `RecordPingResult(PingResult) → AnalysisOutcome?`, `MarkUnreachable(Guid) → AnalysisOutcome?`, `Release() → AnalysisOutcome?`, `EntriesSince(DateTimeOffset) → IReadOnlyList<PingHistoryEntry>`. Removed: `HostTracker.HostStatusSource`, `HostTracker.HostStateChangeSource`, `HostTracker.MinUsableSample`, the `ILogger<HostTracker>` parameter, `TargetMonitor.StatusSource`. Kept: `TargetMonitor.LastStatus` (the polling surface `/status`, SignalR, metrics and MCP read). Test helper `ConEngine.Tests.Fakes.TrackerProbe`.

Every call into the tracker already happens under `TargetMonitor._lock`, so the tracker's own lock is a second layer, and its two subjects were published under the monitor's lock to no production subscriber (`HostStatusSource` has no reference; `HostStateChangeSource` only two test files; `TargetMonitor.StatusSource` none), while every sample paid for a `TargetStatus` closure in the outbox. Per the Global Constraints, the only change to `TargetMonitor`'s outbox is that it no longer carries the status publication; lock order, transition queuing and the drain stay as they are.

- [x] **Step 1: Move the tracker tests onto returned outcomes (still green on the old tracker)**

`Tests/ConEngine.Tests/Fakes/TrackerProbe.cs`:

```csharp
using System.Net;
using ConCore;
using ConCore.Targets;
using ConEngine.Events;
using ConEngine.Tracking;
using Microsoft.Extensions.Logging.Abstractions;

namespace ConEngine.Tests.Fakes;

/// <summary>
/// Drives a <see cref="HostTracker"/> the way TargetMonitor does and keeps
/// every transition its commands RETURN — what the tracker's old
/// HostStateChangeSource subscription used to collect.
/// </summary>
public sealed class TrackerProbe(IPAddress address, PingProfile profile, TimeProvider time)
{
    private readonly HostTracker _tracker = new(address, profile, time, NullLogger<HostTracker>.Instance);

    public List<AnalysisTransition> Transitions { get; } = [];
    public HostState CurrentState => _tracker.CurrentState;
    public bool Assumed => _tracker.Assumed;
    public int HistoryCount => _tracker.HistoryCount;

    public void Seed(HostState state) => Keep(_tracker.Seed(state));
    public void RecordPingResult(PingResult result) => Keep(_tracker.RecordPingResult(result));
    public void MarkUnreachable(Guid ancestorId) => Keep(_tracker.MarkUnreachable(ancestorId));
    public void Release() => Keep(_tracker.Release());
    public IReadOnlyList<PingHistoryEntry> EntriesSince(DateTimeOffset since) => _tracker.EntriesSince(since);

    private void Keep(AnalysisOutcome? outcome)
    {
        if (outcome?.Transition is { } transition)
            Transitions.Add(transition);
    }
}
```

In `Tests/ConEngine.Tests/HostAnalysisEngineTests.cs` add `using ConEngine.Tests.Fakes;` and replace `Make()` and `Feed(...)`'s first parameter:

```csharp
    private static (TrackerProbe tracker, List<AnalysisTransition> transitions, FakeTimeProvider time) Make()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-08-14T00:00:00Z"));
        var tracker = new TrackerProbe(Ip, Profile, time);
        return (tracker, tracker.Transitions, time);
    }

    private static void Feed(TrackerProbe tracker, FakeTimeProvider time, int count, bool success)
```

(the body of `Feed` and every test are unchanged: `TrackerProbe` has the same member names they call).

In `Tests/ConEngine.Tests/WarmStartTests.cs` make the same two replacements, keeping its start time:

```csharp
    private static (TrackerProbe tracker, List<AnalysisTransition> transitions, FakeTimeProvider time) Make()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-11T00:00:00Z"));
        var tracker = new TrackerProbe(Ip, Profile, time);
        return (tracker, tracker.Transitions, time);
    }

    private static void Feed(TrackerProbe tracker, FakeTimeProvider time, int count, bool success)
```

In `Tests/ConEngine.Tests/TargetMonitorTests.cs` add:

```csharp
    [Fact]
    public void LastStatus_follows_every_recorded_sample()
    {
        var (m, _, time) = Make();
        m.StartTracking(Ip);
        Assert.Null(m.LastStatus);

        Feed(m, time, 3, success: true);
        Assert.Equal(3, m.LastStatus!.Host.SampleSize);

        Feed(m, time, 1, success: false);
        Assert.Equal(4, m.LastStatus!.Host.SampleSize);
        Assert.Equal(m.Target.Id, m.LastStatus.TargetId);
    }
```

Run: `dotnet test Tests/ConEngine.Tests -c Debug --filter "FullyQualifiedName~HostAnalysisEngineTests|FullyQualifiedName~WarmStartTests|FullyQualifiedName~TargetMonitorTests"`
Expected: all pass. The tests no longer subscribe to anything; they pin the tracker's behavior through what it returns.

- [x] **Step 2: Make the tracker pure**

Replace `Core/ConEngine/Tracking/HostTracker.cs` with:

```csharp
using System.Net;
using ConCore;
using ConCore.Targets;

namespace ConEngine.Tracking;

/// <summary>
/// The sliding window of ping results and the per-host state machine for one
/// host. Pure: no lock and no streams — every command returns its
/// <see cref="AnalysisOutcome"/>. The owning <see cref="TargetMonitor"/> calls
/// it only under its own lock and publishes the outcome after releasing it.
/// </summary>
public class HostTracker
{
    private readonly CircularBuffer<PingHistoryEntry> _history;
    private readonly int _minUsableSample;
    private readonly HostAnalysisEngine _analysisEngine;

    /// <summary>The IP address of the host being tracked (updated on a DNS re-point).</summary>
    public IPAddress HostAddress { get; private set; }

    public HostTracker(IPAddress hostAddress, PingProfile profile, TimeProvider timeProvider)
    {
        HostAddress = hostAddress ?? throw new ArgumentNullException(nameof(hostAddress));
        ArgumentNullException.ThrowIfNull(profile);

        _history = new CircularBuffer<PingHistoryEntry>(profile.HistorySize);
        _minUsableSample = profile.MinUsableSample;
        _analysisEngine = new HostAnalysisEngine(profile, hostAddress, timeProvider);
    }

    /// <summary>Current state of the host state machine.</summary>
    public HostState CurrentState => _analysisEngine.CurrentState;

    /// <summary>True while running on an unconfirmed warm-restart presumption.</summary>
    public bool Assumed => _analysisEngine.Assumed;

    /// <summary>Entries in the history window (tests and diagnostics).</summary>
    public int HistoryCount => _history.Count;

    /// <summary>Warm restart: adopt a presumed state silently (no transition).</summary>
    public AnalysisOutcome? Seed(HostState state) => _analysisEngine.Seed(state);

    /// <summary>DNS moved the host: keep state and history, change the address.</summary>
    public void UpdateAddress(IPAddress address)
    {
        HostAddress = address;
        _analysisEngine.UpdateAddress(address);
    }

    /// <summary>Records a new ping result in the history and recalculates state.</summary>
    public AnalysisOutcome? RecordPingResult(PingResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        _history.Add(PingHistoryEntry.FromPingResult(result));
        return _analysisEngine.Recalculate(Sample());
    }

    /// <summary>Commanded by topology: an ancestor is Down.</summary>
    public AnalysisOutcome? MarkUnreachable(Guid ancestorId) => _analysisEngine.MarkUnreachable(ancestorId);

    /// <summary>Commanded by topology: the ancestor recovered; clear history and re-prove.</summary>
    public AnalysisOutcome? Release()
    {
        _history.Clear();
        return _analysisEngine.Release();
    }

    /// <summary>History entries with Timestamp &gt;= since, oldest first (a snapshot).</summary>
    public IReadOnlyList<PingHistoryEntry> EntriesSince(DateTimeOffset since) =>
        _history.InReverseOrder().TakeWhile(e => e.Timestamp >= since).Reverse().ToList();

    /// <summary>The most recent MinUsableSample entries, newest first; consumed immediately.</summary>
    private IEnumerable<PingHistoryEntry> Sample() =>
        _history.InReverseOrder().Take(Math.Min(_history.Count, _minUsableSample));

    public override string ToString() => $"{HostAddress}: {CurrentState} (last {HistoryCount})";
}
```

In `Core/ConEngine/Tracking/TargetMonitor.cs`:
- class summary, append to the threading paragraph: `The HostTracker it owns is pure; this monitor's _lock is the only lock around it.`
- delete the `_loggerFactory` field and its assignment in the constructor (the constructor keeps its `ILoggerFactory loggerFactory` parameter for `_log`).
- delete `private readonly Subject<TargetStatus> _statusSubject = new();` and change the comment above `_transitionSubject` to `// A plain subject: every subscriber attaches before the engine activates anything, and LastStatus serves polling consumers.`
- delete `public IObservable<TargetStatus> StatusSource => _statusSubject;`
- `StartTracking`: `_tracker = new HostTracker(resolvedAddress, Profile, _timeProvider);`
- `QueueOutcomeLocked`: replace the four lines from `var targetStatus = new TargetStatus` through `_outbox.Enqueue(() => _statusSubject.OnNext(targetStatus));` with

```csharp
        LastStatus = new TargetStatus
        {
            TargetId = Target.Id, TargetName = Target.Name, Host = outcome.Status,
        };
```

- `Dispose`: delete `_statusSubject.OnCompleted();`.

In `Tests/ConEngine.Tests/Fakes/TrackerProbe.cs` the tracker is now built with three arguments: replace `new(address, profile, time, NullLogger<HostTracker>.Instance)` with `new(address, profile, time)` and delete `using Microsoft.Extensions.Logging.Abstractions;`.

- [x] **Step 3: Confirm nothing else used what was removed**

```powershell
git grep -n -e "HostStatusSource" -e "HostStateChangeSource" -e "StatusSource" -e "_statusSubject" -- Core Libraries Programs Tests
```
Expected: no output.

- [x] **Step 4: Run to verify**

Run: `dotnet test Tests/ConEngine.Tests -c Debug` → 0 failed (incl. `WarmStartTests`, `TopologyCoordinatorTests`, `VerdictCoordinatorTests`, `DnsRefreshTests`, `ProbeThreadingTests`).
Run: `dotnet build ConnectionWatcher.sln -c Debug` → `0 Warning(s)`; full suite → 0 failed.

- [x] **Step 5: Commit**

```powershell
git add Core/ConEngine/Tracking/HostTracker.cs Core/ConEngine/Tracking/TargetMonitor.cs Tests/ConEngine.Tests/Fakes/TrackerProbe.cs Tests/ConEngine.Tests/HostAnalysisEngineTests.cs Tests/ConEngine.Tests/WarmStartTests.cs Tests/ConEngine.Tests/TargetMonitorTests.cs
git commit -m "refactor: HostTracker is pure; TargetMonitor drops the unused status stream"
```

---

## Group E: optional (each may be dropped without affecting the others)

### Task 20 (may be dropped): E3, `PoolCoordinator` and `VerdictCoordinator` publish outside their lock

**Files:**
- Modify: `Core/ConEngine/Topology/PoolCoordinator.cs`
- Modify: `Core/ConEngine/Topology/VerdictCoordinator.cs`
- Modify: `Tests/ConEngine.Tests/PoolCoordinatorTests.cs`, `Tests/ConEngine.Tests/VerdictCoordinatorTests.cs`

**Interfaces:**
- Consumes: Task 12's `SubjectName` (VerdictCoordinator is open anyway).
- Produces: no public change. Both coordinators queue their transition under `_lock` and deliver it after releasing it, through a `TryEnter` drain.

Why optional: the review's lock-order table shows no deadlock today; this removes the last publish-under-lock in the engine and makes the coordinators look like `TargetMonitor`. Why `TryEnter` (the followups' note): a coordinator's `Recalculate` runs inline inside some monitor's publish; a thread that finds another thread already draining this coordinator leaves its item in the queue instead of waiting, and the draining thread re-checks the queue after releasing the lock, so nothing is stranded and no thread blocks on a coordinator while inside a monitor's publish. Queue order is state order because items are enqueued under `_lock`. `System.Threading.Lock` is re-entrant, as `TargetMonitor`'s `_publishLock` relies on already.

- [x] **Step 1: Write the tests**

In `Tests/ConEngine.Tests/PoolCoordinatorTests.cs` add:

```csharp
    [Fact]
    public void Pool_transitions_are_published_outside_the_pool_lock()
    {
        // A subscriber that needs the pool's lock from ANOTHER thread must not
        // wait for the publisher. Seed takes the lock (and is a no-op once the
        // pool has left Initializing).
        var results = new List<bool>();
        using var subscription = _pool.TransitionSource.Subscribe(_ =>
            results.Add(Task.Run(() => _pool.Seed(HostState.Down)).Wait(TimeSpan.FromSeconds(2))));

        Feed(_google, 5, success: true);

        Assert.NotEmpty(results);
        Assert.All(results, r => Assert.True(r));
    }
```

In `Tests/ConEngine.Tests/VerdictCoordinatorTests.cs` add:

```csharp
    [Fact]
    public void Verdict_transitions_are_published_outside_the_verdict_lock()
    {
        var results = new List<bool>();
        using var subscription = _verdict.TransitionSource.Subscribe(_ =>
            results.Add(Task.Run(() => _verdict.Seed(VerdictState.LinkDown)).Wait(TimeSpan.FromSeconds(2))));

        AllHealthy();

        Assert.NotEmpty(results);
        Assert.All(results, r => Assert.True(r));
        Assert.Equal(VerdictState.Healthy, _verdict.CurrentVerdict); // Seed stayed a no-op
    }
```

- [x] **Step 2: Run to verify they fail**

Run: `dotnet test Tests/ConEngine.Tests -c Debug --filter "FullyQualifiedName~published_outside"`
Expected: both FAIL (`Assert.True() Failure`): today the subscriber runs inside `lock (_lock)`, so the other thread's `Seed` waits past the 2 s bound.

- [x] **Step 3: Outbox in `PoolCoordinator`**

Add `using System.Collections.Concurrent;` and two fields:

```csharp
    private readonly Lock _publishLock = new();
    private readonly ConcurrentQueue<TargetTransition> _outbox = new();
```

In `Recalculate`, replace the last two statements inside the lock (`_log.LogInformation("Pool transition: …", transition);` and `_transitionSubject.OnNext(transition);`) with

```csharp
            // Queued under the lock (queue order is state order), published after it.
            _outbox.Enqueue(transition);
```

and add `DrainOutbox();` as the last statement of `Recalculate`, after the `lock` block. Add:

```csharp
    /// <summary>
    /// Publishes queued transitions in state order, never under _lock.
    /// TryEnter, not Enter: a thread that finds another one draining leaves
    /// its item to that thread, which re-checks the queue after releasing the
    /// lock — nothing is stranded, and no thread waits on this coordinator
    /// while it may itself be inside a monitor's publish.
    /// </summary>
    private void DrainOutbox()
    {
        while (!_outbox.IsEmpty)
        {
            if (!_publishLock.TryEnter())
                return;
            try
            {
                while (_outbox.TryDequeue(out var transition))
                {
                    _log.LogInformation("Pool transition: {Transition}", transition);
                    _transitionSubject.OnNext(transition);
                }
            }
            finally
            {
                _publishLock.Exit();
            }
        }
    }
```

- [x] **Step 4: Outbox in `VerdictCoordinator`**

The same two fields and `using System.Collections.Concurrent;`. In `Recalculate`, replace `_log.LogInformation("Verdict: {Transition}", transition);` and `_transitionSubject.OnNext(transition);` with `_outbox.Enqueue(transition);` and add `DrainOutbox();` after the `lock` block. Add the same `DrainOutbox()` method as in Step 3 with the log line `_log.LogInformation("Verdict: {Transition}", transition);`.

- [x] **Step 5: Run to verify**

Run: `dotnet test Tests/ConEngine.Tests -c Debug` → 0 failed (incl. the two new tests, `SuspicionCoordinatorTests`, `WatcherEngineTests`, `PingPumpResilienceTests`).
Run: `dotnet build ConnectionWatcher.sln -c Debug` → `0 Warning(s)`; full suite → 0 failed.

- [x] **Step 6: Commit**

```powershell
git add Core/ConEngine/Topology/PoolCoordinator.cs Core/ConEngine/Topology/VerdictCoordinator.cs Tests/ConEngine.Tests/PoolCoordinatorTests.cs Tests/ConEngine.Tests/VerdictCoordinatorTests.cs
git commit -m "refactor: pool and verdict coordinators publish outside their lock"
```

---

### Task 21 (may be dropped): P4, whole-config reads see one snapshot

**Files:**
- Modify: `Libraries/ConPersistence/Config/ConfigStore.cs` (`LoadWatcherOptionsAsync()`, `ExportAsync()`)
- Create: `Tests/ConPersistence.Tests/ConfigSnapshotReadTests.cs`

**Interfaces:**
- Consumes: Task 14's `FileDbFactory` (with `With(params IInterceptor[])`); Task 17's internal `ConfigStore.ExportAsync(ConWatchDbContext)`.
- Produces: no signature change. `ConfigStore.LoadWatcherOptionsAsync()` and `ConfigStore.ExportAsync()` run their three reads inside one deferred SQLite transaction.

Why optional: the race needs an import committing within milliseconds of a load; its effect is one failed rebuild (`KeyNotFoundException` from `profileNamesById[t.ProfileId]`, "Engine rebuild failed" until the next config change). The fix (the followups: "needs `SqliteConnection.BeginTransaction(deferred: true)` threaded through EF"): EF's own `BeginTransactionAsync` is `BEGIN IMMEDIATE`, which takes the write lock; a deferred `BEGIN` takes none, and in WAL mode its first read pins one snapshot until it ends. The overloads taking a caller's context (`ConfigService.MutateAsync`, `EditDocumentAsync`) already run inside the caller's transaction and are unchanged.

- [x] **Step 1: Write the tests**

`Tests/ConPersistence.Tests/ConfigSnapshotReadTests.cs`:

```csharp
using System.Data.Common;
using ConPersistence.Config;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ConPersistence.Tests;

public class ConfigSnapshotReadTests
{
    /// <summary>Runs one write, once, the moment the first query against
    /// Profiles has opened its reader: between the profile read and the
    /// target read of a whole-config load.</summary>
    private sealed class WriteBetweenReads(Func<Task> write) : DbCommandInterceptor
    {
        private int _fired;

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command,
            CommandExecutedEventData eventData, DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("\"Profiles\"") && Interlocked.Exchange(ref _fired, 1) == 0)
                await write();
            return result;
        }
    }

    // A document without ids: every import mints new profile and target ids,
    // so a load that mixes old profiles with new targets cannot map
    // ProfileId -> name.
    private static ConfigDocument Home(string routerAddress) => new()
    {
        Profiles = [new ProfileDto { Name = "Internal" }],
        Targets = [new TargetDto { Name = "Router", Address = routerAddress, Profile = "Internal" }],
    };

    private static async Task<ConfigService> SeededWriterAsync(FileDbFactory files)
    {
        await files.PrepareAsync();
        var writer = new ConfigService(files, new ConfigStore(files));
        Assert.True((await writer.ImportAsync(Home("192.168.1.1"))).Success);
        return writer;
    }

    [Fact]
    public async Task Load_reads_one_snapshot_while_an_import_commits()
    {
        using var files = new FileDbFactory();
        var writer = await SeededWriterAsync(files);
        var reader = new ConfigStore(files.With(new WriteBetweenReads(async () =>
            Assert.True((await writer.ImportAsync(Home("192.168.1.2"))).Success))));

        var options = await reader.LoadWatcherOptionsAsync();

        Assert.Equal("192.168.1.1", Assert.Single(options.Targets).Address); // the state before the import
        Assert.Equal("192.168.1.2",
            Assert.Single((await new ConfigStore(files).LoadWatcherOptionsAsync()).Targets).Address);
    }

    [Fact]
    public async Task Export_reads_one_snapshot_while_an_import_commits()
    {
        using var files = new FileDbFactory();
        var writer = await SeededWriterAsync(files);
        var reader = new ConfigStore(files.With(new WriteBetweenReads(async () =>
            Assert.True((await writer.ImportAsync(Home("192.168.1.2"))).Success))));

        var doc = await reader.ExportAsync();

        Assert.Equal("192.168.1.1", Assert.Single(doc.Targets).Address);
        Assert.Equal(doc.Targets[0].Profile, Assert.Single(doc.Profiles).Name);
    }
}
```

- [x] **Step 2: Run to verify they fail**

Run: `dotnet test Tests/ConPersistence.Tests -c Debug --filter "FullyQualifiedName~ConfigSnapshotReadTests"`
Expected: both FAIL with `KeyNotFoundException` (the target read sees the new target, whose `ProfileId` is not among the profiles read before the import).

- [x] **Step 3: One deferred transaction around the reads**

In `Libraries/ConPersistence/Config/ConfigStore.cs` add `using Microsoft.Data.Sqlite;`, replace the public `LoadWatcherOptionsAsync()` and `ExportAsync()` with:

```csharp
    /// <summary>Maps config rows to engine options (stable ids, name references), from one snapshot.</summary>
    public async Task<WatcherOptions> LoadWatcherOptionsAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await using var snapshot = await BeginReadSnapshotAsync(db);
        var options = await LoadWatcherOptionsAsync(db);
        await snapshot.CommitAsync();
        return options;
    }

    /// <summary>Whole-config document for backup/transfer, from one snapshot.</summary>
    public async Task<ConfigDocument> ExportAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await using var snapshot = await BeginReadSnapshotAsync(db);
        var doc = await ExportAsync(db);
        await snapshot.CommitAsync();
        return doc;
    }
```

and add:

```csharp
    /// <summary>
    /// A deferred transaction ("BEGIN", not EF's default "BEGIN IMMEDIATE"):
    /// it takes no write lock, and in WAL mode its first read pins one
    /// snapshot until it ends, so several queries see one state of the
    /// database even while an import commits between them.
    /// </summary>
    private static async Task<SqliteTransaction> BeginReadSnapshotAsync(ConWatchDbContext db)
    {
        await db.Database.OpenConnectionAsync();
        var transaction = ((SqliteConnection)db.Database.GetDbConnection()).BeginTransaction(deferred: true);
        await db.Database.UseTransactionAsync(transaction);
        return transaction;
    }
```

- [x] **Step 4: Run to verify**

Run: `dotnet test Tests/ConPersistence.Tests -c Debug` → 0 failed (the in-memory `TestDbFactory` tests call these methods one at a time, so the shared connection never holds two transactions).
Run: `dotnet build ConnectionWatcher.sln -c Debug` → `0 Warning(s)`; full suite → 0 failed.

- [x] **Step 5: Commit**

```powershell
git add Libraries/ConPersistence/Config/ConfigStore.cs Tests/ConPersistence.Tests/ConfigSnapshotReadTests.cs
git commit -m "fix: whole-config reads see one database snapshot"
```

---

## Wire changes (release-note lines)

Everything else in this plan is invisible on the wire. Each line names the task and the test that pins it.

- `GET /api/v1/status` with no engine and monitoring enabled (503) answers `{"errors":["Engine is not running"]}` (was `{"error":"Engine is not running"}`). Task 9, `Status_without_an_engine_uses_the_same_shape`.
- `GET /api/v1/status`, the SignalR `statusChanged` message and the dashboard's initial data carry `operator` on every target. Task 13, `Live_status_shape_is_pinned`.
- SignalR `transition` messages carry `ancestorId` and `avgRtt`. Task 8.
- Webhook bodies and the MQTT `<base>/transitions` payload: `timestamp` is `2026-08-24T12:00:00.5+00:00` (full precision, `+00:00`; was `2026-08-24T12:00:00.500Z`-style, milliseconds, `Z`); they also carry `ancestorId` and `avgRtt`. Task 8, `Payload_is_the_transition_dto_with_a_utc_offset_timestamp`.
- MCP `get_transitions` rows have the REST `/api/v1/transitions` item shape (`id`, `timestamp`, `subjectId`, `from`, `to`, …; were `timestampUtc`, `fromState`, `toState`). MCP `get_current_status` reports the `/status` target and verdict objects (`verdict.operator` was `blamedOperator`; `avgRtt` was `avgRttMs`; adds `id`, `sampleSize`, `pingCount`; `successRate`/`avgRtt` are 0 rather than null before a first sample). Tasks 8 and 13.
- CSV/JSON exports stream: same bytes, no `Content-Length` header (chunked). Task 11.
- `POST /api/v1/config/import` trims addresses before validating, so two addresses that differ only by padding are a duplicate (as they already were for single-target edits); a `null` address is a 400 validation error instead of a 500. Task 6.
- Factory reset seeds the same settings as first boot (adds `Retention.TransitionMonths`, `Engine.WarmRestartMaxGapMinutes`, `Verdict.AnchorPool` from appsettings). Task 14.
- The setup wizard is all-or-nothing and triggers one engine rebuild. Task 17.
- The Targets page draws one "Path to the internet" pipeline per distinct anchor parent (unchanged where all anchors share a parent). Task 18.

---

## Left out on purpose

- S1, S5, S10: done on earlier branches (the brief excludes them).
- Section 3, `WatcherOptionsValidator`'s cycle walk → `TreeWalk.ChainToRoot`: not done. `ChainToRoot` stops at 32 hops, so a cycle longer than 32 would no longer be reported, and `VerdictCoordinator`'s constructor walks parents without a cap; the validator's uncapped walk is the guarantee every cycle is caught.
- Section 3, `UpsertPoolAsync`'s per-member query: not changed. One `Name IN (…)` query relies on SQLite applying the column's `NOCASE` collation to the `json_each` form EF emits for `Contains`; not worth the risk for a path that runs on a click. (`WriteDocumentAsync`'s per-target query is fixed in Task 6; `RollupCollector`'s was fixed with P2; `RetentionService`'s four reads become one in Task 14.)
- Section 3, `RetentionPass` paging/`AsNoTracking`/`ExecuteDeleteAsync`, the report's unbounded transition load and a cap on `HistoryQueries.RollupsAsync`: performance work, and the cap changes what the API returns; each needs its own volume test. A separate plan.
- Section 3, `DnsFollower` extraction and the abandoned `Task.Delay` timers: the review marks both optional / "not a bug"; no behavior to pin.
- Section 3, `DomainResolutionResult` shrink: owner decision 6 keeps the shape. `DomainNameResolverDns` slimming: already done (61 lines at `78bf8e8`).
- Section 3, test-only members kept (listed in Task 3) and `MetricsCounters`/`StatusBroadcaster` subscription lists (S1 removed them).
- `/export/snapshot` streaming: one JSON document with nested lists; streaming it needs a hand-written writer. Only the three row exports stream.
- S8's move of `RebuildDebounceSeconds`: it is a public configuration key (`Persistence:RebuildDebounceSeconds`).
- Followups not folded in (no task opens their code for this purpose, or they change behavior): `SettingsStore` owning the master switch; the `StatusBroadcaster` send-gate items and the five `Detach()` bodies; the boot start outside `_rebuildLock`; fixed-delay tests; the History pre-window row API; the shared `"Minute"` constant and `FailingOnceDbFactory` alias (Task 2 only moves those files); `itemDropped` rate limiting; the EF Design `PrivateAssets` reference (Task 5's message documents the working command); the engine items (`PingResult` `with`, DNS follower tests, error-log throttling, `EngineHealth` tests, the MQTT `Inbox` note). Folded in: MCP and the dashboard read `host.EngineEnabled` (Tasks 13 and 18).

---

## Self-review notes (written at plan time)

- Spec coverage: S2 → Task 15; S3 → Task 18; S4 → Task 19; S6 → Task 2; S7 → Task 16; S8 → Task 14; S9 → Task 17; S11 → Task 1. Section 3: dead members → 3; `GatewayDiscovery` → 4; migrations test → 5; `TargetRow.CopyFrom` + trimming + `WriteDocumentAsync` N+1 → 6; profile pair + defaults → 7; `TransitionDto` → 8; `ApiResults.Error` → 9; snapshot shape → 10; streaming exports → 11; verdict name constant → 12; `StatusSnapshotBuilder` + `Operator` + MCP reuse → 13; the misleading `converted` text → 14. Deferred E3 → 20, P4 → 21. Everything else in section 3 is under "Left out" with a reason.
- Placeholder scan: every code step shows the code; the two "temporarily comment out / exclude" instructions in Tasks 14 and 18 name the file and the expected result; the one conditional instruction (Task 2 Step 5: add a `using` for a type the table missed) names where the namespace comes from.
- Type consistency: `TransitionDto.From(TargetTransition|TransitionRow)`, `FromState/ToState` (JSON `from`/`to`) used identically in Tasks 8's code and tests; `ApiResults.Error(int, string?, params string[])` and `ToHttp(this CloneOutcome)` (Task 9) are what Tasks 11 and 13 rely on; `SettingsDefaults.WarmRestartMaxGapMinutes`/`WeatherThreshold` (Task 14) match Tasks 15 and 18; `RetentionWindows(MinuteDays, HourlyMonths, TransitionMonths)` matches its call sites; `FileDbFactory.With(...)`/`PrepareAsync()` (Task 14) match Task 21; `ConfigStore.ExportAsync(ConWatchDbContext)` (Task 17) matches Task 21; `TopologyNames.Names(…, TrunkChains, HasAnchorPool, WeatherThreshold, Config)` matches History/Targets/Index; `StatusSnapshot/StatusVerdict/StatusTarget/StatusPool` avoid the existing `ConEngine.Events.TargetStatus`; `TrackerProbe` exposes exactly the member names the two test files call.
- Order dependencies a reviewer should know: Task 2 before everything that names a moved namespace; Task 12 before Task 18 (`SubjectName`); Task 14 before 15, 18 and 21; Task 17 before 21; Task 9 before 11 (`BadFormat()` already returns `ApiResults.Error`). Tasks 20 and 21 depend on nothing that depends on them.
