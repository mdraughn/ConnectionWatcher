// Pure helpers over a target's scope (unit-tested): LAN devices, resolvers, and the tree rule
// that mirrors the server's TargetScope.IsTree().
import type { TargetStatus } from "./types";

/** Local targets are LAN devices; a missing scope (older server) means Verdict. */
export const isLocal = (t: { scope?: string }): boolean => t.scope === "Local";

/** Resolver targets are DNS resolvers (spec 2026-10-10): never in the tree, never LAN devices. */
export const isResolver = (t: { scope?: string }): boolean => t.scope === "Resolver";

/** Targets that belong in the tree and the History ribbons: Scope Verdict (a missing scope, from an older server, means Verdict). Mirrors the server's TargetScope.IsTree(). */
export const isTreeTarget = (t: { scope?: string }): boolean => t.scope === "Verdict" || t.scope === undefined;

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
