// Cause-aligned ribbon board: every target on one shared time axis, so
// causality reads as vertical alignment. Trunk rows wear the grey ramp
// (your equipment), everything else the warm ramp; Unreachable is hatched
// (inherited from a down parent — no data, no blame). Placement-agnostic.
import { useEffect, useState } from "react";
import { ribbonFill, type RowRole } from "../lib/encoding";
import { clipAtReset, ribbonSegments, type RibbonSegment } from "../lib/historyData";
import type { TransitionEvent } from "../lib/types";
import { HatchSwatch, MarkSwatch } from "./marks";

export interface BoardSubject {
  id: string;
  name: string;
  kind: string;
  parent: string | null;
  role: RowRole;
}

/** Trunk root→leaf, then anchors, then the rest — cause reads top-down. */
export function orderRows(subjects: BoardSubject[]): BoardSubject[] {
  const targets = subjects.filter((s) => s.kind === "target");
  const depth = (s: BoardSubject): number => {
    let d = 0;
    let parent = s.parent;
    const byName = new Map(targets.map((t) => [t.name, t]));
    while (parent && byName.has(parent) && d < 20) {
      d++;
      parent = byName.get(parent)!.parent;
    }
    return d;
  };
  const rank = (s: BoardSubject) => (s.role === "trunk" ? 0 : s.role === "anchor" ? 1 : 2);
  return [...targets].sort((a, b) =>
    rank(a) - rank(b) || depth(a) - depth(b) || a.name.localeCompare(b.name));
}

function Row({ subject, segments, rangeStart, rangeEnd }: {
  subject: BoardSubject;
  segments: RibbonSegment[];
  rangeStart: Date;
  rangeEnd: Date;
}) {
  const total = rangeEnd.getTime() - rangeStart.getTime();
  if (total <= 0) return null;
  const clampPct = (ms: number) => Math.max(0, Math.min(100, (100 * (ms - rangeStart.getTime())) / total));
  return (
    <svg width="100%" height="16" viewBox="0 0 100 10" preserveAspectRatio="none"
      style={{ display: "block" }}>
      {segments.map((s, i) => {
        const x = clampPct(Date.parse(s.from));
        const width = clampPct(Date.parse(s.to)) - x;
        if (width <= 0) return null;
        const fill = ribbonFill(subject.role, s.state);
        return (
          <rect key={i} x={x} y="0" width={width} height="10"
            fill={fill === "hatch" ? "url(#cw-hatch)" : fill}>
            <title>{`${subject.name} — ${s.state === "Unreachable"
              ? "Unreachable behind a down parent (no data, no blame)" : s.state}\n` +
              `${new Date(s.from).toLocaleString()} → ${new Date(s.to).toLocaleString()}` +
              `${s.evidence ? `\n${s.evidence}` : ""}`}</title>
          </rect>
        );
      })}
    </svg>
  );
}

export function RibbonBoard({ subjects, rangeStart, rangeEnd }: {
  subjects: BoardSubject[];
  rangeStart: Date;
  rangeEnd: Date;
}) {
  const rows = orderRows(subjects);
  const [segmentsById, setSegmentsById] = useState<Map<string, RibbonSegment[]>>(new Map());

  useEffect(() => {
    let live = true;
    Promise.all(rows.map(async (s) => {
      const url = `/api/v1/transitions?subjectId=${s.id}` +
        `&from=${rangeStart.toISOString()}&to=${rangeEnd.toISOString()}&limit=1000`;
      const response = await fetch(url);
      const items = response.ok
        ? ((await response.json()).items as TransitionEvent[]).reverse() : [];
      const { transitions } = clipAtReset(items);
      return [s.id, ribbonSegments(transitions, rangeStart.toISOString(), rangeEnd.toISOString())] as const;
    })).then((pairs) => { if (live) setSegmentsById(new Map(pairs)); });
    return () => { live = false; };
  }, [subjects.map((s) => s.id).join(","), rangeStart.getTime(), rangeEnd.getTime()]);

  const ticks = 4;
  return (
    <div>
      <svg width="0" height="0" style={{ position: "absolute" }}>
        <defs>
          <pattern id="cw-hatch" width="6" height="6" patternTransform="rotate(45)"
            patternUnits="userSpaceOnUse">
            <rect width="6" height="6" fill="var(--surface-2)" />
            <rect width="3" height="6" fill="var(--grey-glitch)" />
          </pattern>
        </defs>
      </svg>
      <div style={{ display: "grid", gridTemplateColumns: "10rem 1fr", gap: "3px 10px",
        alignItems: "center" }}>
        {rows.map((s) => (
          <div key={s.id} style={{ display: "contents" }}>
            <div style={{ fontSize: "0.8rem", textAlign: "right",
              opacity: s.role === "other" ? 0.7 : 1 }}>
              {s.name}
            </div>
            <Row subject={s} segments={segmentsById.get(s.id) ?? []}
              rangeStart={rangeStart} rangeEnd={rangeEnd} />
          </div>
        ))}
        <div />
        <div style={{ display: "flex", justifyContent: "space-between",
          fontSize: "0.65rem", opacity: 0.6 }}>
          {Array.from({ length: ticks + 1 }, (_, i) => {
            const at = new Date(rangeStart.getTime() +
              (i * (rangeEnd.getTime() - rangeStart.getTime())) / ticks);
            return <span key={i}>{at.toLocaleDateString(undefined, { month: "numeric", day: "numeric" })}</span>;
          })}
        </div>
      </div>
      <p style={{ fontSize: "0.75rem", opacity: 0.7, marginTop: 8 }}>
        <MarkSwatch mark={{ shape: "bar", hue: "grey", depth: "out" }} barWidth={12} /> your
        equipment&apos;s own trouble &nbsp;·&nbsp;
        <MarkSwatch mark={{ shape: "bar", hue: "warm", depth: "out" }} barWidth={12} /> trouble out
        there &nbsp;·&nbsp; <HatchSwatch width={12} /> unreachable behind a down parent &nbsp;·&nbsp;
        one incident reads as a vertical stripe across the rows it touched.
      </p>
    </div>
  );
}
