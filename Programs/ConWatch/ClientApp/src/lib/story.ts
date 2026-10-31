// Fetches and derives the incident story (punchcard cells + ledger) from the
// open APIs. Page-agnostic: the dashboard uses it today; any page can.
import type { Mark } from "./encoding";
import type { GlitchItem } from "./glitchData";
import { isoCompare, localDayStart } from "./localTime";
import {
  buildLedger, punchcardCells, stripBins, verdictWindows, windowMark,
  type LedgerItem, type PunchcardCell, type VerdictWindow,
} from "./incidents";
import type { TransitionEvent } from "./types";

export interface StoryTopology {
  trunkNames?: string[];
  anchorNames?: string[];
  weatherThreshold?: number;
}

export interface Story {
  cells: Map<string, PunchcardCell>;
  ledger: LedgerItem[];
  days: number;
  now: Date;
}

interface Derived {
  windows: VerdictWindow[];
  marks: Map<VerdictWindow, Mark>;
  glitches: GlitchItem[];
  trunk: Set<string>;
  now: Date;
}

async function fetchAndDerive(topology: StoryTopology, from: Date): Promise<Derived> {
  const now = new Date();
  const trunk = new Set(topology.trunkNames ?? []);
  const anchors = new Set(topology.anchorNames ?? []);
  const threshold = topology.weatherThreshold ?? 2;

  const [transitionsRes, glitchesRes] = await Promise.all([
    fetch(`/api/v1/transitions?from=${from.toISOString()}&limit=1000&scope=internet`),
    fetch(`/api/v1/glitches?from=${from.toISOString()}&limit=1000&scope=internet`),
  ]);
  const transitions = (transitionsRes.ok
    ? ((await transitionsRes.json()).items as TransitionEvent[]) : []).reverse(); // chronological
  const glitches = glitchesRes.ok
    ? ((await glitchesRes.json()).items as GlitchItem[]) : [];

  const windows = verdictWindows(
    transitions.filter((t) => t.subjectKind === "Verdict"), now.toISOString());
  const anchorDowns = (w: VerdictWindow) => new Set(transitions
    .filter((t) => t.to === "Down" && anchors.has(t.subjectName)
      && isoCompare(t.timestamp, w.from) >= 0 && isoCompare(t.timestamp, w.to) <= 0)
    .map((t) => t.subjectName)).size;
  const marks = new Map<VerdictWindow, Mark>(
    windows.map((w) => [w, windowMark(w, anchorDowns(w), threshold)]));

  return { windows, marks, glitches, trunk, now };
}

export async function fetchStory(topology: StoryTopology, days: number): Promise<Story> {
  const now = new Date();
  const from = localDayStart(now, days - 1);
  const d = await fetchAndDerive(topology, from);
  return {
    cells: punchcardCells(d.windows, d.marks, d.glitches, d.trunk, days, d.now),
    ledger: buildLedger(d.windows, d.marks, d.glitches, d.trunk),
    days,
    now: d.now,
  };
}

/** Bins for the dashboard event strip: `binCount` × `binMs` ending now. */
export async function fetchStrip(topology: StoryTopology, binMs: number,
  binCount: number): Promise<Array<Mark | null>> {
  const from = new Date(Date.now() - binMs * binCount);
  const d = await fetchAndDerive(topology, from);
  return stripBins(d.windows, d.marks, d.glitches, d.trunk, binMs, binCount, d.now);
}
