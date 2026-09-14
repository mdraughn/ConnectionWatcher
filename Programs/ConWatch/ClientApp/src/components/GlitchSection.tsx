// Recent glitches: the small stuff below the outage threshold, as a table,
// with a chronic-target callout. Fetches its own data. Placement-agnostic.
import { useEffect, useState } from "react";
import { chronicTargets, type GlitchItem } from "../lib/glitchData";

async function fetchGlitches(from: Date, to: Date): Promise<GlitchItem[]> {
  const url = `/api/v1/glitches?from=${from.toISOString()}&to=${to.toISOString()}&limit=1000`;
  const response = await fetch(url);
  if (!response.ok) return [];
  return (await response.json()).items as GlitchItem[];
}

export function GlitchSection({ from, to, subtitle }: {
  from: Date;
  to: Date;
  subtitle?: string;
}) {
  const [glitches, setGlitches] = useState<GlitchItem[]>([]);

  useEffect(() => {
    let live = true;
    fetchGlitches(from, to).then((g) => { if (live) setGlitches(g); });
    return () => { live = false; };
  }, [from.getTime(), to.getTime()]);

  const chronic = chronicTargets(glitches, to);
  const recent = glitches.slice(0, 15);

  return (
    <div id="glitch-section">
      <h4>Recent glitches{subtitle && <> <small>({subtitle})</small></>}</h4>
      {chronic.length > 0 && (
        <p>⚠ Chronically glitchy (3+ separate hours in the last 24 h): <b>{chronic.join(", ")}</b></p>
      )}
      {recent.length === 0 && (
        <p style={{ opacity: 0.7 }}>No glitches recorded in this range. Quiet is good.</p>
      )}
      {recent.length > 0 && (
        <table className="cw-stack-cards">
          <thead>
            <tr><th>When</th><th>Subject</th><th>Class</th><th>Lost</th><th>Duration</th><th>Detail</th></tr>
          </thead>
          <tbody>
            {recent.map((g) => (
              <tr key={g.id}>
                <td>{new Date(g.startUtc).toLocaleString()}</td>
                <td>{g.targetName}</td>
                <td>{g.class === "Internet" ? "🌐 internet" : "target"}</td>
                <td>{g.lostCount}/{g.sampleCount}</td>
                <td>{Math.max(1, Math.round((new Date(g.endUtc).getTime() - new Date(g.startUtc).getTime()) / 1000))}s</td>
                <td>{g.detail ?? ""}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </div>
  );
}
