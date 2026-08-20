// Pure helpers for the History page (unit-tested).
import type { RollupPoint, TransitionEvent } from "./types";

/** Ping-count-weighted uptime percentage, or null with no data. */
export function uptimePercent(points: RollupPoint[]): number | null {
  let pings = 0;
  let successes = 0;
  for (const p of points) {
    pings += p.pingCount;
    successes += p.successCount;
  }
  return pings > 0 ? (100 * successes) / pings : null;
}

const INCIDENT_STATES = new Set(["Down", "Unreachable"]);
const INCIDENT_END_STATES = new Set(["Responding"]);

/**
 * An incident spans from a transition into Down/Unreachable until the next
 * transition into Responding; an open incident ends at the range end.
 * Transitions must be in chronological order.
 */
export function incidents(transitions: TransitionEvent[], rangeEndIso: string):
  { count: number; longestMs: number } {
  let count = 0;
  let longestMs = 0;
  let openStart: number | null = null;
  for (const t of transitions) {
    const at = Date.parse(t.timestamp);
    if (openStart === null && INCIDENT_STATES.has(t.to)) {
      openStart = at;
      count++;
    } else if (openStart !== null && INCIDENT_END_STATES.has(t.to)) {
      longestMs = Math.max(longestMs, at - openStart);
      openStart = null;
    }
  }
  if (openStart !== null) {
    longestMs = Math.max(longestMs, Date.parse(rangeEndIso) - openStart);
  }
  return { count, longestMs };
}

export interface RibbonSegment {
  state: string;
  from: string;
  to: string;
  evidence: string | null;
}

/**
 * Contiguous state segments across [rangeStart, rangeEnd] from chronological
 * transitions. The state before the first transition is that transition's
 * `from`; with no transitions at all the whole range is Unknown.
 */
export function ribbonSegments(transitions: TransitionEvent[],
  rangeStartIso: string, rangeEndIso: string): RibbonSegment[] {
  if (transitions.length === 0) {
    return [{ state: "Unknown", from: rangeStartIso, to: rangeEndIso, evidence: null }];
  }
  const segments: RibbonSegment[] = [];
  let cursor = rangeStartIso;
  let state = transitions[0].from;
  let evidence: string | null = null;
  for (const t of transitions) {
    if (t.timestamp > cursor) {
      segments.push({ state, from: cursor, to: t.timestamp, evidence });
      cursor = t.timestamp;
    }
    state = t.to;
    evidence = t.verdict ?? t.detail;
  }
  segments.push({ state, from: cursor, to: rangeEndIso, evidence });
  return segments;
}
