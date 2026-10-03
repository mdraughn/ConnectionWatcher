// Pure helpers for the "How's my LAN" section (unit-tested).
import type { TargetStatus } from "./types";

/** Local targets are LAN devices; a missing scope (older server) means Verdict. */
export const isLocal = (t: { scope?: string }): boolean => t.scope === "Local";

export interface LanSummary {
  total: number;
  up: number;
  down: number;
}

export function summarizeLan(targets: TargetStatus[]): LanSummary {
  const lan = targets.filter(isLocal);
  return {
    total: lan.length,
    up: lan.filter((t) => t.state === "Responding").length,
    down: lan.filter((t) => t.state === "Down" || t.state === "Unreachable").length,
  };
}

/** Time since an ISO timestamp: "just now", "5 min", "3 h", "3 d"; "—" when unknown. */
export function sinceLabel(iso: string | null | undefined, now: Date): string {
  if (!iso) return "—";
  const ms = now.getTime() - Date.parse(iso);
  if (!Number.isFinite(ms)) return "—";
  const seconds = Math.max(0, Math.floor(ms / 1000));
  if (seconds < 60) return "just now";
  const minutes = Math.floor(seconds / 60);
  if (minutes < 60) return `${minutes} min`;
  const hours = Math.floor(minutes / 60);
  if (hours < 48) return `${hours} h`;
  return `${Math.floor(hours / 24)} d`;
}
