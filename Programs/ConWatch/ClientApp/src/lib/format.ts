// One duration formatter per style, so the same span never reads two ways
// inside one surface.

/** Compact ticking clock: 42s · 5m 12s · 3h 24m · 6d 4h 12m. Seconds
 *  show up to an hour; minutes ALWAYS show, however long the streak — a
 *  frozen-looking number reads as a stale page, not a live clock. */
export function formatClock(ms: number): string {
  const s = Math.max(0, Math.floor(ms / 1000));
  if (s < 60) return `${s}s`;
  const m = Math.floor(s / 60);
  if (m < 60) return `${m}m ${s % 60}s`;
  const h = Math.floor(m / 60);
  if (h < 24) return `${h}h ${m % 60}m`;
  const d = Math.floor(h / 24);
  return `${d}d ${h % 24}h ${m % 60}m`;
}

/** Plain-English span for ledgers and tables: 26 s · 4 min 10 s · 46 min · 2.5 h. */
export function formatProse(ms: number): string {
  const seconds = Math.max(1, Math.round(ms / 1000));
  if (seconds < 120) return `${seconds} s`;
  const minutes = Math.floor(seconds / 60);
  // Short events keep their seconds — a 9½-minute outage is not "10 min".
  if (minutes < 10)
    return seconds % 60 === 0 ? `${minutes} min` : `${minutes} min ${seconds % 60} s`;
  if (minutes < 120) return `${Math.round(seconds / 60)} min`;
  return `${(minutes / 60).toFixed(1)} h`;
}
