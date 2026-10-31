// Trunk-and-pool layout for the reachability tree: the path to the internet
// renders flush-left as a spine, off-path targets hang off it on light rails,
// and the anchor pool is its own flat section. Falls back to "plain" (the
// classic nested tree) when the config gives us no trunk/anchor knowledge.
import type { TargetStatus } from "./types";

export type RailCell = "blank" | "pipe" | "tee" | "elbow";
export type SpineSegment = "none" | "top" | "bottom" | "both" | "pass";

export interface TreeRow {
  target: TargetStatus;
  section: "path" | "internet";
  kind: "trunk" | "branch" | "other" | "anchor";
  /** Light rail cells drawn before the dot (file-tree style). */
  rails: RailCell[];
  /** The heavy trunk spine segment this row draws at the dot column. */
  spine: SpineSegment;
}

export interface TreeLayout {
  mode: "trunk" | "plain";
  rows: TreeRow[];
}

interface Topo {
  parents: Record<string, string | null>;
  poolMembers: Record<string, string[]>;
}

/** Children by parent id; a target whose parent is not among `targets` (or is
 *  null/unknown) becomes a root under `null`. Input order is kept. */
export function groupByParent(targets: TargetStatus[],
  parents: Record<string, string | null>): Map<string | null, TargetStatus[]> {
  const ids = new Set(targets.map((t) => t.id));
  const byParent = new Map<string | null, TargetStatus[]>();
  for (const t of targets) {
    let parent = parents[t.id] ?? null;
    if (parent !== null && !ids.has(parent)) parent = null; // adopt orphans as roots
    const list = byParent.get(parent) ?? [];
    list.push(t);
    byParent.set(parent, list);
  }
  return byParent;
}

export function layoutTree(topology: Topo, targets: TargetStatus[],
  trunkNames?: string[], anchorNames?: string[]): TreeLayout {
  const trunk = new Set((trunkNames ?? []).map((n) => n.toLowerCase()));
  const anchors = new Set((anchorNames ?? []).map((n) => n.toLowerCase()));
  if (trunk.size === 0 || anchors.size === 0)
    return { mode: "plain", rows: [] };

  const byParent = groupByParent(targets, topology.parents);
  const isTrunk = (t: TargetStatus) => trunk.has(t.name.toLowerCase());
  const isAnchor = (t: TargetStatus) => anchors.has(t.name.toLowerCase());
  const rows: TreeRow[] = [];

  // Light-railed subtree (file-tree style). `prefix` carries, per ancestor
  // level, whether that ancestor has later siblings (draw a pipe) or not
  // (draw a blank).
  const emitRails = (t: TargetStatus, prefix: RailCell[], last: boolean,
    section: TreeRow["section"], kind: TreeRow["kind"]) => {
    rows.push({ target: t, section, kind, rails: [...prefix, last ? "elbow" : "tee"], spine: "none" });
    const kids = (byParent.get(t.id) ?? []).filter((k) => !isAnchor(k));
    const nextPrefix: RailCell[] = [...prefix, last ? "blank" : "pipe"];
    kids.forEach((k, i) => emitRails(k, nextPrefix, i === kids.length - 1, section, kind));
  };

  // Upper section: trunk nodes flush-left in path order; each trunk node's
  // off-path branches come right after it, then the path continues.
  const emitTrunk = (t: TargetStatus) => {
    rows.push({ target: t, section: "path", kind: "trunk", rails: [], spine: "none" });
    const kids = byParent.get(t.id) ?? [];
    const branches = kids.filter((k) => !isTrunk(k) && !isAnchor(k));
    branches.forEach((b, i) => emitRails(b, [], i === branches.length - 1, "path", "branch"));
    kids.filter(isTrunk).forEach(emitTrunk);
  };
  const roots = byParent.get(null) ?? [];
  roots.filter(isTrunk).forEach(emitTrunk);

  // Unrelated subtrees (roots that are neither trunk nor anchors) keep the
  // path section but never touch the spine.
  for (const r of roots.filter((t) => !isTrunk(t) && !isAnchor(t))) {
    rows.push({ target: r, section: "path", kind: "other", rails: [], spine: "none" });
    const kids = (byParent.get(r.id) ?? []).filter((k) => !isAnchor(k));
    kids.forEach((k, i) => emitRails(k, [], i === kids.length - 1, "path", "other"));
  }

  // Lower section: the anchor pool, flat, each anchor followed by anything
  // hung underneath it.
  for (const t of targets.filter(isAnchor)) {
    rows.push({ target: t, section: "internet", kind: "anchor", rails: [], spine: "none" });
    const kids = (byParent.get(t.id) ?? []).filter((k) => !isAnchor(k));
    kids.forEach((k, i) => emitRails(k, [], i === kids.length - 1, "internet", "branch"));
  }

  const trunkIdx = rows.reduce<number[]>((acc, r, i) => (r.kind === "trunk" ? [...acc, i] : acc), []);
  if (trunkIdx.length === 0)
    return { mode: "plain", rows: [] };
  const first = trunkIdx[0], lastT = trunkIdx[trunkIdx.length - 1];
  rows.forEach((r, i) => {
    if (i < first || i > lastT || first === lastT) return;
    if (r.kind === "trunk")
      r.spine = i === first ? "bottom" : i === lastT ? "top" : "both";
    else
      r.spine = "pass";
  });
  return { mode: "trunk", rows };
}
