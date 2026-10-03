// "How's my LAN": the LAN devices tracked beside the verdict. Never colours
// the verdict, the tree or the tray; its own summary and rows only.
import { isLocal, sinceLabel, summarizeLan } from "../lib/lan";
import type { TargetStatus } from "../lib/types";

export function LanSection({ targets, changes }: {
  targets: TargetStatus[];
  /** Last-change ISO time per target id. */
  changes: Record<string, string>;
}) {
  const lan = targets.filter(isLocal);
  if (lan.length === 0) return null;
  const { up, total } = summarizeLan(targets);
  const now = new Date();
  return (
    <div id="lan-section" style={{ marginTop: "1.5rem" }}>
      <h4>How&apos;s my LAN <small>({up} of {total} up)</small></h4>
      <ul className="cw-tree">
        {lan.map((t) => (
          <li key={t.id}>
            <div className="cw-row">
              <span className={`dot ${t.state.toLowerCase()}`}></span>
              <span>{t.name}</span>
              <span className="addr">
                {t.address}{t.probe ? ` · ${t.probe.replace(":", "/")}` : ""}
              </span>
              <span className="stats">
                <span>{t.state}</span>
                <span className="sep">·</span>
                <span title="since the last state change">{sinceLabel(changes[t.id], now)}</span>
              </span>
            </div>
          </li>
        ))}
      </ul>
    </div>
  );
}
