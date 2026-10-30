# Refactor 4: client dedupes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Carry out survey items C1–C6 and the rest of C7 as client-side dedupes, add the DnsDown "your resolver" mark (ruling 3), and apply the tree rule plus History's Resolvers group (ruling 4). Everything runs on branch `refactor-4-client`.

**Architecture:** There are four groups. A: small pure libraries, `lib/scope.ts`, `lib/format.ts`, `lib/localTime.ts` and `lib/api.ts`, each replacing copies that have drifted apart. B: one verdict table, `lib/verdicts.ts`, then the DnsDown mark built on it. C: shared drawing primitives (ribbon SVG, hatch, mark cell, state chip) and shared React plumbing (`useStory`, `groupByParent`). D: the plain-JS wwwroot pages reuse `site-config.js`, plus a new `site-time.js`. Each behavior-preserving task first pins today's behavior with a test. The two ruling tasks (2 and 7) start with a test that fails.

**Tech Stack:** TypeScript 5.6, React 18.3, Vite 5.4, vitest 2.1 with happy-dom, react-charts 3.0.0-beta.57, plain ES2020 scripts in `wwwroot/lib`, ASP.NET Core Razor Pages (.NET 10), and xUnit for one server-side pin.

**Spec:** `docs/superpowers/reviews/2026-10-22-refactor-survey.md`: rows C1–C7, plus Rulings 3 and 4. The reviewer notes with exact locations at `663ab0f` are in the client report (`survey-0.md` in the session scratchpad). This plan was written against master `1e0c820`, with refactor plans 1–3 assumed merged before it starts.

## Global Constraints

- **Git**
  - Never push.
  - Work on branch `refactor-4-client`, cut from master after `refactor-3-host` has merged.
  - Make one commit per task.
  - Use conventional prefixes (`refactor:`, `feat:`, `test:`, `fix:`) with no attribution or co-author lines.
- **Checks after every task**
  - `dotnet build ConnectionWatcher.sln -c Debug` adds no warnings. The only warnings allowed are the existing test-project ones: CA1416 in `WindowsFolderLockdownTests`, CA2022 ×2 and CS8631 ×1. This build also runs `npm run build` (`tsc --noEmit && vite build`) through `ConWatch.csproj`, so a type error fails it.
  - `dotnet test ConnectionWatcher.sln -c Debug` has 0 failures.
  - From `Programs/ConWatch/ClientApp`, `npx vitest run` passes. Its test count only grows, except where a task moves tests between files.
- **No new dependencies or schema changes**
  - No new npm or NuGet packages and no new projects.
  - No EF model change and no migration.
  - No server JSON shape changes. This plan changes no wire format; see "Visible changes" for what users will see.
- **Time handling.** Timestamps stay UTC end to end. Local time is computed only in the browser, through `lib/localTime.ts` from Task 4 onward. No new `DateTime.Now` and no `new DateTimeOffset(x)` on a value whose Kind is unknown.
- **Waiting in tests.** Tests wait for conditions (`await vi.waitFor(...)` or flushing promises) and never sleep and then assert, because the CI runners have 2 cores.
- **Commands.** Run commands from the repo root in PowerShell. Paths below use forward slashes, which both shells accept. `ClientApp` means `Programs/ConWatch/ClientApp`, and `wwwroot` means `Programs/ConWatch/wwwroot`.
- **Names from plan 1** that this plan consumes:
  - `apiFetch` in `wwwroot/lib/site-config.js` resolves to `{ ok: false, status: 0, body: null }` on a network error and never throws.
  - `withBusy(el, fn)` in the same file sets `aria-busy` on `el`, awaits `fn()`, and always clears it.
  - `ConCore.Targets.TargetScopeExtensions.IsTree()` is the server's tree rule (Pools editor, B1). This plan adds the client twin.
- **Out of scope:**
  - X4 belongs to plan 5. That covers the client dead code (`RAMP` re-export, `TimeChart.lossAxis`, `RowParts.successRate/led`, `Branch.targets`, the `orderRows` export, `unacknowledgedCount`, `LanSummary.down`), the dead `.verdict-banner.siteincident/.internetweather` and `.cw-mono` CSS, and the duplicated dark-token block in `site.css`. Leave them, even in files this plan touches.
  - The client half of O1 is taken here (Task 5) because Tasks 1 and 5 rewrite the same lines. Plan 5 should skip it.
- **Browser checks.** Visual tasks end with a browser check by the executor, using the built-in browser pane against the `ConWatch` dev server.
  - The pane never fires a `<dialog>`'s `close` event. Check any dialog flow by its DOM state after a short wait, never by waiting for `close`.
  - Never sign out, and never change settings beyond what the step says.

## Review Focus

1. **Timestamp formats.**
   - Case: a server timestamp written `…:00.9+00:00` compared with a client `…:00.900Z` for the same instant. It must count as equal, and as ordered against a neighbor.
   - Today, string comparison gets this wrong; `isoCompare` fixes it.
   - Test: Task 4, `isoCompare treats +00:00 and Z, 1 and 7 fraction digits, as the same clock`.
2. **Failed requests.**
   - Case: History, the board or the glitch table on a dropped connection. The page shows no data, never a permanent "loading…", and nothing is left unhandled.
   - Test: Task 5, `fetchTransitions resolves [] when fetch rejects`, plus the same test for `fetchGlitches` and `fetchRollups`.
3. **DnsDown episodes.**
   - Case: a DnsDown stretch in the ISP report. It must never become an attributed episode or count as link time, and it must still show in the dashboard ledger.
   - Tests: Task 7 adds `Assert.DoesNotContain(report.Episodes, e => e.Kind == "DnsDown")` to `IspReportBuilderTests.Incidents_match_the_history_page_cases` (cases 8 and 9 carry DnsDown), and `a DnsDown stretch is a resolver ledger line, never link time` on the client.
4. **Punchcard keys.**
   - Case: a punchcard event at a local hour on a day boundary must land in the cell the grid draws. Today the bucket key and the lookup key are two separate copies that must agree.
   - Test: Task 4, `Punchcard draws the cell punchcardCells filled`.
5. **Old History payloads.**
   - Case: a History subject with no `scope` (an older page payload) still lands in Path, and every subject lands in exactly one group, including resolvers.
   - Test: Task 2, the extended `partitions every subject exactly once`.

---

## Decisions made at plan time

| Item | Survey or ruling said | Decided here |
|---|---|---|
| C4 names | Add `isTreeTarget` in `lib/scope.ts` | `lan.ts` → `lib/scope.ts`. **`isInternetTarget` is deleted, not renamed.** Its two callers (the tree and the board) become `isTreeTarget`. The status store's "is this internet news" check becomes `!isLocal(...)`, matching the server's `ScopeFilter.Internet` ("not Local"). Keeping a client `isInternetTarget` that means something different from the server's `internet` scope is the drift this item exists to remove. |
| C4 scope union | Declared 3× | One `export type TargetScope = "Verdict" \| "Local" \| "Resolver"` in `types.ts`. `TransitionEvent.scope` is `TargetScope \| "Unknown"`. A contracts test pins the union against `vocabulary.json`. |
| Ruling 4 group order | History gets a "Resolvers" group | The picker groups are Path, Pools, Resolvers, LAN. A group shows only when it has subjects, as today. Resolver subjects are `kind: "target"` with role `other`, so they get rollup charts like any target. The recurrence punchcard, board and ledger stay Path-only, as today. |
| C1 styles | Two named styles | `formatClock` (today's `formatDuration`: `1m 40s`) for the slab clock only. `formatProse` (today's `humanDuration`: `100 s`) for everything else: ledger, History stats and the glitch table. Stats keep their `—` for zero. |
| C2 URL encoding | `lib/api.ts` | URLs are built with `URLSearchParams` in a fixed key order (`subjectId, from, to, limit, scope, opening`), so `:` in ISO times becomes `%3A`. The server parses both forms (ASP.NET decodes query values); no wire change. |
| C2 order | Reversal ×3 | `fetchTransitionsNewestFirst` returns the API order (or `null` on failure) for the two newest-first consumers: the slab trail and the status bar seed. `fetchTransitions` returns chronological order with the `opening` rows first, which is today's History rule. The board and the story use it. |
| O1 client half | Plan 5's O1 | Taken here. `GlitchItem` moves from `glitchData.ts` to `types.ts`, and `InitialData` from `dashboard.tsx` to `types.ts`. The contracts test types `islandKeys` against `InitialData`, and the README AUDIT line for `index.island` is deleted. The `glitch[].scope` AUDIT line now names `types.ts`. |
| C3 table | `lib/verdicts.ts` | Each record holds `answer`, `slabCls`, `slabMark`, `ribbon` and `window`. The slab prose stays in `slabFor`. `site-shell.js` is plain JS with no build step, so it cannot import the table. It keeps its own map, and `siteShell.test.ts` (which already checks it against `answerClassOf` for every vocabulary state) stays as the guard. |
| Ruling 3 mark | A distinct "your resolver" mark | **New hue `resolver`**, drawn in the palette's existing blue, the `--primary` values. It is the one hue not already used for warm (out there), grey (your gear) or green (healthy). Blue against orange and blue against grey both stay apart under every common colour-vision deficiency. Tokens:<br>• light: `--resolver-glitch: #c6dcef; --resolver-degraded: #5b9bd5; --resolver-out: #0172ad`<br>• dark: `--resolver-glitch: #1f3347; --resolver-degraded: #3f8fd0; --resolver-out: #01aaff`<br>Only `out` is drawn today: DnsDown has one severity. The other two exist because `RAMP` is total over `Depth`. The shape is `bar`, since DnsDown is shared (every lookup). |
| Ruling 3 reach | Ledger, punchcard and strip | The **slab chip and the History verdict ribbon** also switch from grey-out to resolver-out, so the encoding means one thing everywhere. Today the slab and ribbon wear grey-out ("your gear"), which would make the ledger and the slab disagree. Confirmed by the owner 2026-10-30: blue everywhere. |
| Ruling 3 rank | Cell precedence | `markRank` becomes `shape*10 + depth*3 + hueRank`, with grey 2, resolver 1 and warm 0. LinkDown outranks DnsDown, which outranks a weather outage, and every existing ordering holds (out-warm 9 > degraded-grey 8). |
| Ruling 3 wording | Legend text | Ledger lead: `Name lookups failed for ${dur} — your connection stayed up.` `describeMark`: `your resolver: fully out`. Legend row: `your resolver (bar)`, with only the "fully out" cell drawn. Strip caption: add `, blue marks are your resolver`. |
| Ruling 3 server | ISP report keeps DnsDown out | Already true. `IspReportBuilder.AttributedKinds` is `["LinkDown", "LinkDegraded", "InternetWeather"]`, and `CloseSegment` adds DnsDown to `totals["DnsDown"]` with no episode (`Libraries/ConPersistence/Report/IspReportBuilder.cs`, `AttributedKinds` / `CloseSegment`). Cases 8 and 9 pin the totals but not the episode list, so Task 7 adds that assertion. No server code changes. The client never computes ISP time: `historyData.incidents` already treats DnsDown as not an outage and pins it. |
| C6 orphans | `groupByParent` ×2 | Use treeLayout's rule: orphans join the roots **in input order**. Tree's plain mode appended them at the end. The order differs only in plain mode (no anchor pool) with a target whose parent is unknown. |
| C6 chips | CSS `.statechip.<state>` | A `StateChip({ state, always })` component in `TargetRow.tsx`. Colour comes from CSS classes, not inline styles. TargetRow keeps `marginRight: 10` inline. |
| C7 insecure retry | Helper for ×2 | **Dropped.** The two flows differ by design: clone pre-checks on the client and offers "without secrets", while take-over has neither. What they share is one `code === "insecure-transport"` test, and a helper would save nothing. Only `cloneNeedsWarning`'s unused `password` parameter goes. |
| C7 site-time | One `site-time.js` | One loop, `localizeTimes(selector, attribute, options)`. **Each page keeps its own format:** the Report uses 24-hour time with seconds next to the UTC line, and Discoveries uses the browser default. Report's script moves into `@section Scripts` so it runs after the shared file. |
| C7 traceroute | Use `site-config.js` | If the targets GET fails, it now shows the error list and stops. Today it throws into the catch as `Traceroute failed: TypeError…`, or, on a non-2xx, maps over an error object. |
| Survey client notes not in the C rows | Header pip polling, trunk-name case matching | Not planned. They are not survey items. |

## Visible changes (release-note lines)

1. History stats and the dashboard glitch table write durations the way the ledger does: `100 s`, `9 min 30 s`, `2.5 h`. Before, they showed `2 min` and `100s`.
2. A DnsDown stretch appears in the dashboard ledger, the event strip and the History punchcard as a blue "your resolver" mark, with the line "Name lookups failed for … — your connection stayed up." The slab chip and the History verdict ribbon use the same blue.
3. History's subject picker has a "Resolvers" group. Resolver targets no longer appear under "Path".
4. In the plain dashboard tree (no anchor pool), a target whose parent is unknown is listed in config order instead of last.
5. On the Traceroute page, a failure to load the existing targets shows its error instead of "Traceroute failed: TypeError".

## File structure

| Path | Responsibility after this plan | Tasks |
|---|---|---|
| `ClientApp/src/lib/scope.ts` (was `lan.ts`) | Scope predicates (`isLocal`, `isResolver`, `isTreeTarget`) and the LAN summary | 1, 2 |
| `ClientApp/src/lib/format.ts` | `formatClock`, `formatProse` | 3 |
| `ClientApp/src/lib/localTime.ts` | `HOUR_MS`, `DAY_MS`, `localDayStart`, `localHourKey`, `isoMs`, `isoCompare` | 4 |
| `ClientApp/src/lib/api.ts` | Every `fetch` the islands make; never throws | 5 |
| `ClientApp/src/lib/types.ts` | Every server contract type, plus `TargetScope`, `GlitchItem` and `InitialData` | 1, 5 |
| `ClientApp/src/lib/verdicts.ts` | One record per verdict name; `answerClassOf`, `verdictLook` | 6, 7 |
| `ClientApp/src/lib/encoding.ts` | Marks, the ramps (including `resolver`) and `markRank` | 7 |
| `ClientApp/src/components/marks.tsx` | `MarkSwatch`, `MarkCell`, `HatchSwatch`, `HatchPattern`, `UNREACHABLE_LABEL`, the legend | 7, 8 |
| `ClientApp/src/components/StateRibbon.tsx` | `RibbonSvg` (shared) and `StateRibbon` | 8 |
| `ClientApp/src/lib/story.ts` | Story derivation and the `useStory` hook | 9 |
| `ClientApp/src/lib/treeLayout.ts` | Tree layout and `groupByParent` | 9 |
| `wwwroot/lib/site-time.js` | `localizeTimes` (new) | 10 |
| `wwwroot/css/site.css` | `--resolver-*` tokens; `.statechip.<state>` rules | 7, 8 |

---

## Group A: shared libraries

### Task 1: C4, `lib/scope.ts` and the tree rule

**Files:**
- Rename: `ClientApp/src/lib/lan.ts` → `ClientApp/src/lib/scope.ts`, and `lan.test.ts` → `scope.test.ts` (`git mv`)
- Modify: `ClientApp/src/lib/types.ts`, `lib/statusStore.ts`, `dashboard.tsx`, `history.tsx`, `components/LanSection.tsx`, `lib/contracts.test.ts`, `lib/statusStore.test.ts`

**Interfaces:**
- Consumes: nothing new.
- Produces:
  - `scope.ts`: `isLocal(t: { scope?: string }): boolean`, `isResolver(...)` (both unchanged), and `isTreeTarget(t: { scope?: string }): boolean`. `isTreeTarget` is true for `"Verdict"` and for a missing scope, and mirrors the server's `TargetScope.IsTree()`. `summarizeLan` and `LanSummary` are unchanged.
  - `types.ts`: `export type TargetScope = "Verdict" | "Local" | "Resolver"`. `TargetStatus.scope?: TargetScope`; `TransitionEvent.scope?: TargetScope | "Unknown"`.
  - `isInternetTarget` no longer exists.

- [ ] **Step 1: Pin the status store's rule for resolvers** (add to `statusStore.test.ts`; it passes today)

```ts
it("a resolver's live transition is internet news: it reaches the status line", () => {
  const store = createStatusStore();
  store.applySnapshot({ verdict: null, pools: [], targets: [{ ...target("dns"), scope: "Resolver" }] });
  store.applyTransition({ ...transition("dns") });
  expect(store.getState().latestTransition?.subjectId).toBe("dns");
  expect(store.getState().recentTransitions).toHaveLength(1);
});
```
Use the file's existing target and transition builders; adapt the helper names to what `statusStore.test.ts` already defines.

- [ ] **Step 2: Write the `isTreeTarget` test** (in the renamed `scope.test.ts`; replaces the `isInternetTarget` cases)

```ts
describe("isTreeTarget", () => {
  it("is the server's IsTree(): Verdict and a missing scope only", () => {
    expect(isTreeTarget(t({ scope: "Verdict" }))).toBe(true);
    expect(isTreeTarget(t({}))).toBe(true);
    expect(isTreeTarget(t({ scope: "Local" }))).toBe(false);
    expect(isTreeTarget(t({ scope: "Resolver" }))).toBe(false);
  });
});
```
Add to `contracts.test.ts`:
```ts
const targetScopes = ["Local", "Resolver", "Verdict"] as const satisfies readonly TargetScope[];
it("TargetScope is the server's vocabulary", () => {
  expect([...targetScopes].sort()).toEqual([...vocabulary("TargetScope")].sort());
});
```
Use the file's existing fixture reader for `vocabulary.json`.

- [ ] **Step 3: Run them**

Run: `cd Programs/ConWatch/ClientApp; npx vitest run src/lib/scope.test.ts src/lib/contracts.test.ts`
Expected: FAIL. `isTreeTarget` and `TargetScope` are not exported yet.

- [ ] **Step 4: Implement**
  - In `scope.ts`, replace `isInternetTarget` with `isTreeTarget`, and update the file comment.
  - Add `TargetScope` to `types.ts` and use it in `TargetStatus`, `TransitionEvent` and History's local `Subject.scope`.
  - `statusStore.applyTransition` keeps a transition out when `isLocal(target)`; its logic is unchanged.
  - `dashboard.tsx:126` and `history.tsx` (board subjects) use `isTreeTarget`.
  - `LanSection` imports from `./scope`.

- [ ] **Step 5: Run the suite**

Run: `npx vitest run`
Expected: PASS. Then run `dotnet build ConnectionWatcher.sln -c Debug` and expect no new warnings.

- [ ] **Step 6: Commit**

```bash
git add -A Programs/ConWatch/ClientApp/src
git commit -m "refactor(client): lib/scope.ts with the tree rule (isTreeTarget = Scope Verdict); one TargetScope type"
```

---

### Task 2: Ruling 4, History's Path picker uses the tree rule and gets a Resolvers group

**Files:**
- Modify: `ClientApp/src/lib/historyData.ts` (`HistoryScope`, `subjectsForScope`), `ClientApp/src/history.tsx` (`SCOPES`), `ClientApp/src/lib/historyData.test.ts`

**Interfaces:**
- Consumes: `isLocal`, `isResolver` and `isTreeTarget` from Task 1.
- Produces: `export type HistoryScope = "path" | "pools" | "resolvers" | "local"`. `subjectsForScope` sorts subjects into groups as follows:
  - LAN: Local subjects.
  - Resolvers: Resolver subjects.
  - Pools: pools that are neither.
  - Path: everything else that is not a pool, i.e. the verdict and the tree targets.

- [ ] **Step 1: Extend the tests** (`historyData.test.ts`, `describe("subjectsForScope")`)

Add `{ id: "dns", name: "Router DNS", kind: "target", scope: "Resolver" }` to the `subjects` fixture, then:
```ts
it("path keeps the verdict and tree targets, never resolvers", () => {
  expect(subjectsForScope(subjects, "path").map((s) => s.id)).toEqual(["r", "v"]);
});
it("resolvers keeps only Resolver targets", () => {
  expect(subjectsForScope(subjects, "resolvers").map((s) => s.id)).toEqual(["dns"]);
});
```
The existing `partitions every subject exactly once` test must also loop over `"resolvers"`. Add it to its scope list.

- [ ] **Step 2: Run them**

Run: `npx vitest run src/lib/historyData.test.ts`
Expected: FAIL. `path` includes `dns`, and `"resolvers"` is not a scope.

- [ ] **Step 3: Implement**
  - `subjectsForScope` checks `isLocal`, then `isResolver`, then `kind === "pool"`; everything else is Path.
  - `history.tsx` `SCOPES` becomes `[["path","Path"],["pools","Pools"],["resolvers","Resolvers"],["local","LAN"]]`.

- [ ] **Step 4: Run the suite and build** (same commands as Task 1). Expected: PASS, no new warnings.

- [ ] **Step 5: Browser check**
  1. Start the dev server and open `/History`.
  2. With at least one Resolver target configured (the Name resolution page creates one), the picker shows a "Resolvers" segment holding only resolver targets.
  3. "Path" no longer lists them.
  4. Choosing a resolver draws its ribbon and charts.
  5. `read_console_messages` with `onlyErrors` returns nothing.

- [ ] **Step 6: Commit**

```bash
git commit -am "feat(history): resolvers get their own subject group; Path follows the tree rule (ruling 4)"
```

---

### Task 3: C1, `lib/format.ts`

**Files:**
- Create: `ClientApp/src/lib/format.ts`, `ClientApp/src/lib/format.test.ts`, `ClientApp/src/components/StatsRow.test.tsx`
- Modify: `lib/verdictClock.ts` (delete `formatDuration`), `lib/incidents.ts` (delete `humanDuration`), `components/VerdictBanner.tsx`, `components/StatsRow.tsx`, `components/GlitchSection.tsx`, `lib/verdictClock.test.ts`, `lib/incidents.test.ts`

**Interfaces:**
- Produces:
  - `formatClock(ms: number): string`, with today's `formatDuration` body.
  - `formatProse(ms: number): string`, with today's `humanDuration` body.

- [ ] **Step 1: Move the existing tests and add the cross-style pin**

Move `describe("formatDuration")` from `verdictClock.test.ts` to `format.test.ts` as `describe("formatClock")`. Move `describe("humanDuration")` from `incidents.test.ts` as `describe("formatProse")`. Rename the calls and keep their assertions. Add:
```ts
it("the same 100 s reads one way per style", () => {
  expect(formatClock(100_000)).toBe("1m 40s");
  expect(formatProse(100_000)).toBe("100 s");
});
```
`StatsRow.test.tsx` (render with `react-dom/client` into a happy-dom div, the way `TargetRow.test.tsx` does):
```ts
it("writes durations in prose and a dash for none", () => {
  // longestOutageMs 100_000, unreachableMs 0, outageCount 1
  expect(text).toContain("longest 100 s");
  expect(text).toContain("—unreachable — no data");
});
```

- [ ] **Step 2: Run them.** `npx vitest run src/lib/format.test.ts src/components/StatsRow.test.tsx`. Expected: FAIL. `format.ts` does not exist, and StatsRow says `2 min`.

- [ ] **Step 3: Implement**
  - Create `format.ts` with both functions.
  - `VerdictBanner` uses `formatClock`.
  - `buildLedger` uses `formatProse`.
  - `StatsRow.duration` becomes `ms <= 0 ? "—" : formatProse(ms)`.
  - The glitch table's duration cell becomes `formatProse(isoMs(g.endUtc) - isoMs(g.startUtc))`. Use `Date.parse` until Task 4 lands; Task 4 swaps it.

- [ ] **Step 4: Run the suite and build.** Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A Programs/ConWatch/ClientApp/src
git commit -m "refactor(client): one duration formatter per style (clock, prose); stats and glitch table use prose"
```

---

### Task 4: C5, `lib/localTime.ts`

**Files:**
- Create: `ClientApp/src/lib/localTime.ts`, `ClientApp/src/lib/localTime.test.ts`, `ClientApp/src/components/Punchcard.test.tsx`
- Modify:
  - `lib/story.ts` (the `from` day start; the anchor-Down window test)
  - `lib/incidents.ts` (`punchcardCells`' oldest and `cellKey`)
  - `components/Punchcard.tsx` (`dayDate`, the inline key)
  - `lib/glitchData.ts` (the cutoff and the hour key)
  - `dashboard.tsx` (the 24 h range; the trail dedupe)
  - `history.tsx` (`RANGES` plus the range buttons; the board range; `periodStart >= resetAt`)
  - `lib/historyData.ts` (`ribbonSegments`' `t.timestamp > cursor`)

**Interfaces:**
- Produces:
  - `HOUR_MS = 3_600_000` and `DAY_MS = 24 * HOUR_MS`.
  - `localDayStart(now: Date, daysBack = 0): Date`: local midnight, `daysBack` days before `now`'s local day.
  - `localHourKey(at: Date): string`: `"yyyy-M-d-H"` in local time, today's punchcard key exactly.
  - `isoMs(iso: string): number`: `Date.parse`.
  - `isoCompare(a: string, b: string): number`: `isoMs(a) - isoMs(b)`.

- [ ] **Step 1: Write the tests**

```ts
describe("localTime", () => {
  it("keys an hour the way the punchcard always has", () => {
    expect(localHourKey(new Date(2026, 9, 5, 7, 30))).toBe("2026-10-5-7");
  });
  it("starts a local day daysBack days ago", () => {
    expect(localDayStart(new Date(2026, 9, 5, 7, 30), 2)).toEqual(new Date(2026, 9, 3));
    expect(localDayStart(new Date(2026, 9, 5, 7, 30))).toEqual(new Date(2026, 9, 5));
  });
  it("isoCompare treats +00:00 and Z, 1 and 7 fraction digits, as the same clock", () => {
    expect(isoCompare("2026-10-29T10:00:00.9+00:00", "2026-10-29T10:00:00.900Z")).toBe(0);
    expect(isoCompare("2026-10-29T10:00:00.1234567+00:00", "2026-10-29T10:00:00.123Z")).toBe(0);
    expect(isoCompare("2026-10-29T10:00:00+00:00", "2026-10-29T10:00:00.500Z")).toBeLessThan(0);
  });
});
```
`Punchcard.test.tsx` pins that the bucket key and the lookup key agree:
```ts
it("Punchcard draws the cell punchcardCells filled", () => {
  const now = new Date(2026, 9, 5, 12, 0);
  const g = { id: 1, targetId: "t", targetName: "Site", class: "Target" as const,
    startUtc: new Date(2026, 9, 5, 0, 10).toISOString(), endUtc: new Date(2026, 9, 5, 0, 11).toISOString(),
    lostCount: 2, sampleCount: 10, detail: null };
  const cells = punchcardCells([], new Map(), [g], new Set(), 30, now);
  // render <Punchcard cells={cells} days={30} now={now} />
  expect(container.querySelectorAll("[title]")).toHaveLength(1);
  expect(container.querySelector("[title]")!.getAttribute("title")).toMatch(/^10\/5 0:00 — one site: glitch/);
});
```
Midnight local is the boundary that a day-start or key mismatch would break.

- [ ] **Step 2: Run them.** Expected: `localTime.test.ts` FAILs (no module). `Punchcard.test.tsx` PASSes; it is a pin, which is fine.

- [ ] **Step 3: Implement and replace the copies**
  - Every `24 * 3_600_000` becomes `DAY_MS`.
  - Every "local midnight N days back" becomes `localDayStart`.
  - Both punchcard keys and the glitch hour key become `localHourKey`. `chronicTargets` only counts distinct keys, so its old 0-based month made no difference.
  - Every ISO string `<`/`>`/`===` becomes `isoCompare`/`isoMs`.
  - History's `RANGES` becomes one array `[{ key: "24h", label: "24 h", ms: DAY_MS }, { key: "7d", label: "7 days", ms: 7 * DAY_MS }, { key: "30d", label: "30 days", ms: 30 * DAY_MS }]`. Both the lookup and the buttons read it.

- [ ] **Step 4: Run the suite and build.** Expected: PASS, including the existing `incidents`, `story`, `historyData`, `glitchData` and `verdictClock` tests unchanged.

- [ ] **Step 5: Commit**

```bash
git add -A Programs/ConWatch/ClientApp/src
git commit -m "refactor(client): lib/localTime.ts; compare ISO instants by value, not by string"
```

---

### Task 5: C2 and O1 (client half), `lib/api.ts` and contract types in `types.ts`

**Files:**
- Create: `ClientApp/src/lib/api.ts`, `ClientApp/src/lib/api.test.ts`
- Modify:
  - `lib/hub.ts` (its `fetchSnapshot` moves out)
  - `lib/story.ts`
  - `components/GlitchSection.tsx`
  - `components/RibbonBoard.tsx`
  - `components/NotesPanel.tsx` (its `acknowledgeWarning` moves out)
  - `dashboard.tsx`
  - `history.tsx` (its `fetchRollups`/`fetchTransitions` move out)
  - `lib/glitchData.ts`
  - `lib/types.ts`
  - `lib/contracts.test.ts`
  - `Tests/contracts/README.md`

**Interfaces:**
- Consumes: `isoMs` (Task 4).
- Produces (`api.ts`; no function here ever throws):
  ```ts
  export interface TransitionQuery { subjectId?: string; from?: Date; to?: Date; limit: number;
    scope?: "all" | "internet" | "local"; opening?: boolean }
  export function transitionsUrl(q: TransitionQuery): string
  export function fetchTransitionsNewestFirst(q: TransitionQuery): Promise<TransitionEvent[] | null>
  export function fetchTransitions(q: TransitionQuery): Promise<TransitionEvent[]>   // chronological, opening rows first
  export function fetchGlitches(q: { from: Date; to?: Date; limit: number; scope?: "all" | "internet" | "local" }): Promise<GlitchItem[]>
  export function fetchRollups(subjectId: string, from: Date, to: Date): Promise<RollupPoint[]>
  export function fetchSnapshot(): Promise<Snapshot | null>
  export function acknowledgeWarning(key: string): Promise<boolean>
  ```
  A private `getJson<T>(url, init?)` returns `null` on a rejection, a non-2xx response or a bad body.
- `types.ts` gains `GlitchItem` (moved verbatim from `glitchData.ts`) and `InitialData` (moved from `dashboard.tsx`, exported).

- [ ] **Step 1: Write the tests** (`api.test.ts`, `vi.stubGlobal("fetch", …)` as `story.test.ts` does)

```ts
it("builds the transitions URL in a fixed key order", () => {
  expect(transitionsUrl({ subjectId: "s", from: new Date(Date.UTC(2026, 9, 29, 10)), limit: 1000, opening: true }))
    .toBe("/api/v1/transitions?subjectId=s&from=2026-10-29T10%3A00%3A00.000Z&limit=1000&opening=true");
  expect(transitionsUrl({ limit: 50, scope: "internet" })).toBe("/api/v1/transitions?limit=50&scope=internet");
});
it("fetchTransitions puts opening rows first, then in-range rows oldest first", async () => {
  stub([{ id: 2, opening: true }, { id: 1, opening: true }, { id: 5 }, { id: 4 }]);
  expect((await fetchTransitions({ limit: 10 })).map((t) => t.id)).toEqual([1, 2, 4, 5]);
});
it("fetchTransitions resolves [] when fetch rejects", async () => {
  vi.stubGlobal("fetch", vi.fn(async () => { throw new TypeError("network"); }));
  await expect(fetchTransitions({ limit: 10 })).resolves.toEqual([]);
  await expect(fetchTransitionsNewestFirst({ limit: 10 })).resolves.toBeNull();
  await expect(fetchGlitches({ from: new Date(), limit: 10 })).resolves.toEqual([]);
  await expect(fetchRollups("s", new Date(), new Date())).resolves.toEqual([]);
  await expect(fetchSnapshot()).resolves.toBeNull();
  await expect(acknowledgeWarning("k")).resolves.toBe(false);
});
it("a 500 is the same as no data", async () => { /* ok: false → [] / null */ });
```
In `contracts.test.ts`, type `islandKeys` with `satisfies readonly (keyof InitialData)[]`, add `true satisfies Covers<InitialData, typeof islandKeys>` to `covered`, and import `GlitchItem` from `./types`.

- [ ] **Step 2: Run them.** Expected: FAIL (no `api.ts`; `InitialData` is not exported).

- [ ] **Step 3: Implement and switch every caller**
  - Each `fetch(` in `src/` except `hub.ts`'s SignalR goes through `api.ts`.
  - `story.ts` uses `fetchTransitions` (chronological) and `fetchGlitches`.
  - The board uses `fetchTransitions` without `opening`, which is today's call.
  - The dashboard trail and the status-bar seed use `fetchTransitionsNewestFirst`.
  - History's `Promise.all` now always settles.
  - Delete the README line `AUDIT: index.island…`, and repoint the `glitch[].scope` line from `glitchData.ts` to `types.ts`.

- [ ] **Step 4: Run the suite and build.** Expected: PASS. `rg -n "fetch\(" Programs/ConWatch/ClientApp/src --glob '!*.test.*'` lists only `lib/api.ts`.

- [ ] **Step 5: Commit**

```bash
git add -A Programs/ConWatch/ClientApp/src Tests/contracts/README.md
git commit -m "refactor(client): lib/api.ts for every island fetch (never throws); contract types live in types.ts"
```

---

## Group B: verdicts and the resolver mark

### Task 6: C3, one verdict table (behavior-preserving)

**Files:**
- Create: `ClientApp/src/lib/verdicts.ts`, `ClientApp/src/lib/verdicts.test.ts`
- Modify:
  - `lib/verdictClock.ts` (`answerClassOf` moves out)
  - `lib/encoding.ts` (`verdictFill` reads the table)
  - `lib/incidents.ts` (`ATTRIBUTED` and `windowMark` read the table)
  - `components/VerdictBanner.tsx` (`slabFor` takes `cls`, `icon` and `mark` from the table)
  - Importers of `answerClassOf`: `siteShell.test.ts`, `contracts.test.ts`, `VerdictBanner.test.ts`

**Interfaces:**
- Produces:
  ```ts
  export type AnswerClass = "up" | "struggling" | "down" | "checking";      // moved from verdictClock
  export type VerdictName = "Healthy" | "SiteIncident" | "InternetWeather" | "LinkDegraded" | "LinkDown" | "DnsDown";
  export interface VerdictLook {
    answer: Exclude<AnswerClass, "checking">;
    slabCls: "healthy" | "linkdegraded" | "linkdown";
    slabMark: Mark | null;
    ribbon: string;                                                  // verdictFill's fill
    window: ((anchorDowns: number, threshold: number) => Mark) | null; // null = no ledger/strip/punchcard window
  }
  export const VERDICTS: Record<VerdictName, VerdictLook>;
  export function verdictLook(name: string | null | undefined): VerdictLook | null;
  export function answerClassOf(name: string | null | undefined): AnswerClass; // verdictLook(name)?.answer ?? "checking"
  ```
- `VerdictWindow.kind` becomes `VerdictName`. `windowMark(w, downs, threshold)` keeps its signature and returns `VERDICTS[w.kind].window!(downs, threshold)`.
- In this task `DnsDown.window` stays `null`, and its `slabMark` and `ribbon` stay grey-out (today's values).

- [ ] **Step 1: Write the coverage test**

```ts
it("covers every server VerdictState except Unknown", () => {
  expect(Object.keys(VERDICTS).sort()).toEqual(vocabulary.VerdictState.filter((s) => s !== "Unknown").sort());
});
it("an unknown name has no look and reads as checking", () => {
  expect(verdictLook("Unknown")).toBeNull();
  expect(answerClassOf(undefined)).toBe("checking");
});
```
Read `vocabulary.json` the way `siteShell.test.ts` does. These existing tests pin the behavior: `slabFor` (VerdictBanner.test), `verdictFill`/`windowMark`/`verdictWindows` (incidents.test), the `site-shell.js` pip (siteShell.test), and `answerClock` (verdictClock.test).

- [ ] **Step 2: Run them.** Expected: FAIL (no module).

- [ ] **Step 3: Implement.** The values are today's:
  - `answer` and `slabCls`: from `answerClassOf` and `slabFor`.
  - `slabMark`: from `slabFor`.
  - `ribbon`: from `verdictFill`.
  - `window`:
    - LinkDown is grey-out.
    - LinkDegraded is grey-degraded.
    - InternetWeather is warm, `out` when `downs >= threshold`, else `degraded`.
    - Every other verdict is `null`.
  - `slabFor` keeps its headlines and caveats.
  - `verdictWindows` opens a window when `verdictLook(t.verdict)?.window` is non-null.

- [ ] **Step 4: Run the suite and build.** Expected: PASS, with no existing assertion edited.

- [ ] **Step 5: Commit**

```bash
git add -A Programs/ConWatch/ClientApp/src
git commit -m "refactor(client): lib/verdicts.ts, one table for answer class, slab, ribbon fill and incident window"
```

---

### Task 7: Ruling 3, DnsDown gets a "your resolver" mark

**Files:**
- Modify:
  - `ClientApp/src/lib/encoding.ts` (`Hue`, `RAMP`, `markRank`)
  - `lib/verdicts.ts` (DnsDown's `window`, `slabMark` and `ribbon`)
  - `lib/incidents.ts` (the `buildLedger` lead)
  - `components/marks.tsx` (`describeMark` and `EncodingLegend`)
  - `components/EventStrip.tsx` (caption)
  - `wwwroot/css/site.css` (tokens in `:root` **and in both dark blocks**; plan 5 merges the two dark blocks)
- Test:
  - `lib/incidents.test.ts`, `lib/story.test.ts`, `components/VerdictBanner.test.ts`
  - `Tests/ConPersistence.Tests/IspReportBuilderTests.cs`

**Interfaces:**
- Consumes: `VERDICTS` (Task 6) and `formatProse` (Task 3).
- Produces:
  - `type Hue = "warm" | "grey" | "resolver"` and `RAMP.resolver = { glitch: "var(--resolver-glitch)", degraded: "var(--resolver-degraded)", out: "var(--resolver-out)" }`.
  - `export const RESOLVER_OUT: Mark = { shape: "bar", hue: "resolver", depth: "out" }` in `encoding.ts`: the DnsDown mark everywhere.

- [ ] **Step 1: Write the failing client tests**

```ts
// incidents.test.ts
it("a DnsDown verdict opens a resolver window that closes at the next verdict", () => {
  const w = verdictWindows([vt("2026-10-29T10:00:00+00:00", "DnsDown"), vt("2026-10-29T10:20:00+00:00", "Healthy")], END);
  expect(w).toEqual([{ kind: "DnsDown", from: "2026-10-29T10:00:00+00:00", to: "2026-10-29T10:20:00+00:00", detail: null }]);
  expect(windowMark(w[0], 5, 2)).toEqual({ shape: "bar", hue: "resolver", depth: "out" });
});
it("ranks link-out above resolver-out above weather-out above any degradation", () => {
  const r = (hue: Hue, depth: Depth) => markRank({ shape: "bar", hue, depth });
  expect(r("grey", "out")).toBeGreaterThan(r("resolver", "out"));
  expect(r("resolver", "out")).toBeGreaterThan(r("warm", "out"));
  expect(r("warm", "out")).toBeGreaterThan(r("grey", "degraded"));
});
it("verdictFill draws DnsDown in the resolver ramp", () => {
  expect(verdictFill("DnsDown", "Down")).toBe("var(--resolver-out)");
});
// story.test.ts
it("a DnsDown stretch is a resolver ledger line, never link time", async () => {
  // verdict rows: Healthy -> DnsDown at t0, DnsDown -> Healthy at t0+20 min (newest first)
  const story = await fetchStory(topology, 2); // 2 days: t0 = now - 1 h never falls before the range at midnight
  expect(story.ledger).toHaveLength(1);
  expect(story.ledger[0].lead).toBe("Name lookups failed for 20 min — your connection stayed up.");
  expect(story.ledger[0].mark).toEqual({ shape: "bar", hue: "resolver", depth: "out" });
});
// VerdictBanner.test.ts
it("DnsDown's chip is the resolver mark", () => {
  expect(slabFor({ state: "DnsDown", detail: null, operator: null }).mark)
    .toEqual({ shape: "bar", hue: "resolver", depth: "out" });
});
```
Also update the existing `incidents.test.ts` case `DnsDown is your gear (the resolver) at the out depth…`. It now expects `RAMP.resolver.out` and keeps its "never the warm internet-outage fill" assertion.

- [ ] **Step 2: Add the server pin.** In `IspReportBuilderTests.Incidents_match_the_history_page_cases`, after the `DnsDownTime` assertion, add:
```csharp
Assert.DoesNotContain(report.Episodes, e => e.Kind == "DnsDown");
```

- [ ] **Step 3: Run them**

Run: `npx vitest run` and `dotnet test Tests/ConPersistence.Tests -c Debug --filter "FullyQualifiedName~IspReportBuilderTests"`
Expected: the new client tests FAIL. The server theory PASSes; it is the pin.

- [ ] **Step 4: Implement**
  - `encoding.ts`:
    - Add the `resolver` hue to `Hue` and `RAMP`.
    - `markRank` becomes `shapeRank + depthRank * 3 + { warm: 0, resolver: 1, grey: 2 }[mark.hue]`.
    - `markCss`'s ambiguous branch stays grey/warm only.
  - `VERDICTS.DnsDown`: `window: () => RESOLVER_OUT`, `slabMark: RESOLVER_OUT`, `ribbon: RAMP.resolver.out`.
  - `buildLedger` lead for `kind === "DnsDown"`: `` `Name lookups failed for ${dur} — your connection stayed up.` ``
  - `describeMark`: the resolver hue reads `your resolver`.
  - `EncodingLegend`: a fourth row, `your resolver (bar)`, with empty glitch and degraded cells and a resolver-out swatch.
  - `EventStrip` caption: `…grey marks are your own gear, blue marks are your resolver`.
  - `site.css`: the six tokens from Decisions, the light values in `:root` and the dark values in both dark blocks.

- [ ] **Step 5: Run the suite and build.** Expected: PASS.

- [ ] **Step 6: Browser check**
  1. Start the dev server.
  2. In the browser console, run the `fetchStory` derivation against a stubbed DnsDown trail, or point at a dev database that has a DnsDown stretch.
  3. Check the dashboard ledger line and mark, an event-strip bin painted blue, the punchcard legend's new row, and the History verdict ribbon's DnsDown segment.
  4. Check in both light and dark (`resize_window` with `colorScheme`).
  5. Take a screenshot for the user.
  6. `read_console_messages` with `onlyErrors` returns nothing.

- [ ] **Step 7: Commit**

```bash
git add -A Programs/ConWatch/ClientApp/src Programs/ConWatch/wwwroot/css/site.css Tests/ConPersistence.Tests/IspReportBuilderTests.cs
git commit -m "feat(dashboard): DnsDown gets its own 'your resolver' mark in the ledger, strip and punchcard (ruling 3)"
```

---

## Group C: drawing and React plumbing

### Task 8: C6, shared ribbon, hatch, mark cell and state chip

**Files:**
- Create: `ClientApp/src/components/StateRibbon.test.tsx`, `ClientApp/src/components/RibbonBoard.test.tsx`
- Modify:
  - `components/StateRibbon.tsx`, `components/RibbonBoard.tsx`
  - `components/marks.tsx`, `components/EventStrip.tsx`, `components/Punchcard.tsx`
  - `components/TargetRow.tsx`, `components/NameResolutionSection.tsx`
  - `wwwroot/css/site.css`
  - `components/TargetRow.test.tsx`, `components/NameResolutionSection.test.tsx`

**Interfaces:**
- Produces:
  - `marks.tsx`:
    - `export const UNREACHABLE_LABEL = "Unreachable behind a down parent (no data, no blame)"`. The legends use `UNREACHABLE_LABEL.toLowerCase()`.
    - `export function HatchPattern({ id }: { id: string }): JSX.Element`, the `<pattern>` element.
    - `export function MarkCell({ mark, background, size, title, style }: { mark: Mark | null; background: string; size: number; title?: string; style?: React.CSSProperties })`. It draws a dot mark centered on `background`, and any other mark as the cell fill.
  - `StateRibbon.tsx`:
    - `export function RibbonSvg({ segments, rangeStart, rangeEnd, fillFor, titleFor, dimBefore, hatchId, className, height }: {...})`.
    - It draws one `<rect>` per visible segment, using `fill === "hatch"` → `url(#hatchId)`. A segment that starts before `dimBefore` is drawn at opacity 0.35.
  - `TargetRow.tsx`: `export function StateChip({ state, always = false, style }: {...})`.
    - It returns null when `!always` and `state` is not one of Faltering, Recovering, Down, Unreachable, Starting or Initializing.
    - Otherwise it renders `<span className={`statechip ${state.toLowerCase()}`}>`.
  - `site.css`: `.statechip.faltering, .statechip.recovering` get `--cw-warn`; `.down` gets `--cw-down`; `.unreachable` gets `--cw-unreach`; `.starting, .initializing` get `--cw-quiet`. Each sets `color: var(x); background: color-mix(in srgb, var(x) 14%, transparent)`, today's inline pair.

- [ ] **Step 1: Pin today's drawing**

`StateRibbon.test.tsx`: render `<StateRibbon>` with three fixed segments (Responding, Unreachable, Down), a `resetAt` inside the first segment, and `kind="target" role="trunk"`. Assert each `rect`'s `x`, `width`, `fill` and `opacity`, and its `<title>` first line:
```ts
expect(rects.map((r) => r.getAttribute("fill"))).toEqual(["var(--healthy)", "url(#cw-hatch-timeline)", "var(--grey-out)"]);
expect(rects[0].getAttribute("opacity")).toBe("0.35");
expect(titles[1]).toMatch(/^Unreachable behind a down parent \(no data, no blame\): /);
```
`RibbonBoard.test.tsx`: stub `fetch` (via `api.ts`) with one subject's rows. `await vi.waitFor(() => expect(container.querySelectorAll("rect").length).toBeGreaterThan(0))`. Then assert the fills, the `cw-hatch` pattern id, and that the title starts with the subject's name.

Extend `TargetRow.test.tsx` and `NameResolutionSection.test.tsx`:
- A Down row has an element with classes `statechip down`.
- A Responding resolver still shows a chip reading `Responding`.

- [ ] **Step 2: Run them.** Expected: the ribbon pins PASS. The chip-class assertions FAIL, because colours are inline today.

- [ ] **Step 3: Implement**
  - `StateRibbon` and the board's `Row` both render `RibbonSvg`. Each keeps its own `fillFor` and `titleFor`, so today's two tooltip formats stay as they are; only the drawing is shared. The board passes no `dimBefore`.
  - Both use `HatchPattern` (ids `cw-hatch-timeline` and `cw-hatch`) and `UNREACHABLE_LABEL`.
  - `EventStrip` and `Punchcard` draw cells through `MarkCell`.
  - Delete both `CHIP_COLOR` maps. `TargetRow` uses `<StateChip state always={critical} style={{ marginRight: 10 }} />` when the chip is shown. `NameResolutionSection` uses `<StateChip state always />`.

- [ ] **Step 4: Run the suite and build.** Expected: PASS.

- [ ] **Step 5: Browser check**
  1. On the dashboard, check the tree rows' chips and the Name resolution chips.
  2. On `/History`, check the state ribbon (hatching, dimmed pre-reset) and the board.
  3. Compare against screenshots taken before Step 3. The pixels should match.
  4. Check light and dark.
  5. No console errors.

- [ ] **Step 6: Commit**

```bash
git add -A Programs/ConWatch/ClientApp/src Programs/ConWatch/wwwroot/css/site.css
git commit -m "refactor(client): one ribbon SVG, hatch pattern, mark cell and state chip"
```

---

### Task 9: C6, `useStory`, topology parsed once, `groupByParent`

**Files:**
- Modify: `ClientApp/src/lib/story.ts`, `dashboard.tsx` (`RecentEvents`), `history.tsx` (`RecurrenceSection`, `LedgerSection`, boot), `lib/treeLayout.ts`, `components/Tree.tsx`, `lib/treeLayout.test.ts`

**Interfaces:**
- Produces:
  - `story.ts`: `export function useStory(topology: StoryTopology, days: number, refreshKey?: number): Story | null`. It fetches on mount and again whenever `refreshKey` changes, and it ignores a result that arrives after unmount.
  - `treeLayout.ts`: `export function groupByParent(targets: TargetStatus[], parents: Record<string, string | null>): Map<string | null, TargetStatus[]>`. A target whose parent is not among `targets` becomes a root, in input order.
- In `history.tsx`, `#history-topology` is parsed once at boot, beside `#history-data`, and passed down as a `topology` prop.

- [ ] **Step 1: Write the test**

```ts
describe("groupByParent", () => {
  it("adopts orphans as roots in input order", () => {
    const a = tgt("a"), b = tgt("b"), c = tgt("c");
    const g = groupByParent([a, b, c], { a: "missing", b: null, c: "b" });
    expect(g.get(null)!.map((t) => t.id)).toEqual(["a", "b"]);
    expect(g.get("b")!.map((t) => t.id)).toEqual(["c"]);
  });
});
```

- [ ] **Step 2: Run it.** Expected: FAIL (not exported).

- [ ] **Step 3: Implement**
  - `layoutTree` and `Tree`'s plain branch both call `groupByParent`.
  - The three story effects become `useStory(topology, STORY_DAYS)`, `useStory(topology, OVERVIEW_DAYS, to.getTime())` and `useStory(topology, LEDGER_DAYS)`.

- [ ] **Step 4: Run the suite and build.** Expected: PASS.

- [ ] **Step 5: Browser check.**
  - The dashboard ledger, and History's punchcard and ledger, load as before.
  - Switching History's range refreshes only the punchcard, as today.
  - No console errors.

- [ ] **Step 6: Commit**

```bash
git commit -am "refactor(client): useStory hook, History topology parsed once, one groupByParent"
```

---

## Group D: wwwroot pages

### Task 10: C7 (rest), traceroute and retention reuse `site-config.js`; one `site-time.js`

**Files:**
- Create: `wwwroot/lib/site-time.js`, `ClientApp/src/lib/siteTime.test.ts`
- Modify:
  - `wwwroot/lib/site-traceroute.js`
  - `Pages/Traceroute.cshtml` (load `site-config.js` first)
  - `wwwroot/lib/site-data.js`
  - `wwwroot/lib/site-setup.js` (`cloneNeedsWarning(url)`)
  - `Pages/Report.cshtml` (the script moves into `@section Scripts` and loads `site-time.js`)
  - `Pages/Config/Discoveries.cshtml` (loads `site-time.js`; drops its inline loop)

**Interfaces:**
- Consumes: from plan 1, `apiFetch` (never throws), `withBusy(el, fn)`, `showErrors` and `clearErrors`.
- Produces: `site-time.js` defines the global `localizeTimes(selector, attribute, options)`. For each element matching `selector`, it reads `el.getAttribute(attribute)`. If that parses to a valid date, it sets `textContent = date.toLocaleString(undefined, options)`. It returns the number of elements localized.

- [ ] **Step 1: Write the test** (`siteTime.test.ts`; load the file text with `new Function(source)()` the way `siteShell.test.ts` does)

```ts
it("localizes each valid instant and leaves a bad one alone", () => {
  document.body.innerHTML = `<small data-utc="2026-10-29T10:00:00.0000000Z">u1</small><small data-utc="nope">u2</small>`;
  const n = (globalThis as any).localizeTimes("[data-utc]", "data-utc");
  expect(n).toBe(1);
  expect(document.body.children[0].textContent).toBe(new Date("2026-10-29T10:00:00Z").toLocaleString());
  expect(document.body.children[1].textContent).toBe("u2");
});
it("passes the page's format options through", () => {
  // options { hour12: false, hour: "2-digit", minute: "2-digit" } -> same string as toLocaleString with them
});
```
`new Function` scopes a `function` declaration to that call. Have the file assign `globalThis.localizeTimes = localizeTimes;` as its last line. Make the test read it from `globalThis`.

- [ ] **Step 2: Run it.** Expected: FAIL (no file).

- [ ] **Step 3: Implement**
  - **`site-time.js`:** add the file as specified.
  - **Discoveries:** `localizeTimes("[data-utc]", "data-utc")`.
  - **Report:** the script moves into `@section Scripts`. It keeps its zone `try`, calls `localizeTimes("time.cw-lt", "datetime", { year: "numeric", month: "2-digit", day: "2-digit", hour: "2-digit", minute: "2-digit", second: "2-digit", hour12: false })`, and sets `tz-name`/`tz-detail` only when that returns more than 0.
  - **`site-traceroute.js`:**
    - `const targets = await apiFetch("GET", "/api/v1/targets")`. If it is not ok, run `showErrors(errors, targets)` and return.
    - The POST goes through `apiFetch` too.
    - A failed run uses `showErrors` (it reads `body.errors` the same way).
    - The busy state goes through `withBusy(button, …)`, and the page keeps its own `button.disabled` toggle in the same `finally`.
  - **`site-data.js`:** the submit handler becomes `wireSettingsForm("retention-form", "retention-errors");`. The form has only number inputs, so the checkbox branch never runs.
  - **`site-setup.js`:** `cloneNeedsWarning(url)`; its caller stops passing `password`.

- [ ] **Step 4: Run the suite and build.** Expected: PASS.

- [ ] **Step 5: Browser check**
  1. **`/Traceroute`:** run a trace to the router address. The hop table fills, the button's busy state clears, and there are no console errors.
  2. **`/Data`:** submit the retention form with an out-of-range value (`0`). The error list shows the server's message. A valid value saves and reloads.
  3. **`/Report`:** times show as local 24-hour time with seconds over the UTC line, and the zone name is filled in.
  4. **`/Config/Discoveries`:** the first-seen and last-seen times are local.
  5. **Error check:** `read_console_messages` with `onlyErrors` returns nothing on all four pages.

- [ ] **Step 6: Commit**

```bash
git add -A Programs/ConWatch/wwwroot Programs/ConWatch/Pages Programs/ConWatch/ClientApp/src/lib/siteTime.test.ts
git commit -m "refactor(web): traceroute and retention reuse site-config.js; one site-time.js for local times"
```

---

### Task 11: Whole-branch check

- [ ] **Step 1:** `dotnet build ConnectionWatcher.sln -c Debug`. Expected: no warnings beyond the four listed under Global Constraints.
- [ ] **Step 2:** `dotnet test ConnectionWatcher.sln -c Debug`. Expected: 0 failed.
- [ ] **Step 3:** `cd Programs/ConWatch/ClientApp; npx vitest run`. Expected: PASS.
- [ ] **Step 4: Search for leftovers.** Search `src/` for each of these strings. Each must have no hits outside its new home and its tests:
  - `isInternetTarget`, `humanDuration`, `formatDuration`
  - `CHIP_COLOR`, `ATTRIBUTED`
  - `24 * 3_600_000`
  - `"Unreachable behind a down parent`
  - `<pattern` (only in `marks.tsx`)
- [ ] **Step 5: Browser pass.**
  1. Walk the dashboard, History (all four groups), Data, Traceroute, Report and Discoveries in light and dark.
  2. `read_console_messages` with `onlyErrors` on each page returns nothing.
  3. Send the user one screenshot of the dashboard and one of History.
- [ ] **Step 6:** Nothing to commit. Report the branch head and the visible-changes list.

## Owner rulings (2026-10-30)

- **Slab and ribbon colour (Ruling 3 reach):** the slab chip and the History verdict ribbon turn blue for DnsDown too, as planned, so DnsDown means one thing everywhere.
