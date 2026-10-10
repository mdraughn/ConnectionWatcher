// One target row, shared by the verdict tree and the "How's my LAN" section.
// Defaults reproduce the verdict tree; `show` switches parts off.
import type { RailCell, SpineSegment } from "../lib/treeLayout";
import type { TargetStatus } from "../lib/types";

export interface RowParts {
  stateChip?: boolean;
  successRate?: boolean;
  rtt?: boolean;
  led?: boolean;
}

const SPINE_BG: Record<SpineSegment, string | undefined> = {
  none: undefined,
  top: "linear-gradient(var(--cw-spine), var(--cw-spine)) 14px 0 / 2px 50% no-repeat",
  bottom: "linear-gradient(var(--cw-spine), var(--cw-spine)) 14px 100% / 2px 50% no-repeat",
  both: "linear-gradient(var(--cw-spine), var(--cw-spine)) 14px 0 / 2px 100% no-repeat",
  pass: "linear-gradient(var(--cw-spine), var(--cw-spine)) 14px 0 / 2px 100% no-repeat",
};

const CHIP_COLOR: Record<string, string> = {
  Faltering: "var(--cw-warn)", Recovering: "var(--cw-warn)",
  Down: "var(--cw-down)", Unreachable: "var(--cw-unreach)",
  Starting: "var(--cw-quiet)", Initializing: "var(--cw-quiet)",
};

export function TargetRow({ target, rails = [], spine = "none", show }: {
  target: TargetStatus; rails?: RailCell[]; spine?: SpineSegment; show?: RowParts;
}) {
  const { stateChip = true, successRate = true, rtt = true, led = true } = show ?? {};
  const assumedTitle = target.assumed
    ? "assumed from before the restart — confirming with fresh pings" : null;
  // a critical row always shows its state (spec §4.7: prominently)
  const chip = (stateChip || target.critical) ? CHIP_COLOR[target.state] : undefined;
  return (
    <div className="cw-row" title={assumedTitle ?? undefined}
      style={spine !== "none" ? { background: SPINE_BG[spine] } : undefined}>
      {rails.map((cell, i) => <span key={i} className={`cw-g ${cell}`}></span>)}
      <span className={`dot ${target.state.toLowerCase()}`}
        style={target.assumed ? { opacity: 0.45 } : undefined}></span>
      <span>{target.name}{target.assumed && <small style={{ opacity: 0.5 }}> (assumed)</small>}
        {target.critical && <span className="cw-critical"
          title="Notified when this is down, even if the internet is fine">critical</span>}</span>
      <span className="addr">
        {target.address}{target.probe ? ` · ${target.probe.replace(":", "/")}` : ""}
      </span>
      <span className="stats">
        {chip && <span className="statechip" style={{
          color: chip, background: `color-mix(in srgb, ${chip} 14%, transparent)`,
          marginRight: 10,
        }}>{target.state}</span>}
        {successRate && <span className="pct">{(target.successRate * 100).toFixed(0)}%</span>}
        {successRate && rtt && <span className="sep">·</span>}
        {rtt && <span className="rtt">{target.avgRtt.toFixed(0)} ms</span>}
      </span>
      {/* Activity LED, drive-light style: keyed by the monotonic ping count,
          so a changed count remounts the node and replays the flash. */}
      {led && target.pingCount !== undefined &&
        <span className="cw-led" key={`led-${target.pingCount}`}
          title="ping activity" aria-hidden="true"></span>}
    </div>
  );
}
