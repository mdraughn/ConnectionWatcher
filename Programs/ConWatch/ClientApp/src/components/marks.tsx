// Reusable mark-drawing primitives for the shared incident encoding.
// Every display (punchcard, ledger, ribbons, legends) draws marks through
// these, so the encoding has exactly one visual implementation.
import { HATCH, markCss, RAMP, RESOLVER_OUT, type Mark } from "../lib/encoding";

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

/** The "unreachable" tooltip/legend wording, shared by every ribbon and legend. */
export const UNREACHABLE_LABEL = "Unreachable behind a down parent (no data, no blame)";

/** The SVG `<pattern>` behind HATCH, for ribbons drawn as <rect>s. Give each
 *  svg that defines it its own id. */
export function HatchPattern({ id }: { id: string }) {
  return (
    <pattern id={id} width="6" height="6" patternTransform="rotate(45)"
      patternUnits="userSpaceOnUse">
      <rect width="6" height="6" fill="var(--surface-2)" />
      <rect width="3" height="6" fill="var(--grey-glitch)" />
    </pattern>
  );
}

/** One cell of a grid or strip: a dot mark sits centered on `background`,
 *  any other mark is the cell's own fill. `size` is the dot's diameter;
 *  `style` carries the cell's geometry. */
export function MarkCell({ mark, background, size, title, style }: {
  mark: Mark | null; background: string; size: number; title?: string; style?: React.CSSProperties;
}) {
  if (mark?.shape === "dot") {
    return (
      <div title={title}
        style={{ ...style, background, display: "flex", alignItems: "center", justifyContent: "center" }}>
        <span style={{ width: size, height: size, borderRadius: "50%", flexShrink: 0, ...markCss(mark) }} />
      </div>
    );
  }
  return <div title={title} style={{ ...style, ...(mark ? markCss(mark) : { background }) }} />;
}

export function describeMark(mark: Mark): string {
  const who = mark.ambiguous ? "trunk or outside (ambiguous)"
    : mark.hue === "grey" ? "your equipment"
    : mark.hue === "resolver" ? "your resolver"
    : mark.shape === "dot" ? "one site" : "the internet";
  const how = mark.depth === "out" ? "fully out"
    : mark.depth === "degraded" ? "degraded" : "glitch";
  return `${who}: ${how}`;
}

/** The scope × severity matrix, drawn in the marks themselves — the one
 *  legend every display shares. */
export function EncodingLegend({ withHatch = false }: { withHatch?: boolean }) {
  const depths = ["glitch", "degraded", "out"] as const;
  const rows: Array<{ label: string; note: string; mark: (d: typeof depths[number]) => Mark | null }> = [
    { label: "the internet", note: "bar", mark: (d) => ({ shape: "bar", hue: "warm", depth: d }) },
    { label: "one site", note: "dot", mark: (d) => ({ shape: "dot", hue: "warm", depth: d }) },
    { label: "your equipment", note: "bar", mark: (d) => ({ shape: "bar", hue: "grey", depth: d }) },
    { label: "your resolver", note: "bar", mark: (d) => (d === "out" ? RESOLVER_OUT : null) },
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
            {depths.map((d) => {
              const mark = row.mark(d);
              return <div key={d}>{mark && <MarkSwatch mark={mark} />}</div>;
            })}
            <div />
          </div>
        ))}
      </div>
      <div style={{ marginTop: 6 }}>
        <MarkSwatch mark={{ shape: "bar", hue: "warm", depth: "glitch", ambiguous: true }} />
        {" "}ambiguous — evidence splits between your equipment and the outside world
        {withHatch && <>{"  ·  "}<HatchSwatch /> {UNREACHABLE_LABEL.toLowerCase()}</>}
      </div>
    </div>
  );
}
