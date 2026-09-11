# Incident Display Redesign Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship the three chosen displays in Mark's shared encoding — punchcard month + incident ledger on the dashboard (the story), cause-aligned ribbons on History (the evidence) — replacing the interim heatmap.

**Architecture:** All client-side against existing open APIs (transitions, glitches) plus two small server data additions (dashboard initial-data gains weatherThreshold; History subjects gain parent/role). A shared `encoding.ts` holds Mark's palette/marks; `incidents.ts` derives verdict windows, cell marks, and ledger sentences — all pure and Vitest-covered. Ribbons reuse `ribbonSegments` per target, recolored by row role. Single-trunk for now; twin WAN lanes arrive with failover groups.

**Tech Stack:** existing React islands + Vitest; no schema or API-surface changes.

**Spec:** `docs/superpowers/specs/2026-09-10-incident-display-redesign-design.md` (+ encoding decisions therein)

## Global Constraints

- Branch `display-redesign`. TDD for the pure libs; component work verified by `npm run build` + page smokes; commit per task.
- Encoding (verbatim from spec): warm ramp `#eda94f/#e08a00/#cc3333`, grey ramp `#d4d4d4/#7d7d7d/#2b2b2b`; bar = shared path, centered dot = one target; ambiguous = half grey/half warm split on the ascending diagonal (`linear-gradient(to bottom right, grey 49.5%, warm 50.5%)`); hatch = `repeating-linear-gradient(45deg, #c9c9c9 0 3px, #f2f2f2 3px 6px)`; healthy ribbon `#eaf3ec`.
- Ledger icons use the exact same marks (cross-referencing by eye).
- Every mark carries a tooltip/direct label (the `#e08a00` contrast relief).
- Timezone: all bucketing/labels in browser-local time.
- The dashboard punchcard is fixed 30 days; History ribbons follow the page's range selector.

---

### Task 1: `encoding.ts` + `incidents.ts` (pure libs, Vitest-first)

**Files:**
- Create: `Programs/ConWatch/ClientApp/src/lib/encoding.ts`, `src/lib/incidents.ts`
- Test: `src/lib/incidents.test.ts`

**Interfaces (produces):**

```ts
// encoding.ts
export type Depth = "glitch" | "degraded" | "out";
export type Hue = "warm" | "grey";
export interface Mark { shape: "bar" | "dot"; hue: Hue; depth: Depth; ambiguous?: boolean }
export const RAMP: Record<Hue, Record<Depth, string>> = { warm: {...}, grey: {...} };
export const HATCH: string; export const HEALTHY = "#eaf3ec";
export function markCss(mark: Mark): { background: string };   // handles ambiguous gradient
export function markRank(mark: Mark): number; // cell precedence: grey/out highest … warm dot glitch lowest
```

```ts
// incidents.ts
export interface VerdictWindow { kind: "LinkDown" | "LinkDegraded" | "InternetWeather"; from: string; to: string; detail: string | null }
export function verdictWindows(verdictTransitions: TransitionEvent[], rangeEndIso: string): VerdictWindow[];
// Weather depth: "out" when ≥ weatherThreshold distinct anchor Down transitions overlap the window, else "degraded".
export function windowMark(w: VerdictWindow, anchorDownsInWindow: number, weatherThreshold: number): Mark;
export function glitchMark(g: GlitchItem, trunkNames: Set<string>): Mark;
//   Internet-class glitch → bar/warm/glitch; ambiguous=true when detail includes "trunk affected".
//   Target glitch on a trunk name → bar/grey/glitch; otherwise dot/warm/glitch.
export interface LedgerItem { at: string; endAt: string; lead: string; body: string | null; mark: Mark }
export function buildLedger(windows: VerdictWindow[], marks: Map<VerdictWindow, Mark>,
  glitches: GlitchItem[], trunkNames: Set<string>): LedgerItem[]; // newest first
```

Ledger lead templates (duration humanized as "45 s" / "4 min" / "1.2 h"):
- LinkDown → `Your link went down for {dur}.`
- LinkDegraded → `Your link was degraded for {dur}.`
- Weather depth out → `The internet went out for {dur} — not your line.`
- Weather depth degraded → `Internet weather for {dur}.`
- Internet glitch → `Internet-wide blip ({dur}).`
- Trunk-target glitch → `Brief loss on your link ({g.targetName}, {lost}/{samples} pings).`
- Other target glitch → `{g.targetName} dropped {lost} pings ({dur}).`
Body = the transition/glitch `detail` (already operator-aware server-side).

- [ ] Vitest failing tests: window derivation from a verdict transition sequence (incl. open window to range end); weather depth by anchor-down count vs threshold; glitch marks incl. trunk/ambiguous; ledger ordering, lead wording (operator detail passthrough), duration formatting edges (45 s / 4 min / 1.2 h).
- [ ] Implement; `npm test` green.
- [ ] Commit `feat(ui): shared incident encoding and story derivation libs`

---

### Task 2: Dashboard punchcard

**Files:**
- Modify: `Programs/ConWatch/Pages/Index.cshtml.cs` — InitialDataJson gains `weatherThreshold` (from SettingsStore, fallback 2) and `trunkIds`/`anchorIds` are already derivable from Parents/PoolMembers — add `trunkIds` explicitly (walk pool-members' shared parent chain server-side, mirroring IspReportBuilder.BuildTopologyAsync).
- Create: `src/components/Punchcard.tsx`
- Modify: `src/dashboard.tsx` — fetch 30d glitches + transitions (open APIs), render `<Punchcard/>` in a "Last 30 days" section under the tree.
- Test: cell-derivation logic goes in `incidents.ts` (`punchcardCells(...)` — pure, Vitest): buckets by local day×hour, one winning Mark per cell by `markRank`, counts for the tooltip.

Component: 24 rows × 30 fixed 22px columns (`width: max-content`, horizontal scroll), bars fill the cell, dots 9px centered, hour labels every 6, day labels sparse, matrix legend underneath (the legend IS the scope×severity matrix). Tooltip: local date/hour + what happened.

- [ ] Vitest for `punchcardCells` (precedence: LinkDown beats weather beats glitches in one cell; dot only when no bar); implement; wire dashboard; `npm run build` + full smokes green.
- [ ] Commit `feat(ui): dashboard punchcard month in the shared encoding`

---

### Task 3: Dashboard incident ledger

**Files:**
- Create: `src/components/Ledger.tsx`; modify `src/dashboard.tsx` (below punchcard).

Renders `buildLedger` output: mark chip (12px bar / 9px dot, `markCss`), bold local "Weekday, MMM d, h:mm AM" lead, body in muted text, cap 15 items with "…and N more on the History page", quiet footer ("K of the last 30 days: nothing to report."). Empty state: "Nothing to report in 30 days. Quiet is good."

- [ ] Implement (logic already tested in T1); build + smokes green.
- [ ] Commit `feat(ui): dashboard incident ledger`

---

### Task 4: History cause-aligned ribbons

**Files:**
- Modify: `Programs/ConWatch/Pages/History.cshtml.cs` — SubjectsJson entries gain `parent` (name) and `role`: `"trunk" | "anchor" | "other"` (trunk = anchor-pool parent chain; anchors = pool members; server-side via ConfigStore, mirroring report topology).
- Create: `src/components/RibbonBoard.tsx`
- Modify: `src/history.tsx` — new "All targets — cause-aligned" section replacing the heatmap part of "Glitches & recurrence" (the recent-glitch list + chronic badge stay on History; the heatmap now lives on the dashboard as the punchcard).

RibbonBoard: rows ordered trunk (root→leaf), then anchors, then others; per row fetch transitions (`subjectId=`, page range) → `ribbonSegments` → recolor by row role: trunk rows grey ramp (Down→`#2b2b2b`, Faltering→`#7d7d7d`), warm ramp elsewhere (Down→`#cc3333`, Faltering→`#e08a00`), `Unreachable` → SVG hatch pattern (45°, `#c9c9c9`/`#f2f2f2`), Starting/Unknown pale neutral, healthy `#eaf3ec`; shared axis with date ticks; row labels left; existing per-row tooltips preserved. SVG `<pattern>` for hatch (one def per board).

- [ ] Vitest for the recolor mapping (role×state→fill, hatch for Unreachable); implement; build + History smoke green.
- [ ] Commit `feat(ui): cause-aligned ribbon board on History`

---

### Task 5: Wrap-up

- Remove now-dead heatmap code path from history island (`bucketByHourLocal` moves to punchcard usage or is superseded by `punchcardCells`; keep `chronicTargets`); README Web UI section rewrite for the three displays; BACKLOG mark display redesign done (note: twin WAN lanes pending failover groups).
- Full suites (`dotnet test`, `npm test`, `npm run build`), real-data snapshot pass, finishing-a-development-branch.
