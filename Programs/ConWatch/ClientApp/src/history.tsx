// History island: subject + range pickers, stats, charts, and the ribbon.
import { useEffect, useMemo, useState } from "react";
import { createRoot } from "react-dom/client";
import { Ledger } from "./components/Ledger";
import { StateRibbon } from "./components/StateRibbon";
import { StatsRow } from "./components/StatsRow";
import { TimeChart } from "./components/TimeChart";
import { Punchcard } from "./components/Punchcard";
import { RibbonBoard, type BoardSubject } from "./components/RibbonBoard";
import { isInternetTarget } from "./lib/lan";
import { fetchStory, type Story, type StoryTopology } from "./lib/story";
import { clipAtReset, incidents, ribbonSegments, subjectsForScope, uptimePercent, type HistoryScope } from "./lib/historyData";
import type { RollupPoint, TransitionEvent } from "./lib/types";

interface Subject {
  id: string;
  name: string;
  kind: "target" | "pool" | "verdict";
  parent: string | null;
  role: "trunk" | "anchor" | "other";
  scope?: "Verdict" | "Local" | "Resolver";
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

// The recurrence punchcard and the cause-aligned board always cover 30 days,
// independent of the range picker: recurrence needs columns to compare, and
// short spans stretch both into mush.
const OVERVIEW_DAYS = 30;

function RecurrenceSection({ to }: { to: Date }) {
  const topology = JSON.parse(
    document.getElementById("history-topology")?.textContent ?? "{}") as StoryTopology;
  const [story, setStory] = useState<Story | null>(null);

  useEffect(() => {
    let live = true;
    fetchStory(topology, OVERVIEW_DAYS).then((s) => { if (live) setStory(s); }).catch(() => undefined);
    return () => { live = false; };
  }, [to.getTime()]);

  if (!story) return null;
  return (
    <div id="recurrence-section">
      <h4>Recurrence punchcard <small>(Internet targets; hours &times; days, last {OVERVIEW_DAYS} days)</small></h4>
      <Punchcard cells={story.cells} days={story.days} now={story.now} />
    </div>
  );
}

const LEDGER_DAYS = 90;

function LedgerSection() {
  const topology = JSON.parse(
    document.getElementById("history-topology")?.textContent ?? "{}") as StoryTopology;
  const [story, setStory] = useState<Story | null>(null);

  useEffect(() => {
    let live = true;
    fetchStory(topology, LEDGER_DAYS).then((s) => { if (live) setStory(s); }).catch(() => undefined);
    return () => { live = false; };
  }, []);

  if (!story) return null;
  return (
    <div id="ledger-section">
      <h4>What happened — the last {story.days} days</h4>
      <Ledger items={story.ledger} days={story.days} maxItems={500} />
    </div>
  );
}

const SCOPES: ReadonlyArray<readonly [HistoryScope, string]> =
  [["path", "Path"], ["pools", "Pools"], ["local", "LAN"]];

function App({ subjects }: { subjects: Subject[] }) {
  const groups = SCOPES.filter(([key]) => subjectsForScope(subjects, key).length > 0);
  const [scope, setScope] = useState<HistoryScope>(groups[0]?.[0] ?? "path");
  const visible = subjectsForScope(subjects, scope);
  const [subjectId, setSubjectId] = useState(visible[0]?.id ?? "");
  const [rangeKey, setRangeKey] = useState("24h");
  const [rollups, setRollups] = useState<RollupPoint[]>([]);
  const [transitions, setTransitions] = useState<TransitionEvent[]>([]);
  const [loading, setLoading] = useState(false);

  const subject = visible.find((s) => s.id === subjectId);
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

  const scopeToggle = groups.length > 1 && (
    <div className="cw-segmented" style={{ alignSelf: "center" }}>
      {groups.map(([key, label]) => (
        <button key={key} type="button" className={scope === key ? "active" : ""}
          onClick={() => {
            if (key === scope) return;
            setScope(key);
            setSubjectId(subjectsForScope(subjects, key)[0]?.id ?? "");
          }}>{label}</button>
      ))}
    </div>
  );

  if (!subject) return (
    <div>
      <div style={{ display: "flex", gap: "1rem", flexWrap: "wrap" }}>{scopeToggle}</div>
      <p>{subjects.length > 0 ? "No subjects in this view." : "No subjects yet — add targets on the Targets page."}</p>
    </div>
  );

  // Attribution stops at the latest HistoryReset (address correction/repoint):
  // stats and charts use only the current data history; the ribbon still shows
  // the earlier era, dimmed.
  const { transitions: clipped, resetAt } = clipAtReset(transitions);
  const measuredRollups = resetAt
    ? rollups.filter((p) => p.periodStart >= resetAt)
    : rollups;

  const stats = incidents(clipped, (resetAt ?? from.toISOString()), to.toISOString());
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
        {scopeToggle}
        <select value={subjectId} onChange={(e) => setSubjectId(e.target.value)} style={{ maxWidth: "18rem" }}>
          {visible.map((s) => (
            <option key={s.id} value={s.id}>
              {s.kind === "verdict" ? "🌐 " : s.kind === "pool" ? "▣ " : ""}{s.name}
            </option>
          ))}
        </select>
        <div className="cw-segmented" style={{ alignSelf: "center" }}>
          {[["24h", "24 h"], ["7d", "7 days"], ["30d", "30 days"]].map(([key, label]) => (
            <button key={key} type="button" className={rangeKey === key ? "active" : ""}
              onClick={() => setRangeKey(key)}>{label}</button>
          ))}
        </div>
        <small style={{ alignSelf: "center", opacity: 0.55 }}>
          times in {Intl.DateTimeFormat().resolvedOptions().timeZone} (your browser&apos;s zone);
          the ISP report shows UTC alongside
        </small>
      </div>

      {loading && <p aria-busy="true">loading…</p>}

      <StatsRow uptime={uptime} outageCount={stats.outageCount}
        longestOutageMs={stats.longestOutageMs} unreachableMs={stats.unreachableMs} avgRtt={avgRtt} />

      <h4>State timeline</h4>
      <StateRibbon segments={segments} rangeStart={from} rangeEnd={to} resetAt={resetAt}
        kind={subject.kind} role={subject.role} />

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
        <p style={{ opacity: 0.7 }}>
          {subject.kind === "pool"
            ? "Pools have no ping rollups; their story is the timeline above."
            : "The verdict has no ping rollups; its story is the timeline above."}
        </p>
      )}

      {scope === "path" && (
        <>
          <RecurrenceSection to={to} />

          <h4>Internet targets, cause-aligned <small>(last {OVERVIEW_DAYS} days)</small></h4>
          <RibbonBoard rangeStart={new Date(to.getTime() - OVERVIEW_DAYS * 24 * 3_600_000)} rangeEnd={to}
            subjects={subjects.filter((s): s is Subject & { kind: "target" } => s.kind === "target" && isInternetTarget(s))
              .map((s): BoardSubject => ({
                id: s.id, name: s.name, kind: s.kind,
                parent: s.parent ?? null, role: s.role ?? "other",
              }))} />

          <LedgerSection />
        </>
      )}
    </div>
  );
}

const el = document.getElementById("history-root");
const dataEl = document.getElementById("history-data");
if (el && dataEl) {
  const subjects = JSON.parse(dataEl.textContent ?? "[]") as Subject[];
  createRoot(el).render(<App subjects={subjects} />);
}
