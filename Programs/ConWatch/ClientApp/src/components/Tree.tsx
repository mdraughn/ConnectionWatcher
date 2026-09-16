// The reachability tree: plain rows, no tap-to-expand — the ledger and
// History carry the "what happened" story. With trunk/anchor knowledge the
// tree renders as trunk-and-pool: the path to the internet flush-left on a
// heavy spine, off-path targets railed off it, anchors as their own section.
import { layoutTree, type RailCell, type SpineSegment, type TreeRow } from "../lib/treeLayout";
import type { PoolStatus, TargetStatus } from "../lib/types";

export interface Topology {
  parents: Record<string, string | null>;
  poolMembers: Record<string, string[]>;
}

const SPINE_BG: Record<SpineSegment, string | undefined> = {
  none: undefined,
  top: "linear-gradient(var(--cw-spine), var(--cw-spine)) 14px 0 / 2px 50% no-repeat",
  bottom: "linear-gradient(var(--cw-spine), var(--cw-spine)) 14px 100% / 2px 50% no-repeat",
  both: "linear-gradient(var(--cw-spine), var(--cw-spine)) 14px 0 / 2px 100% no-repeat",
  pass: "linear-gradient(var(--cw-spine), var(--cw-spine)) 14px 0 / 2px 100% no-repeat",
};

function Row({ target, rails = [], spine = "none" }: {
  target: TargetStatus; rails?: RailCell[]; spine?: SpineSegment;
}) {
  const assumedTitle = target.assumed
    ? "assumed from before the restart — confirming with fresh pings" : null;
  const chipColor: Record<string, string> = {
    Faltering: "var(--cw-warn)", Recovering: "var(--cw-warn)",
    Down: "var(--cw-down)", Unreachable: "var(--cw-unreach)",
    Starting: "var(--cw-quiet)", Initializing: "var(--cw-quiet)",
  };
  const chip = chipColor[target.state];
  return (
    <div className="cw-row" title={assumedTitle ?? undefined}
      style={spine !== "none" ? { background: SPINE_BG[spine] } : undefined}>
      {rails.map((cell, i) => <span key={i} className={`cw-g ${cell}`}></span>)}
      <span className={`dot ${target.state.toLowerCase()}`}
        style={target.assumed ? { opacity: 0.45 } : undefined}></span>
      <span>{target.name}{target.assumed && <small style={{ opacity: 0.5 }}> (assumed)</small>}</span>
      <span className="addr">
        {target.address}{target.probe ? ` · ${target.probe.replace(":", "/")}` : ""}
      </span>
      <span className="stats">
        {chip && <span className="statechip" style={{
          color: chip, background: `color-mix(in srgb, ${chip} 14%, transparent)`,
          marginRight: 10,
        }}>{target.state}</span>}
        <span className="pct">{(target.successRate * 100).toFixed(0)}%</span>
        <span className="sep">·</span>
        <span className="rtt">{target.avgRtt.toFixed(0)} ms</span>
      </span>
      {/* Activity LED, drive-light style: keyed by the monotonic ping count,
          so a changed count remounts the node and replays the flash. */}
      {target.pingCount !== undefined &&
        <span className="cw-led" key={`led-${target.pingCount}`}
          title="ping activity" aria-hidden="true"></span>}
    </div>
  );
}

function Section({ label, rows }: { label: string; rows: TreeRow[] }) {
  if (rows.length === 0) return null;
  return (
    <>
      <div className="cw-treecap">{label}</div>
      {rows.map((r) => <Row key={r.target.id} target={r.target} rails={r.rails} spine={r.spine} />)}
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
          <Row target={t} />
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
    const byParent = new Map<string | null, TargetStatus[]>();
    for (const t of targets) {
      const parent = topology.parents[t.id] ?? null;
      const list = byParent.get(parent) ?? [];
      list.push(t);
      byParent.set(parent, list);
    }
    // Targets whose parent is unknown to the topology map render as roots.
    for (const [parent, list] of [...byParent]) {
      if (parent !== null && !targetMap.has(parent)) {
        const roots = byParent.get(null) ?? [];
        byParent.set(null, roots.concat(list));
        byParent.delete(parent);
      }
    }
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
