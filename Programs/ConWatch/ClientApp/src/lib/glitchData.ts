// Pure helpers for the "Glitches & recurrence" section. All bucketing is in
// LOCAL time (consistent with the rest of the History page): the nightly-
// pattern heatmap only makes sense in the viewer's own clock.

import { DAY_MS, localHourKey } from "./localTime";
import type { GlitchItem } from "./types";

/**
 * Targets with glitches in >= 3 distinct local hours of the last 24 —
 * the reporting-layer "chronically glitchy" indicator.
 */
export function chronicTargets(glitches: GlitchItem[], now: Date): string[] {
  const cutoff = now.getTime() - DAY_MS;
  const hoursByTarget = new Map<string, Set<string>>();
  for (const g of glitches) {
    if (g.class !== "Target") continue;
    const at = new Date(g.startUtc);
    if (at.getTime() < cutoff || at.getTime() > now.getTime()) continue;
    const key = localHourKey(at);
    if (!hoursByTarget.has(g.targetName)) hoursByTarget.set(g.targetName, new Set());
    hoursByTarget.get(g.targetName)!.add(key);
  }
  return [...hoursByTarget.entries()]
    .filter(([, hours]) => hours.size >= 3)
    .map(([name]) => name)
    .sort();
}
