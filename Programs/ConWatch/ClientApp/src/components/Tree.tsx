// The reachability tree: plain rows, no tap-to-expand — the ledger and
// History carry the "what happened" story. With trunk/anchor knowledge the
// tree renders as trunk-and-pool: the path to the internet flush-left on a
// heavy spine, off-path targets railed off it, anchors as their own section.
import { groupByParent, layoutTree, type TreeRow } from "../lib/treeLayout";
import { TargetRow } from "./TargetRow";
import type { PoolStatus, TargetStatus } from "../lib/types";

export interface Topology {
  parents: Record<string, string | null>;
  poolMembers: Record<string, string[]>;
}

function Section({ label, rows }: { label: string; rows: TreeRow[] }) {
  if (rows.length === 0) return null;
  return (
    <>
      <div className="cw-treecap">{label}</div>
      {rows.map((r) => <TargetRow key={r.target.id} target={r.target} rails={r.rails} spine={r.spine} />)}
    </>
  );
}

function Branch({ parentId, targets, byParent }: {
  parentId: string | null;
  targets: Map<string, TargetStatus>;
  byParent: Map<string | null, TargetStatus[]>;
}) {
  const children = byParent.get(parentId) ?? [];
  if (children.length === 0) return null;
  return (
    <ul className={parentId === null ? "cw-tree" : ""}>
      {children.map((t) => (
        <li key={t.id}>
          <TargetRow target={t} />
          <Branch parentId={t.id} targets={targets} byParent={byParent} />
        </li>
      ))}
    </ul>
  );
}

export function Tree({ topology, targets, pools, trunkNames, anchorNames }: {
  topology: Topology;
  targets: TargetStatus[];
  pools: PoolStatus[];
  trunkNames?: string[];
  anchorNames?: string[];
}) {
  const targetMap = new Map(targets.map((t) => [t.id, t]));
  const layout = layoutTree(topology, targets, trunkNames, anchorNames);

  let body: React.ReactNode;
  if (layout.mode === "trunk") {
    body = (
      <div>
        <Section label="Path to the internet" rows={layout.rows.filter((r) => r.section === "path")} />
        <Section label="The internet" rows={layout.rows.filter((r) => r.section === "internet")} />
      </div>
    );
  } else {
    // Plain fallback: the classic nested tree for configs without an anchor
    // pool (nothing to call "the path" yet).
    const byParent = groupByParent(targets, topology.parents);
    body = <Branch parentId={null} targets={targetMap} byParent={byParent} />;
  }

  return (
    <div>
      {body}
      {pools.length > 0 && (
        <>
          <h4 style={{ margin: "0.8rem 0 0.2rem" }}>Pools</h4>
          <ul className="cw-tree">
            {pools.map((p) => (
              <li key={p.id}>
                <div className="cw-row">
                  <span className={`dot ${p.state.toLowerCase()}`}></span>
                  <span>{p.name}</span>
                  <span className="stats">
                    {(topology.poolMembers[p.id] ?? [])
                      .map((id) => targetMap.get(id)?.name)
                      .filter(Boolean)
                      .join(", ")}
                  </span>
                </div>
              </li>
            ))}
          </ul>
        </>
      )}
    </div>
  );
}
