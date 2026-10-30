// "How's my LAN": the LAN devices tracked beside the verdict. Never colours
// the verdict, the tree or the tray; its own summary and rows only.
import { isLocal, summarizeLan } from "../lib/scope";
import type { TargetStatus } from "../lib/types";
import { TargetRow } from "./TargetRow";

export function LanSection({ targets }: { targets: TargetStatus[] }) {
  const lan = targets.filter(isLocal);
  if (lan.length === 0) return null;
  const { up, total } = summarizeLan(targets);
  return (
    <div id="lan-section" style={{ marginTop: "1.5rem" }}>
      <h4>How&apos;s my LAN <small>({up} of {total} up)</small></h4>
      <ul className="cw-tree">
        {lan.map((t) => (
          <li key={t.id}>
            <TargetRow target={t} show={{ stateChip: false, rtt: false }} />
          </li>
        ))}
      </ul>
    </div>
  );
}
