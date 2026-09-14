// The state-timeline ribbon: colored stripes from the transition log, in the
// shared incident encoding — grey ramp for trunk rows (your equipment), warm
// for everything else, verdict names mapped by whose gear was blamed, and
// hatching for Unreachable (inherited, no data, no blame).
import { ribbonFill, verdictFill, type RowRole } from "../lib/encoding";
import type { RibbonSegment } from "../lib/historyData";

export function StateRibbon({ segments, rangeStart, rangeEnd, resetAt, kind = "target", role = "other" }: {
  segments: RibbonSegment[];
  rangeStart: Date;
  rangeEnd: Date;
  /** Latest HistoryReset marker: segments before it render dimmed (different data history). */
  resetAt?: string | null;
  kind?: "target" | "pool" | "verdict";
  role?: RowRole;
}) {
  const total = rangeEnd.getTime() - rangeStart.getTime();
  if (total <= 0) return null;
  const clampPct = (ms: number) => Math.max(0, Math.min(100, (100 * (ms - rangeStart.getTime())) / total));
  const resetMs = resetAt ? Date.parse(resetAt) : null;
  const fillFor = (s: RibbonSegment) =>
    kind === "verdict" ? verdictFill(s.evidence, s.state) : ribbonFill(role, s.state);
  return (
    <svg className="cw-ribbon" viewBox="0 0 100 10" preserveAspectRatio="none">
      <defs>
        <pattern id="cw-hatch-timeline" width="6" height="6" patternTransform="rotate(45)"
          patternUnits="userSpaceOnUse">
          <rect width="6" height="6" fill="var(--surface-2)" />
          <rect width="3" height="6" fill="var(--grey-glitch)" />
        </pattern>
      </defs>
      {segments.map((s, i) => {
        const x = clampPct(Date.parse(s.from));
        const width = clampPct(Date.parse(s.to)) - x;
        if (width <= 0) return null;
        const preReset = resetMs !== null && Date.parse(s.from) < resetMs;
        const note = preReset ? "\n(before address change — different data history)" : "";
        const fill = fillFor(s);
        const label = s.state === "Unreachable"
          ? "Unreachable behind a down parent (no data, no blame)"
          : kind === "verdict" && s.evidence ? s.evidence : s.state;
        return (
          <rect key={i} x={x} y="0" width={width} height="10"
            fill={fill === "hatch" ? "url(#cw-hatch-timeline)" : fill}
            opacity={preReset ? 0.35 : 1}>
            <title>{`${label}: ${new Date(s.from).toLocaleString()} → ${new Date(s.to).toLocaleString()}${s.evidence && s.evidence !== label ? `\n${s.evidence}` : ""}${note}`}</title>
          </rect>
        );
      })}
    </svg>
  );
}
