# Phase 3 Web UI Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement the Phase 3 spec (`docs/superpowers/specs/2026-08-20-phase3-web-ui-design.md`): Razor Pages UI in ConWatch with SignalR live updates, React/Vite islands (dashboard tree with rule-C expansion, TanStack charts), config editors, and traceroute assist.

**Architecture:** Razor page shells + two hydrating islands (`dashboard`, `history`) built by Vite into `wwwroot/js/`. `StatusBroadcaster` feeds a SignalR hub from engine streams (reattach-on-rebuild, like the recorder). Traceroute is a TTL-stepped prober in ConInfrastructure behind an `ITtlProbe` abstraction.

**Tech Stack:** ASP.NET Core Razor Pages + SignalR, Vite + React 18 + TypeScript, TanStack React Charts (`react-charts@beta`; sanctioned fallback: uPlot inside the same component boundary), Pico.css (vendored), Vitest, xUnit.

## Global Constraints

- **NEVER `git push`.** Branch `phase3` off master; commit per task.
- `dotnet build ConnectionWatcher.sln` and all existing suites (66 + 35 + 10) stay green after every task.
- Node/npm required — verify availability in Task 1 Step 1; STOP and ask if missing.
- Bundles are emitted as stable names (`dashboard.js`, `history.js`) and referenced with `asp-append-version="true"` for cache busting (spec said "hashed"; the tag-helper querystring achieves the same without a manifest — record as an approved simplification).
- Quiet set for rule C: `Responding`, plus `Starting` when the engine has never seen the target leave `Starting` (fresh boot). Cool-down: 5 minutes. Pins in localStorage key `conwatch.pins`.
- Snapshot pushes: regular 1 s cadence, immediate on transition, ≥250 ms between sends.
- All UI data comes from the existing `/api/v1` endpoints + the hub; no new query endpoints except `POST /api/v1/traceroute`.

---

### Task 1: Branch, ClientApp scaffold, Razor shell, build integration

**Files:**
- Create: `Programs/ConWatch/ClientApp/` (package.json, vite.config.ts, tsconfig.json, src/dashboard.tsx, src/history.tsx as placeholders)
- Create: `Programs/ConWatch/Pages/` (_Layout.cshtml, _ViewImports.cshtml, _ViewStart.cshtml, Index.cshtml(+.cs), History.cshtml(+.cs), Config/Targets.cshtml(+.cs), Config/Profiles.cshtml(+.cs), Config/Pools.cshtml(+.cs), Traceroute.cshtml(+.cs))
- Create: `Programs/ConWatch/wwwroot/css/pico.min.css` (vendored), `wwwroot/css/site.css`
- Modify: `Programs/ConWatch/ConWatch.csproj` (client build targets), `Programs/ConWatch/Program.cs` (AddRazorPages/MapRazorPages/UseStaticFiles)

**Interfaces:** produces the page routes `/`, `/History`, `/Config/{Targets|Profiles|Pools}`, `/Traceroute`, a working `npm run build` emitting `wwwroot/js/dashboard.js` + `history.js`, and `npm test` (vitest, zero tests yet). Placeholders render "island pending" text.

- [ ] **Step 1: Verify toolchain, branch, scaffold**

```powershell
node --version; npm --version   # STOP if missing
git checkout -b phase3
```

`ClientApp/package.json`:

```json
{
  "name": "conwatch-client",
  "private": true,
  "type": "module",
  "scripts": { "build": "vite build", "watch": "vite build --watch", "test": "vitest run" },
  "dependencies": {
    "@microsoft/signalr": "^8.0.0",
    "react": "^18.3.0",
    "react-dom": "^18.3.0",
    "react-charts": "3.0.0-beta.57"
  },
  "devDependencies": {
    "@types/react": "^18.3.0",
    "@types/react-dom": "^18.3.0",
    "@vitejs/plugin-react": "^4.3.0",
    "typescript": "^5.6.0",
    "vite": "^5.4.0",
    "vitest": "^2.1.0"
  }
}
```

`ClientApp/vite.config.ts`:

```typescript
import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import { resolve } from "path";

export default defineConfig({
  plugins: [react()],
  build: {
    outDir: "../wwwroot/js",
    emptyOutDir: true,
    rollupOptions: {
      input: {
        dashboard: resolve(__dirname, "src/dashboard.tsx"),
        history: resolve(__dirname, "src/history.tsx"),
      },
      output: {
        entryFileNames: "[name].js",
        chunkFileNames: "chunk-[name].js",
        assetFileNames: "[name][extname]",
      },
    },
  },
});
```

`tsconfig.json`: standard Vite React TS (`"jsx": "react-jsx"`, `"strict": true`, `"module": "ESNext"`, `"moduleResolution": "bundler"`, `"types": ["vitest/globals"]` omitted — import vitest explicitly). Placeholder `src/dashboard.tsx` / `src/history.tsx`: mount into `#dashboard-root` / `#history-root` rendering `<p>island pending</p>` via `createRoot`.

Run `npm install` (creates package-lock.json for `npm ci`), download Pico:

```powershell
Invoke-WebRequest https://cdn.jsdelivr.net/npm/@picocss/pico@2/css/pico.min.css -OutFile Programs/ConWatch/wwwroot/css/pico.min.css
```

- [ ] **Step 2: csproj build integration** — add to `ConWatch.csproj`:

```xml
<ItemGroup>
  <None Include="ClientApp\**" Exclude="ClientApp\node_modules\**;ClientApp\dist\**" />
  <Content Remove="ClientApp\**" />
</ItemGroup>
<Target Name="EnsureClientBuild" BeforeTargets="Build"
        Condition="!Exists('wwwroot\js\dashboard.js')">
  <Exec Command="npm ci" WorkingDirectory="ClientApp" Condition="!Exists('ClientApp\node_modules')" />
  <Exec Command="npm run build" WorkingDirectory="ClientApp" />
</Target>
<Target Name="PublishClientBuild" BeforeTargets="ComputeFilesToPublish">
  <Exec Command="npm ci" WorkingDirectory="ClientApp" />
  <Exec Command="npm run build" WorkingDirectory="ClientApp" />
</Target>
```

- [ ] **Step 3: Razor shell** — `Program.cs` gains `builder.Services.AddRazorPages();`, `app.UseStaticFiles();`, `app.MapRazorPages();`. `_Layout.cshtml`: Pico + site.css links (`asp-append-version`), `<nav>` with the four links, `RenderBody`. Each page: `@page`, title, and for Index/History an island root div + `<script type="module" src="~/js/dashboard.js" asp-append-version="true"></script>` (history likewise); Config/Traceroute pages render placeholder headings (filled in Tasks 6–8). `site.css`: status color variables (`--ok #2e9e52; --warn #e08a00; --down #cc3333; --unreach #999`), tree indent/tile/status-bar classes (final styling in Task 4).

- [ ] **Step 4: Verify** — `npm run build` emits both bundles; `dotnet build ConnectionWatcher.sln` green; `dotnet run` serves `/` with nav and "island pending"; existing suites green (`dotnet test Tests/ConWatch.Tests` — pages must not break WebApplicationFactory).

- [ ] **Step 5: Commit** — `git add -A; git commit -m "feat(ui): ClientApp scaffold, Razor shell, Vite build integration"`

---

### Task 2: StatusBroadcaster + SignalR hub

**Files:**
- Create: `Programs/ConWatch/StatusHub.cs`, `Programs/ConWatch/StatusBroadcaster.cs`, `Programs/ConWatch/StatusSnapshotBuilder.cs`
- Modify: `Programs/ConWatch/Program.cs` (AddSignalR, MapHub), `Programs/ConWatch/Api/StatusEndpoints.cs` (use the shared builder), `Programs/ConWatch/WatcherHostService.cs` (attach broadcaster in StartEngineAsync)
- Test: `Tests/ConWatch.Tests/SignalRTests.cs` (+ package `Microsoft.AspNetCore.SignalR.Client`)

**Interfaces:**

```csharp
public class StatusHub : Hub { }   // server->client only

public record StatusSnapshot(object? Verdict, List<object> Targets, List<object> Pools); // shapes identical to GET /status
public static class StatusSnapshotBuilder
{
    public static object? Build(WatcherEngine? engine);   // null when engine down; same anonymous shape the endpoint returns today (refactor endpoint to call this)
}

public class StatusBroadcaster(IHubContext<StatusHub> hub, WatcherHostService host,
    TimeProvider timeProvider, ILogger<StatusBroadcaster> log) : BackgroundService
{
    public void Attach(WatcherEngine engine);   // subscribe TransitionSource; replaces prior subscription
    // loop: every 1s send "statusChanged" if engine running; on transition:
    // send "transition" immediately + statusChanged (>=250ms since last send)
}
```

Note: `StatusBroadcaster` takes `WatcherHostService` lazily via `IServiceProvider` if a constructor cycle appears (host now needs broadcaster for Attach; broadcaster needs host for Engine — break the cycle by having Attach receive the engine and the loop use the attached engine reference only).

- [ ] **Step 1: Failing test** (`SignalRTests.cs`): via `ConWatchFactory`, seed home config (reuse `ConfigApiTests.SeedHomeAsync`), open a `HubConnection` with `factory.Server.CreateHandler()`, collect `transition` and `statusChanged` payloads (as `JsonElement`); kill Router via `factory.Pings.SetUp` → assert within 20 s: a `transition` arrives with `subjectName` "Router" and `to` "Down", and a subsequent `statusChanged` shows Router state "Down"; also assert ≥2 `statusChanged` messages arrive ≥ 250 ms apart during a quiet 3 s window (cadence works).
- [ ] **Step 2:** verify failure (hub route 404).
- [ ] **Step 3:** implement (hub, builder refactor, broadcaster with `System.Threading.PeriodicTimer` over TimeProvider, `_lastSnapshotSent` gate, attach call in `WatcherHostService.StartEngineAsync` alongside recorder/collector).
- [ ] **Step 4:** tests green (SignalR + all existing).
- [ ] **Step 5:** commit `feat(ui): SignalR status hub + broadcaster`.

---

### Task 3: Island core logic — expansion machine, status store, hub client (Vitest)

**Files:**
- Create: `ClientApp/src/lib/expansion.ts`, `ClientApp/src/lib/statusStore.ts`, `ClientApp/src/lib/hub.ts`, `ClientApp/src/lib/types.ts`
- Test: `ClientApp/src/lib/expansion.test.ts`, `ClientApp/src/lib/statusStore.test.ts`

**Interfaces (`types.ts` mirrors the API JSON):**

```typescript
export interface TargetStatus { id: string; name: string; address: string | null; state: string; successRate: number; avgRtt: number; sampleSize: number; }
export interface PoolStatus { id: string; name: string; state: string; }
export interface VerdictStatus { state: string; detail: string | null; }
export interface Snapshot { verdict: VerdictStatus | null; targets: TargetStatus[]; pools: PoolStatus[]; }
export interface TransitionEvent { id?: number; timestamp: string; subjectId: string; subjectName: string; subjectKind: string; from: string; to: string; trigger: string; detail: string | null; verdict: string | null; successRate: number | null; sampleSize: number | null; burstLossRate: number | null; }
```

**`expansion.ts`** — pure, fully unit-tested:

```typescript
export interface RowUi { expanded: boolean; pinned: boolean; cooldownUntil: number | null; everLeftStarting: boolean; }
export const COOLDOWN_MS = 5 * 60_000;
export function initialRow(): RowUi;
export function onStateChange(row: RowUi, state: string, nowMs: number): RowUi;
// quiet: "Responding" always; "Starting"/"Initializing" quiet only while !everLeftStarting
// leaving quiet -> expanded=true, cooldownUntil=null; entering Responding from non-quiet -> cooldownUntil=now+COOLDOWN_MS (stays expanded)
export function tick(row: RowUi, nowMs: number): RowUi;      // collapses when cooldown elapsed and not pinned
export function togglePin(row: RowUi): RowUi;                 // pin forces expanded; unpin re-evaluates via tick semantics
export function isExpanded(row: RowUi): boolean;              // pinned || expanded
```

**`statusStore.ts`** — tiny store (no Redux): holds `Snapshot`, `RowUi` map, recent transitions (max 50), latest transition; reducers `applySnapshot(snap, nowMs)` (runs onStateChange per target/pool), `applyTransition(t, nowMs)`, `tickAll(nowMs)`; `loadPins()/savePins()` on localStorage key `conwatch.pins`; subscribe/notify for React `useSyncExternalStore`.

**`hub.ts`** — SignalR wrapper: connect to `/hubs/status`, `onSnapshot`/`onTransition` callbacks, `withAutomaticReconnect`, `onreconnected` → fetch `/api/v1/status` and feed as snapshot; exposes `connectionState` callback for the reconnecting pill.

- [ ] **Step 1: Failing tests** — expansion: fresh-boot Starting stays collapsed; Starting-after-release expands (everLeftStarting set once state leaves Starting family); Faltering expands; return to Responding holds through cooldown then `tick` collapses; pinned never collapses; unpin after recovery collapses on next tick; pool rows use same machine. statusStore: snapshot then transition ordering; transitions list capped at 50; pins round-trip localStorage (mock).
- [ ] **Step 2:** `npm test` fails (modules missing). **Step 3:** implement. **Step 4:** `npm test` green. **Step 5:** commit `feat(ui): island core logic with tests`.

---

### Task 4: Dashboard island + server-rendered tree

**Files:**
- Create: `ClientApp/src/dashboard.tsx` (replace placeholder), `ClientApp/src/components/Tree.tsx`, `ClientApp/src/components/Tile.tsx`, `ClientApp/src/components/VerdictBanner.tsx`, `ClientApp/src/components/StatusBar.tsx`, `ClientApp/src/components/MiniChart.tsx`
- Modify: `Programs/ConWatch/Pages/Index.cshtml(+.cs)` (server-rendered tree from `StatusSnapshotBuilder` + hierarchy from `ConfigStore`), `wwwroot/css/site.css` (tile/tree/banner/status-bar styles, height transitions)

**Interfaces:** the island reads an inline `<script id="initial-data" type="application/json">` blob (snapshot + parent map + pool member map) rendered by the page model, so first paint and hydration agree. `MiniChart` fetches `/api/v1/rollups/{id}?from=<now-60m>&to=<now>&resolution=minute` on mount and每 60 s; renders latency line + loss shading via the chart component (TanStack; on integration failure use uPlot per the sanctioned fallback and note it in the commit message).

Behavior per spec §4: tree nesting from parent map; collapsed row = dot/name/rate/RTT; expanded tile = state + latest transition evidence line + burst hint (`state ∈ {Faltering, Recovering}` or Down < BurstMaxDuration — client approximates with "probing" label on non-quiet states) + MiniChart; verdict banner colors (ok green / SiteIncident still green-with-note / weather+degraded orange / LinkDown red / unknown gray); status bar bottom-fixed with latest transition + slide-up panel (last 50); reconnecting pill when hub not connected.

- [ ] **Step 1:** Page model: inject `WatcherHostService` + `ConfigStore`; build initial JSON; Razor renders static nested `<ul>` (correct at load, replaced on hydration).
- [ ] **Step 2:** implement components + wire store/hub; `npm run build`; manual check with `dotnet run` + Development fake sim? (real ICMP fine — use real router/8.8.8.8 config or FakePings via env in tests only).
- [ ] **Step 3:** verify: `npm test` green, dotnet suites green, manual: dashboard renders, live updates visible (watch success-rate numbers move), pin toggles and survives refresh.
- [ ] **Step 4:** commit `feat(ui): live dashboard with rule-C expanding tree`.

---

### Task 5: History page + charts island

**Files:**
- Create: `ClientApp/src/history.tsx` (replace placeholder), `ClientApp/src/components/LatencyBand.tsx`, `ClientApp/src/components/LossBars.tsx`, `ClientApp/src/components/StateRibbon.tsx`, `ClientApp/src/components/StatsRow.tsx`, `ClientApp/src/lib/historyData.ts`
- Modify: `Programs/ConWatch/Pages/History.cshtml(+.cs)` (subject list rendered server-side into the data blob)
- Test: `ClientApp/src/lib/historyData.test.ts`

**Interfaces (`historyData.ts`, pure + tested):**

```typescript
export function uptimePercent(points: RollupPoint[]): number | null;          // sum success / sum count
export function incidents(transitions: TransitionEvent[]): { count: number; longestMs: number };
// incident = entry into Down or Unreachable; ends at next entry into Responding/Recovering...
// precise rule: spans from a transition with to∈{Down,Unreachable} until the next transition with to=Responding (per subject); open incidents end at range end
export function ribbonSegments(transitions: TransitionEvent[], rangeStart: string, rangeEnd: string, initialState?: string): Array<{ state: string; from: string; to: string; evidence: string | null }>;
```

Page behavior per spec §5: pickers (subject incl. pools + "Internet" verdict subject id constant `6e7e12e7-0000-4000-8000-000000000001`; ranges 24h/7d/30d/custom date inputs); fetch rollups (`resolution=auto`) + transitions (paged until range covered, cap 2000); StatsRow; LatencyBand + LossBars (chart lib) + StateRibbon (plain SVG rects + title tooltips); pools/verdict → ribbon + transition-derived stats only.

- [ ] **Step 1:** failing Vitest for the three pure functions (hand-computed fixtures incl. open-ended incident and pre-range initial state).
- [ ] **Step 2:** implement lib + components + page. **Step 3:** `npm test` + manual render against accumulated real data. **Step 4:** commit `feat(ui): history page with charts, ribbon, stats`.

---

### Task 6: Config editor pages

**Files:**
- Create: `wwwroot/js/site-config.js` (plain JS, no build: apiFetch helper, form wiring, error rendering), fill in `Pages/Config/Targets.cshtml(+.cs)`, `Profiles.cshtml(+.cs)`, `Pools.cshtml(+.cs)`
- Test: extend `Tests/ConWatch.Tests` with page smoke tests (GET each config page → 200, contains table markers)

Behavior per spec §6: each page model loads current config via `ConfigStore.ExportAsync()` and renders the table + form server-side; `site-config.js` intercepts submit → fetch POST/PUT/DELETE to `/api/v1/...` → on 400/409 render `errors[]` into `<ul class="form-errors">`; on success reload. Parent/profile dropdowns from the same export. Pools: members as checkbox list + index order = check order (rendered list with up/down buttons, vanilla). Export button = link to `/api/v1/config/export` (download attribute); import = file input + confirm dialog + POST.

- [ ] Steps: failing page smoke tests → implement → suites green → commit `feat(ui): config editor pages`.

---

### Task 7: TracerouteRunner + endpoint

**Files:**
- Create: `ConInfrastructure/Traceroute/ITtlProbe.cs`, `ConInfrastructure/Traceroute/IcmpTtlProbe.cs`, `ConInfrastructure/Traceroute/TracerouteRunner.cs`
- Modify: `Programs/ConWatch/Program.cs` (DI + endpoint), `Programs/ConWatch/Api/` (new `TracerouteEndpoints.cs`)
- Test: `Tests/ConEngine.Tests/TracerouteRunnerTests.cs` (fake probe), `Tests/ConWatch.Tests/TracerouteApiTests.cs`

**Interfaces:**

```csharp
public record TtlProbeResult(IPAddress? Hop, long RttMs, bool ReachedDestination, bool TimedOut);
public interface ITtlProbe { Task<TtlProbeResult> ProbeAsync(IPAddress destination, int ttl, int timeoutMs, CancellationToken ct); }
public class IcmpTtlProbe : ITtlProbe;   // System.Net.NetworkInformation.Ping with PingOptions(ttl, true); TtlExpired->hop, Success->reached

public record TracerouteHop(int Ttl, string? Address, string? Hostname, long? RttMs, bool ReachedDestination);
public class TracerouteRunner(ITtlProbe probe, ILogger<TracerouteRunner> log)
{
    // resolve host (IP literal or DNS, IPv4 first); ttl 1..30, 3 probes/hop (keep best RTT, first non-null hop addr);
    // reverse-DNS per hop via Dns.GetHostEntryAsync with 1s cap, errors -> null; stop when reached.
    public Task<IReadOnlyList<TracerouteHop>> RunAsync(string host, CancellationToken ct);
}
```

Endpoint: `POST /api/v1/traceroute` body `{ "host": "8.8.8.8" }` → 200 `{ host, hops: [...] }`, 400 unresolvable/empty host. DI: `ITtlProbe` → `IcmpTtlProbe` singleton (test factory swaps in a scripted fake: e.g. hops 10.0.0.1, 198.51.100.11, then destination at ttl 3; one middle hop all-timeouts → `Address=null`).

- [ ] Steps: failing runner tests (ordered hops, silent hop preserved as null row, stops at destination, unresolvable throws → endpoint 400) → implement → green → commit `feat(infra): TTL-stepped traceroute + API`.

---

### Task 8: Traceroute page

**Files:** fill `Pages/Traceroute.cshtml(+.cs)`, `wwwroot/js/site-traceroute.js`
- Test: extend page smoke tests.

Behavior per spec §7: host input (default `8.8.8.8`), Run button + spinner; results table (TTL, address, hostname, RTT, "you are here" marks for hops that already match a monitored target's address); per-hop **Add as target** navigates to `/Config/Targets?address=<ip>&name=hop-<ttl>&parent=<name>` where parent = nearest earlier hop whose address matches an existing target (client-side lookup against `/api/v1/targets`); the Targets page pre-fills its form from those query params (Task 6 form supports it).

- [ ] Steps: smoke test → implement → manual run against real path → commit `feat(ui): traceroute assist page`.

---

### Task 9: End-to-end verification and wrap-up

- [ ] **Step 1:** New xUnit test: publish smoke — skip (publish exercised manually); instead full-suite pass + `npm test` + `dotnet publish Programs/ConWatch -o <temp>` verifying `wwwroot/js/dashboard.js` lands in output.
- [ ] **Step 2:** Manual exit-criteria checklist (spec §10): run ConWatch in Development with the fake-sim seed (use ConWatchFactory-style env: real run against home LAN is fine too); walk criteria 1–5.
- [ ] **Step 3:** README: add UI section (pages, dev loop `vite build --watch` + `dotnet run`).
- [ ] **Step 4:** All suites + vitest green; commit `feat(ui): phase 3 complete`; then superpowers:finishing-a-development-branch.

## Plan Self-Review (completed)

- **Spec coverage:** §3 arch→T1/T2, §4 dashboard→T3/T4, §5 history→T5, §6 config→T6, §7 traceroute→T7/T8, §8 errors woven into T2–T8 implementations, §9 testing→per-task, §10 exit→T9. NET_RAW note is Phase 4 scope (spec'd, no task here).
- **Placeholder scan:** clean; component code intentionally specified by contract + behavior reference to spec sections rather than full listings (single-session inline execution; the spec is the source of truth for behavior).
- **Type consistency:** `RowUi`/store/hub types defined T3, consumed T4/T5; `StatusSnapshotBuilder` defined T2, consumed T4 page model; DTO field names match Phase 2 API camelCase JSON.
