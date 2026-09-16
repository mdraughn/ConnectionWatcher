// "How long has it been like this?" — the verdict slab's clock. Two rules,
// decided with Mark:
//  - The ANSWER classes are up / struggling / down (SiteIncident and
//    InternetWeather read as UP — your internet was up through them).
//  - The up-streak is "time since the internet was last DOWN": struggling
//    blemishes a streak but does not reset it; only LinkDown does. The
//    ledger and History carry the blemishes.
// Pure functions, unit-tested.
import type { TransitionEvent } from "./types";

export type AnswerClass = "up" | "struggling" | "down" | "checking";

export function answerClassOf(verdictState: string | null | undefined): AnswerClass {
  switch (verdictState) {
    case "Healthy": case "SiteIncident": case "InternetWeather": return "up";
    case "LinkDegraded": return "struggling";
    case "LinkDown": return "down";
    default: return "checking";
  }
}

export interface AnswerClock {
  cls: AnswerClass;
  /** Primary timer start: up → end of the last down; struggling/down →
   *  start of the current stretch. Null while checking. */
  sinceIso: string | null;
  /** True when history ran out before the timer's real start was found. */
  atLeast: boolean;
  /** The up-streak the trouble sits on: while struggling, the still-counting
   *  streak start; while down, the length of the streak this outage ended.
   *  Null when up (the primary timer IS the streak) or unknown. */
  upStreakMs: number | null;
}

/**
 * Derive the clock from the verdict transition trail (newest first, each row
 * carrying the verdict it switched TO) plus the live verdict state.
 * `historyComplete` says the trail reaches back to the first verdict ever.
 */
export function answerClock(currentState: string | null | undefined,
  trail: TransitionEvent[], nowIso: string, historyComplete: boolean): AnswerClock {
  const cls = answerClassOf(currentState);
  if (cls === "checking")
    return { cls, sinceIso: null, atLeast: false, upStreakMs: null };

  // Chronological merged runs of equal answer class.
  const chron = [...trail].reverse();
  const runs: { cls: AnswerClass; fromIso: string; toIso: string }[] = [];
  for (const t of chron) {
    const c = answerClassOf(t.verdict);
    if (c === "checking") continue;
    const last = runs[runs.length - 1];
    if (last && last.cls === c) { last.toIso = t.timestamp; continue; }
    if (last) last.toIso = t.timestamp;
    runs.push({ cls: c, fromIso: t.timestamp, toIso: nowIso });
  }

  const current = runs[runs.length - 1];
  if (!current || current.cls !== cls) {
    // No trail yet, or the live verdict outran the recorded trail: the
    // stretch effectively starts now.
    return { cls, sinceIso: nowIso, atLeast: false, upStreakMs: null };
  }

  // End of the newest down run strictly before index `end` — the anchor an
  // up-streak counts from. Falls back to the first recorded run (monitoring
  // start), flagged atLeast when the trail was truncated there.
  const streakStart = (end: number): { iso: string; atLeast: boolean } => {
    for (let i = end - 1; i >= 0; i--)
      if (runs[i].cls === "down") return { iso: runs[i].toIso, atLeast: false };
    return { iso: runs[0].fromIso, atLeast: !historyComplete };
  };

  if (cls === "up") {
    const s = streakStart(runs.length);
    return { cls, sinceIso: s.iso, atLeast: s.atLeast, upStreakMs: null };
  }
  if (cls === "struggling") {
    const s = streakStart(runs.length - 1);
    return {
      cls, sinceIso: current.fromIso, atLeast: false,
      upStreakMs: Date.parse(nowIso) - Date.parse(s.iso), // still counting
    };
  }
  // down: the streak this outage ended, frozen at the moment it broke.
  const s = streakStart(runs.length - 1);
  return {
    cls, sinceIso: current.fromIso, atLeast: false,
    upStreakMs: Date.parse(current.fromIso) - Date.parse(s.iso),
  };
}

/** Compact ticking duration: 42s · 5m 12s · 3h 24m · 6d 4h 12m. Seconds
 *  show up to an hour; minutes ALWAYS show, however long the streak — a
 *  frozen-looking number reads as a stale page, not a live clock. */
export function formatDuration(ms: number): string {
  const s = Math.max(0, Math.floor(ms / 1000));
  if (s < 60) return `${s}s`;
  const m = Math.floor(s / 60);
  if (m < 60) return `${m}m ${s % 60}s`;
  const h = Math.floor(m / 60);
  if (h < 24) return `${h}h ${m % 60}m`;
  const d = Math.floor(h / 24);
  return `${d}d ${h % 24}h ${m % 60}m`;
}
