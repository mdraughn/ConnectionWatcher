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
