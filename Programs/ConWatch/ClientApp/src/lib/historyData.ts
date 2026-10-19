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

/** Average RTT weighted by each bucket's successful pings, or null when nothing succeeded. */
export function weightedRtt(points: RollupPoint[]): number | null {
  const totalSuccess = points.reduce((n, p) => n + p.successCount, 0);
  return totalSuccess > 0
    ? points.reduce((n, p) => n + p.rttAvg * p.successCount, 0) / totalSuccess
    : null;
}

/** One bar's loss percentage (0..100), or null for a bucket with no pings. */
export function lossPercent(point: RollupPoint): number | null {
  return point.pingCount > 0 ? (100 * (point.pingCount - point.successCount)) / point.pingCount : null;
}

/**
 * Split incident semantics:
 * - An OUTAGE (`Down`) is the target's own problem: counted, with the longest
 *   window reported. It ends at recovery (`Responding`) or when superseded by
 *   `Unreachable`.
 * - UNREACHABLE is a coverage gap — no data, never the target's fault: only
 *   total time is reported. It ends at the release (`ParentRecovered`), at
 *   any transition out of `Unreachable` (e.g. to `Recovering`), or at any
 *   transition to `Responding`/`Down`.
 * - A verdict transition into `DnsDown` (internet up, name lookups failing)
 *   is never an outage, and it closes an open `LinkDown` outage.
 * Open windows run to the range end. Transitions must be chronological and
 * already clipped via clipAtReset().
 */
export interface IncidentStats {
  outageCount: number;
  longestOutageMs: number;
  unreachableMs: number;
}

/**
 * Range-start seeding: the state when the range opens is the `to` of an
 * `opening` row (the API's `opening=true` sends each subject's latest
 * transition before the range), else the first transition's `from` (as
 * ribbonSegments assumes). A Down or Unreachable window already open then
 * counts from rangeStart. Opening rows only seed that state; they are never
 * counted as events.
 */
export function incidents(transitions: TransitionEvent[], rangeStartIso: string,
  rangeEndIso: string): IncidentStats {
  let outageCount = 0;
  let longestOutageMs = 0;
  let unreachableMs = 0;
  let open: { kind: "outage" | "unreach"; start: number } | null = null;
  const openingRow = transitions.find((t) => t.opening);
  const openingState = openingRow ? openingRow.to : transitions[0]?.from;
  if (openingState === "Down") {
    open = { kind: "outage", start: Date.parse(rangeStartIso) };
    outageCount++;
  } else if (openingState === "Unreachable") {
    open = { kind: "unreach", start: Date.parse(rangeStartIso) };
  }

  const close = (atMs: number) => {
    if (!open) return;
    if (open.kind === "outage") longestOutageMs = Math.max(longestOutageMs, atMs - open.start);
    else unreachableMs += atMs - open.start;
    open = null;
  };

  for (const t of transitions) {
    if (t.opening) continue;
    const at = Date.parse(t.timestamp);
    if (t.verdict === "DnsDown") {
      // The internet is up; name lookups fail (the user's resolver). Never an
      // outage — and it ends any LinkDown outage that was open.
      if (open?.kind === "outage") close(at);
    } else if (t.to === "Down") {
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
    } else if (open?.kind === "unreach" && (t.trigger === "ParentRecovered" || t.from === "Unreachable")) {
      // The release, or the target answering again (Unreachable -> Recovering).
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

export type HistoryScope = "path" | "pools" | "local";

/**
 * The History subjects for one scope: LAN devices ("local"), non-LAN pools
 * ("pools"), or everything else — the verdict and the path targets ("path").
 */
export function subjectsForScope<T extends { scope?: string; kind?: string }>(subjects: T[], scope: HistoryScope): T[] {
  return subjects.filter((s) => {
    if (s.scope === "Local") return scope === "local";
    return scope === "pools" ? s.kind === "pool" : scope === "path" && s.kind !== "pool";
  });
}