// The one home of local-clock and ISO-instant arithmetic. Timestamps stay UTC
// on the wire; local time is computed only here, in the browser.

export const HOUR_MS = 3_600_000;
export const DAY_MS = 24 * HOUR_MS;

/** Local midnight, `daysBack` days before `now`'s local day. */
export function localDayStart(now: Date, daysBack = 0): Date {
  return new Date(now.getFullYear(), now.getMonth(), now.getDate() - daysBack);
}

/** The punchcard's local hour bucket key: "yyyy-M-d-H" (month is 1-based). */
export function localHourKey(at: Date): string {
  return `${at.getFullYear()}-${at.getMonth() + 1}-${at.getDate()}-${at.getHours()}`;
}

/** `hour` o'clock on local day `day`; null when that wall-clock hour does not exist (a spring-forward gap). */
export function localHourAt(day: Date, hour: number): Date | null {
  const at = new Date(day.getFullYear(), day.getMonth(), day.getDate(), hour);
  return at.getHours() === hour ? at : null;
}

/** An ISO-8601 instant as epoch milliseconds. */
export function isoMs(iso: string): number {
  return Date.parse(iso);
}

/** Orders two ISO instants by value (not by string: offsets and fraction digits vary). */
export function isoCompare(a: string, b: string): number {
  return isoMs(a) - isoMs(b);
}
