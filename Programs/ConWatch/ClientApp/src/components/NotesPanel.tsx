// "ConnectionWatcher notes": admin-facing, never alarming (spec 2026-10-09
// §4.6, D3). Where the map disagrees with what the probes prove; acknowledging
// hides a note until it clears and comes back.
import { useEffect } from "react";
import type { ModelWarning } from "../lib/types";

export function unacknowledgedCount(warnings: ModelWarning[]): number {
  return warnings.filter((w) => !w.acknowledged).length;
}

export async function acknowledgeWarning(key: string): Promise<boolean> {
  const r = await fetch(`/api/v1/warnings/${encodeURIComponent(key)}/acknowledge`, { method: "POST" });
  return r.ok;
}

export function NotesPanel({ warnings, onAcknowledge }: {
  warnings: ModelWarning[];
  onAcknowledge?: (key: string) => void;
}) {
  const any = warnings.length > 0;
  useEffect(() => {
    // The header badge links to /#notes; the panel renders after load.
    if (any && location.hash === "#notes")
      document.getElementById("notes")?.scrollIntoView();
  }, [any]);
  if (!any) return null;
  return (
    <section id="notes" className="cw-notes" aria-label="ConnectionWatcher notes">
      <h3>ConnectionWatcher notes</h3>
      <p className="cw-notes-intro">
        Where ConnectionWatcher's map of your network disagrees with what the
        probes prove. These never change the verdict.
      </p>
      <ul>
        {warnings.map((w) => (
          <li key={w.key} className={w.acknowledged ? "acknowledged" : undefined}>
            <span className="message">{w.message}</span>
            <span className="since">since {new Date(w.since).toLocaleString()}</span>
            {w.acknowledged
              ? <span className="ack">acknowledged</span>
              : onAcknowledge && (
                <button type="button" className="secondary outline"
                  onClick={() => onAcknowledge(w.key)}>Acknowledge</button>)}
          </li>
        ))}
      </ul>
    </section>
  );
}
