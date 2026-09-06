// Pure helpers for the "Glitches & recurrence" section. All bucketing is in
// LOCAL time (consistent with the rest of the History page): the nightly-
// pattern heatmap only makes sense in the viewer's own clock.

export interface GlitchItem {
  id: number;
  targetId: string;
  targetName: string;
  class: "Target" | "Internet";
  startUtc: string;
  endUtc: string;
  lostCount: number;
  sampleCount: number;
  detail: string | null;
}

/** Midnight (local) of the given date. */
function localMidnight(d: Date): Date {
  return new Date(d.getFullYear(), d.getMonth(), d.getDate());
}

/**
 * Buckets event timestamps into a [day][hour] count grid covering the `days`
 * local calendar days ending at `now` (index 0 = oldest day, hour = local).
 */
export function bucketByHourLocal(timestamps: string[], days: number, now: Date): number[][] {
  const grid: number[][] = Array.from({ length: days }, () => new Array(24).fill(0));
  const end = localMidnight(now).getTime() + 24 * 3_600_000;
  const dayStarts: number[] = [];
  for (let i = days - 1; i >= 0; i--) {
    const d = new Date(now.getFullYear(), now.getMonth(), now.getDate() - i);
    dayStarts.push(d.getTime());
  }
  for (const ts of timestamps) {
    const at = new Date(ts);
    const t = at.getTime();
    if (Number.isNaN(t) || t >= end || t < dayStarts[0]) continue;
    // Walk from the newest day down: calendar-day compare beats division
    // because DST days are not 24 hours long.
    for (let day = dayStarts.length - 1; day >= 0; day--) {
      if (t >= dayStarts[day]) {
        grid[day][at.getHours()]++;
        break;
      }
    }
  }
  return grid;
}

/**
 * Targets with glitches in >= 3 distinct local hours of the last 24 —
 * the reporting-layer "chronically glitchy" indicator.
 */
export function chronicTargets(glitches: GlitchItem[], now: Date): string[] {
  const cutoff = now.getTime() - 24 * 3_600_000;
  const hoursByTarget = new Map<string, Set<string>>();
  for (const g of glitches) {
    if (g.class !== "Target") continue;
    const at = new Date(g.startUtc);
    if (at.getTime() < cutoff || at.getTime() > now.getTime()) continue;
    const key = `${at.getFullYear()}-${at.getMonth()}-${at.getDate()}-${at.getHours()}`;
    if (!hoursByTarget.has(g.targetName)) hoursByTarget.set(g.targetName, new Set());
    hoursByTarget.get(g.targetName)!.add(key);
  }
  return [...hoursByTarget.entries()]
    .filter(([, hours]) => hours.size >= 3)
    .map(([name]) => name)
    .sort();
}
