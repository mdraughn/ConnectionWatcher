// The event strip: "is our internet down — and what was it doing just now?"
// One horizontal row of continuous 1-minute bins ending at this moment,
// painted with the same encoding as everything else. Healthy bins wear the
// VIVID green: at 10px the pale surface tint is illegible.
import { useEffect, useRef, useState } from "react";
import { HEALTHY_VIVID, markCss, type Mark } from "../lib/encoding";
import { fetchStrip, type StoryTopology } from "../lib/story";
import { describeMark } from "./marks";

const BIN_MS = 60_000;
const CELL = 10;   // px wide
const GAP = 2;
const REFRESH_MS = 30_000;

export function EventStrip({ topology }: { topology: StoryTopology }) {
  const containerRef = useRef<HTMLDivElement>(null);
  const [binCount, setBinCount] = useState(0);
  const [bins, setBins] = useState<Array<Mark | null> | null>(null);

  // As many one-minute bins as fit the container without scrolling.
  useEffect(() => {
    const measure = () => {
      const width = containerRef.current?.clientWidth ?? 0;
      // Never demand more bins than the container fits: a forced minimum
      // wider than a phone screen makes the whole page scroll sideways.
      if (width > 0)
        setBinCount(Math.max(10, Math.min(240, Math.floor((width - 60) / (CELL + GAP)))));
    };
    measure();
    window.addEventListener("resize", measure);
    return () => window.removeEventListener("resize", measure);
  }, []);

  useEffect(() => {
    if (binCount === 0) return;
    let live = true;
    const load = () =>
      fetchStrip(topology, BIN_MS, binCount)
        .then((b) => { if (live) setBins(b); })
        .catch(() => undefined);
    load();
    const timer = setInterval(load, REFRESH_MS);
    return () => { live = false; clearInterval(timer); };
  }, [binCount]);

  const minutesAgo = (i: number) => (binCount - 1 - i);
  const binTitle = (i: number, mark: Mark | null) => {
    const ago = minutesAgo(i);
    const when = ago === 0 ? "this minute" : `${ago} min ago`;
    return `${when} — ${mark ? describeMark(mark) : "all quiet"}`;
  };

  return (
    <div ref={containerRef} style={{ margin: "0.35rem 0 1rem" }}>
      {bins && (
        <>
          <div style={{ display: "flex", gap: GAP, alignItems: "center" }}>
            {bins.map((mark, i) => {
              if (mark?.shape === "dot") {
                return (
                  <span key={i} title={binTitle(i, mark)}
                    style={{ width: CELL, height: 10, borderRadius: 2, background: HEALTHY_VIVID,
                      display: "inline-flex", alignItems: "center", justifyContent: "center",
                      flexShrink: 0 }}>
                    <span style={{ width: 7, height: 7, borderRadius: "50%", ...markCss(mark) }} />
                  </span>
                );
              }
              return <span key={i} title={binTitle(i, mark)}
                style={{ width: CELL, height: 10, borderRadius: 2, flexShrink: 0,
                  ...(mark ? markCss(mark) : { background: HEALTHY_VIVID }) }} />;
            })}
            <span style={{ fontSize: "0.65rem", opacity: 0.55, whiteSpace: "nowrap" }}>now</span>
          </div>
          <div style={{ fontSize: "0.65rem", opacity: 0.55, marginTop: 2 }}>
            the last {binCount} minutes — green is quiet; warm marks are trouble out there,
            grey marks are your own gear
          </div>
        </>
      )}
    </div>
  );
}
