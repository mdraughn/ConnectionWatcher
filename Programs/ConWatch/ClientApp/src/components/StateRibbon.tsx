// The state-timeline ribbon: colored stripes from the transition log.
import type { RibbonSegment } from "../lib/historyData";

const COLORS: Record<string, string> = {
  Responding: "#2e9e52",
  Healthy: "#2e9e52",
  SiteIncident: "#7db85c",
  Faltering: "#e08a00",
  Recovering: "#e08a00",
  InternetWeather: "#e08a00",
  LinkDegraded: "#e08a00",
  Down: "#cc3333",
  LinkDown: "#cc3333",
  Unreachable: "#999999",
  Starting: "#6aa88a",
  Initializing: "#6aa88a",
  Unknown: "#cccccc",
};

export function StateRibbon({ segments, rangeStart, rangeEnd, resetAt }: {
  segments: RibbonSegment[];
  rangeStart: Date;
  rangeEnd: Date;
  /** Latest HistoryReset marker: segments before it render dimmed (different data history). */
  resetAt?: string | null;
}) {
  const total = rangeEnd.getTime() - rangeStart.getTime();
  if (total <= 0) return null;
  const clampPct = (ms: number) => Math.max(0, Math.min(100, (100 * (ms - rangeStart.getTime())) / total));
  const resetMs = resetAt ? Date.parse(resetAt) : null;
  return (
    <svg className="cw-ribbon" viewBox="0 0 100 10" preserveAspectRatio="none">
      {segments.map((s, i) => {
        const x = clampPct(Date.parse(s.from));
        const width = clampPct(Date.parse(s.to)) - x;
        if (width <= 0) return null;
        const preReset = resetMs !== null && Date.parse(s.from) < resetMs;
        const note = preReset ? "\n(before address change — different data history)" : "";
        return (
          <rect key={i} x={x} y="0" width={width} height="10"
            fill={COLORS[s.state] ?? COLORS.Unknown} opacity={preReset ? 0.35 : 1}>
            <title>{`${s.state}: ${new Date(s.from).toLocaleString()} → ${new Date(s.to).toLocaleString()}${s.evidence ? `\n${s.evidence}` : ""}${note}`}</title>
          </rect>
        );
      })}
    </svg>
  );
}
