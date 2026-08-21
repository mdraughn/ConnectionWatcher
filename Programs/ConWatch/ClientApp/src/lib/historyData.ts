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

/**
 * Split incident semantics:
 * - An OUTAGE (`Down`) is the target's own problem: counted, with the longest
 *   window reported. It ends at recovery (`Responding`) or when superseded by
 *   `Unreachable`.
 * - UNREACHABLE is a coverage gap — no data, never the target's fault: only
 *   total time is reported. It ends at the release (`ParentRecovered`) or any
 *   transition to `Responding`/`Down`.
 * Open windows run to the range end. Transitions must be chronological and
 * already clipped via clipAtReset().
 */
export interface IncidentStats {
  outageCount: number;
  longestOutageMs: number;
  unreachableMs: number;
}

export function incidents(transitions: TransitionEvent[], rangeEndIso: string): IncidentStats {
  let outageCount = 0;
  let longestOutageMs = 0;
  let unreachableMs = 0;
  let open: { kind: "outage" | "unreach"; start: number } | null = null;

  const close = (atMs: number) => {
    if (!open) return;
    if (open.kind === "outage") longestOutageMs = Math.max(longestOutageMs, atMs - open.start);
    else unreachableMs += atMs - open.start;
    open = null;
  };

  for (const t of transitions) {
    const at = Date.parse(t.timestamp);
    if (t.to === "Down") {
      if (open?.kind === "unreach") close(at);
      if (open === null) {
        open = { kind: "outage", start: at };
        outageCount++;
      }
    } else if (t.to === "Unreachable") {
      close(at);
      open = { kind: "unreach", start: at };
    } else if (t.to === "Responding") {
      close(at);
    } else if (t.trigger === "ParentRecovered" && open?.kind === "unreach") {
      close(at);
    }
  }
  close(Date.parse(rangeEndIso));
  return { outageCount, longestOutageMs, unreachableMs };
}

/**
 * Cuts the series at the latest HistoryReset marker (an address correction or
 * repoint): everything before it belongs to a different data history and is
 * excluded from attribution. Returns the surviving transitions and the reset
 * timestamp (null when the series is unbroken).
 */
export function clipAtReset(transitions: TransitionEvent[]):
  { transitions: TransitionEvent[]; resetAt: string | null } {
  let resetIndex = -1;
  for (let i = transitions.length - 1; i >= 0; i--) {
    if (transitions[i].trigger === "HistoryReset") {
      resetIndex = i;
      break;
    }
  }
  if (resetIndex < 0)
    return { transitions, resetAt: null };
  return {
    transitions: transitions.slice(resetIndex + 1),
    resetAt: transitions[resetIndex].timestamp,
  };
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
