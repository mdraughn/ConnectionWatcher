import { readFileSync, writeFileSync } from "node:fs";
import { ICON, MONO, appbar, card, page, pageband, primaryButton, section } from "./lib-shell.mjs";

const W = 1440, H = 1460;
const DARK = readFileSync("tokens-dark.txt", "utf8");

/** A panel that re-declares the dark tokens locally, so both themes can be
 *  compared side by side on one light page. */
const darkPanel = (inner, pad = "16px 18px") =>
  `<div style="${DARK.trimEnd()}background:var(--bg);color:var(--text);border:1px solid #2a3140;border-radius:8px;padding:${pad}">${inner}</div>`;
const lightPanel = (inner, pad = "16px 18px") =>
  `<div style="background:var(--surface);border:1px solid var(--border);border-radius:8px;box-shadow:var(--shadow);padding:${pad}">${inner}</div>`;

const panelTitle = (t) => `<div style="font-size:0.72rem;font-weight:650;letter-spacing:0.09em;text-transform:uppercase;color:var(--faint);margin-bottom:12px">${t}</div>`;

function swatchRow(name, hex, round = false) {
  return `<div style="display:flex;align-items:center;gap:11px;height:29px">
      <span style="width:26px;height:19px;border-radius:${round ? "50%" : "4px"};background:${hex};border:1px solid var(--border-strong);flex-shrink:0"></span>
      <span style="font-size:0.8rem;color:var(--text)">${name}</span>
      <span style="flex:1"></span>
      <span style="font-family:${MONO};font-size:0.73rem;color:var(--muted)">${hex}</span>
    </div>`;
}

const LIGHT_TOKENS = [
  ["page", "#ffffff"], ["surface", "#fbfcfd"], ["raised", "#f4f6f9"], ["well", "#eef1f5"],
  ["hairline", "#e7eaf0"], ["border", "#d6dbe4"],
  ["ink", "#373c44"], ["ink muted", "#646b79"], ["ink faint", "#8b93a1"], ["accent", "#0172ad"],
];
const DARK_TOKENS = [
  ["page", "#13161f"], ["surface", "#181c26"], ["raised", "#1e2331"], ["well", "#252b3a"],
  ["hairline", "#262d3b"], ["border", "#323a4b"],
  ["ink", "#c2c7d0"], ["ink muted", "#7b8495"], ["ink faint", "#68707f"], ["accent", "#01aaff"],
];

// ------------------------------------------------------------ encoding ramp

function rampGrid(warm, grey, healthy) {
  const cell = (hex, dot = false) => dot
    ? `<div style="display:flex;justify-content:center"><span style="width:11px;height:11px;border-radius:50%;background:${hex}"></span></div>`
    : `<div style="height:13px;border-radius:2px;background:${hex}"></div>`;
  const head = (t) => `<div style="font-size:0.7rem;color:var(--faint);text-align:center">${t}</div>`;
  const lbl = (t, n) => `<div style="font-size:0.775rem;color:var(--text);white-space:nowrap">${t} <span style="color:var(--faint)">(${n})</span></div>`;
  return `<div style="display:grid;grid-template-columns:auto repeat(3, 74px);gap:9px 14px;align-items:center">
      <div></div>${head("glitch")}${head("degraded")}${head("fully out")}
      ${lbl("the internet", "bar")}${cell(warm[0])}${cell(warm[1])}${cell(warm[2])}
      ${lbl("one site", "dot")}${cell(warm[0], true)}${cell(warm[1], true)}${cell(warm[2], true)}
      ${lbl("your equipment", "bar")}${cell(grey[0])}${cell(grey[1])}${cell(grey[2])}
      ${lbl("quiet", "surface")}<div style="grid-column:span 3;height:13px;border-radius:2px;background:${healthy}"></div>
    </div>`;
}

// ----------------------------------------------------------- verdict rows

function verdictRow(color, label, detail, bar = 4) {
  return `<div style="border:1px solid var(--border);border-left:${bar}px solid ${color};border-radius:7px;background:color-mix(in srgb, ${color} 7%, var(--surface));padding:9px 13px">
      <div style="font-size:0.83rem;font-weight:650;color:${color}">${label}</div>
      <div style="font-size:0.765rem;color:var(--muted);margin-top:2px">${detail}</div>
    </div>`;
}

const verdictSet = (c) => [
  verdictRow(c.ok, "Internet: healthy", "All anchors responding."),
  verdictRow(c.glitch, "Internet: site incident", "One anchor degraded; the path is fine."),
  verdictRow(c.warn, "Internet: weather", "Two or more anchors degraded at once."),
  verdictRow(c.warn, "Internet: link degraded", "Your trunk is losing pings.", 6),
  verdictRow(c.down, "Internet: link down", "LinkDown: Comcast — Comcast Edge is Down.", 6),
].join(`<div style="height:8px"></div>`);

// ------------------------------------------------------------------ icons

const ICON_NAMES = [
  ["dashboard", "Dashboard"], ["history", "History"], ["data", "Data"], ["targets", "Targets"],
  ["traceroute", "Traceroute"], ["admin", "Admin"], ["lock", "Admin mode"], ["alert", "Warning"],
  ["report", "ISP report"], ["download", "Export"], ["pin", "Pin row"], ["plus", "Add"],
  ["search", "Find"], ["chevron", "Expand"], ["moon", "Dark"], ["sun", "Light"],
  ["menu", "Menu"], ["close", "Close"],
];

const iconGrid = `<div style="display:grid;grid-template-columns:repeat(6, minmax(0, 1fr));gap:16px 10px">
    ${ICON_NAMES.map(([k, n]) => `<div style="display:flex;flex-direction:column;align-items:center;gap:7px">
      <span style="width:38px;height:38px;border:1px solid var(--border);border-radius:7px;background:var(--surface-2);color:var(--text);display:flex;align-items:center;justify-content:center">${ICON[k]}</span>
      <span style="font-size:0.685rem;color:var(--muted);text-align:center;line-height:1.25">${n}</span>
    </div>`).join("")}
  </div>`;

// ------------------------------------------------------------- type scale

const typeRow = (sample, spec, style) => `<div style="display:flex;align-items:baseline;gap:16px;padding:9px 0;border-top:1px solid var(--border)">
    <span style="flex:1;color:var(--text);${style}">${sample}</span>
    <span style="font-family:${MONO};font-size:0.7rem;color:var(--faint);white-space:nowrap">${spec}</span>
  </div>`;

const typeScale = `
  ${typeRow("Dashboard", "1.33rem / 650 / -0.015em", "font-size:1.33rem;font-weight:650;letter-spacing:-0.015em")}
  ${typeRow("REACHABILITY", "0.70rem / 650 / 0.09em", "font-size:0.70rem;font-weight:650;letter-spacing:0.09em;color:var(--faint)")}
  ${typeRow("Your link went down for 4 min.", "0.855rem / 600", "font-size:0.855rem;font-weight:600")}
  ${typeRow("LinkDown: Comcast — Comcast Edge is Down.", "0.825rem / 450 muted", "font-size:0.825rem;color:var(--muted)")}
  ${typeRow("the last 96 minutes — green is quiet", "0.72rem / 450 muted", "font-size:0.72rem;color:var(--muted)")}
  ${typeRow("99.72%&nbsp;&nbsp;21.4 ms&nbsp;&nbsp;9.9.9.9", `0.765rem mono tabular`, `font-family:${MONO};font-size:0.765rem;color:var(--muted)`)}`;

// -------------------------------------------------------------- controls

const chip = (t, c) => `<span style="font-size:0.69rem;font-weight:640;color:${c};background:color-mix(in srgb, ${c} 14%, transparent);border-radius:4px;padding:2px 8px">${t}</span>`;

const controls = `<div style="display:flex;flex-direction:column;gap:16px">
    <div style="display:flex;align-items:center;gap:10px;flex-wrap:wrap">
      ${primaryButton("Save target")}
      <button style="height:32px;padding:0 14px;border:1px solid var(--border-strong);background:var(--surface);color:var(--text);border-radius:5px;font-size:0.815rem;font-weight:520;font-family:inherit">Cancel</button>
      <button style="height:32px;padding:0 14px;border:1px solid color-mix(in srgb, var(--down) 40%, var(--border));background:var(--surface);color:var(--down);border-radius:5px;font-size:0.815rem;font-weight:520;font-family:inherit">Delete</button>
    </div>
    <div style="display:flex;align-items:center;gap:10px;flex-wrap:wrap">
      ${chip("Faltering", "var(--warn)")}${chip("Down", "var(--down)")}${chip("Unreachable", "var(--unreach)")}${chip("Comcast", "var(--warm-degraded)")}
      <span style="display:flex;align-items:center;gap:7px;background:var(--surface-2);border:1px solid var(--border);border-radius:999px;padding:4px 11px 4px 9px">
        <span style="width:8px;height:8px;border-radius:50%;background:var(--ok)"></span>
        <span style="font-size:0.775rem;font-weight:600;color:var(--text)">Healthy</span>
      </span>
    </div>
    <div style="display:flex;align-items:center;gap:14px;flex-wrap:wrap">
      <div style="display:flex;border:1px solid var(--border-strong);border-radius:5px;overflow:hidden;background:var(--surface)">
        ${["24 h", "7 days", "30 days"].map((o, i) => `<span style="padding:0 14px;height:31px;display:flex;align-items:center;font-size:0.815rem;font-weight:${i === 0 ? 600 : 450};color:var(--${i === 0 ? "primary" : "muted"});background:${i === 0 ? "var(--primary-soft)" : "transparent"};${i > 0 ? "border-left:1px solid var(--border-strong);" : ""}">${o}</span>`).join("")}
      </div>
      <span style="width:34px;height:20px;border-radius:999px;background:var(--primary);display:flex;align-items:center;padding:0 2px;justify-content:flex-end"><span style="width:16px;height:16px;border-radius:50%;background:#fff"></span></span>
      <span style="width:34px;height:20px;border-radius:999px;background:var(--surface-3);display:flex;align-items:center;padding:0 2px"><span style="width:16px;height:16px;border-radius:50%;background:var(--surface)"></span></span>
    </div>
    <div style="display:flex;align-items:center;gap:9px;height:35px;padding:0 11px;border:1px solid var(--border-strong);background:var(--bg);border-radius:5px;max-width:280px">
      <span style="flex:1;font-size:0.845rem;color:var(--faint);font-family:${MONO}">IP or hostname</span>
    </div>
  </div>`;

// ----------------------------------------------------------------- layout

const body = [
  appbar({ active: "admin", verdict: "healthy", theme: "light" }),
  pageband("Design language", "The tokens, marks, icons and controls every ConnectionWatcher page is built from."),
  `<main style="flex:1;padding:0 26px 30px">
    <div style="max-width:1250px;margin:0 auto;display:flex;flex-direction:column;gap:24px">

      <div style="display:grid;grid-template-columns:repeat(2, minmax(0, 1fr));gap:24px;align-items:start">

        <div style="display:flex;flex-direction:column;gap:24px">
          <div>${section("Surfaces and ink")}
            <div style="display:grid;grid-template-columns:repeat(2, minmax(0, 1fr));gap:14px">
              ${lightPanel(panelTitle("Light") + LIGHT_TOKENS.map(([n, h]) => swatchRow(n, h)).join(""))}
              ${darkPanel(panelTitle("Dark") + DARK_TOKENS.map(([n, h]) => swatchRow(n, h)).join(""))}
            </div>
          </div>

          <div>${section("Verdict states", "tinted card + accent edge")}
            <div style="display:grid;grid-template-columns:repeat(2, minmax(0, 1fr));gap:14px;align-items:start">
              ${lightPanel(panelTitle("Light") + verdictSet({ ok: "var(--ok)", glitch: "var(--warm-glitch)", warn: "var(--warn)", down: "var(--down)" }))}
              ${darkPanel(panelTitle("Dark") + verdictSet({ ok: "var(--ok)", glitch: "var(--warm-glitch)", warn: "var(--warn)", down: "var(--down)" }))}
            </div>
          </div>

          <div>${section("Controls")}${lightPanel(controls, "20px")}</div>
        </div>

        <div style="display:flex;flex-direction:column;gap:24px">
          <div>${section("Incident encoding", "shape = scope · hue = whose gear · depth = severity")}
            <div style="display:grid;grid-template-columns:repeat(2, minmax(0, 1fr));gap:14px;align-items:start">
              ${lightPanel(panelTitle("Light") + rampGrid(["#eda94f", "#e08a00", "#cc3333"], ["#d4d4d4", "#7d7d7d", "#2b2b2b"], "#eaf3ec"))}
              ${darkPanel(panelTitle("Dark") + rampGrid(["#c98a3c", "#f0a02a", "#e8544b"], ["#39404f", "#8c94a6", "#e2e6ee"], "#1b2620"))}
            </div>
            <div style="display:flex;gap:11px;align-items:flex-start;border:1px solid var(--border);border-left:4px solid var(--primary);border-radius:7px;background:var(--surface-2);padding:12px 15px;margin-top:14px">
              <span style="color:var(--primary);display:flex;flex-shrink:0;margin-top:1px">${ICON.alert}</span>
              <p style="margin:0;font-size:0.79rem;color:var(--text);line-height:1.55">
                <b style="font-weight:600">The grey ramp has to invert in dark.</b> In light it runs pale
                <span style="display:inline-block;width:13px;height:9px;border-radius:2px;background:#d4d4d4;vertical-align:middle"></span>
                &rarr; black
                <span style="display:inline-block;width:13px;height:9px;border-radius:2px;background:#2b2b2b;vertical-align:middle"></span>,
                and <code style="font-family:${MONO};font-size:0.75rem">#2b2b2b</code> is invisible on a
                <code style="font-family:${MONO};font-size:0.75rem">#13161f</code> page. In dark it runs dark &rarr; near-white, so
                &ldquo;deeper = worse&rdquo; still holds &mdash; it just runs toward light. The warm ramp keeps its
                direction in both themes, so warm-vs-grey separation survives.
              </p>
            </div>
          </div>

          <div>${section("Icons", "20px grid · 1.6px stroke · round caps")}${lightPanel(iconGrid, "20px")}</div>

          <div>${section("Type", "system-ui for text · tabular mono for every number")}${lightPanel(`<div style="margin-top:-9px">${typeScale}</div>`, "16px 18px")}</div>
        </div>
      </div>

      <div>${section("Layout and breakpoints")}
        ${lightPanel(`<div style="display:grid;grid-template-columns:repeat(3, minmax(0, 1fr));gap:22px">
          <div>
            <div style="font-size:0.84rem;font-weight:600;color:var(--text);margin-bottom:5px">&ge; 900px &mdash; full bar</div>
            <p style="margin:0;font-size:0.79rem;color:var(--muted);line-height:1.5">Brand, both nav groups split by a hairline, then the verdict pip, theme and admin-mode buttons. Content centres at 1250px with 26px gutters.</p>
          </div>
          <div>
            <div style="font-size:0.84rem;font-weight:600;color:var(--text);margin-bottom:5px">&lt; 900px &mdash; drawer</div>
            <p style="margin:0;font-size:0.79rem;color:var(--muted);line-height:1.5">Nav collapses to a hamburger; the verdict pip stays in the bar, because knowing the verdict is why the page is open. Gutters drop to 16px, rows grow to 44px, tables restack as cards.</p>
          </div>
          <div>
            <div style="font-size:0.84rem;font-weight:600;color:var(--text);margin-bottom:5px">Wayfinding</div>
            <p style="margin:0;font-size:0.79rem;color:var(--muted);line-height:1.5">Every page opens with a title and one sentence saying what it is for. Sections are named by a hairline rule, never a bare heading. The active page is marked in the bar and the drawer.</p>
          </div>
        </div>`, "20px")}
      </div>
    </div>
  </main>`,
].join("\n");

writeFileSync("System.dc.html", page({ theme: "light", w: W, h: H, body }));
console.log("wrote System.dc.html");
