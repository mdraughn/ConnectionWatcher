// History-page figures: stat tiles, state ribbon, latency line, loss bars,
// the 30-day recurrence punchcard, the cause-aligned ribbon board, legend.
import { MONO, card } from "./lib-shell.mjs";
import { HATCH } from "./lib-parts.mjs";

/** Deterministic pseudo-random so rebuilds are byte-identical. */
function rng(seed) {
  let s = seed;
  return () => ((s = (s * 1103515245 + 12345) & 0x7fffffff) / 0x7fffffff);
}

// ------------------------------------------------------------- stat tiles

export function statTiles(tiles) {
  return `<div style="display:grid;grid-template-columns:repeat(4, minmax(0, 1fr));gap:14px">
    ${tiles.map((t) => `<div style="background:var(--surface);border:1px solid var(--border);border-radius:8px;box-shadow:var(--shadow);padding:14px 16px">
        <div style="font-family:${MONO};font-size:1.45rem;font-weight:600;letter-spacing:-0.02em;color:${t.color ?? "var(--text)"};line-height:1.15">${t.value}</div>
        <div style="font-size:0.755rem;color:var(--muted);margin-top:5px">${t.label}</div>
      </div>`).join("")}
  </div>`;
}

// ----------------------------------------------------------- state ribbon

let hatchSeq = 0;

/** segs: {frac, fill} where fill is a token name or "hatch". */
export function stateRibbon(segs, w = 1204, h = 30) {
  const hid = `hatch${++hatchSeq}`;
  let x = 0;
  const rects = segs.map((s, i) => {
    const sw = s.frac * w;
    const fill = s.fill === "hatch" ? `url(#${hid})` : `var(--${s.fill})`;
    const r = `<rect x="${x.toFixed(1)}" y="0" width="${Math.max(sw - (i < segs.length - 1 ? 2 : 0), 1).toFixed(1)}" height="${h}" rx="3" fill="${fill}"></rect>`;
    x += sw;
    return r;
  }).join("");
  return `<svg viewBox="0 0 ${w} ${h}" width="100%" height="${h}" preserveAspectRatio="none" style="display:block">
      <defs><pattern id="${hid}" width="6" height="6" patternUnits="userSpaceOnUse" patternTransform="rotate(45)">
        <rect width="6" height="6" fill="var(--surface-2)"></rect><rect width="3" height="6" fill="var(--grey-glitch)"></rect>
      </pattern></defs>${rects}
    </svg>`;
}

export function axisRow(labels) {
  return `<div style="display:flex;justify-content:space-between;margin-top:6px">
    ${labels.map((l) => `<span style="font-family:${MONO};font-size:0.68rem;color:var(--faint)">${l}</span>`).join("")}
  </div>`;
}

// ---------------------------------------------------------- latency line

export function latencyChart(w = 1204, h = 150) {
  const rand = rng(7);
  const n = 144;                       // 24 h at 10-min resolution
  const pad = { l: 42, r: 12, t: 12, b: 22 };
  const pw = w - pad.l - pad.r, ph = h - pad.t - pad.b;
  const yMax = 100;
  const pts = [];
  for (let i = 0; i < n; i++) {
    let v = 19 + rand() * 5;
    if (i > 84 && i < 92) v = 34 + rand() * 22;   // the degradation ramp
    if (i >= 92 && i < 96) v = 88 + rand() * 9;   // the spike before Down
    pts.push(v);
  }
  const X = (i) => pad.l + (i / (n - 1)) * pw;
  const Y = (v) => pad.t + ph - (v / yMax) * ph;
  const line = pts.map((v, i) => `${X(i).toFixed(1)},${Y(v).toFixed(1)}`).join(" ");
  const grid = [0, 25, 50, 75, 100].map((v) =>
    `<line x1="${pad.l}" y1="${Y(v).toFixed(1)}" x2="${w - pad.r}" y2="${Y(v).toFixed(1)}" stroke="var(--border)" stroke-width="1"></line>
     <text x="${pad.l - 9}" y="${(Y(v) + 3.5).toFixed(1)}" text-anchor="end" font-family="${MONO}" font-size="10" fill="var(--faint)">${v}</text>`).join("");
  const xl = ["15:00", "19:00", "23:00", "03:00", "07:00", "11:00", "14:08"].map((t, i, a) =>
    `<text x="${(pad.l + (i / (a.length - 1)) * pw).toFixed(1)}" y="${h - 5}" text-anchor="${i === 0 ? "start" : i === a.length - 1 ? "end" : "middle"}" font-family="${MONO}" font-size="10" fill="var(--faint)">${t}</text>`).join("");
  const peak = pts.indexOf(Math.max(...pts));
  return `<svg viewBox="0 0 ${w} ${h}" width="100%" height="${h}" style="display:block">
      ${grid}${xl}
      <polyline points="${line}" fill="none" stroke="var(--primary)" stroke-width="2" stroke-linejoin="round" stroke-linecap="round"></polyline>
      <circle cx="${X(peak).toFixed(1)}" cy="${Y(pts[peak]).toFixed(1)}" r="4" fill="var(--primary)" stroke="var(--surface)" stroke-width="2"></circle>
      <text x="${(X(peak) - 10).toFixed(1)}" y="${(Y(pts[peak]) - 8).toFixed(1)}" text-anchor="end" font-family="${MONO}" font-size="10.5" fill="var(--muted)">96 ms peak</text>
    </svg>`;
}

// ------------------------------------------------------------- loss bars

export function lossChart(w = 1204, h = 96) {
  const rand = rng(23);
  const n = 72;
  const pad = { l: 42, r: 12, t: 10, b: 20 };
  const pw = w - pad.l - pad.r, ph = h - pad.t - pad.b;
  const bw = pw / n - 2;
  const bars = [];
  for (let i = 0; i < n; i++) {
    let v = rand() < 0.86 ? 0 : rand() * 6;
    if (i >= 43 && i <= 45) v = 35 + rand() * 25;
    if (i === 46) v = 100;
    if (v === 0) continue;
    const bh = Math.max((v / 100) * ph, 3);
    const color = v > 30 ? "var(--down)" : "var(--warn)";
    bars.push(`<rect x="${(pad.l + i * (pw / n)).toFixed(1)}" y="${(pad.t + ph - bh).toFixed(1)}" width="${bw.toFixed(1)}" height="${bh.toFixed(1)}" rx="2" fill="${color}"></rect>`);
  }
  const grid = [0, 50, 100].map((v) =>
    `<line x1="${pad.l}" y1="${(pad.t + ph - (v / 100) * ph).toFixed(1)}" x2="${w - pad.r}" y2="${(pad.t + ph - (v / 100) * ph).toFixed(1)}" stroke="var(--border)" stroke-width="1"></line>
     <text x="${pad.l - 9}" y="${(pad.t + ph - (v / 100) * ph + 3.5).toFixed(1)}" text-anchor="end" font-family="${MONO}" font-size="10" fill="var(--faint)">${v}</text>`).join("");
  return `<svg viewBox="0 0 ${w} ${h}" width="100%" height="${h}" style="display:block">${grid}${bars.join("")}</svg>`;
}

// ------------------------------------------------------------- punchcard

const CW = 22, CH = 14;

export function punchcard(days = 30) {
  const rand = rng(101);
  // A nightly gremlin at 03:00 plus scattered daytime noise: the horizontal
  // run along one hour's row is the whole point of the display.
  const cells = new Map();
  const put = (d, h, m) => cells.set(`${d}-${h}`, m);
  for (let d = 6; d < days; d++) {
    if (rand() < 0.72) put(d, 3, { shape: "bar", hue: "grey", depth: rand() < 0.3 ? "out" : "degraded" });
  }
  for (let i = 0; i < 34; i++) {
    const d = Math.floor(rand() * days), h = Math.floor(rand() * 24);
    if (cells.has(`${d}-${h}`)) continue;
    put(d, h, { shape: "dot", hue: "warm", depth: rand() < 0.75 ? "glitch" : "degraded" });
  }
  put(28, 14, { shape: "bar", hue: "warm", depth: "degraded" });
  put(28, 15, { shape: "bar", hue: "warm", depth: "degraded" });
  put(29, 14, { shape: "bar", hue: "grey", depth: "out" });

  const rows = [];
  for (let h = 0; h < 24; h++) {
    const label = `<div style="font-family:${MONO};font-size:0.63rem;color:var(--faint);text-align:right;padding-right:6px;line-height:${CH}px">${h % 6 === 0 ? `${String(h).padStart(2, "0")}:00` : ""}</div>`;
    const cs = [];
    for (let d = 0; d < days; d++) {
      const m = cells.get(`${d}-${h}`);
      if (!m) { cs.push(`<div style="height:${CH}px;border-radius:2px;background:var(--surface-3)"></div>`); continue; }
      if (m.shape === "dot") {
        cs.push(`<div style="height:${CH}px;border-radius:2px;background:var(--surface-3);display:flex;align-items:center;justify-content:center"><span style="width:8px;height:8px;border-radius:50%;background:var(--${m.hue}-${m.depth})"></span></div>`);
      } else {
        cs.push(`<div style="height:${CH}px;border-radius:2px;background:var(--${m.hue}-${m.depth})"></div>`);
      }
    }
    rows.push(label + cs.join(""));
  }
  const dayLabels = `<div></div>` + Array.from({ length: days }, (_, d) =>
    `<div style="font-family:${MONO};font-size:0.63rem;color:var(--faint);text-align:center">${d % 5 === 4 ? `${8 + Math.floor((d + 1) / 5)}/${((d * 1 + 14) % 28) + 1}` : ""}</div>`).join("");
  return `<div style="display:grid;gap:2px;grid-template-columns:44px repeat(${days}, ${CW}px);width:max-content">
      ${rows.join("")}${dayLabels}
    </div>`;
}

/** The scope x severity matrix - the one legend every display shares. */
export function encodingLegend() {
  const sw = (shape, hue, depth) => shape === "dot"
    ? `<span style="display:inline-block;width:9px;height:9px;border-radius:50%;background:var(--${hue}-${depth})"></span>`
    : `<span style="display:inline-block;width:22px;height:9px;border-radius:2px;background:var(--${hue}-${depth})"></span>`;
  const row = (label, note, shape, hue) => `
    <div style="font-size:0.755rem;color:var(--text)">${label} <span style="color:var(--faint)">(${note})</span></div>
    <div>${sw(shape, hue, "glitch")}</div><div>${sw(shape, hue, "degraded")}</div><div>${sw(shape, hue, "out")}</div>`;
  return `<div style="margin-top:14px">
      <div style="display:grid;gap:7px 18px;align-items:center;grid-template-columns:auto auto auto auto;width:max-content">
        <div></div>
        ${["glitch", "degraded", "fully out"].map((d) => `<div style="font-size:0.7rem;color:var(--faint)">${d}</div>`).join("")}
        ${row("the internet", "bar", "bar", "warm")}
        ${row("one site", "dot", "dot", "warm")}
        ${row("your equipment", "bar", "bar", "grey")}
      </div>
      <div style="display:flex;align-items:center;gap:9px;margin-top:12px;font-size:0.745rem;color:var(--muted)">
        <span style="display:inline-block;width:22px;height:9px;border-radius:2px;background:linear-gradient(to bottom right, var(--grey-glitch) 49.5%, var(--warm-glitch) 50.5%)"></span>
        ambiguous &mdash; evidence splits between your equipment and the outside world
        <span style="width:14px"></span>
        <span style="display:inline-block;width:22px;height:9px;border-radius:2px;background:${HATCH}"></span>
        unreachable behind a down parent (no data, no blame)
      </div>
    </div>`;
}

// ---------------------------------------------------------- ribbon board

/** Every target on one shared axis, so an incident reads as a vertical stripe. */
export function ribbonBoard(subjects) {
  const rows = subjects.map((s) => `<div style="display:flex;align-items:center;gap:14px;height:26px">
      <span style="width:132px;flex-shrink:0;font-size:0.79rem;color:var(--${s.role === "trunk" ? "text" : "muted"});font-weight:${s.role === "trunk" ? 560 : 440};text-align:right;white-space:nowrap;overflow:hidden;text-overflow:ellipsis">${s.name}</span>
      <span style="flex:1">${stateRibbon(s.segs, 1040, 18)}</span>
    </div>`).join("");
  return `<div>${rows}</div>`;
}
