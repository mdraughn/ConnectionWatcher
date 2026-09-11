// Reusable mark-drawing primitives for the shared incident encoding.
// Every display (punchcard, ledger, ribbons, legends) draws marks through
// these, so the encoding has exactly one visual implementation.
import { HATCH, markCss, RAMP, type Mark } from "../lib/encoding";

/** One mark, drawn at swatch size: a rounded bar, or a centered dot. */
export function MarkSwatch({ mark, barWidth = 22, size = 9 }: {
  mark: Mark; barWidth?: number; size?: number;
}) {
  if (mark.shape === "dot") {
    return <span style={{ display: "inline-block", width: size, height: size,
      borderRadius: "50%", verticalAlign: "baseline", ...markCss(mark) }} />;
  }
  return <span style={{ display: "inline-block", width: barWidth, height: size,
    borderRadius: 2, verticalAlign: "baseline", ...markCss(mark) }} />;
}

/** The "unreachable behind a down parent" fill (ribbons + legends). */
export function HatchSwatch({ width = 22, height = 9 }: { width?: number; height?: number }) {
  return <span style={{ display: "inline-block", width, height, borderRadius: 2,
    verticalAlign: "baseline", background: HATCH }} />;
}

export function describeMark(mark: Mark): string {
  const who = mark.ambiguous ? "trunk or outside (ambiguous)"
    : mark.hue === "grey" ? "your equipment"
    : mark.shape === "dot" ? "one site" : "the internet";
  const how = mark.depth === "out" ? "fully out"
    : mark.depth === "degraded" ? "degraded" : "glitch";
  return `${who}: ${how}`;
}

/** The scope × severity matrix, drawn in the marks themselves — the one
 *  legend every display shares. */
export function EncodingLegend({ withHatch = false }: { withHatch?: boolean }) {
  const depths = ["glitch", "degraded", "out"] as const;
  const rows: Array<{ label: string; note: string; mark: (d: typeof depths[number]) => Mark }> = [
    { label: "the internet", note: "bar", mark: (d) => ({ shape: "bar", hue: "warm", depth: d }) },
    { label: "one site", note: "dot", mark: (d) => ({ shape: "dot", hue: "warm", depth: d }) },
    { label: "your equipment", note: "bar", mark: (d) => ({ shape: "bar", hue: "grey", depth: d }) },
  ];
  return (
    <div style={{ fontSize: "0.75rem", opacity: 0.85, marginTop: 8 }}>
      <div style={{ display: "grid", gap: "4px 12px", alignItems: "center",
        gridTemplateColumns: "auto repeat(3, auto) 1fr", maxWidth: 560 }}>
        <div />
        {depths.map((d) => <div key={d} style={{ opacity: 0.6 }}>{d === "out" ? "fully out" : d}</div>)}
        <div />
        {rows.map((row) => (
          <div key={row.label} style={{ display: "contents" }}>
            <div>{row.label} <small style={{ opacity: 0.6 }}>({row.note})</small></div>
            {depths.map((d) => <div key={d}><MarkSwatch mark={row.mark(d)} /></div>)}
            <div />
          </div>
        ))}
      </div>
      <div style={{ marginTop: 6 }}>
        <MarkSwatch mark={{ shape: "bar", hue: "warm", depth: "glitch", ambiguous: true }} />
        {" "}ambiguous — evidence splits between your equipment and the outside world
        {withHatch && <>{"  ·  "}<HatchSwatch /> unreachable behind a down parent (no data, no blame)</>}
      </div>
    </div>
  );
}

export { RAMP };
