// The one place the React islands call fetch. Nothing here throws: a network
// failure, a non-2xx response or an unreadable body all read as "no data"
// (null, [] or false), so a caller never has to guard a rejection.
import type { GlitchItem, RollupPoint, Snapshot, TransitionEvent } from "./types";

type ScopeFilter = "all" | "internet" | "local";

export interface TransitionQuery {
  subjectId?: string;
  from?: Date;
  to?: Date;
  limit: number;
  scope?: ScopeFilter;
  opening?: boolean;
}

async function getJson<T>(url: string): Promise<T | null> {
  try {
    const response = await fetch(url);
    if (!response.ok) return null;
    return (await response.json()) as T;
  } catch {
    return null;
  }
}

/** The query string in a fixed key order, so URLs are stable and testable. */
function withQuery(path: string, params: Array<[string, string | number | boolean | undefined]>): string {
  const parts = params
    .filter((p): p is [string, string | number | boolean] => p[1] !== undefined)
    .map(([k, v]) => `${k}=${encodeURIComponent(String(v))}`);
  return parts.length ? `${path}?${parts.join("&")}` : path;
}

export function transitionsUrl(q: TransitionQuery): string {
  return withQuery("/api/v1/transitions", [
    ["subjectId", q.subjectId], ["from", q.from?.toISOString()], ["to", q.to?.toISOString()],
    ["limit", q.limit], ["scope", q.scope], ["opening", q.opening ? true : undefined],
  ]);
}

/** The API's own order: newest first, with any opening rows leading. null when there is no data. */
export async function fetchTransitionsNewestFirst(q: TransitionQuery): Promise<TransitionEvent[] | null> {
  const body = await getJson<{ items?: TransitionEvent[] }>(transitionsUrl(q));
  return Array.isArray(body?.items) ? body.items : null;
}

/** Chronological: the opening rows (the state before `from`) first, then the in-range rows oldest first. */
export async function fetchTransitions(q: TransitionQuery): Promise<TransitionEvent[]> {
  const items = await fetchTransitionsNewestFirst(q);
  if (!items) return [];
  return [...items.filter((i) => i.opening).reverse(), ...items.filter((i) => !i.opening).reverse()];
}

/** The glitches as the API sends them. null when there is no data. */
export async function fetchGlitchesOrNull(
  q: { from: Date; to?: Date; limit: number; scope?: ScopeFilter }): Promise<GlitchItem[] | null> {
  const body = await getJson<{ items?: GlitchItem[] }>(withQuery("/api/v1/glitches", [
    ["from", q.from.toISOString()], ["to", q.to?.toISOString()], ["limit", q.limit], ["scope", q.scope],
  ]));
  return Array.isArray(body?.items) ? body.items : null;
}

export async function fetchGlitches(
  q: { from: Date; to?: Date; limit: number; scope?: ScopeFilter }): Promise<GlitchItem[]> {
  return (await fetchGlitchesOrNull(q)) ?? [];
}

export async function fetchRollups(subjectId: string, from: Date, to: Date): Promise<RollupPoint[]> {
  const body = await getJson<{ points?: RollupPoint[] }>(withQuery(
    `/api/v1/rollups/${subjectId}`,
    [["from", from.toISOString()], ["to", to.toISOString()], ["resolution", "auto"]]));
  return Array.isArray(body?.points) ? body.points : [];
}

export function fetchSnapshot(): Promise<Snapshot | null> {
  return getJson<Snapshot>("/api/v1/status");
}

export async function acknowledgeWarning(key: string): Promise<boolean> {
  try {
    const response = await fetch(`/api/v1/warnings/${encodeURIComponent(key)}/acknowledge`, { method: "POST" });
    return response.ok;
  } catch {
    return false;
  }
}
