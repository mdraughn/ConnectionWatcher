import { writeFileSync } from "node:fs";
import { ICON, MONO, card, page, section, verdictPip } from "./lib-shell.mjs";
import { HATCH, eventStrip } from "./lib-parts.mjs";

const W = 390;

/** Mobile bar: hamburger, mark, and the verdict pip - the two things worth
 *  the width on a phone. Everything else moves into the drawer. */
function mobileBar(verdict, theme) {
  return `<header style="flex-shrink:0;height:54px;background:var(--surface);border-bottom:1px solid var(--border);display:flex;align-items:center;gap:10px;padding:0 12px">
      <button style="width:38px;height:38px;display:flex;align-items:center;justify-content:center;border:none;background:transparent;color:var(--text);border-radius:6px;cursor:pointer;padding:0;flex-shrink:0">${ICON.menu}</button>
      <span style="color:var(--primary);display:flex;flex-shrink:0">${ICON.brand}</span>
      <span style="font-size:0.885rem;font-weight:640;letter-spacing:-0.015em;color:var(--text);white-space:nowrap;overflow:hidden;text-overflow:ellipsis">ConnectionWatcher</span>
      <span style="flex:1"></span>
      ${verdictPip(verdict)}
    </header>`;
}

function mobileBand(title, purpose) {
  return `<div style="flex-shrink:0;padding:16px 16px 12px">
      <h1 style="margin:0 0 3px;font-size:1.16rem;font-weight:650;letter-spacing:-0.015em;color:var(--text);line-height:1.2">${title}</h1>
      <p style="margin:0;font-size:0.795rem;color:var(--muted);line-height:1.4">${purpose}</p>
    </div>`;
}

const CHECK = `<svg width="17" height="17" viewBox="0 0 20 20" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><circle cx="10" cy="10" r="7.2"></circle><path d="M6.8 10.2 9 12.4l4-4.4"></path></svg>`;

const MOBILE_VERDICT = {
  healthy: { c: "var(--ok)", icon: CHECK, label: "Internet: healthy", detail: "All anchors responding &middot; nothing on the trunk is degraded." },
  siteincident: { c: "var(--warm-glitch)", icon: ICON.alert, label: "Internet: site incident", detail: "Quad9 is degraded &mdash; your equipment and the rest of the path are healthy." },
};

/** Stacked verdict: label, wrapped detail, then the strip. */
function mobileVerdict(stripHtml, kind = "healthy") {
  const v = MOBILE_VERDICT[kind];
  return `<div style="border:1px solid var(--border);border-left:4px solid ${v.c};border-radius:8px;background:color-mix(in srgb, ${v.c} 7%, var(--surface));box-shadow:var(--shadow);padding:13px 14px 14px">
      <div style="display:flex;align-items:center;gap:9px">
        <span style="color:${v.c};display:flex;flex-shrink:0">${v.icon}</span>
        <span style="font-size:0.925rem;font-weight:660;color:${v.c};letter-spacing:-0.008em">${v.label}</span>
        <span style="flex:1"></span>
        <span style="display:flex;align-items:center;gap:5px;font-size:0.71rem;color:var(--muted)"><span style="width:6px;height:6px;border-radius:50%;background:var(--ok)"></span>live</span>
      </div>
      <p style="margin:7px 0 0;font-size:0.825rem;color:var(--text);opacity:0.82;line-height:1.45">${v.detail}</p>
      <div style="height:1px;background:var(--border);margin:13px 0 12px"></div>
      ${stripHtml}
    </div>`;
}

/** 44px rows, reduced indent, address dropped - it lives on Targets. */
function mobileTree(rows) {
  const STATE_COLOR = { Responding: "var(--ok)", Faltering: "var(--warn)", Down: "var(--down)", Unreachable: "var(--unreach)" };
  const html = rows.map((r) => {
    const color = STATE_COLOR[r.state];
    const chip = r.state === "Responding" ? "" :
      `<span style="font-size:0.69rem;font-weight:640;color:${color};background:color-mix(in srgb, ${color} 14%, transparent);border-radius:4px;padding:2px 7px;white-space:nowrap">${r.state}</span>`;
    return `<div style="display:flex;align-items:center;gap:9px;height:44px;padding:0 12px 0 ${12 + r.depth * 15}px;border-radius:5px;background:${r.pinned ? "var(--surface-2)" : "transparent"}">
        ${r.depth > 0 ? `<span style="width:9px;height:1px;background:var(--border-strong);flex-shrink:0;margin-left:-9px"></span>` : ""}
        <span style="width:9px;height:9px;border-radius:50%;flex-shrink:0;background:${r.hatched ? HATCH : color}"></span>
        <span style="font-size:0.85rem;font-weight:${r.depth === 0 ? 580 : 460};color:var(--text);white-space:nowrap;overflow:hidden;text-overflow:ellipsis">${r.name}</span>
        <span style="flex:1"></span>
        ${chip}
        <span style="font-family:${MONO};font-size:0.735rem;color:var(--muted);white-space:nowrap">${r.rate} &middot; ${r.rtt}</span>
      </div>`;
  }).join("");
  return card(`<div style="padding:6px 4px">${html}</div>`);
}

function mobileLedger(items) {
  const swatch = (m) => m.shape === "dot"
    ? `<span style="display:inline-block;width:9px;height:9px;border-radius:50%;background:var(--${m.hue}-${m.depth})"></span>`
    : `<span style="display:inline-block;width:14px;height:9px;border-radius:2px;background:var(--${m.hue}-${m.depth})"></span>`;
  const rows = items.map((it) => `<div style="display:flex;gap:11px;align-items:flex-start;padding:11px 0">
      <span style="flex-shrink:0;width:15px;display:flex;justify-content:center;margin-top:4px">${swatch(it.mark)}</span>
      <span style="font-size:0.83rem;line-height:1.5;color:var(--text)">
        <b style="font-weight:600">${it.lead}</b><span style="color:var(--muted);font-weight:400"> ${it.body}</span>
        <span style="display:block;font-family:${MONO};font-size:0.715rem;color:var(--faint);margin-top:3px">${it.when}</span>
      </span>
    </div>`).join(`<div style="height:1px;background:var(--border)"></div>`);
  return card(`<div style="padding:4px 14px">${rows}</div>`);
}

/** Tables do not survive 390px: each glitch becomes its own stacked row. */
function mobileGlitches(items) {
  const rows = items.map((g) => `<div style="padding:11px 14px">
      <div style="display:flex;align-items:center;gap:8px">
        <span style="font-size:0.845rem;font-weight:570;color:var(--text)">${g.subject}</span>
        <span style="flex:1"></span>
        <span style="display:inline-flex;align-items:center;gap:5px;font-size:0.715rem;font-weight:600;color:var(--${g.cls === "internet" ? "warm-degraded" : "muted"})">
          <span style="${g.cls === "internet" ? "width:11px;height:7px;border-radius:2px" : "width:7px;height:7px;border-radius:50%"};background:var(--warm-glitch)"></span>${g.cls}
        </span>
      </div>
      <div style="font-family:${MONO};font-size:0.725rem;color:var(--muted);margin-top:4px">${g.when} &middot; lost ${g.lost} &middot; ${g.dur}</div>
      <div style="font-size:0.78rem;color:var(--faint);margin-top:2px">${g.detail}</div>
    </div>`).join(`<div style="height:1px;background:var(--border)"></div>`);
  return card(`<div>${rows}</div>`);
}

// ------------------------------------------------------- mobile dashboard

const quiet = {};
quiet[9] = { shape: "dot", hue: "warm", depth: "glitch" };
quiet[18] = { shape: "dot", hue: "warm", depth: "degraded" };

const mobileBody = [
  mobileBar("siteincident", "light"),
  mobileBand("Dashboard", "Live reachability &mdash; what your connection is doing right now."),
  `<main style="flex:1;padding:0 16px 26px;display:flex;flex-direction:column;gap:20px">
    ${mobileVerdict(eventStrip(24, quiet), "siteincident")}
    <div>${section("Reachability")}${mobileTree([
      { depth: 0, name: "Router", state: "Responding", rate: "100%", rtt: "1 ms" },
      { depth: 1, name: "Synology NAS", state: "Responding", rate: "100%", rtt: "1 ms" },
      { depth: 1, name: "Home Assistant", state: "Responding", rate: "100%", rtt: "2 ms" },
      { depth: 1, name: "Cable Modem", state: "Responding", rate: "100%", rtt: "8 ms" },
      { depth: 2, name: "Comcast Edge", state: "Responding", rate: "100%", rtt: "14 ms" },
      { depth: 3, name: "Comcast Core", state: "Responding", rate: "99%", rtt: "21 ms" },
      { depth: 4, name: "Google DNS", state: "Responding", rate: "100%", rtt: "19 ms" },
      { depth: 4, name: "Cloudflare DNS", state: "Responding", rate: "100%", rtt: "17 ms" },
      { depth: 4, name: "Quad9", state: "Faltering", rate: "85%", rtt: "31 ms", pinned: true },
      { depth: 4, name: "OpenDNS", state: "Responding", rate: "100%", rtt: "23 ms" },
    ])}</div>
    <div>${section("What happened", "most recent")}${mobileLedger([
      { mark: { shape: "dot", hue: "warm", depth: "glitch" }, when: "Sat, Sep 12, 9:14 PM", lead: "Quad9 glitched for 8 s.", body: "3 of 20 pings lost — no other anchor saw it." },
      { mark: { shape: "bar", hue: "grey", depth: "out" }, when: "Fri, Sep 11, 3:02 AM", lead: "Your link went down for 4 min.", body: "LinkDown: Comcast — Comcast Edge is Down." },
      { mark: { shape: "bar", hue: "warm", depth: "degraded" }, when: "Thu, Sep 10, 8:47 PM", lead: "Internet weather for 11 min.", body: "Two anchors degraded at once; your own gear stayed healthy." },
    ])}</div>
    <div>${section("Recent glitches", "last 24 h")}${mobileGlitches([
      { subject: "Quad9", cls: "internet", when: "9/12 21:14", lost: "3/20", dur: "8 s", detail: "burst loss 15%" },
      { subject: "Comcast Edge", cls: "target", when: "9/12 18:02", lost: "2/20", dur: "5 s", detail: "single-hop wobble" },
      { subject: "Quad9", cls: "internet", when: "9/12 11:37", lost: "4/20", dur: "11 s", detail: "burst loss 20%" },
    ])}</div>
  </main>`,
  `<footer style="flex-shrink:0;height:44px;border-top:1px solid var(--border);background:var(--surface);display:flex;align-items:center;gap:10px;padding:0 16px">
      <span style="width:6px;height:6px;border-radius:50%;background:var(--ok);flex-shrink:0"></span>
      <span style="font-family:${MONO};font-size:0.72rem;color:var(--muted);white-space:nowrap;overflow:hidden;text-overflow:ellipsis">21:14:07 Quad9: Responding &rarr; Faltering</span>
      <span style="flex:1"></span>
      <span style="display:flex;color:var(--muted);transform:rotate(180deg)">${ICON.chevron}</span>
    </footer>`,
].join("\n");

writeFileSync("Mobile.dc.html", page({ theme: "light", w: W, h: 1660, body: mobileBody }));

// ------------------------------------------------------------ nav drawer

function drawerItem(key, label, active) {
  return `<a href="#" style="display:flex;align-items:center;gap:12px;height:46px;padding:0 16px;border-radius:7px;text-decoration:none;font-size:0.9rem;font-weight:${active ? 600 : 460};color:var(--${active ? "text" : "muted"});background:${active ? "var(--primary-soft)" : "transparent"};${active ? "box-shadow:inset 3px 0 0 var(--primary);" : ""}">
      <span style="color:var(--${active ? "primary" : "muted"});display:flex">${ICON[key]}</span>${label}
    </a>`;
}

const drawerLabel = (t) => `<div style="font-size:0.665rem;font-weight:650;letter-spacing:0.1em;text-transform:uppercase;color:var(--faint);padding:0 16px 6px">${t}</div>`;

const navBody = `<div style="position:relative;width:${W}px;height:844px;overflow:hidden">
    <div style="position:absolute;inset:0;display:flex;flex-direction:column;filter:blur(1px)">
      ${mobileBar("healthy", "dark")}
      ${mobileBand("Dashboard", "Live reachability &mdash; what your connection is doing right now.")}
      <div style="padding:0 16px">${mobileVerdict(eventStrip(24, {}))}</div>
    </div>
    <div style="position:absolute;inset:0;background:rgba(6,8,13,0.62)"></div>
    <aside style="position:absolute;top:0;left:0;bottom:0;width:302px;background:var(--surface);border-right:1px solid var(--border);box-shadow:6px 0 28px rgba(0,0,0,0.45);display:flex;flex-direction:column">
      <div style="display:flex;align-items:center;gap:9px;padding:14px 14px 14px 16px;border-bottom:1px solid var(--border)">
        <span style="color:var(--primary);display:flex">${ICON.brand}</span>
        <span style="font-size:0.92rem;font-weight:640;letter-spacing:-0.012em;color:var(--text)">ConnectionWatcher</span>
        <span style="flex:1"></span>
        <button style="width:34px;height:34px;display:flex;align-items:center;justify-content:center;border:none;background:transparent;color:var(--muted);border-radius:6px;cursor:pointer;padding:0">${ICON.close}</button>
      </div>
      <nav style="flex:1;padding:16px 10px;display:flex;flex-direction:column;gap:4px">
        ${drawerLabel("Watch")}
        ${drawerItem("dashboard", "Dashboard", true)}
        ${drawerItem("history", "History", false)}
        <div style="height:1px;background:var(--border);margin:14px 16px 12px"></div>
        ${drawerLabel("Manage")}
        ${drawerItem("data", "Data", false)}
        ${drawerItem("targets", "Targets", false)}
        ${drawerItem("traceroute", "Traceroute", false)}
        ${drawerItem("admin", "Admin", false)}
      </nav>
      <div style="border-top:1px solid var(--border);padding:14px 16px">
        <div style="font-size:0.665rem;font-weight:650;letter-spacing:0.1em;text-transform:uppercase;color:var(--faint);margin-bottom:9px">Appearance</div>
        <div style="display:flex;border:1px solid var(--border-strong);border-radius:6px;overflow:hidden;background:var(--surface-2)">
          ${["Light", "Dark", "System"].map((o, i) => `<span style="flex:1;height:32px;display:flex;align-items:center;justify-content:center;font-size:0.78rem;font-weight:${i === 1 ? 600 : 450};color:var(--${i === 1 ? "primary" : "muted"});background:${i === 1 ? "var(--primary-soft)" : "transparent"};${i > 0 ? "border-left:1px solid var(--border-strong);" : ""}">${o}</span>`).join("")}
        </div>
        <div style="display:flex;align-items:center;gap:9px;margin-top:14px">
          <span style="color:var(--muted);display:flex">${ICON.lock}</span>
          <span style="font-size:0.82rem;color:var(--muted)">Enter admin mode</span>
        </div>
        <div style="font-family:${MONO};font-size:0.665rem;color:var(--faint);margin-top:14px">v1.4.2 &middot; built Sep 12, 3:14 PM</div>
      </div>
    </aside>
  </div>`;

writeFileSync("MobileNav.dc.html", page({ theme: "dark", w: W, h: 844, body: navBody }));
console.log("wrote Mobile.dc.html, MobileNav.dc.html");
