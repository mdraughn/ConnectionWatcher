// Fetches and derives the incident story (punchcard cells + ledger) from the
// open APIs. Page-agnostic: the dashboard uses it today; any page can.
import type { Mark } from "./encoding";
import type { GlitchItem } from "./glitchData";
import {
  buildLedger, punchcardCells, verdictWindows, windowMark,
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

export async function fetchStory(topology: StoryTopology, days: number): Promise<Story> {
  const now = new Date();
  const from = new Date(now.getFullYear(), now.getMonth(), now.getDate() - (days - 1));
  const trunk = new Set(topology.trunkNames ?? []);
  const anchors = new Set(topology.anchorNames ?? []);
  const threshold = topology.weatherThreshold ?? 2;

  const [transitionsRes, glitchesRes] = await Promise.all([
    fetch(`/api/v1/transitions?from=${from.toISOString()}&limit=1000`),
    fetch(`/api/v1/glitches?from=${from.toISOString()}&limit=1000`),
  ]);
  const transitions = (transitionsRes.ok
    ? ((await transitionsRes.json()).items as TransitionEvent[]) : []).reverse(); // chronological
  const glitches = glitchesRes.ok
    ? ((await glitchesRes.json()).items as GlitchItem[]) : [];

  const windows = verdictWindows(
    transitions.filter((t) => t.subjectKind === "Verdict"), now.toISOString());
  const anchorDowns = (w: VerdictWindow) => new Set(transitions
    .filter((t) => t.to === "Down" && anchors.has(t.subjectName)
      && t.timestamp >= w.from && t.timestamp <= w.to)
    .map((t) => t.subjectName)).size;
  const marks = new Map<VerdictWindow, Mark>(
    windows.map((w) => [w, windowMark(w, anchorDowns(w), threshold)]));

  return {
    cells: punchcardCells(windows, marks, glitches, trunk, days, now),
    ledger: buildLedger(windows, marks, glitches, trunk),
    days,
    now,
  };
}
