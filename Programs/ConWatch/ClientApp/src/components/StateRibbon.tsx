// The state-timeline ribbon: colored stripes from the transition log, in the
// shared incident encoding — grey ramp for trunk rows (your equipment), warm
// for everything else, verdict names mapped by whose gear was blamed, and
// hatching for Unreachable (explained by an upstream failure; no data
// recorded, no blame).
import type { ReactNode } from "react";
import { ribbonFill, type RowRole } from "../lib/encoding";
import { verdictFill } from "../lib/verdicts";
import type { RibbonSegment } from "../lib/historyData";
import { HatchPattern, UNREACHABLE_LABEL } from "./marks";

/** The one ribbon drawing: a rect per visible segment on a 0-100 axis.
 *  A fill of "hatch" paints the pattern `hatchId` (defined by the caller,
 *  inside this svg via `children` or elsewhere on the page). */
export function RibbonSvg({ segments, rangeStart, rangeEnd, fillFor, titleFor, dimBefore, hatchId,
  className, height, children }: {
  segments: RibbonSegment[];
  rangeStart: Date;
  rangeEnd: Date;
  fillFor: (s: RibbonSegment) => string;
  titleFor: (s: RibbonSegment, dimmed: boolean) => string;
  /** Segments starting before this instant (ms) are drawn dimmed. */
  dimBefore?: number | null;
  hatchId: string;
  className?: string;
  /** Pixel height; when set the svg is a full-width block, otherwise CSS sizes it. */
  height?: number;
  children?: ReactNode;
}) {
  const total = rangeEnd.getTime() - rangeStart.getTime();
  if (total <= 0) return null;
  const clampPct = (ms: number) => Math.max(0, Math.min(100, (100 * (ms - rangeStart.getTime())) / total));
  return (
    <svg className={className} viewBox="0 0 100 10" preserveAspectRatio="none"
      {...(height !== undefined ? { width: "100%", height, style: { display: "block" } } : {})}>
      {children}
      {segments.map((s, i) => {
        const x = clampPct(Date.parse(s.from));
        const width = clampPct(Date.parse(s.to)) - x;
        if (width <= 0) return null;
        const dimmed = dimBefore != null && Date.parse(s.from) < dimBefore;
        const fill = fillFor(s);
        return (
          <rect key={i} x={x} y="0" width={width} height="10"
            fill={fill === "hatch" ? `url(#${hatchId})` : fill}
            opacity={dimmed ? 0.35 : 1}>
            <title>{titleFor(s, dimmed)}</title>
          </rect>
        );
      })}
    </svg>
  );
}

export function StateRibbon({ segments, rangeStart, rangeEnd, resetAt, kind = "target", role = "other" }: {
  segments: RibbonSegment[];
  rangeStart: Date;
  rangeEnd: Date;
  /** Latest HistoryReset marker: segments before it render dimmed (different data history). */
  resetAt?: string | null;
  kind?: "target" | "pool" | "verdict";
  role?: RowRole;
}) {
  const fillFor = (s: RibbonSegment) =>
    kind === "verdict" ? verdictFill(s.evidence, s.state) : ribbonFill(role, s.state);
  const titleFor = (s: RibbonSegment, dimmed: boolean) => {
    const note = dimmed ? "\n(before address change — different data history)" : "";
    const label = s.state === "Unreachable"
      ? UNREACHABLE_LABEL
      : kind === "verdict" && s.evidence ? s.evidence : s.state;
    return `${label}: ${new Date(s.from).toLocaleString()} → ${new Date(s.to).toLocaleString()}${s.evidence && s.evidence !== label ? `\n${s.evidence}` : ""}${note}`;
  };
  return (
    <RibbonSvg className="cw-ribbon" segments={segments} rangeStart={rangeStart} rangeEnd={rangeEnd}
      fillFor={fillFor} titleFor={titleFor} hatchId="cw-hatch-timeline"
      dimBefore={resetAt ? Date.parse(resetAt) : null}>
      <defs><HatchPattern id="cw-hatch-timeline" /></defs>
    </RibbonSvg>
  );
}
