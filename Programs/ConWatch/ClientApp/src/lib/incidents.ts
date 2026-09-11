// Derives the incident STORY from raw transitions and glitches: attributed
// verdict windows, one encoded mark per event, punchcard cells, and the
// plain-English ledger. Pure functions, unit-tested.
import type { Mark } from "./encoding";
import { markRank } from "./encoding";
import type { GlitchItem } from "./glitchData";
import type { TransitionEvent } from "./types";

export interface VerdictWindow {
  kind: "LinkDown" | "LinkDegraded" | "InternetWeather";
  from: string;
  to: string;
  detail: string | null;
}

const ATTRIBUTED = new Set(["LinkDown", "LinkDegraded", "InternetWeather"]);

/** Attributed windows from chronological verdict transitions; an open window
 *  closes at the range end. */
export function verdictWindows(verdictTransitions: TransitionEvent[],
  rangeEndIso: string): VerdictWindow[] {
  const windows: VerdictWindow[] = [];
  let open: VerdictWindow | null = null;
  for (const t of verdictTransitions) {
    if (open) {
      open.to = t.timestamp;
      windows.push(open);
      open = null;
    }
    if (t.verdict && ATTRIBUTED.has(t.verdict)) {
      open = {
        kind: t.verdict as VerdictWindow["kind"],
        from: t.timestamp, to: t.timestamp, detail: t.detail,
      };
    }
  }
  if (open) {
    open.to = rangeEndIso;
    windows.push(open);
  }
  return windows;
}

/** The encoded mark for a verdict window. Weather depth escalates to "out"
 *  when enough anchors were fully Down during the window. */
export function windowMark(w: VerdictWindow, anchorDownsInWindow: number,
  weatherThreshold: number): Mark {
  if (w.kind === "LinkDown") return { shape: "bar", hue: "grey", depth: "out" };
  if (w.kind === "LinkDegraded") return { shape: "bar", hue: "grey", depth: "degraded" };
  return {
    shape: "bar", hue: "warm",
    depth: anchorDownsInWindow >= weatherThreshold ? "out" : "degraded",
  };
}

/** The encoded mark for a glitch row. */
export function glitchMark(g: GlitchItem, trunkNames: Set<string>): Mark {
  if (g.class === "Internet") {
    return {
      shape: "bar", hue: "warm", depth: "glitch",
      ambiguous: (g.detail ?? "").includes("trunk affected"),
    };
  }
  if (trunkNames.has(g.targetName))
    return { shape: "bar", hue: "grey", depth: "glitch", ambiguous: false };
  return { shape: "dot", hue: "warm", depth: "glitch", ambiguous: false };
}

export interface PunchcardCell { mark: Mark; count: number }

function cellKey(at: Date): string {
  return `${at.getFullYear()}-${at.getMonth() + 1}-${at.getDate()}-${at.getHours()}`;
}

/** One winning mark per local day×hour cell (by markRank), with the number of
 *  events that landed there. Keys: "yyyy-M-d-H" in local time. */
export function punchcardCells(windows: VerdictWindow[], windowMarks: Map<VerdictWindow, Mark>,
  glitches: GlitchItem[], trunkNames: Set<string>, days: number, now: Date):
  Map<string, PunchcardCell> {
  const cells = new Map<string, PunchcardCell>();
  const oldest = new Date(now.getFullYear(), now.getMonth(), now.getDate() - (days - 1)).getTime();

  const place = (iso: string, mark: Mark) => {
    const at = new Date(iso);
    if (Number.isNaN(at.getTime()) || at.getTime() < oldest || at.getTime() > now.getTime())
      return;
    const key = cellKey(at);
    const cell = cells.get(key);
    if (!cell) cells.set(key, { mark, count: 1 });
    else {
      cell.count++;
      if (markRank(mark) > markRank(cell.mark)) cell.mark = mark;
    }
  };

  for (const w of windows) {
    const mark = windowMarks.get(w);
    if (mark) place(w.from, mark);
  }
  for (const g of glitches)
    place(g.startUtc, glitchMark(g, trunkNames));
  return cells;
}

/**
 * The event strip: `binCount` continuous bins of `binMs` ending at `now`,
 * oldest first. Each bin holds the highest-rank mark of any incident
 * overlapping it, or null for healthy time. Incidents paint EVERY bin they
 * overlap (a 4-minute outage fills 4 one-minute bins); instantaneous events
 * still fill their bin.
 */
export function stripBins(windows: VerdictWindow[], windowMarks: Map<VerdictWindow, Mark>,
  glitches: GlitchItem[], trunkNames: Set<string>, binMs: number, binCount: number,
  now: Date): Array<Mark | null> {
  const bins: Array<Mark | null> = new Array(binCount).fill(null);
  const binIndex = (ms: number) =>
    binCount - 1 - Math.floor((now.getTime() - ms) / binMs);

  const paint = (fromIso: string, toIso: string, mark: Mark) => {
    const from = Date.parse(fromIso);
    const to = Date.parse(toIso);
    if (Number.isNaN(from) || Number.isNaN(to)) return;
    const first = Math.max(0, binIndex(from));
    const last = Math.min(binCount - 1, binIndex(Math.max(from, to)));
    for (let i = first; i <= last; i++) {
      const existing = bins[i];
      if (existing === null || markRank(mark) > markRank(existing))
        bins[i] = mark;
    }
  };

  for (const w of windows) {
    const mark = windowMarks.get(w);
    if (mark) paint(w.from, w.to, mark);
  }
  for (const g of glitches)
    paint(g.startUtc, g.endUtc, glitchMark(g, trunkNames));
  return bins;
}

export interface LedgerItem {
  at: string;
  endAt: string;
  lead: string;
  body: string | null;
  mark: Mark;
}

export function humanDuration(ms: number): string {
  const seconds = Math.max(1, Math.round(ms / 1000));
  if (seconds < 120) return `${seconds} s`;
  const minutes = seconds / 60;
  if (minutes < 120) return `${Math.round(minutes)} min`;
  return `${(minutes / 60).toFixed(1)} h`;
}

/** The dashboard's plain-English incident list, newest first. */
export function buildLedger(windows: VerdictWindow[], windowMarks: Map<VerdictWindow, Mark>,
  glitches: GlitchItem[], trunkNames: Set<string>): LedgerItem[] {
  const items: LedgerItem[] = [];

  for (const w of windows) {
    const mark = windowMarks.get(w) ?? windowMark(w, 0, 2);
    const dur = humanDuration(Date.parse(w.to) - Date.parse(w.from));
    const lead =
      w.kind === "LinkDown" ? `Your link went down for ${dur}.`
      : w.kind === "LinkDegraded" ? `Your link was degraded for ${dur}.`
      : mark.depth === "out" ? `The internet went out for ${dur} — not your line.`
      : `Internet weather for ${dur}.`;
    items.push({ at: w.from, endAt: w.to, lead, body: w.detail, mark });
  }

  for (const g of glitches) {
    const mark = glitchMark(g, trunkNames);
    const dur = humanDuration(Date.parse(g.endUtc) - Date.parse(g.startUtc));
    const lead =
      g.class === "Internet" ? `Internet-wide blip (${dur}).`
      : trunkNames.has(g.targetName)
        ? `Brief loss on your link (${g.targetName}, ${g.lostCount}/${g.sampleCount} pings).`
        : `${g.targetName} dropped ${g.lostCount} pings (${dur}).`;
    items.push({ at: g.startUtc, endAt: g.endUtc, lead, body: g.detail, mark });
  }

  return items.sort((a, b) => Date.parse(b.at) - Date.parse(a.at));
}
