import { writeFileSync } from "node:fs";
import { ICON, MONO, appbar, card, ghostButton, page, pageband, section } from "./lib-shell.mjs";
import { ledger } from "./lib-parts.mjs";
import {
  axisRow, encodingLegend, latencyChart, lossChart, punchcard, ribbonBoard, stateRibbon, statTiles,
} from "./lib-history.mjs";

const W = 1440, H = 2260;

function selectBox(label, width) {
  return `<div style="display:flex;align-items:center;gap:9px;height:33px;padding:0 11px;border:1px solid var(--border-strong);background:var(--surface);border-radius:5px;width:${width}px">
      <span style="flex:1;font-size:0.835rem;color:var(--text);white-space:nowrap;overflow:hidden;text-overflow:ellipsis">${label}</span>
      <span style="color:var(--muted);display:flex;flex-shrink:0">${ICON.chevron}</span>
    </div>`;
}

function segmented(options, activeIndex) {
  return `<div style="display:flex;border:1px solid var(--border-strong);border-radius:5px;overflow:hidden;background:var(--surface)">
    ${options.map((o, i) => `<span style="padding:0 14px;height:31px;display:flex;align-items:center;font-size:0.815rem;font-weight:${i === activeIndex ? 600 : 450};color:var(--${i === activeIndex ? "primary" : "muted"});background:${i === activeIndex ? "var(--primary-soft)" : "transparent"};${i > 0 ? "border-left:1px solid var(--border-strong);" : ""}white-space:nowrap">${o}</span>`).join("")}
  </div>`;
}

const board = [
  { name: "Router", role: "trunk", segs: [{ frac: 1, fill: "healthy" }] },
  { name: "Cable Modem", role: "trunk", segs: [{ frac: 0.62, fill: "healthy" }, { frac: 0.02, fill: "grey-degraded" }, { frac: 0.36, fill: "healthy" }] },
  { name: "Comcast Edge", role: "trunk", segs: [{ frac: 0.90, fill: "healthy" }, { frac: 0.025, fill: "grey-out" }, { frac: 0.075, fill: "healthy" }] },
  { name: "Comcast Core", role: "other", segs: [{ frac: 0.90, fill: "healthy" }, { frac: 0.025, fill: "hatch" }, { frac: 0.075, fill: "healthy" }] },
  { name: "Google DNS", role: "other", segs: [{ frac: 0.90, fill: "healthy" }, { frac: 0.025, fill: "hatch" }, { frac: 0.075, fill: "healthy" }] },
  { name: "Cloudflare DNS", role: "other", segs: [{ frac: 0.40, fill: "healthy" }, { frac: 0.012, fill: "warm-glitch" }, { frac: 0.488, fill: "healthy" }, { frac: 0.025, fill: "hatch" }, { frac: 0.075, fill: "healthy" }] },
  { name: "Quad9", role: "other", segs: [{ frac: 0.22, fill: "healthy" }, { frac: 0.012, fill: "warm-glitch" }, { frac: 0.30, fill: "healthy" }, { frac: 0.015, fill: "warm-degraded" }, { frac: 0.353, fill: "healthy" }, { frac: 0.025, fill: "hatch" }, { frac: 0.075, fill: "healthy" }] },
  { name: "OpenDNS", role: "other", segs: [{ frac: 0.90, fill: "healthy" }, { frac: 0.025, fill: "hatch" }, { frac: 0.075, fill: "healthy" }] },
];

const controls = `<div style="display:flex;align-items:center;gap:12px;flex-wrap:wrap">
    ${selectBox("Comcast Edge", 240)}
    ${segmented(["24 h", "7 days", "30 days"], 0)}
    <span style="flex:1"></span>
    <span style="font-size:0.745rem;color:var(--faint)">times in America/Chicago (your browser&rsquo;s zone) &mdash; the ISP report shows UTC alongside</span>
  </div>`;

const body = [
  appbar({ active: "history", verdict: "healthy", theme: "dark" }),
  pageband(
    "History",
    "The evidence &mdash; uptime, latency, recurrence, and a 90-day incident ledger.",
    ghostButton(ICON.report, "ISP report")),
  `<main style="flex:1;padding:0 26px 30px">
    <div style="max-width:1250px;margin:0 auto;display:flex;flex-direction:column;gap:24px">
      ${controls}
      ${statTiles([
        { value: "99.72%", label: "uptime (measured)" },
        { value: "1", label: "outage · 4 min long" },
        { value: "11 min", label: "unreachable — no data", color: "var(--muted)" },
        { value: "21.4 ms", label: "average round trip" },
      ])}
      <div>${section("State timeline", "Comcast Edge · last 24 h")}
        ${card(`<div style="padding:16px 18px">${stateRibbon([
          { frac: 0.58, fill: "healthy" }, { frac: 0.012, fill: "grey-degraded" },
          { frac: 0.30, fill: "healthy" }, { frac: 0.02, fill: "grey-degraded" },
          { frac: 0.028, fill: "grey-out" }, { frac: 0.06, fill: "healthy" },
        ])}${axisRow(["15:00", "19:00", "23:00", "03:00", "07:00", "11:00", "14:08"])}</div>`)}
      </div>
      <div>${section("Latency", "average round trip, ms")}
        ${card(`<div style="padding:16px 18px">${latencyChart()}</div>`)}
      </div>
      <div>${section("Loss", "% of pings lost")}
        ${card(`<div style="padding:16px 18px">${lossChart()}</div>`)}
      </div>
      <div>${section("Recurrence punchcard", "all targets · hours × days · last 30 days")}
        ${card(`<div style="padding:18px">
          <div style="overflow-x:auto">${punchcard(30)}</div>
          ${encodingLegend()}
          <p style="font-size:0.765rem;color:var(--muted);margin:14px 0 0;max-width:760px;line-height:1.5">
            Rows are hours of your day, columns are days. Blank is the good news. Trouble recurring at the
            same time each day forms a horizontal run along that hour&rsquo;s row &mdash; the 03:00 row here is
            a nightly gremlin, not bad luck.
          </p>
        </div>`)}
      </div>
      <div>${section("All targets, cause-aligned", "last 30 days")}
        ${card(`<div style="padding:16px 18px">${ribbonBoard(board)}
          <p style="font-size:0.765rem;color:var(--muted);margin:14px 0 0;max-width:760px;line-height:1.5">
            One shared axis, trunk rows named in full strength. A single incident reads as a vertical stripe
            across exactly the rows it touched; hatching is &ldquo;unreachable behind a down parent&rdquo; &mdash;
            no data, no blame.
          </p>
        </div>`)}
      </div>
      <div>${section("What happened", "last 90 days")}
        ${ledger([
          { mark: { shape: "bar", hue: "grey", depth: "out" }, when: "Sun, Sep 13, 2:08 PM", lead: "Your link went down for 4 min.", body: "LinkDown: Comcast — Comcast Edge is Down; the modem still answered." },
          { mark: { shape: "dot", hue: "warm", depth: "glitch" }, when: "Sat, Sep 12, 9:14 PM", lead: "Quad9 glitched for 8 s.", body: "3 of 20 pings lost — no other anchor saw it." },
          { mark: { shape: "bar", hue: "grey", depth: "out" }, when: "Fri, Sep 11, 3:02 AM", lead: "Your link went down for 4 min.", body: "LinkDown: Comcast — Comcast Edge is Down." },
          { mark: { shape: "bar", hue: "warm", depth: "degraded" }, when: "Thu, Sep 10, 8:47 PM", lead: "Internet weather for 11 min.", body: "Two anchors degraded at once; your own gear stayed healthy." },
          { mark: { shape: "dot", hue: "warm", depth: "degraded" }, when: "Wed, Sep 9, 6:31 AM", lead: "Cloudflare DNS had a site incident for 2 min.", body: "Only that anchor was affected — the rest of the path was fine." },
        ])}
        <div style="font-size:0.8rem;color:var(--muted);padding:10px 2px 0">
          The other 85 of the last 90 days: nothing to report.
        </div>
      </div>
    </div>
  </main>`,
].join("\n");

writeFileSync("History.dc.html", page({ theme: "dark", w: W, h: H, body }));
console.log("wrote History.dc.html");
