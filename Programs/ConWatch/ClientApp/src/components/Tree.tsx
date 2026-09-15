// The reachability tree: plain rows, no tap-to-expand — the ledger and
// History carry the "what happened" story.
import type { PoolStatus, TargetStatus } from "../lib/types";

export interface Topology {
  parents: Record<string, string | null>;
  poolMembers: Record<string, string[]>;
}

function Row({ target }: { target: TargetStatus }) {
  const statsText = `${(target.successRate * 100).toFixed(0)}% · ${target.avgRtt.toFixed(0)} ms`;
  const assumedTitle = target.assumed
    ? "assumed from before the restart — confirming with fresh pings" : null;
  const chipColor: Record<string, string> = {
    Faltering: "var(--cw-warn)", Recovering: "var(--cw-warn)",
    Down: "var(--cw-down)", Unreachable: "var(--cw-unreach)",
    Starting: "var(--cw-quiet)", Initializing: "var(--cw-quiet)",
  };
  const chip = chipColor[target.state];
  return (
    <div className="cw-row" title={assumedTitle ?? undefined}>
      <span className={`dot ${target.state.toLowerCase()}`}
        style={target.assumed ? { opacity: 0.45 } : undefined}></span>
      <span>{target.name}{target.assumed && <small style={{ opacity: 0.5 }}> (assumed)</small>}</span>
      <span className="addr">{target.address}</span>
      <span className="stats">
        {chip && <span className="statechip" style={{
          color: chip, background: `color-mix(in srgb, ${chip} 14%, transparent)`,
          marginRight: 10,
        }}>{target.state}</span>}
        {statsText}
      </span>
    </div>
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

export function Tree({ topology, targets, pools }: {
  topology: Topology;
  targets: TargetStatus[];
  pools: PoolStatus[];
}) {
  const targetMap = new Map(targets.map((t) => [t.id, t]));
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

  return (
    <div>
      <Branch parentId={null} targets={targetMap} byParent={byParent} />
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
