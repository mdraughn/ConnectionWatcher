// Page-level building blocks: verdict card, event strip, reachability tree,
// ledger, glitch table, status bar, punchcard, ribbon board.
// The encoding (shape = scope, hue = whose gear, depth = severity) is the
// app's own, from ClientApp/src/lib/encoding.ts.
import { ICON, MONO, card, section } from "./lib-shell.mjs";

const STATE_COLOR = {
  Responding: "var(--ok)",
  Faltering: "var(--warn)",
  Recovering: "var(--warn)",
  Down: "var(--down)",
  Unreachable: "var(--unreach)",
};

export const HATCH = "repeating-linear-gradient(45deg, var(--grey-glitch) 0 3px, var(--surface-2) 3px 6px)";

// ---------------------------------------------------------------- verdict

const VERDICT_COPY = {
  healthy: {
    color: "var(--ok)",
    label: "Internet: healthy",
    detail: "All anchors responding · nothing on the trunk is degraded.",
    bar: 4,
  },
  // One degraded anchor with the trunk healthy is a SiteIncident, not Healthy.
  siteincident: {
    color: "var(--warm-glitch)",
    label: "Internet: site incident",
    detail: "Quad9 is degraded — your equipment and the rest of the path are healthy.",
    bar: 4,
  },
  linkdown: {
    color: "var(--down)",
    label: "Internet: link down",
    detail: "LinkDown: Comcast — Comcast Edge is Down. Everything beyond your modem is unreachable.",
    bar: 6,
  },
};

/** The verdict is a tinted card with an accent edge, not a full-bleed slab:
 *  it still reads at a glance but stops shouting on a healthy page. */
export function verdictCard(kind, stripHtml) {
  const v = VERDICT_COPY[kind];
  const icon = kind === "healthy"
    ? `<svg width="18" height="18" viewBox="0 0 20 20" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><circle cx="10" cy="10" r="7.2"></circle><path d="M6.8 10.2 9 12.4l4-4.4"></path></svg>`
    : ICON.alert;
  return `<div style="border:1px solid var(--border);border-left:${v.bar}px solid ${v.color};border-radius:8px;background:color-mix(in srgb, ${v.color} 7%, var(--surface));box-shadow:var(--shadow);padding:14px 18px 16px">
      <div style="display:flex;align-items:center;gap:11px">
        <span style="color:${v.color};display:flex;flex-shrink:0">${icon}</span>
        <span style="font-size:0.97rem;font-weight:660;color:${v.color};letter-spacing:-0.008em;white-space:nowrap">${v.label}</span>
        <span style="font-size:0.86rem;color:var(--text);opacity:0.82">${v.detail}</span>
        <span style="flex:1"></span>
        <span style="display:flex;align-items:center;gap:6px;font-size:0.74rem;color:var(--muted);white-space:nowrap">
          <span style="width:6px;height:6px;border-radius:50%;background:var(--ok)"></span>live
        </span>
      </div>
      <div style="height:1px;background:var(--border);margin:14px 0 12px"></div>
      ${stripHtml}
    </div>`;
}

// ------------------------------------------------------------ event strip

const CELL = 10, GAP = 2;

/** marks: map of bin index -> {shape, hue, depth}. Everything else is quiet. */
export function eventStrip(binCount, marks) {
  const ramp = (hue, depth) => `var(--${hue}-${depth})`;
  const cells = [];
  for (let i = 0; i < binCount; i++) {
    const m = marks[i];
    if (!m) {
      cells.push(`<span style="width:${CELL}px;height:10px;border-radius:2px;flex-shrink:0;background:var(--healthy-vivid)"></span>`);
    } else if (m.shape === "dot") {
      cells.push(`<span style="width:${CELL}px;height:10px;border-radius:2px;flex-shrink:0;background:var(--healthy-vivid);display:inline-flex;align-items:center;justify-content:center"><span style="width:7px;height:7px;border-radius:50%;background:${ramp(m.hue, m.depth)}"></span></span>`);
    } else {
      cells.push(`<span style="width:${CELL}px;height:10px;border-radius:2px;flex-shrink:0;background:${ramp(m.hue, m.depth)}"></span>`);
    }
  }
  return `<div>
      <div style="display:flex;gap:${GAP}px;align-items:center">
        ${cells.join("")}
        <span style="font-size:0.66rem;color:var(--faint);white-space:nowrap;margin-left:8px">now</span>
      </div>
      <div style="font-size:0.72rem;color:var(--muted);margin-top:7px">
        The last ${binCount} minutes &mdash; green is quiet; warm marks are trouble out there, grey marks are your own gear.
      </div>
    </div>`;
}

// ------------------------------------------------------------------- tree

/** rows: {depth, name, addr, state, rate, rtt, pinned, hatched} */
export function tree(rows) {
  const html = rows.map((r) => {
    const indent = r.depth * 22;
    const color = STATE_COLOR[r.state];
    const chip = r.state === "Responding" ? "" :
      `<span style="font-size:0.69rem;font-weight:640;color:${color};background:color-mix(in srgb, ${color} 14%, transparent);border-radius:4px;padding:2px 7px;white-space:nowrap">${r.state}</span>`;
    const dot = r.hatched
      ? `<span style="width:9px;height:9px;border-radius:50%;flex-shrink:0;background:${HATCH}"></span>`
      : `<span style="width:9px;height:9px;border-radius:50%;flex-shrink:0;background:${color}"></span>`;
    const rowBg = r.pinned ? "var(--surface-2)" : "transparent";
    const stats = r.rate === null
      ? `<span style="font-family:${MONO};font-size:0.765rem;color:var(--faint);width:114px;text-align:right;flex-shrink:0">no data</span>`
      : `<span style="font-family:${MONO};font-size:0.765rem;color:var(--muted);width:46px;text-align:right;flex-shrink:0">${r.rate}</span>
         <span style="font-family:${MONO};font-size:0.765rem;color:var(--muted);width:60px;text-align:right;flex-shrink:0">${r.rtt}</span>`;
    return `<div style="display:flex;align-items:center;gap:10px;height:33px;padding:0 12px 0 ${12 + indent}px;border-radius:5px;background:${rowBg}">
        ${r.depth > 0 ? `<span style="width:12px;height:1px;background:var(--border-strong);flex-shrink:0;margin-left:-10px"></span>` : ""}
        ${dot}
        <span style="font-size:0.875rem;font-weight:${r.depth === 0 ? 580 : 470};color:var(--text);white-space:nowrap">${r.name}</span>
        <span style="font-family:${MONO};font-size:0.735rem;color:var(--faint);white-space:nowrap">${r.addr}</span>
        ${r.pinned ? `<span style="color:var(--muted);display:flex">${ICON.pin}</span>` : ""}
        <span style="flex:1"></span>
        ${chip}
        ${stats}
      </div>`;
  }).join("\n");
  return card(`<div style="padding:8px 6px">${html}</div>`);
}

// ----------------------------------------------------------------- ledger

/** items: {mark:{shape,hue,depth}, when, lead, body} */
export function ledger(items) {
  const swatch = (m) => m.shape === "dot"
    ? `<span style="display:inline-block;width:9px;height:9px;border-radius:50%;background:var(--${m.hue}-${m.depth})"></span>`
    : `<span style="display:inline-block;width:14px;height:9px;border-radius:2px;background:var(--${m.hue}-${m.depth})"></span>`;
  const rows = items.map((it) => `<div style="display:flex;gap:13px;align-items:center;padding:9px 0">
      <span style="flex-shrink:0;width:16px;display:flex;justify-content:center">${swatch(it.mark)}</span>
      <span style="font-size:0.855rem;line-height:1.45;color:var(--text)">
        <b style="font-weight:600">${it.when}</b> &mdash; <b style="font-weight:600">${it.lead}</b><span style="color:var(--muted);font-weight:400"> ${it.body}</span>
      </span>
    </div>`).join(`<div style="height:1px;background:var(--border)"></div>`);
  return card(`<div style="padding:6px 18px">${rows}</div>`);
}

// ---------------------------------------------------------------- glitches

export function glitchTable(rows) {
  const th = (t, w = "") => `<th style="text-align:left;font-size:0.675rem;font-weight:650;letter-spacing:0.08em;text-transform:uppercase;color:var(--faint);padding:0 14px 9px;${w}">${t}</th>`;
  const td = (t, mono = false, extra = "") => `<td style="font-size:0.82rem;color:var(--text);padding:9px 14px;border-top:1px solid var(--border);${mono ? `font-family:${MONO};font-size:0.775rem;color:var(--muted);` : ""}${extra}">${t}</td>`;
  const body = rows.map((r) => `<tr>
      ${td(r.when, true)}
      ${td(r.subject)}
      ${td(r.cls === "internet"
        ? `<span style="display:inline-flex;align-items:center;gap:5px;font-size:0.745rem;font-weight:600;color:var(--warm-degraded)"><span style="width:11px;height:7px;border-radius:2px;background:var(--warm-glitch)"></span>internet</span>`
        : `<span style="display:inline-flex;align-items:center;gap:5px;font-size:0.745rem;font-weight:600;color:var(--muted)"><span style="width:7px;height:7px;border-radius:50%;background:var(--warm-glitch)"></span>target</span>`)}
      ${td(r.lost, true)}
      ${td(r.dur, true)}
      ${td(`<span style="color:var(--muted)">${r.detail}</span>`)}
    </tr>`).join("");
  return card(`<div style="padding:14px 4px 4px">
      <table><thead><tr>${th("When")}${th("Subject")}${th("Class")}${th("Lost")}${th("Duration")}${th("Detail")}</tr></thead>
      <tbody>${body}</tbody></table>
    </div>`);
}

// -------------------------------------------------------------- statusbar

export function statusBar(text, connected = true) {
  return `<footer style="flex-shrink:0;height:38px;border-top:1px solid var(--border);background:var(--surface);display:flex;align-items:center;gap:12px;padding:0 26px">
      <span style="width:6px;height:6px;border-radius:50%;background:var(--${connected ? "ok" : "warn"});flex-shrink:0"></span>
      <span style="font-family:${MONO};font-size:0.755rem;color:var(--muted)">${text}</span>
      <span style="flex:1"></span>
      <a href="#" style="display:flex;align-items:center;gap:5px;font-size:0.775rem;color:var(--muted)">show all events<span style="display:flex;transform:rotate(180deg)">${ICON.chevron}</span></a>
    </footer>`;
}
