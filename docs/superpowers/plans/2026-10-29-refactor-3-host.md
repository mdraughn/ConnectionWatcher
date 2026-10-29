# Refactor 3: host and library dedupes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Carry out survey items D4, D8, the rest of D11 and the rest of D13 as refactors on branch `refactor-3-host`. Each task can be reviewed and reverted on its own. The few deliberate behavior changes are pinned by tests that fail first.

**Architecture:** Four groups, run in order.
- A (D4): one table of setting rules, used by every reader, the validator and the Razor inputs.
- B (D8): one status source and one health report, used by the host's pages and endpoints.
- C (D11): server-URL normalization, the legacy `url` reader, the health probe, and the identity strings, each with one owner across ConConfig, the CLI and the tray.
- D (D13): private helpers inside the classes that repeat themselves. These are ConfigService, DiscoveryStore, CloneService, the export endpoints over HistoryQueries, the host's JSON options, ProbeSpec, the TargetTransition mapping and the rollup arithmetic.

**Tech Stack:** .NET 10, ASP.NET Core minimal APIs + Razor Pages + SignalR, EF Core 10 on SQLite, ModelContextProtocol.AspNetCore, WinForms (the tray, build-only), xUnit.

**Spec:** `docs/superpowers/reviews/2026-10-22-refactor-survey.md`: rows D4, D8, D11 and D13, plus the "Rulings (Mark, 2026-10-28)" section. No ruling touches this plan directly. The reviewer reports behind those rows are in the session scratchpad (`survey-2.md` persistence, `survey-3.md` host, `survey-4.md` infra/CLI/tray). This plan restates every location it needs.

**Depends on:** R1 (`2026-10-29-refactor-1-bugs.md`) and R2 (`2026-10-29-refactor-2-engine.md`), both merged into master. This plan uses the following from R1:
- `ConfigFile.Update(path, Func<ConWatchConfig?, ConWatchConfig>, out warning)`. R1 moves ConfigCommands, ServiceCommands and TrayCommands onto it. `TraySettings` keeps its own load-time corrupt check: it caches the state and saves many times, which is a different shape. This plan does not touch it.
- The B8 catches in `ConfigService.ImportAsync` and `CloneService.TakeOverAsync`.
- `ScopeTags` and `GlitchDto`. This plan does not touch either.

## Global Constraints

- Never push. Cut branch `refactor-3-host` from master after R2 is merged, and commit only there. Nothing in this plan merges into master.
- Conventional commit prefixes (`refactor:`, `fix:`, `test:`). No attribution or co-author lines. One commit per task.
- After every task:
  - `dotnet build ConnectionWatcher.sln -c Debug` adds no warnings. The only existing ones are in test projects: CA1416 in `WindowsFolderLockdownTests`, CA2022 ×2 and CS8631 ×1.
  - `dotnet test ConnectionWatcher.sln -c Debug` has 0 failed.
  - From `Programs/ConWatch/ClientApp`, `npx vitest run` passes.
- Behavior-preserving except where a task says otherwise and pins the change with a test that fails first. Every such change is listed in "Wire and UI changes" at the end.
- No new NuGet or npm packages, no new projects, and no new project references. No EF model change or migration.
- UTC end to end. Never `new DateTimeOffset(dt)` on a value whose Kind is not known to be UTC. No `DateTime.Now`. Local time is computed only in the browser.
- Tests wait for conditions (`ConfigApiTests.Eventually`, `HostTestWait`). They never sleep and then assert, because the CI runners have 2 cores.
- Setting flags keep today's ordinal, case-sensitive comparison. A default-on flag is off only for exactly `"false"`. A default-off flag is on only for exactly `"true"`.
- Run commands from the repo root in PowerShell.
- Line numbers in the survey are from `663ab0f` and have drifted, so locate code by symbol.

## Review Focus

1. **A flag stored in another case.** A default-on flag stored as `"FALSE"` must still read as on, and a default-off flag stored as `"TRUE"` must still read as off, exactly as today. Test: Task 1, `Every_flag_reads_as_its_consumers_did` (the table includes both).
2. **A bad URL in the tray's Test button.** A URL with no scheme (`localhost:8080`) or a bare `http://` must show a message, not escape as an exception. Test: Task 7, `A_url_that_is_not_absolute_reports_invalid_url`.
3. **Archive mode keeps its answers.** `GET /status` is 200 with `enabled:false`, the MCP status tool still reports `monitoringEnabled:false` with null `targets`, and the dashboard renders the archive view. Tests: Task 4, `Archive_mode_status_and_mcp_answers_are_unchanged` and `Archive_dashboard_embeds_the_disabled_snapshot`.
4. **One PUT with several bad values.** Every error is reported, not just the first, and each error names the full key. Test: Task 3, `Every_bad_value_in_one_put_is_reported`.
5. **Filtered exports.** An export filtered by subject, target or resolution returns only those rows, and a filter that matches nothing still gives a header line or `[]`. Tests: Task 11, `Exports_filter_by_subject_target_and_resolution`; the existing `Empty_exports_are_a_header_line_or_an_empty_array`.

---

## Decisions made at plan time

| Item | Survey said | Found / decided |
|---|---|---|
| D4 flag API | `GetBoolAsync(key)` or `GetFlagAsync(key, defaultOn)` | `SettingsStore.GetBoolAsync(string key)` with **no default parameter**. Each flag's default lives once, in `SettingRules`. A parameter would let every caller restate it, which is the drift this item removes. A key that is not a flag throws `ArgumentException`. |
| D4 flag defaults | — | Read from the code. On unless `"false"`: `Engine.Enabled`, `Dns.Enabled`, `Webhook.SkipExplained`, `Clone.IncludeLocalDevices`. Off unless `"true"`: `Mqtt.Enabled`, `Webhook.Enabled`, `Webhook.VerdictOnly`, `Traceroute.AsnLookup`. |
| D4 int defaults | "one defaults source" | The retention defaults come from `new PersistenceOptions()` (90/13/24/3) and the weather threshold from `SettingsDefaults.WeatherThreshold` (2). The others are fixed: MQTT port 1883 and heartbeat 5. The runtime fallbacks that read appsettings (`RetentionWindows`, `RetentionService`) stay as they are: the rule default is what the Razor input shows when the row is missing. Readers do not start clamping, except heartbeat, which already clamps. |
| D4 messages | Drift in naming only | Unified to `"{full key} must be {min}–{max}."`, and `"Verdict.WeatherThreshold must be an integer ≥ 1."` for the unbounded rule. No test or client reads these strings. Wire change. |
| D4 scope | — | The http(s) URL check (×2 in SettingsEndpoints) and the webhook URL split (SettingsEndpoints + WebhookNotifier) join `SettingRules`. CloneService's source-URL parse stays: it also builds the base URI and has its own error code. |
| D8 StatusSource | Absorb all four `Build` calls | It absorbs three: `/status`, `Index` and the MCP tool. `StatusBroadcaster` keeps calling `StatusSnapshotBuilder.Build` with the engine it was handed, for two reasons. `WatcherHostService` takes `StatusBroadcaster` in its constructor, so a broadcaster that depended on a host-reading source would be a DI cycle. And the broadcaster must build for a specific engine, not "the current one". |
| D8 archive answers | `Current()` returns the snapshot or `Disabled` | `Live()` is the snapshot or null; `Current()` is `Live() ?? (EngineEnabled ? null : Disabled)`. `/status` and `Index` use `Current()`. MCP uses `Live()`, so its archive answer (null verdict, targets and dns) does not change. `Index` now embeds `Disabled` instead of `null` in archive mode. The client's `applySnapshot` already maps `enabled:false` to the archive view, and `engineDisabled` already forces it. Wire change (initial-data only). |
| D8 `clone-source-active` on the dashboard count | Decide | **Not on the dashboard count.** `CloneHealth.EvaluateAsync` makes a network probe (2 s timeout, cached 30 s) whenever `Clone.SourceUrl` is set. A decommissioned source is "the normal end state", and it stays set, so every 30 s one dashboard render would wait up to 2 s on a black-holed address. The warning is fixed on Settings, which is where the take-over card lives, so the Settings page gets the banner. The dashboard count gains `tcp-refused`, which is in memory and free. |
| D8 config tabs | — | All four config tabs show config + `tcp-refused`; today only Targets does. A warning shows on every tab with a link to the tab that fixes it, which matches how the config warnings already behave. |
| D11 identity strings | A constants class in ConConfig | **No new class.** The host does not reference ConConfig, and adding the reference for one string couples the server to the tray's config library. The real duplicates are only these: the service name (host `ConWatchApp` and CLI `CliApp`, and the CLI references the host), and `"conwatch-tray"` (twice, both in the CLI). The mutex name and the Run value are each used once and are different identities, so they are left alone. |
| D11 URL normalization | `NormalizeServerUrl` | `ConfigResolver.NormalizeServerUrl(string url) => url.Trim().TrimEnd('/')`. Copies that skipped `Trim()` change only for a URL with surrounding whitespace. `IScoutPasswordStore.Normalize` is a **stored-key** normalizer and is left alone: changing it could orphan saved scout passwords. `ListenToServerUrl`/`WithPortOf` are listen-URL rules, not this one. |
| D11 legacy reader | `LegacyTrayFile.TryReadUrl` | `LegacyInstall.Detect` calls the existing `LegacyTrayFile.TryRead(path)?.ServerUrl`. That fixes the uncaught `InvalidOperationException` on a non-string `"url"` (test fails first). No new method. |
| D11 health probe | Move to ConConfig | `IHealthProbe`, `HealthResult` and `HttpHealthProbe` move to ConConfig (namespace `ConConfig`), next to `LocalServerHttp`. The probe gains one arm: a URL `HttpClient` cannot use becomes `HealthResult(null, "invalid URL", ms)` instead of throwing. The CLI never sees that arm (it validates first); the tray needs it. The tray's Test button maps the result to its three existing messages. |
| D11 out of scope | — | The `.corrupt` and read-modify-write sites are R1's. The repeated `ConfigResolver.ListenUrl/DataDir(s.Paths, …)` calls in the CLI are not in the survey's D11 row and are left. |
| D13 ConfigService | Validate/write/catch helpers; profile and parent lookups | As proposed. `UpsertPoolAsync`'s per-member query is a performance item, not duplication, so it is dropped (pools hold a handful of members). The two "managed by ConnectionWatcher" messages say different things and stay. `ConfigStore.SeedAsync` validating raw options is outside this row and is left. |
| D13 DiscoveryStore | `ChangeAsync`; share `IsLiveAdoptionAsync` | As proposed. The live-adoption conflict reads `"This discovery was adopted into a target; delete the target first."` on Dismiss, Restore and Remove (Remove said "remove the target to undo it"). No test reads it. Wire change. |
| D13 CloneService | Fold prep and import catches | `Prepare` is folded. The import catch ladders are folded **only if** R1 left them mapping the same exceptions to the same codes, differing only in the message suffix and clone's settings restore (Task 10 states the rule). The two transport checks differ in sensitivity and message and stay. |
| D13 exports | Generic skeleton + HistoryQueries base queries | As proposed. HistoryQueries gains three unordered base queries; paged reads and streaming exports each order and limit on top. |
| D13 ProbeSpec | `Target` carries a parsed `ProbeSpec`; `Describe()` | `Target.ParsedProbe()` is a **method**, so it never joins a record's equality or a JSON serialization of `Target`. `ProbeSpec.Describe()` keeps today's note wording ("tcp/80", "dns", "ping") exactly, because those strings are stored in model-warning messages. |
| D13 rollups | Arithmetic ×3, flooring ×4 | `RollupBucket.Add` replaces the inline sample arithmetic. `RollupResolution.FloorUtc` replaces the four floors. `RollupMerge.Combine` works on averaged rows, not sums: it is a different representation and stays. |
| D13 TargetTransition | One field list | `TransitionRecorder.ToRow` becomes `TransitionRow.From(TargetTransition)`; `TransitionDto.From(TargetTransition)` maps through it. Pinned by the existing `TransitionDtoTests`. |

---

## File structure

| Path | Responsibility after this plan | Tasks |
|---|---|---|
| `Libraries/ConPersistence/Settings/SettingRules.cs` (new) | Every setting's rule: flag defaults, int ranges and defaults, URL checks, MQTT text defaults | 1, 3 |
| `Libraries/ConPersistence/Settings/SettingsStore.cs` | Gains `GetBoolAsync` | 1 |
| `Programs/ConWatch/StatusSource.cs` (new) | The live/archive status snapshot for pages, `/status` and MCP | 4 |
| `Programs/ConWatch/Health/HealthReport.cs` (new, namespace `ConWatch.Health`) | Config + runtime (+ clone) warnings for every banner and count | 5 |
| `Libraries/ConConfig/HttpHealthProbe.cs` (moved from `Programs/ConWatchCli/Services/`) | `IHealthProbe`, `HealthResult`, `HttpHealthProbe` | 7 |
| `Tests/ConConfig.Tests/HttpHealthProbeTests.cs` (moved from `Tests/ConWatchCli.Tests/`) | Probe tests | 7 |
| `Programs/ConWatch/WebJson.cs` (new) | The host's one `JsonSerializerDefaults.Web` options instance | 12 |

R5 later moves `EngineHealth`, `RuntimeHealth` and `CloneHealth` into `Health/`. This plan creates the folder with `HealthReport` and leaves those three where they are.

---

## Group A — Settings (D4)

### Task 1: `SettingRules` flags and `SettingsStore.GetBoolAsync`

**Files:**
- Create: `Libraries/ConPersistence/Settings/SettingRules.cs`
- Modify: `Libraries/ConPersistence/Settings/SettingsStore.cs` (`SettingsStore` class)
- Test: `Tests/ConPersistence.Tests/SettingRulesTests.cs` (new), `Tests/ConPersistence.Tests/SettingsStoreTests.cs`

**Interfaces:**
- Consumes: `SettingKeys` (same file as `SettingsStore`).
- Produces:
  - `public static class SettingRules` in `ConPersistence.Settings`, with `public static bool IsFlag(string key)` and `public static bool ReadFlag(string key, string? raw)`. `ReadFlag` throws `ArgumentException` for a key that is not a flag.
  - `public Task<bool> SettingsStore.GetBoolAsync(string key)`.
  - Task 3 adds the int members to the same class.

- [ ] **Step 1: Write the failing tests.** In `SettingRulesTests`:

```csharp
[Theory]
// default on: off only for exactly "false"
[InlineData(SettingKeys.EngineEnabled, null, true)]
[InlineData(SettingKeys.EngineEnabled, "", true)]
[InlineData(SettingKeys.EngineEnabled, "false", false)]
[InlineData(SettingKeys.EngineEnabled, "FALSE", true)]
[InlineData(SettingKeys.DnsEnabled, "false", false)]
[InlineData(SettingKeys.WebhookSkipExplained, null, true)]
[InlineData(SettingKeys.CloneIncludeLocalDevices, "false", false)]
// default off: on only for exactly "true"
[InlineData(SettingKeys.MqttEnabled, null, false)]
[InlineData(SettingKeys.MqttEnabled, "true", true)]
[InlineData(SettingKeys.MqttEnabled, "TRUE", false)]
[InlineData(SettingKeys.WebhookEnabled, "true", true)]
[InlineData(SettingKeys.WebhookVerdictOnly, "yes", false)]
[InlineData(SettingKeys.TracerouteAsnLookup, "", false)]
public void Every_flag_reads_as_its_consumers_did(string key, string? raw, bool expected) =>
    Assert.Equal(expected, SettingRules.ReadFlag(key, raw));

[Fact]
public void A_key_that_is_not_a_flag_is_refused() =>
    Assert.Throws<ArgumentException>(() => SettingRules.ReadFlag(SettingKeys.MqttPort, "true"));
```

In `SettingsStoreTests`, add `GetBool_uses_the_keys_own_default`. On an empty store, `EngineEnabled` reads true and `MqttEnabled` reads false. After `SetManyAsync` writes `"false"` and `"true"` respectively, both values flip.

- [ ] **Step 2: Run them, expect a compile failure** (`SettingRules` is not defined).

Run: `dotnet test Tests/ConPersistence.Tests -c Debug --filter "FullyQualifiedName~SettingRules|FullyQualifiedName~SettingsStoreTests"`

- [ ] **Step 3: Implement.**
  - Two private `HashSet<string>` (ordinal), one per default, with the eight keys from Decisions.
  - `ReadFlag` is `DefaultOn ? raw != "false" : raw == "true"`, using ordinal comparison.
  - `GetBoolAsync` is `SettingRules.ReadFlag(key, await GetAsync(key))`.
  - Each set gets one doc line saying which keys are on by default and why.

- [ ] **Step 4: Run the same filter. Expected: PASS.**

- [ ] **Step 5: Commit**

```bash
git add Libraries/ConPersistence/Settings/SettingRules.cs Libraries/ConPersistence/Settings/SettingsStore.cs Tests/ConPersistence.Tests/SettingRulesTests.cs Tests/ConPersistence.Tests/SettingsStoreTests.cs
git commit -m "refactor(settings): one home for each flag's default (SettingRules.ReadFlag, SettingsStore.GetBoolAsync)"
```

---

### Task 2: Every flag read goes through `SettingRules`

**Files (modify; the existing string compare at each site is replaced):**
- `Programs/ConWatch/WatcherHostService.cs`:
  - `EngineEnabledAsync`.
  - The `dnsEnabled` read in the DNS-watch block.
  - The `written.TryGetValue(SettingKeys.EngineEnabled, …)` line becomes `EngineEnabled = SettingRules.ReadFlag(SettingKeys.EngineEnabled, enabled)`.
- `Programs/ConWatch/Pages/Settings.cshtml.cs`: `EngineEnabled`; add `public bool Flag(string key) => SettingRules.ReadFlag(key, Settings.GetValueOrDefault(key))`.
- `Programs/ConWatch/Pages/Settings.cshtml`:
  - The ASN switch `checked`.
  - The `selected` pairs for `Mqtt.Enabled`, `Webhook.Enabled`, `Webhook.VerdictOnly` and `Webhook.SkipExplained`. They become `Model.Flag(key)` / `!Model.Flag(key)`.
- `Programs/ConWatch/Pages/Config/Dns.cshtml.cs`, `Programs/ConWatch/Dns/SystemResolverService.cs`, `Programs/ConWatch/Api/TracerouteEndpoints.cs`.
- `Programs/ConWatch/Clone/CloneHealth.cs`: `== "false"` becomes `!await settings.GetBoolAsync(...)`.
- `Programs/ConWatch/Clone/CloneService.cs`: the take-over's `CloneIncludeLocalDevices == "false"` becomes `!await settings.GetBoolAsync(...)`.
- `Libraries/ConNotify/HeartbeatNotifier.cs`, `Libraries/ConNotify/MqttNotifier.cs` (two reads), `Libraries/ConNotify/WebhookNotifier.cs` (three reads).
- `Libraries/ConPersistence/Config/ConfigHealth.cs` (`dnsEnabled`).

**Interfaces:**
- Consumes: Task 1.
- Produces: nothing new.

Writes such as `"true" : "false"` stay as they are. Pinned by Task 1's table and the existing `MasterEnableTests`, `NotifierIntegrationTests`, `DnsSettingsApiTests`, `SettingsPageTests`, `CloneWithoutLocalDevicesTests` and `TracerouteApiTests`.

- [ ] **Step 1: Replace the reads.**

- [ ] **Step 2: Confirm none are left.**

Run: `git grep -n -E "GetAsync\(SettingKeys\.[A-Za-z]+\) *[!=]= *\"(true|false)\"" -- Programs Libraries`
Expected: no output. Also confirm no `Setting(SettingKeys.X, …) == "true"` / `!= "false"` remains in `Settings.cshtml`.

- [ ] **Step 3: Full build and test (Global Constraints). Expected: 0 failed.**

- [ ] **Step 4: Commit**

```bash
git commit -am "refactor(settings): every flag read goes through SettingRules, so each default is stated once"
```

---

### Task 3: Int and URL rules (validator, Razor inputs, consumer fallbacks)

**Files:**
- Modify: `Libraries/ConPersistence/Settings/SettingRules.cs`
- Modify:
  - `Programs/ConWatch/Api/SettingsEndpoints.cs` (`PUT /settings` switch).
  - `Programs/ConWatch/Pages/Data.cshtml` (four inputs).
  - `Programs/ConWatch/Pages/Settings.cshtml`: the MQTT port, heartbeat interval, base-topic and prefix placeholders.
  - `Programs/ConWatch/Pages/Config/Pools.cshtml` (weather-threshold `min`).
  - `Libraries/ConNotify/MqttNotifier.cs` (`RunConnectedSessionAsync` port/topic/prefix fallbacks).
  - `Libraries/ConNotify/HeartbeatNotifier.cs` (interval fallback and clamp).
  - `Libraries/ConNotify/WebhookNotifier.cs` (URL split).
  - `Programs/ConWatch/Api/AdminEndpoints.cs` (`/admin/mqtt-test`: 1883 and the 1–65535 check).
  - `Libraries/ConPersistence/Retention/RetentionWindows.cs` (`FromSettings`'s local `Get`).
  - `Libraries/ConPersistence/Retention/RetentionService.cs` (`EffectiveAsync`'s hour parse).
- Test: `Tests/ConPersistence.Tests/SettingRulesTests.cs`, `Tests/ConWatch.Tests/SettingsApiTests.cs`

**Interfaces:**
- Consumes: Task 1.
- Produces, in `ConPersistence.Settings`:
  - `public sealed record IntRule(string Key, int Min, int Max, int Default)`.
  - `SettingRules`:
    - `IReadOnlyDictionary<string, IntRule> Ints`.
    - `IntRule Int(string key)`, which throws `KeyNotFoundException`.
    - `string? IntError(string key, string? value)`, which is null when valid.
    - `bool IsHttpUrl(string? value)`, meaning absolute with an http or https scheme.
    - `IReadOnlyList<string> SplitUrlList(string? value)`, which splits on `'\n'` with `RemoveEmptyEntries | TrimEntries`.
    - `int IntOr(IReadOnlyDictionary<string, string> all, string key, int fallback)`.
    - The constants `MqttBaseTopicDefault = "conwatch"` and `MqttDiscoveryPrefixDefault = "homeassistant"`.

The rule table:

| Key | Min | Max | Default |
|---|---|---|---|
| `Verdict.WeatherThreshold` | 1 | `int.MaxValue` | `SettingsDefaults.WeatherThreshold` |
| `Retention.MinuteRetentionDays` | 1 | 3650 | `new PersistenceOptions().MinuteRetentionDays` |
| `Retention.HourlyRetentionMonths` | 1 | 120 | `….HourlyRetentionMonths` |
| `Retention.TransitionMonths` | 1 | 120 | `….TransitionRetentionMonths` |
| `Retention.RetentionRunHourUtc` | 0 | 23 | `….RetentionRunHourUtc` |
| `Mqtt.Port` | 1 | 65535 | 1883 |
| `Heartbeat.IntervalMinutes` | 1 | 1440 | 5 |

- [ ] **Step 1: Write the failing tests.**

In `SettingRulesTests`:

```csharp
[Fact]
public void Int_errors_name_the_full_key_and_range()
{
    Assert.Null(SettingRules.IntError(SettingKeys.MinuteRetentionDays, "90"));
    Assert.Equal("Retention.MinuteRetentionDays must be 1–3650.", SettingRules.IntError(SettingKeys.MinuteRetentionDays, "0"));
    Assert.Equal("Retention.RetentionRunHourUtc must be 0–23.", SettingRules.IntError(SettingKeys.RetentionRunHourUtc, "x"));
    Assert.Equal("Verdict.WeatherThreshold must be an integer ≥ 1.", SettingRules.IntError(SettingKeys.WeatherThreshold, null));
}

[Fact]
public void Defaults_match_the_compiled_options()
{
    var p = new PersistenceOptions();
    Assert.Equal(p.MinuteRetentionDays, SettingRules.Int(SettingKeys.MinuteRetentionDays).Default);
    Assert.Equal(1883, SettingRules.Int(SettingKeys.MqttPort).Default);
    Assert.Equal(5, SettingRules.Int(SettingKeys.HeartbeatIntervalMinutes).Default);
}
```

In `SettingsApiTests`, add `Every_bad_value_in_one_put_is_reported`. PUT `{ "Retention.MinuteRetentionDays": "0", "Mqtt.Port": "70000", "Heartbeat.Url": "ftp://x" }`. Expect a 400 whose `errors` contains, in request order, these three strings:
- `"Retention.MinuteRetentionDays must be 1–3650."`
- `"Mqtt.Port must be 1–65535."`
- `"Heartbeat.Url must be an absolute http(s) URL."`

The first assertion fails today, because the message says `MinuteRetentionDays`.

- [ ] **Step 2: Run them. Expected: compile failure, then FAIL on the message text.**

Run: `dotnet test Tests/ConPersistence.Tests Tests/ConWatch.Tests -c Debug --filter "FullyQualifiedName~SettingRules|FullyQualifiedName~SettingsApiTests"`

- [ ] **Step 3: Implement the rules and wire the sites.**
  - **Validator.** Keep the explicit cases for `AnchorPool`, `DnsLookupNames` and the read-only and free-text keys. That includes `CloneSourceUrl`/`CloneIncludeLocalDevices`: they must stay read-only even though one of them is a flag. Then `default:` checks three things in order:
    1. `SettingRules.Ints` → `IntError`.
    2. `SettingRules.IsFlag` → the existing `null/"true"/"false"` check and message.
    3. Otherwise, `Unknown setting`.
  - **URL keys.** `Heartbeat.Url` uses `IsHttpUrl`, and `Webhook.Urls` uses `SplitUrlList` + `IsHttpUrl`. Their messages are unchanged.
  - **Razor.** Inputs render `min`/`max`/the fallback `value` from `SettingRules.Int(key)`, formatted with `CultureInfo.InvariantCulture`.
  - **MQTT.** MqttNotifier and `/admin/mqtt-test` take the port default and range from the rule.
  - **Heartbeat.** HeartbeatNotifier's `GetIntAsync(…, 5)` and `Math.Clamp(…, 1, 1440)` come from the rule.
  - **Retention.** `RetentionWindows.FromSettings` and `RetentionService.EffectiveAsync` call `IntOr`.

- [ ] **Step 4: Run the filter from Step 2, then the full build and test. Expected: PASS, 0 failed.**

- [ ] **Step 5: Confirm the literals are gone.**

Run: `git grep -n -E "\b(1883|3650|1440)\b|\"homeassistant\"|\"conwatch\"" -- Programs/ConWatch Libraries/ConNotify Libraries/ConPersistence/Settings`
Expected: only `SettingRules.cs`. MQTT topic strings under `Builders.cs` that build topic paths are a different thing and may remain.

- [ ] **Step 6: Commit**

```bash
git commit -am "refactor(settings): int ranges, defaults and URL checks come from SettingRules; errors name the full key"
```

---

## Group B — Status and health (D8)

### Task 4: `StatusSource`

**Files:**
- Create: `Programs/ConWatch/StatusSource.cs`
- Modify:
  - `Programs/ConWatch/Startup/ConWatchServices.cs` (register a singleton next to `StatusBroadcaster`).
  - `Programs/ConWatch/Api/StatusEndpoints.cs` (`/status`).
  - `Programs/ConWatch/Pages/Index.cshtml.cs` (drop the `warnings` and `lookupNames` constructor parameters).
  - `Programs/ConWatch/Mcp/ConWatchMcpTools.cs` (`GetCurrentStatus`, and its constructor dependencies).
- Test: `Tests/ConWatch.Tests/MasterEnableTests.cs`

**Interfaces:**
- Consumes: `StatusSnapshotBuilder.Build(WatcherEngine?, ModelWarningLedger, IReadOnlyList<…> lookupNames)`, `StatusSnapshot.Disabled`, `WatcherHostService.Engine`/`EngineEnabled`.
- Produces: `public sealed class StatusSource(WatcherHostService host, ModelWarningLedger warnings, DnsLookupNames lookupNames)` with:
  - `public StatusSnapshot? Live()`.
  - `public StatusSnapshot? Current()`, which is `Live() ?? (host.EngineEnabled ? null : StatusSnapshot.Disabled)`.

`StatusBroadcaster` is **not** changed (see Decisions: DI cycle).

- [ ] **Step 1: Write the tests.** In `MasterEnableTests`:
  - **`Archive_mode_status_and_mcp_answers_are_unchanged`.** Disable the engine and wait for `host.Engine is null`. Then:
    - `GET /api/v1/status` is 200 with `enabled == false` and `targets` an empty array.
    - Calling `ConWatchMcpTools.GetCurrentStatus()` gives JSON with `monitoringEnabled == false` and `targets` JSON null. Resolve the tool from DI, or follow how `McpEndpointTests` invokes tools.
    - This one should already pass.
  - **`Archive_dashboard_embeds_the_disabled_snapshot`.** Same setup. `GET /` (unlocked, with a user target seeded so there is no Setup redirect). The `initial-data` JSON's `snapshot` is an object with `enabled == false`, not null. This one fails today.

- [ ] **Step 2: Run them. Expected: the first passes; the second fails, because `snapshot` is null.**

Run: `dotnet test Tests/ConWatch.Tests -c Debug --filter "FullyQualifiedName~MasterEnableTests"`

- [ ] **Step 3: Implement `StatusSource` and switch the three sites.**
  - `/status` keeps its 503 for enabled-but-no-engine. That is `Current()` null.
  - `Index` embeds `Current()`.
  - MCP uses `Live()`.

- [ ] **Step 4: Run the filter, then `McpEndpointTests`, `StatusShapeTests` and `SignalRTests`. Expected: PASS.**

- [ ] **Step 5: Commit**

```bash
git commit -am "refactor(host): one StatusSource for /status, the dashboard and MCP; the archive dashboard embeds the disabled snapshot"
```

---

### Task 5: `HealthReport` (every banner and the dashboard count)

**Files:**
- Create: `Programs/ConWatch/Health/HealthReport.cs`
- Modify:
  - `Libraries/ConPersistence/Config/ConfigHealth.cs`: add `public bool IsConfigTab => Page.StartsWith("/Config/", StringComparison.Ordinal)` to `HealthWarning`.
  - `Programs/ConWatch/Startup/ConWatchServices.cs` (register a singleton).
  - `Programs/ConWatch/Api/SettingsEndpoints.cs` (`/config/health`).
  - `Programs/ConWatch/Pages/Index.cshtml.cs` and `Index.cshtml` (the hint uses `IsConfigTab`).
  - `Programs/ConWatch/Pages/Shared/_HealthBanner.cshtml` (uses `IsConfigTab`).
  - `Programs/ConWatch/Pages/Config/{Targets,Dns,Pools,Profiles}.cshtml.cs`.
  - `Programs/ConWatch/Pages/Settings.cshtml.cs` and `Settings.cshtml` (new `Warnings`, `<partial name="_HealthBanner" model="Model.Warnings" />` under the page heading).
- Test: `Tests/ConWatch.Tests/RuntimeHealthTests.cs`, `Tests/ConWatch.Tests/CloneTests.cs`, `Tests/ConPersistence.Tests/HealthWarningPageTests.cs`

**Interfaces:**
- Consumes: `ConfigHealth.EvaluateAsync()`, `RuntimeHealth.RefusalWarnings(WatcherEngine?)`, `CloneHealth.EvaluateAsync()`.
- Produces: `public sealed class HealthReport(ConfigHealth config, CloneHealth clone, WatcherHostService host)` in `ConWatch.Health`, with:
  - `Task<IReadOnlyList<HealthWarning>> ForPagesAsync()` = config + refusal. It never probes the network.
  - `Task<IReadOnlyList<HealthWarning>> AllAsync()` = config + clone + refusal, in the order `/config/health` returns today.

| Consumer | Uses |
|---|---|
| `GET /config/health` | `AllAsync` (response unchanged) |
| Dashboard count and hint | `ForPagesAsync` |
| Targets, Dns, Pools, Profiles tabs | `ForPagesAsync` |
| Settings page banner | `AllAsync` |

- [ ] **Step 1: Write the failing tests.**
  - **`Dashboard_and_every_config_tab_count_a_refused_tcp_target`** (RuntimeHealthTests). Use the setup of `Config_health_endpoint_concatenates_runtime_refusal_warnings`. Let `n` = the number of warnings `/api/v1/config/health` returns. Then:
    - `GET /` contains `$"⚠ {n} configuration warning"`.
    - `/Config/Dns` contains `data-code="tcp-refused"`.
  - **`Settings_page_shows_the_clone_source_warning`** (CloneTests). Use the flow of `Both_active_after_clone_raises_health_warning`. Once `Eventually` sees the code in `/config/health`, `GET /Settings` contains `data-code="clone-source-active"`.
  - **`IsConfigTab_is_true_only_for_config_pages`** (HealthWarningPageTests). `dns-not-watched` gives true; `clone-source-active` gives false.

- [ ] **Step 2: Run them. Expected: the first two FAIL** (the count leaves out the refusal; the Dns tab and the Settings page have no such entry). **The third fails to compile.**

Run: `dotnet test Tests/ConWatch.Tests Tests/ConPersistence.Tests -c Debug --filter "FullyQualifiedName~RuntimeHealthTests|FullyQualifiedName~CloneTests|FullyQualifiedName~HealthWarningPageTests"`

- [ ] **Step 3: Implement `HealthReport`, `IsConfigTab`, and switch every consumer in the table.**
  - Page models drop their `ConfigHealth` dependency for `HealthReport`. A page that still needs `ConfigHealth` for something else keeps it.
  - Both views keep their wording: "→ Pools tab" / "→ Settings page", and "review on the Config → X tab" / "review on the X page".

- [ ] **Step 4: Run the filter, then `PageSmokeTests`, `AdminLockTests`, `DnsPageTests` and `ConfigApiTests`. Expected: PASS.**

- [ ] **Step 5: Commit**

```bash
git commit -am "fix(host): one HealthReport; the dashboard counts refused TCP targets and Settings shows the clone-source warning"
```

---

## Group C — ConConfig, CLI and tray (rest of D11)

### Task 6: Server-URL normalization, the legacy reader, identity strings

**Files:**
- Modify:
  - `Libraries/ConConfig/ConfigResolver.cs` (add `NormalizeServerUrl`; use it in `ServerUrl`).
  - `Libraries/ConConfig/LegacyTrayFile.cs`, `Libraries/ConConfig/TraySettings.cs` (`ApplyUrl` both sides, `SetOverride`).
  - `Programs/ConWatchCli/Commands/ConfigCommands.cs` (`server-url` set path), `Programs/ConWatchCli/Commands/TrayCommands.cs` (any `TrimEnd('/')` left after R1).
  - `Programs/ConWatchCli/Services/LegacyInstall.cs` (`Detect` reads through `LegacyTrayFile.TryRead`).
  - `Programs/ConWatchTray/Program.cs` (the `server.TrimEnd('/') + "/"` line).
  - `Programs/ConWatch/ConWatchApp.cs` (add `public const string ServiceName = "ConnectionWatcher"`; use it in `AddWindowsService`).
  - `Programs/ConWatchCli/CliApp.cs`: `ServiceName = ConWatch.ConWatchApp.ServiceName`, and add `public const string TrayProcessName = "conwatch-tray"`.
  - `Programs/ConWatchCli/CliServices.cs` (`TrayExePath`) and `Programs/ConWatchCli/Services/ProcessTrayLauncher.cs` (the current-tray tuple and its message).
- Test: `Tests/ConConfig.Tests/ConfigResolverTests.cs`, `Tests/ConWatchCli.Tests/LegacyInstallTests.cs`

**Interfaces:**
- Produces:
  - `public static string ConfigResolver.NormalizeServerUrl(string url)`, which is `url.Trim().TrimEnd('/')`.
  - `ConWatchApp.ServiceName` and `CliApp.TrayProcessName`.
- Unchanged: `IScoutPasswordStore.Normalize`, `ListenToServerUrl` and `WithPortOf` (see Decisions).

- [ ] **Step 1: Write the failing tests.**

```csharp
// ConfigResolverTests
[Theory]
[InlineData(" http://a:8080/ ", "http://a:8080")]
[InlineData("http://a//", "http://a")]
[InlineData("http://a", "http://a")]
[InlineData("", "")]
public void NormalizeServerUrl_trims_whitespace_then_trailing_slashes(string raw, string expected) =>
    Assert.Equal(expected, ConfigResolver.NormalizeServerUrl(raw));
```

In `LegacyInstallTests`, add `A_non_string_url_is_skipped_not_thrown`. Write `Watcher/contray.json` as `{"url": 5}` and `Tray/contray.json` as `{"url": "http://old:9000/"}`. `Detect` returns `ServerUrl == "http://old:9000"`. Today it throws `InvalidOperationException`.

- [ ] **Step 2: Run them. Expected: compile failure, then FAIL with InvalidOperationException.**

Run: `dotnet test Tests/ConConfig.Tests Tests/ConWatchCli.Tests -c Debug --filter "FullyQualifiedName~ConfigResolverTests|FullyQualifiedName~LegacyInstallTests"`

- [ ] **Step 3: Implement and switch the sites in Files.**

- [ ] **Step 4: Confirm the copies are gone.**

Run: `git grep -n "TrimEnd('/')" -- Libraries/ConConfig Programs/ConWatchCli Programs/ConWatchTray`
Expected: only `ConfigResolver.cs` (`NormalizeServerUrl`, `ListenToServerUrl`, `WithPortOf`) and the moved health probe if Task 7 has not run yet. `IScoutPasswordStore.cs` is under ConInfrastructure and is not in the grep.

- [ ] **Step 5: Full build and test. Expected: 0 failed.**

- [ ] **Step 6: Commit**

```bash
git commit -am "refactor(config): one server-URL normalizer, the legacy url read through LegacyTrayFile, one service name"
```

---

### Task 7: The health probe moves to ConConfig; the tray uses it

**Files:**
- Move (`git mv`): `Programs/ConWatchCli/Services/HttpHealthProbe.cs` → `Libraries/ConConfig/HttpHealthProbe.cs` (namespace `ConConfig`). Move `HealthResult` and `IHealthProbe` out of `Programs/ConWatchCli/Services/Seams.cs` into the same file.
- Move (`git mv`): `Tests/ConWatchCli.Tests/HttpHealthProbeTests.cs` → `Tests/ConConfig.Tests/HttpHealthProbeTests.cs` (namespace and usings only). Move any helper it uses from the CLI test project's `Fakes.cs` with it, if no other CLI test uses that helper; otherwise copy it.
- Modify:
  - `Programs/ConWatchCli/CliServices.cs` and its using lines.
  - `Programs/ConWatchTray/SettingsForm.cs`: take an `IHealthProbe` instead of the `HttpClient`, and `TestAsync` maps the result.
  - `Programs/ConWatchTray/Program.cs`: construct `new HttpHealthProbe()` for the form. `_http` stays for status polling.
- Test: `Tests/ConConfig.Tests/HttpHealthProbeTests.cs`

**Interfaces:**
- Produces: `ConConfig.IHealthProbe`, `ConConfig.HealthResult(string? Status, string? Failure, long ElapsedMs)` and `ConConfig.HttpHealthProbe(HttpMessageHandler? handler = null, TimeSpan? timeout = null)`. The signatures are unchanged; only the namespace moves.

The tray's mapping:

| `HealthResult` | Text (color unchanged) |
|---|---|
| `Failure == null` | `Connected — ConnectionWatcher is answering.` |
| `Failure` starts with `"HTTP "` | `Server answered {Failure} — is that the right URL?` |
| any other `Failure` | `Could not connect: {Failure}.` |

- [ ] **Step 1: Move the files and fix the namespaces. Run the moved tests. Expected: PASS.**

Run: `dotnet test Tests/ConConfig.Tests -c Debug --filter "FullyQualifiedName~HttpHealthProbeTests"`

- [ ] **Step 2: Write the failing test.**

```csharp
[Theory]
[InlineData("localhost:8080")]
[InlineData("http://")]
public async Task A_url_that_is_not_absolute_reports_invalid_url(string url)
{
    var result = await new HttpHealthProbe().ProbeAsync(url, CancellationToken.None);
    Assert.Null(result.Status);
    Assert.Equal("invalid URL", result.Failure);
}
```

- [ ] **Step 3: Run it. Expected: FAIL with an exception escaping `ProbeAsync`.**

- [ ] **Step 4: Add the catch to `ProbeAsync`.**

Add `catch (Exception ex) when (ex is InvalidOperationException or UriFormatException or ArgumentException or NotSupportedException)` (HttpClient rejects a non-http scheme with ArgumentException or NotSupportedException depending on where it fails; `localhost:8080` parses as scheme `localhost`), returning `new HealthResult(null, "invalid URL", watch.ElapsedMilliseconds)`. Then switch the tray to the probe per the table, with `NormalizeServerUrl` (Task 6) on the text box value.

- [ ] **Step 5: Run the ConConfig and CLI tests, then the full build.**

The full build compiles the tray; the tray has no tests. Expected: PASS, no new warnings.

- [ ] **Step 6: Commit**

```bash
git commit -am "refactor(config): the CLI and the tray share one health probe; a malformed URL reports invalid URL"
```

---

## Group D — In-class dedupes (rest of D13)

### Task 8: ConfigService helpers

**Files:**
- Modify: `Libraries/ConPersistence/Config/ConfigService.cs`
- Test: existing `Tests/ConPersistence.Tests/ConfigServiceTests.cs`, `TargetWriteTests.cs`, `TargetSystemManagedTests.cs` and `DiscoveryAdoptTests.cs`; R1's B8 import test.

**Interfaces (all private to `ConfigService`):**
- `static ConfigResult? Invalid(WatcherOptions options)`. It returns null when the validator passes, else `Fail(Validation, failures)`. Used by `ImportAsync`, `EditDocumentAsync` and `MutateAsync`.
- `static ConfigResult UniquenessFailure()`. It returns the existing message `"Change violates a uniqueness constraint (duplicate name or address)."`.
- `async Task<ConfigResult?> WriteDocumentCheckedAsync(ConWatchDbContext db, ConfigDocument doc, string what)`. It calls `ConfigStore.WriteDocumentAsync`. It catches `DbUpdateException` when `SqliteErrors.IsUniqueViolation`, logs `"{What} rejected by a uniqueness constraint"` at Debug, and returns `UniquenessFailure()`. Used by `ImportAsync` (replacing R1's catch) and `EditDocumentAsync`.
- `static async Task<(ProfileRow? Row, ConfigResult? Error)> ProfileNamedAsync(ConWatchDbContext db, string name)`. It returns the existing `"Unknown profile '{name}'."` error. Used by `UpsertTargetAsync` and `InsertParentAsync`.
- `static async Task<(Guid? Id, ConfigResult? Error)> ParentIdNamedAsync(ConWatchDbContext db, string? name)`. It returns `(null, null)` for null or empty. Otherwise it returns `"Unknown parent target '{name}'."`. Used by `UpsertTargetAsync` and `UpsertPoolAsync`.
- `UpsertTargetAsync`'s adopted-discovery check calls `DiscoveryStore.IsLiveAdoptionAsync` (Task 9), keeping its own message.

Pure refactor. Every error string is unchanged.

- [ ] **Step 1: Run the pinning tests. Expected: PASS before any change.**

Run: `dotnet test Tests/ConPersistence.Tests -c Debug --filter "FullyQualifiedName~ConfigService|FullyQualifiedName~TargetWrite|FullyQualifiedName~TargetSystemManaged|FullyQualifiedName~DiscoveryAdopt"`

- [ ] **Step 2: Introduce the helpers and replace the copies (the Task 9 call comes with Task 9).**

- [ ] **Step 3: Run the same filter, then the full suite. Expected: PASS.**

- [ ] **Step 4: Commit**

```bash
git commit -am "refactor(config): ConfigService validates, writes and resolves profiles and parents in one place each"
```

---

### Task 9: DiscoveryStore state changes

**Files:**
- Modify: `Libraries/ConPersistence/Discoveries/DiscoveryStore.cs` (`DismissAsync`, `RestoreAsync`, `RemoveAsync`, `IsLiveAdoptionAsync`), `Libraries/ConPersistence/Config/ConfigService.cs` (the adoption check)
- Test: `Tests/ConPersistence.Tests/DiscoveryStoreTests.cs`

**Interfaces:**
- Produces:
  - `internal static Task<bool> DiscoveryStore.IsLiveAdoptionAsync(ConWatchDbContext db, DiscoveryRow row)` (was private).
  - A private `Task<ConfigResult> ChangeAsync(Guid id, Action<ConWatchDbContext, DiscoveryRow> change)`. It opens the context and transaction, gives NotFound and live-adoption Conflict, applies the change, saves, commits and returns `Ok(id)`.
  - A private const `AdoptedConflict = "This discovery was adopted into a target; delete the target first."`.

- [ ] **Step 1: Write the failing test.**

Add `Removing_a_live_adoption_names_the_same_fix_as_hiding_it`. Adopt a discovery into a target, then call `RemoveAsync`. The result is a Conflict whose single error equals `"This discovery was adopted into a target; delete the target first."`. Today it says "remove the target to undo it".

- [ ] **Step 2: Run it. Expected: FAIL on the message.**

Run: `dotnet test Tests/ConPersistence.Tests -c Debug --filter "FullyQualifiedName~DiscoveryStoreTests"`

- [ ] **Step 3: Implement `ChangeAsync` and route the three methods through it.**

Then make `IsLiveAdoptionAsync` internal and call it from `ConfigService.UpsertTargetAsync`, which keeps its own message.

- [ ] **Step 4: Run `DiscoveryStoreTests`, `DiscoveryAdoptTests` and `DiscoveryApiTests`. Expected: PASS.**

- [ ] **Step 5: Commit**

```bash
git commit -am "refactor(discoveries): one state-change path and one live-adoption check, shared with ConfigService"
```

---

### Task 10: CloneService snapshot prep (and the import ladder, if R1 made it uniform)

**Files:**
- Modify: `Programs/ConWatch/Clone/CloneService.cs`
- Test: existing `Tests/ConWatch.Tests/CloneTests.cs`, `CloneWithoutLocalDevicesTests.cs` and `CloneWithoutSystemManagedTests.cs`

**Interfaces:**
- Produces: a private `static (Snapshot Snapshot, int LocalSkipped) Prepare(Snapshot fetched, bool includeLocalDevices)`. It removes system-managed targets, counts Local targets, and removes them when `includeLocalDevices` is false. It uses `SnapshotFilter` directly. The `internal` pass-throughs `WithoutLocalDevices`/`WithoutSystemManaged` stay, because R5's X3 owns them.
  - Clone passes its request flag.
  - Take-over passes `await settings.GetBoolAsync(SettingKeys.CloneIncludeLocalDevices)` (Task 2).

The rule for the import catches: open `CloneAsync` and `TakeOverAsync` as R1 left them. If both map the same exception filters to the same status codes and error codes, fold them. They should differ only in (a) the message suffix and (b) clone's `RestoreAsync(previous)` before a rolled-back failure, plus clone's extra `import-incomplete` arm. The fold is a private `async Task<CloneOutcome?> ImportOrFailAsync(Snapshot snapshot, string suffix, Func<Task>? onRolledBack, CancellationToken ct)` that returns null on success. If they differ in anything else, leave both ladders and say so in the commit body. Do not change a code or a status.

- [ ] **Step 1: Run the pinning tests. Expected: PASS.**

Run: `dotnet test Tests/ConWatch.Tests -c Debug --filter "FullyQualifiedName~Clone"`

- [ ] **Step 2: Introduce `Prepare`, apply the import rule, and replace the copies.**

- [ ] **Step 3: Run the same filter. Expected: PASS, with the same test count.**

- [ ] **Step 4: Commit**

```bash
git commit -am "refactor(clone): clone and take-over prepare the snapshot in one place"
```

---

### Task 11: Export endpoints over HistoryQueries base queries

**Files:**
- Modify: `Libraries/ConPersistence/Queries/HistoryQueries.cs`, `Programs/ConWatch/Api/ExportEndpoints.cs`
- Test: `Tests/ConWatch.Tests/ExportApiTests.cs`

**Interfaces:**
- Produces, in `HistoryQueries` (unordered, `AsNoTracking`, the same `>=`/`<=` bounds as today):
  - `public static IQueryable<TransitionRow> TransitionsQuery(ConWatchDbContext db, Guid? subjectId, DateTime? fromUtc, DateTime? toUtc, string? subjectName = null, ScopeFilter scope = ScopeFilter.All)`. This is the existing private `Filtered` plus the time bounds; `OpeningTransitionsAsync` keeps calling `Filtered`.
  - `public static IQueryable<RollupRow> RollupsQuery(ConWatchDbContext db, Guid? targetId, string? resolution, DateTime? fromUtc, DateTime? toUtc)`. An empty `resolution` means any.
  - `public static IQueryable<GlitchRow> GlitchesQuery(ConWatchDbContext db, Guid? targetId, DateTime? fromUtc, DateTime? toUtc, ScopeFilter scope = ScopeFilter.All)`.
  - `TransitionsAsync`, `RollupsAsync` and `GlitchesAsync` keep their signatures and build on these.
- In `ExportEndpoints`, a private `static async Task<IResult> ExportAsync<T>(HttpContext http, IDbContextFactory<ConWatchDbContext> dbFactory, TimeProvider time, string format, string? from, string? to, string kind, string csvHeader, Func<T, string> csvLine, Func<ConWatchDbContext, DateTime?, DateTime?, IQueryable<T>> query)`. It runs the format check, the range parse, `OpenForResponseAsync`, the stamp, and then JSON or CSV, with the download name `conwatch-{kind}-{stamp}.csv`. Each endpoint passes its query with its ordering: transitions by `Id`, rollups by `TargetId` then `PeriodStartUtc`, glitches by `Id`.

- [ ] **Step 1: Write the pinning test.**

Add `Exports_filter_by_subject_target_and_resolution`. Seed two targets' transitions, Minute and Hour rollups, and glitches the way the file's other tests do. Then:
- `/export/transitions?format=json&subjectId={A}` returns only A's rows.
- `/export/rollups?format=json&targetId={A}&resolution=Hour` returns only A's Hour rows.
- `/export/glitches?format=json&targetId={A}&to={t}` returns only A's glitches starting at or before `t`.

- [ ] **Step 2: Run it. Expected: PASS before any change** (it pins today's behavior).

Run: `dotnet test Tests/ConWatch.Tests -c Debug --filter "FullyQualifiedName~ExportApiTests"`

- [ ] **Step 3: Add the base queries and `ExportAsync<T>`, and replace the three handlers' bodies.**

- [ ] **Step 4: Run `ExportApiTests` (including the pinned-bytes tests), `HistoryApiTests`, `HistoryScopeApiTests` and `GlitchApiTests`, plus ConPersistence's `HistoryQueryTests`. Expected: PASS.**

- [ ] **Step 5: Commit**

```bash
git commit -am "refactor(export): exports reuse HistoryQueries' filters and share one handler skeleton"
```

---

### Task 12: The host's one JSON options instance

**Files:**
- Create: `Programs/ConWatch/WebJson.cs`
- Modify:
  - `Programs/ConWatch/Clone/CloneService.cs`, `Programs/ConWatch/Mcp/ConWatchMcpTools.cs`.
  - `Programs/ConWatch/Pages/Index.cshtml.cs`, `Programs/ConWatch/Pages/History.cshtml.cs`.
  - `Programs/ConWatch/Pages/Config/{Dns,Pools,Profiles,Targets}.cshtml` (each view's `var json = …` lambda).

**Interfaces:**
- Produces: `public static class WebJson` in namespace `ConWatch`, with `public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)` and `public static string Serialize<T>(T value)`. A doc line says the instance is shared and must never be mutated.

ScoutClient (ConInfrastructure) and ConNotify's `Builders.cs` keep their own instances; they are other assemblies. Pinned by the existing `PageSmokeTests`, `McpEndpointTests`, `CloneTests` and `HistoryPageTests`.

- [ ] **Step 1: Add `WebJson` and switch the eight sites.**

- [ ] **Step 2: Confirm the copies are gone.**

Run: `git grep -n "JsonSerializerDefaults.Web" -- Programs/ConWatch`
Expected: only `WebJson.cs`.

- [ ] **Step 3: Full build and test. Expected: 0 failed.**

- [ ] **Step 4: Commit**

```bash
git commit -am "refactor(host): one shared web JSON options instance"
```

---

### Task 13: ProbeSpec parsed and described in one place

**Files:**
- Modify:
  - `Core/ConCore/ProbeSpec.cs` (add `Describe`).
  - `Core/ConCore/Targets/Target.cs` (add `ParsedProbe`).
  - `Core/ConEngine/WatcherEngine.cs` (`StartController`).
  - `Core/ConEngine/Topology/ModelCheckCoordinator.cs`: the probe-only-loss advice, the call that used `ProbeText`, and delete `ProbeText`.
- Test: `Tests/ConEngine.Tests/ProbeSpecTests.cs`

**Interfaces:**
- Produces:
  - `public string ProbeSpec.Describe()`: `"tcp/{Port}"`, `"dns"` or `"ping"`. This is the note wording, which differs from `ToString()`'s config form on purpose.
  - `public ProbeSpec Target.ParsedProbe()`: `ProbeSpec.TryParse(Probe, out var spec) ? spec : ProbeSpec.Icmp`. That is the value today's call sites already get, because they ignore the bool.

- [ ] **Step 1: Write the failing tests.**

```csharp
[Theory]
[InlineData("tcp:80", "tcp/80")]
[InlineData("dns", "dns")]
[InlineData(null, "ping")]
public void Describe_is_the_model_note_wording(string? stored, string expected)
{
    ProbeSpec.TryParse(stored, out var spec);
    Assert.Equal(expected, spec.Describe());
}

[Fact]
public void An_unparseable_probe_reads_as_icmp() =>
    Assert.Equal(ProbeSpec.Icmp, new Target { Id = Guid.NewGuid(), Name = "x", Address = "1.1.1.1", ProfileName = "Public", Probe = "tcp:0" }.ParsedProbe());
```

- [ ] **Step 2: Run them. Expected: compile failure.**

Run: `dotnet test Tests/ConEngine.Tests -c Debug --filter "FullyQualifiedName~ProbeSpecTests"`

- [ ] **Step 3: Implement both and replace the three `ProbeSpec.TryParse(x.Target.Probe, out var probe)` call sites and `ProbeText`.**

- [ ] **Step 4: Run `ProbeSpecTests` and the `ModelCheck` tests, then the full suite. Expected: PASS.**

- [ ] **Step 5: Commit**

```bash
git commit -am "refactor(engine): targets parse their probe in one place; ProbeSpec.Describe is the note wording"
```

---

### Task 14: One TargetTransition field list

**Files:**
- Modify:
  - `Libraries/ConPersistence/Entities/DataRows.cs` (`TransitionRow`: add the factory).
  - `Libraries/ConPersistence/Recording/TransitionRecorder.cs` (delete `ToRow`, call the factory).
  - `Libraries/ConPersistence/Queries/TransitionDto.cs` (`From(TargetTransition)`).
- Test: existing `Tests/ConPersistence.Tests/TransitionDtoTests.cs` and `RecorderCollectorTests.cs`

**Interfaces:**
- Produces: `public static TransitionRow TransitionRow.From(TargetTransition t)`. It holds exactly today's `ToRow` field list. `TimestampUtc = t.Timestamp.UtcDateTime`.
- `TransitionDto.From(TargetTransition t)` becomes `From(TransitionRow.From(t)) with { Id = null, Dns = TransitionDnsBlame.From(t.Reason.Dns) }`. The row's `TimestampUtc` is `Kind=Utc`, and `From(row)` tags it UTC, so the instant and the `+00:00` encoding do not change.

- [ ] **Step 1: Run the pins. Expected: PASS.**

Run: `dotnet test Tests/ConPersistence.Tests -c Debug --filter "FullyQualifiedName~TransitionDtoTests|FullyQualifiedName~RecorderCollectorTests"`

`Live_transition_maps_every_field_and_normalizes_the_offset` and `Live_json_has_from_and_to_and_no_id` are the ones that guard this change.

- [ ] **Step 2: Add the factory and route both mappings through it.**

- [ ] **Step 3: Run the filter, then `StatusBroadcasterTests` and `NotifierIntegrationTests`. Expected: PASS.**

- [ ] **Step 4: Commit**

```bash
git commit -am "refactor(persistence): one TargetTransition field list for the recorder and the live DTO"
```

---

### Task 15: Rollup bucket arithmetic and UTC flooring

**Files:**
- Modify:
  - `Libraries/ConPersistence/Entities/RollupResolution.cs` (add `FloorUtc`).
  - `Libraries/ConPersistence/Recording/RollupAccumulator.cs` (`RollupBucket.Add`; `AddSample` and `TakeClosed` use it).
  - `Libraries/ConPersistence/Retention/RetentionPass.cs` (`TruncateToHour`/`TruncateToDay` delegate to `FloorUtc`).
- Test: `Tests/ConPersistence.Tests/RollupAccumulatorTests.cs`

**Interfaces:**
- Produces:
  - `public static DateTime RollupResolution.FloorUtc(DateTime t, long unitTicks)`. It returns `new(t.Ticks - t.Ticks % unitTicks, DateTimeKind.Utc)`.
  - `internal void RollupBucket.Add(bool success, long rttMs)`. It holds the four lines `AddSample` did inline, and `AddSample` still calls it under the accumulator's lock.
- `RollupBucket.Absorb` and `RollupMerge.Combine` are unchanged (see Decisions).

- [ ] **Step 1: Write a test for `FloorUtc`.**

Add `FloorUtc_floors_to_the_unit_and_tags_utc`. `FloorUtc(new DateTime(2026,10,29,13,47,59,999, DateTimeKind.Unspecified), TimeSpan.TicksPerHour)` equals `new DateTime(2026,10,29,13,0,0, DateTimeKind.Utc)` and has `Kind == DateTimeKind.Utc`. Expected: compile failure.

- [ ] **Step 2: Implement and replace the four floors and the inline sample arithmetic.**

- [ ] **Step 3: Run `RollupAccumulatorTests`, `RetentionTests`, `RetentionServiceTests` and `RecorderCollectorTests`. Expected: PASS.**

- [ ] **Step 4: Commit**

```bash
git commit -am "refactor(rollups): one bucket Add and one UTC floor"
```

---

## Wire and UI changes (release-note lines)

- `PUT /api/v1/settings` error text: an out-of-range int now names its full key, as in `"Retention.MinuteRetentionDays must be 1–3650."` (it was `"MinuteRetentionDays must be 1–3650."`). `Verdict.WeatherThreshold` reads `"Verdict.WeatherThreshold must be an integer ≥ 1."` (Task 3)
- The dashboard's configuration-warning count and hint now include refused TCP targets. The DNS, Pools and Profiles tabs show the refused-TCP warning that only Targets showed before. The Settings page has a warnings banner, which is where the "cloned-from node still monitoring" warning now appears. `GET /api/v1/config/health` is unchanged. (Task 5)
- In archive mode, the dashboard's embedded initial data carries the disabled snapshot (`enabled:false`) instead of `null`. What the page shows is unchanged. (Task 4)
- The tray's Test button reports "Could not connect: refused." / "timed out." / "invalid URL." instead of the raw exception message. (Task 7)
- `conwatch service install` with a pre-0.10 `contray.json` whose `url` is not a string skips it instead of failing. (Task 6)
- Discoveries: removing an adopted discovery whose target still exists says "delete the target first", the same as hiding it. (Task 9)
