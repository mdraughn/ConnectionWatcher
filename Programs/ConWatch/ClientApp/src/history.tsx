// History island: subject + range pickers, stats, charts, and the ribbon.
import { Fragment, useEffect, useMemo, useState } from "react";
import { createRoot } from "react-dom/client";
import { StateRibbon } from "./components/StateRibbon";
import { StatsRow } from "./components/StatsRow";
import { TimeChart } from "./components/TimeChart";
import { RibbonBoard, type BoardSubject } from "./components/RibbonBoard";
import { clipAtReset, incidents, ribbonSegments, uptimePercent } from "./lib/historyData";
import { chronicTargets, type GlitchItem } from "./lib/glitchData";
import type { RollupPoint, TransitionEvent } from "./lib/types";

interface Subject {
  id: string;
  name: string;
  kind: "target" | "pool" | "verdict";
  parent: string | null;
  role: "trunk" | "anchor" | "other";
}

const RANGES: Record<string, number> = {
  "24h": 24 * 3_600_000,
  "7d": 7 * 24 * 3_600_000,
  "30d": 30 * 24 * 3_600_000,
};

async function fetchRollups(subjectId: string, from: Date, to: Date): Promise<RollupPoint[]> {
  const url = `/api/v1/rollups/${subjectId}?from=${from.toISOString()}&to=${to.toISOString()}&resolution=auto`;
  const response = await fetch(url);
  if (!response.ok) return [];
  return (await response.json()).points as RollupPoint[];
}

async function fetchTransitions(subjectId: string, from: Date, to: Date): Promise<TransitionEvent[]> {
  const url = `/api/v1/transitions?subjectId=${subjectId}&from=${from.toISOString()}&to=${to.toISOString()}&limit=1000`;
  const response = await fetch(url);
  if (!response.ok) return [];
  const items = (await response.json()).items as TransitionEvent[];
  return items.reverse(); // API is newest-first; we want chronological
}

async function fetchGlitches(from: Date, to: Date): Promise<GlitchItem[]> {
  const url = `/api/v1/glitches?from=${from.toISOString()}&to=${to.toISOString()}&limit=1000`;
  const response = await fetch(url);
  if (!response.ok) return [];
  return (await response.json()).items as GlitchItem[];
}

function GlitchSection({ from, to, rangeKey }: { from: Date; to: Date; rangeKey: string }) {
  const [glitches, setGlitches] = useState<GlitchItem[]>([]);

  useEffect(() => {
    let live = true;
    fetchGlitches(from, to).then((g) => { if (live) setGlitches(g); });
    return () => { live = false; };
  }, [rangeKey, from, to]);

  const chronic = chronicTargets(glitches, to);
  const recent = glitches.slice(0, 15);

  return (
    <div id="glitch-section">
      <h4>Recent glitches <small>(all targets; the recurrence punchcard lives on the dashboard)</small></h4>
      {chronic.length > 0 && (
        <p>⚠ Chronically glitchy (3+ separate hours in the last 24 h): <b>{chronic.join(", ")}</b></p>
      )}
      {recent.length === 0 && (
        <p style={{ opacity: 0.7 }}>No glitches recorded in this range. Quiet is good.</p>
      )}
      {recent.length > 0 && (
        <table>
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

function App({ subjects }: { subjects: Subject[] }) {
  const [subjectId, setSubjectId] = useState(subjects[0]?.id ?? "");
  const [rangeKey, setRangeKey] = useState("24h");
  const [rollups, setRollups] = useState<RollupPoint[]>([]);
  const [transitions, setTransitions] = useState<TransitionEvent[]>([]);
  const [loading, setLoading] = useState(false);

  const subject = subjects.find((s) => s.id === subjectId);
  const to = useMemo(() => new Date(), [subjectId, rangeKey]);
  const from = useMemo(() => new Date(to.getTime() - (RANGES[rangeKey] ?? RANGES["24h"])), [to, rangeKey]);

  useEffect(() => {
    if (!subject) return;
    let live = true;
    setLoading(true);
    Promise.all([
      subject.kind === "target" ? fetchRollups(subject.id, from, to) : Promise.resolve([]),
      fetchTransitions(subject.id, from, to),
    ]).then(([r, t]) => {
      if (!live) return;
      setRollups(r);
      setTransitions(t);
      setLoading(false);
    });
    return () => { live = false; };
  }, [subject, from, to]);

  if (!subject) return <p>No subjects yet — add targets on the Config page.</p>;

  // Attribution stops at the latest HistoryReset (address correction/repoint):
  // stats and charts use only the current data history; the ribbon still shows
  // the earlier era, dimmed.
  const { transitions: clipped, resetAt } = clipAtReset(transitions);
  const measuredRollups = resetAt
    ? rollups.filter((p) => p.periodStart >= resetAt)
    : rollups;

  const stats = incidents(clipped, to.toISOString());
  const uptime = subject.kind === "target" ? uptimePercent(measuredRollups) : null;
  const totalSuccess = measuredRollups.reduce((n, p) => n + p.successCount, 0);
  const avgRtt = totalSuccess > 0
    ? measuredRollups.reduce((n, p) => n + p.rttAvg * p.successCount, 0) / totalSuccess
    : null;
  const latency = measuredRollups.map((p) => ({ x: new Date(p.periodStart), y: p.rttAvg }));
  const lossBars = measuredRollups.map((p) => ({
    x: new Date(p.periodStart),
    y: p.pingCount > 0 ? (100 * (p.pingCount - p.successCount)) / p.pingCount : null,
  }));
  const segments = ribbonSegments(transitions, from.toISOString(), to.toISOString());

  return (
    <div>
      <div style={{ display: "flex", gap: "1rem", flexWrap: "wrap" }}>
        <select value={subjectId} onChange={(e) => setSubjectId(e.target.value)} style={{ maxWidth: "18rem" }}>
          {subjects.map((s) => (
            <option key={s.id} value={s.id}>
              {s.kind === "verdict" ? "🌐 " : s.kind === "pool" ? "▣ " : ""}{s.name}
            </option>
          ))}
        </select>
        <select value={rangeKey} onChange={(e) => setRangeKey(e.target.value)} style={{ maxWidth: "8rem" }}>
          <option value="24h">Last 24 h</option>
          <option value="7d">Last 7 days</option>
          <option value="30d">Last 30 days</option>
        </select>
      </div>

      {loading && <p aria-busy="true">loading…</p>}

      <StatsRow uptime={uptime} outageCount={stats.outageCount}
        longestOutageMs={stats.longestOutageMs} unreachableMs={stats.unreachableMs} avgRtt={avgRtt} />

      <h4>State timeline</h4>
      <StateRibbon segments={segments} rangeStart={from} rangeEnd={to} resetAt={resetAt} />

      {subject.kind === "target" && (
        <>
          <h4>Latency (avg ms)</h4>
          <div style={{ height: 180 }}>
            <TimeChart series={[{ label: "RTT avg", data: latency }]} height={180} />
          </div>
          <h4>Loss (%)</h4>
          <div style={{ height: 120 }}>
            <TimeChart series={[{ label: "loss %", data: lossBars }]} height={120} />
          </div>
        </>
      )}
      {subject.kind !== "target" && (
        <p style={{ opacity: 0.7 }}>Pools and the verdict have no ping rollups; their story is the timeline above.</p>
      )}

      <h4>All targets, cause-aligned</h4>
      <RibbonBoard rangeStart={from} rangeEnd={to}
        subjects={subjects.filter((s): s is Subject & { kind: "target" } => s.kind === "target")
          .map((s): BoardSubject => ({
            id: s.id, name: s.name, kind: s.kind,
            parent: s.parent ?? null, role: s.role ?? "other",
          }))} />

      <GlitchSection from={from} to={to} rangeKey={rangeKey} />
    </div>
  );
}

const el = document.getElementById("history-root");
const dataEl = document.getElementById("history-data");
if (el && dataEl) {
  const subjects = JSON.parse(dataEl.textContent ?? "[]") as Subject[];
  createRoot(el).render(<App subjects={subjects} />);
}
