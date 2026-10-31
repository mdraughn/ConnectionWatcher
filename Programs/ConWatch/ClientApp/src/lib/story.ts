// Fetches and derives the incident story (punchcard cells + ledger) from the
// open APIs. Page-agnostic: the dashboard uses it today; any page can.
import { useEffect, useState } from "react";
import type { Mark } from "./encoding";
import { fetchGlitches, fetchTransitions } from "./api";
import { isoCompare, localDayStart } from "./localTime";
import {
  buildLedger, punchcardCells, stripBins, verdictWindows, windowMark,
  type LedgerItem, type PunchcardCell, type VerdictWindow,
} from "./incidents";
import type { GlitchItem } from "./types";

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

  const [transitions, glitches] = await Promise.all([
    fetchTransitions({ from, limit: 1000, scope: "internet" }),
    fetchGlitches({ from, limit: 1000, scope: "internet" }),
  ]);

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

/** The story for `days`, fetched on mount and again whenever `refreshKey`
 *  changes; a result that lands after unmount (or after a newer fetch began)
 *  is ignored. `topology` is read at fetch time and is not a dependency. */
export function useStory(topology: StoryTopology, days: number, refreshKey?: number): Story | null {
  const [story, setStory] = useState<Story | null>(null);
  useEffect(() => {
    let live = true;
    fetchStory(topology, days).then((s) => { if (live) setStory(s); }).catch(() => undefined);
    return () => { live = false; };
  }, [days, refreshKey]);
  return story;
}
