// Shared markup helpers for the ConnectionWatcher UI artboards.
// Values lifted from the real app: Pico 2 tokens + wwwroot/css/site.css
// + ClientApp/src/lib/encoding.ts.
import { readFileSync } from "node:fs";

export const LIGHT = readFileSync(new URL("tokens-light.txt", import.meta.url), "utf8");
export const DARK = readFileSync(new URL("tokens-dark.txt", import.meta.url), "utf8");

// Single-quoted family names: these stacks are interpolated into
// double-quoted style="..." attributes, where a double quote would end the
// attribute and silently drop everything after it (the token block included).
export const FONT = "system-ui, 'Segoe UI', Roboto, Oxygen, Ubuntu, Cantarell, Helvetica, Arial, sans-serif";
export const MONO = "ui-monospace, 'Cascadia Mono', 'Segoe UI Mono', Consolas, monospace";

const S = 'fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"';

export const ICON = {
  brand: `<svg width="22" height="22" viewBox="0 0 22 22" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round"><circle cx="11" cy="11" r="2" fill="currentColor" stroke="none"></circle><path d="M15.1 6.9a5.8 5.8 0 0 1 0 8.2"></path><path d="M6.9 15.1a5.8 5.8 0 0 1 0-8.2"></path><path d="M17.9 4.1a9.7 9.7 0 0 1 0 13.8" opacity="0.4"></path><path d="M4.1 17.9a9.7 9.7 0 0 1 0-13.8" opacity="0.4"></path></svg>`,
  dashboard: `<svg width="16" height="16" viewBox="0 0 20 20" ${S}><path d="M3.4 14.2a7.6 7.6 0 1 1 13.2 0"></path><path d="M10 11.4 13.1 7.6"></path><circle cx="10" cy="12.4" r="1.3" fill="currentColor" stroke="none"></circle></svg>`,
  history: `<svg width="16" height="16" viewBox="0 0 20 20" ${S}><path d="M3.4 9.4a6.8 6.8 0 1 0 1.9-4.6"></path><path d="M3 3.4v3.6h3.6"></path><path d="M10 6.6V10l2.5 1.5"></path></svg>`,
  data: `<svg width="16" height="16" viewBox="0 0 20 20" ${S}><ellipse cx="10" cy="5.4" rx="5.8" ry="2.4"></ellipse><path d="M4.2 5.4v9.2c0 1.33 2.6 2.4 5.8 2.4s5.8-1.07 5.8-2.4V5.4"></path><path d="M4.2 10c0 1.33 2.6 2.4 5.8 2.4s5.8-1.07 5.8-2.4"></path></svg>`,
  targets: `<svg width="16" height="16" viewBox="0 0 20 20" ${S}><circle cx="4.6" cy="10" r="2.1"></circle><circle cx="15.4" cy="5.2" r="2.1"></circle><circle cx="15.4" cy="14.8" r="2.1"></circle><path d="M6.6 9.1 13.4 6"></path><path d="M6.6 10.9 13.4 14"></path></svg>`,
  traceroute: `<svg width="16" height="16" viewBox="0 0 20 20" ${S}><circle cx="4.4" cy="15.6" r="1.9"></circle><circle cx="15.6" cy="4.4" r="1.9"></circle><circle cx="10" cy="10" r="1.2" fill="currentColor" stroke="none"></circle><path d="M6.3 15.6h2.2a3.2 3.2 0 0 0 3.2-3.2V8.2a3.2 3.2 0 0 1 2-2.9"></path></svg>`,
  admin: `<svg width="16" height="16" viewBox="0 0 20 20" ${S}><path d="M3.4 6.2h13.2M3.4 10h13.2M3.4 13.8h13.2"></path><circle cx="7.6" cy="6.2" r="1.9" fill="var(--surface)"></circle><circle cx="13" cy="10" r="1.9" fill="var(--surface)"></circle><circle cx="6.6" cy="13.8" r="1.9" fill="var(--surface)"></circle></svg>`,
  lock: `<svg width="16" height="16" viewBox="0 0 20 20" ${S}><rect x="4.6" y="8.6" width="10.8" height="7.8" rx="2"></rect><path d="M7.2 8.6V6.4a2.8 2.8 0 0 1 5.6 0v2.2"></path></svg>`,
  moon: `<svg width="16" height="16" viewBox="0 0 20 20" ${S}><path d="M15.6 11.9A6.4 6.4 0 0 1 8.1 4.4a6.4 6.4 0 1 0 7.5 7.5z"></path></svg>`,
  sun: `<svg width="16" height="16" viewBox="0 0 20 20" ${S}><circle cx="10" cy="10" r="3.5"></circle><path d="M10 2.6v1.9M10 15.5v1.9M2.6 10h1.9M15.5 10h1.9M4.8 4.8l1.35 1.35M13.85 13.85 15.2 15.2M15.2 4.8l-1.35 1.35M6.15 13.85 4.8 15.2"></path></svg>`,
  menu: `<svg width="20" height="20" viewBox="0 0 20 20" ${S}><path d="M3.4 6h13.2M3.4 10h13.2M3.4 14h13.2"></path></svg>`,
  close: `<svg width="20" height="20" viewBox="0 0 20 20" ${S}><path d="M5.2 5.2l9.6 9.6M14.8 5.2l-9.6 9.6"></path></svg>`,
  alert: `<svg width="16" height="16" viewBox="0 0 20 20" ${S}><path d="M10 3.4 17.4 16.2H2.6z"></path><path d="M10 8.2v3.4"></path><circle cx="10" cy="13.9" r="0.85" fill="currentColor" stroke="none"></circle></svg>`,
  download: `<svg width="15" height="15" viewBox="0 0 20 20" ${S}><path d="M10 3.4v8.8"></path><path d="M6.6 9.2 10 12.6l3.4-3.4"></path><path d="M4.2 16.2h11.6"></path></svg>`,
  report: `<svg width="15" height="15" viewBox="0 0 20 20" ${S}><path d="M5 2.8h6.6L15.6 6.8v10.4H5z"></path><path d="M11.4 2.8v4.2h4.2"></path><path d="M7.6 11.2h5M7.6 14h3.4"></path></svg>`,
  chevron: `<svg width="14" height="14" viewBox="0 0 20 20" ${S}><path d="M6.4 8.2 10 11.8l3.6-3.6"></path></svg>`,
  pin: `<svg width="13" height="13" viewBox="0 0 20 20" ${S}><path d="M12.4 2.8 17.2 7.6l-2.6 1-1.1 4.1-5.2-5.2 4.1-1.1z"></path><path d="M8.3 11.7 3.6 16.4"></path></svg>`,
  plus: `<svg width="15" height="15" viewBox="0 0 20 20" ${S}><path d="M10 4.4v11.2M4.4 10h11.2"></path></svg>`,
  search: `<svg width="15" height="15" viewBox="0 0 20 20" ${S}><circle cx="8.8" cy="8.8" r="5.2"></circle><path d="M12.6 12.6 16.6 16.6"></path></svg>`,
};

/** A section rule: uppercase micro-label, hairline, optional right-hand note. */
export function section(label, note = "") {
  return `<div style="display:flex;align-items:center;gap:12px;margin-bottom:10px">
      <span style="font-size:0.7rem;font-weight:650;letter-spacing:0.09em;text-transform:uppercase;color:var(--faint);white-space:nowrap">${label}</span>
      <span style="flex:1;height:1px;background:var(--border)"></span>
      ${note ? `<span style="font-size:0.75rem;color:var(--muted);white-space:nowrap">${note}</span>` : ""}
    </div>`;
}

export function card(inner, extra = "") {
  return `<div style="background:var(--surface);border:1px solid var(--border);border-radius:8px;box-shadow:var(--shadow);${extra}">${inner}</div>`;
}

const NAV_WATCH = [["dashboard", "Dashboard"], ["history", "History"]];
const NAV_MANAGE = [["data", "Data"], ["targets", "Targets"], ["traceroute", "Traceroute"], ["admin", "Admin"]];

function navItem(key, label, active) {
  const on = key === active;
  return `<a href="#" style="display:flex;align-items:center;gap:7px;height:56px;padding:0 3px;text-decoration:none;font-size:0.855rem;font-weight:${on ? 600 : 450};color:var(--${on ? "text" : "muted"});box-shadow:${on ? "inset 0 -2px 0 var(--primary)" : "none"}">${ICON[key]}<span>${label}</span></a>`;
}

const VERDICTS = {
  healthy: { label: "Healthy", color: "var(--ok)" },
  siteincident: { label: "Site incident", color: "var(--warm-glitch)" },
  weather: { label: "Weather", color: "var(--warn)" },
  linkdown: { label: "Link down", color: "var(--down)" },
};

/** Verdict is carried in the bar on EVERY page, not just the dashboard. */
export function verdictPip(key) {
  const v = VERDICTS[key] ?? VERDICTS.healthy;
  return `<span style="display:flex;align-items:center;gap:7px;background:var(--surface-2);border:1px solid var(--border);border-radius:999px;padding:4px 11px 4px 9px">
      <span style="width:8px;height:8px;border-radius:50%;background:${v.color};flex-shrink:0"></span>
      <span style="font-size:0.775rem;font-weight:600;color:var(--text)">${v.label}</span>
    </span>`;
}

/** The top bar: brand, grouped nav, then the always-present verdict pip. */
export function appbar({ active, verdict, theme }) {
  const themeIcon = theme === "dark" ? ICON.sun : ICON.moon;
  return `<header style="flex-shrink:0;height:56px;background:var(--surface);border-bottom:1px solid var(--border);display:flex;align-items:center;gap:26px;padding:0 26px">
    <div style="display:flex;align-items:center;gap:9px;flex-shrink:0">
      <span style="color:var(--primary);display:flex">${ICON.brand}</span>
      <span style="font-size:0.97rem;font-weight:640;letter-spacing:-0.012em;color:var(--text)">ConnectionWatcher</span>
      <span style="font-family:${MONO};font-size:0.66rem;color:var(--faint);background:var(--surface-2);border:1px solid var(--border);border-radius:4px;padding:1px 5px">v1.4.2</span>
    </div>
    <nav style="display:flex;align-items:center;gap:22px">
      ${NAV_WATCH.map(([k, l]) => navItem(k, l, active)).join("\n      ")}
      <span style="width:1px;height:20px;background:var(--border-strong)"></span>
      ${NAV_MANAGE.map(([k, l]) => navItem(k, l, active)).join("\n      ")}
    </nav>
    <div style="flex:1"></div>
    <div style="display:flex;align-items:center;gap:10px;flex-shrink:0">
      ${verdictPip(verdict)}
      <button style="width:31px;height:31px;display:flex;align-items:center;justify-content:center;border:1px solid var(--border);background:var(--surface-2);color:var(--muted);border-radius:5px;cursor:pointer;padding:0">${themeIcon}</button>
      <button style="width:31px;height:31px;display:flex;align-items:center;justify-content:center;border:1px solid var(--border);background:var(--surface-2);color:var(--muted);border-radius:5px;cursor:pointer;padding:0">${ICON.lock}</button>
    </div>
  </header>`;
}

/** Page band: title, one-line purpose, right-hand actions. */
export function pageband(title, purpose, right = "") {
  return `<div style="flex-shrink:0;padding:20px 26px 16px;display:flex;align-items:flex-end;gap:24px">
    <div style="flex:1;min-width:0">
      <h1 style="margin:0 0 3px;font-size:1.33rem;font-weight:650;letter-spacing:-0.015em;color:var(--text);line-height:1.2">${title}</h1>
      <p style="margin:0;font-size:0.825rem;color:var(--muted);line-height:1.4">${purpose}</p>
    </div>
    ${right}
  </div>`;
}

export function ghostButton(icon, label) {
  return `<a href="#" style="display:flex;align-items:center;gap:7px;height:32px;padding:0 12px;border:1px solid var(--border-strong);background:var(--surface);color:var(--text);border-radius:5px;font-size:0.815rem;font-weight:520;text-decoration:none;white-space:nowrap">${icon}<span>${label}</span></a>`;
}

export function primaryButton(label) {
  return `<button style="height:32px;padding:0 14px;border:1px solid var(--primary);background:var(--primary);color:#fff;border-radius:5px;font-size:0.815rem;font-weight:560;cursor:pointer;font-family:inherit">${label}</button>`;
}

/** Wraps a finished page. Theme swaps only the token block. */
export function page({ theme, w, h, body }) {
  const tokens = theme === "dark" ? DARK : LIGHT;
  return `<!doctype html>
<html>
<head>
  <meta charset="utf-8">
  <script src="./support.js"></script>
</head>
<body>
<x-dc>
<helmet>
  <style>
    body { margin: 0; font-family: ${FONT}; }
    a { color: var(--primary); text-decoration: none; }
    a:hover { color: var(--primary); text-decoration: underline; }
    table { border-collapse: collapse; width: 100%; }
  </style>
</helmet>
<div style="width:${w}px;min-height:${h}px;background:var(--bg);color:var(--text);font-family:${FONT};font-size:14px;line-height:1.5;display:flex;flex-direction:column;
${tokens}">
${body}
</div>
</x-dc>
<script data-dc-script data-props='{"$preview":{"width":${w},"height":${h}}}'>
class Component extends DCLogic {}
</script>
</body>
</html>
`;
}
