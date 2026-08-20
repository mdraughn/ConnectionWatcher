// Bottom status line + slide-up recent-events panel.
import { useState } from "react";
import type { TransitionEvent } from "../lib/types";

function describe(t: TransitionEvent): string {
  const time = new Date(t.timestamp).toLocaleTimeString();
  const extra = t.verdict ?? t.detail ??
    (t.successRate != null ? `success ${(t.successRate * 100).toFixed(0)}%` : "");
  return `${time}  ${t.subjectName}: ${t.from} → ${t.to}${extra ? ` (${extra})` : ""}`;
}

export function StatusBar({ latest, recent, connected }: {
  latest: TransitionEvent | null;
  recent: TransitionEvent[];
  connected: boolean;
}) {
  const [open, setOpen] = useState(false);
  return (
    <>
      {open && (
        <div className="cw-events-panel">
          <table>
            <tbody>
              {recent.map((t, i) => (
                <tr key={t.id ?? `${t.timestamp}-${i}`}>
                  <td>{describe(t)}</td>
                </tr>
              ))}
              {recent.length === 0 && <tr><td>No events yet.</td></tr>}
            </tbody>
          </table>
        </div>
      )}
      <div className="cw-statusbar">
        <span>{latest ? describe(latest) : connected ? "All quiet." : "Connecting…"}</span>
        <span className="events-toggle" onClick={() => setOpen(!open)}>
          {open ? "hide events ▾" : "show all events ▴"}
        </span>
      </div>
    </>
  );
}
