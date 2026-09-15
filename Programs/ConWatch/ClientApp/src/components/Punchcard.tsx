// The 30-day punchcard: hours as rows, days as columns, ink only for trouble.
// Placement-agnostic: feed it cells from lib/story and put it on any page.
import type { PunchcardCell } from "../lib/incidents";
import { markCss } from "../lib/encoding";
import { describeMark, EncodingLegend, MarkSwatch } from "./marks";

const CELL_W = 22;
const CELL_H = 14;

export function Punchcard({ cells, days, now }: {
  cells: Map<string, PunchcardCell>;
  days: number;
  now: Date;
}) {
  const dayDate = (i: number) =>
    new Date(now.getFullYear(), now.getMonth(), now.getDate() - (days - 1 - i));
  const dayLabel = (i: number) => {
    const d = dayDate(i);
    return `${d.getMonth() + 1}/${d.getDate()}`;
  };

  return (
    <div style={{ overflowX: "auto" }}>
      <div style={{
        display: "grid", gap: 2, width: "max-content",
        gridTemplateColumns: `2.6rem repeat(${days}, ${CELL_W}px)`,
      }}>
        {Array.from({ length: 24 }, (_, hour) => (
          <div key={`row${hour}`} style={{ display: "contents" }}>
            <div style={{ fontSize: "0.6rem", opacity: 0.6, textAlign: "right", paddingRight: 4 }}>
              {hour % 6 === 0 ? `${hour}:00` : ""}
            </div>
            {Array.from({ length: days }, (_, day) => {
              const d = dayDate(day);
              const cell = cells.get(`${d.getFullYear()}-${d.getMonth() + 1}-${d.getDate()}-${hour}`);
              const base: React.CSSProperties = { height: CELL_H, borderRadius: 2 };
              if (!cell) {
                return <div key={`c${day}-${hour}`}
                  style={{ ...base, background: "rgba(128,128,128,0.10)" }} />;
              }
              const title = `${dayLabel(day)} ${hour}:00 — ${describeMark(cell.mark)}` +
                (cell.count > 1 ? ` (+${cell.count - 1} more this hour)` : "");
              if (cell.mark.shape === "dot") {
                return (
                  <div key={`c${day}-${hour}`} title={title}
                    style={{ ...base, background: "rgba(128,128,128,0.10)", display: "flex",
                      alignItems: "center", justifyContent: "center" }}>
                    <MarkSwatch mark={cell.mark} />
                  </div>
                );
              }
              return <div key={`c${day}-${hour}`} title={title}
                style={{ ...base, ...markCss(cell.mark) }} />;
            })}
          </div>
        ))}
        <div />
        {Array.from({ length: days }, (_, day) => (
          // Labels are wider than a 22px column ("8/21"): never wrap, let
          // them overflow the column. The last column is today — align it
          // right so the overflow spills left, not off the container edge.
          <div key={`d${day}`} style={{ fontSize: "0.6rem", opacity: 0.6,
            whiteSpace: "nowrap",
            textAlign: day === days - 1 ? "right" : "center" }}>
            {day % 5 === 4 ? dayLabel(day) : ""}
          </div>
        ))}
      </div>
      <EncodingLegend />
      <p style={{ fontSize: "0.75rem", opacity: 0.7, marginTop: 6 }}>
        Rows are hours of your day, columns are days. Blank is the good news.
        Trouble recurring at the same time each day forms a horizontal run along that hour's row.
      </p>
    </div>
  );
}
