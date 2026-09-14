import { writeFileSync } from "node:fs";
import { ICON, MONO, appbar, card, page, pageband, section } from "./lib-shell.mjs";
import { eventStrip, glitchTable, ledger, statusBar, tree, verdictCard } from "./lib-parts.mjs";

const W = 1440, H = 1360;

const wrap = (inner) => `<main style="flex:1;padding:0 26px 30px">
    <div style="max-width:1250px;margin:0 auto;display:flex;flex-direction:column;gap:24px">${inner}</div>
  </main>`;

function pools(rows) {
  const html = rows.map((r) => `<div style="display:flex;align-items:center;gap:10px;height:33px;padding:0 12px">
      <span style="width:9px;height:9px;border-radius:50%;flex-shrink:0;background:${r.color}"></span>
      <span style="font-size:0.875rem;font-weight:580;color:var(--text);white-space:nowrap">${r.name}</span>
      <span style="flex:1"></span>
      <span style="font-size:0.79rem;color:var(--muted)">${r.members}</span>
    </div>`).join("");
  return card(`<div style="padding:8px 6px">${html}</div>`);
}

// ---------------------------------------------------------------- light

const quiet = {};
quiet[31] = { shape: "dot", hue: "warm", depth: "glitch" };
quiet[58] = { shape: "dot", hue: "warm", depth: "glitch" };
quiet[59] = { shape: "dot", hue: "warm", depth: "degraded" };
quiet[87] = { shape: "dot", hue: "warm", depth: "glitch" };

const lightBody = [
  appbar({ active: "dashboard", verdict: "siteincident", theme: "light" }),
  pageband("Dashboard", "Live reachability &mdash; what your connection is doing right now."),
  wrap([
    verdictCard("siteincident", eventStrip(96, quiet)),
    `<div>${section("Reachability", "click a row to pin it open")}${tree([
      { depth: 0, name: "Router", addr: "192.168.1.1", state: "Responding", rate: "100%", rtt: "1 ms" },
      { depth: 1, name: "Synology NAS", addr: "192.168.1.20", state: "Responding", rate: "100%", rtt: "1 ms" },
      { depth: 1, name: "Home Assistant", addr: "192.168.1.24", state: "Responding", rate: "100%", rtt: "2 ms" },
      { depth: 1, name: "Cable Modem", addr: "192.168.100.1", state: "Responding", rate: "100%", rtt: "8 ms" },
      { depth: 2, name: "Comcast Edge", addr: "198.51.100.14", state: "Responding", rate: "100%", rtt: "14 ms" },
      { depth: 3, name: "Comcast Core", addr: "198.51.100.9", state: "Responding", rate: "99%", rtt: "21 ms" },
      { depth: 4, name: "Google DNS", addr: "8.8.8.8", state: "Responding", rate: "100%", rtt: "19 ms" },
      { depth: 4, name: "Cloudflare DNS", addr: "1.1.1.1", state: "Responding", rate: "100%", rtt: "17 ms" },
      { depth: 4, name: "Quad9", addr: "9.9.9.9", state: "Faltering", rate: "85%", rtt: "31 ms", pinned: true },
      { depth: 4, name: "OpenDNS", addr: "208.67.222.222", state: "Responding", rate: "100%", rtt: "23 ms" },
    ])}</div>`,
    `<div>${section("Pools")}${pools([
      { name: "Internet anchors", color: "var(--ok)", members: "Google DNS, Cloudflare DNS, Quad9, OpenDNS" },
    ])}</div>`,
    `<div>${section("What happened", "most recent")}${ledger([
      { mark: { shape: "dot", hue: "warm", depth: "glitch" }, when: "Sat, Sep 12, 9:14 PM", lead: "Quad9 glitched for 8 s.", body: "3 of 20 pings lost — no other anchor saw it." },
      { mark: { shape: "bar", hue: "grey", depth: "out" }, when: "Fri, Sep 11, 3:02 AM", lead: "Your link went down for 4 min.", body: "LinkDown: Comcast — Comcast Edge is Down." },
      { mark: { shape: "bar", hue: "warm", depth: "degraded" }, when: "Thu, Sep 10, 8:47 PM", lead: "Internet weather for 11 min.", body: "Two anchors degraded at once; your own gear stayed healthy." },
      { mark: { shape: "dot", hue: "warm", depth: "degraded" }, when: "Wed, Sep 9, 6:31 AM", lead: "Cloudflare DNS had a site incident for 2 min.", body: "Only that anchor was affected — the rest of the path was fine." },
    ])}</div>`,
    `<div>${section("Recent glitches", "all targets, last 24 h · chronically glitchy: Quad9")}${glitchTable([
      { when: "9/12 21:14:07", subject: "Quad9", cls: "internet", lost: "3/20", dur: "8 s", detail: "burst loss 15%" },
      { when: "9/12 18:02:51", subject: "Comcast Edge", cls: "target", lost: "2/20", dur: "5 s", detail: "single-hop wobble" },
      { when: "9/12 11:37:19", subject: "Quad9", cls: "internet", lost: "4/20", dur: "11 s", detail: "burst loss 20%" },
    ])}</div>`,
  ].join("")),
  statusBar("21:14:07&nbsp;&nbsp;Quad9: Responding &rarr; Faltering (success 85%)"),
].join("\n");

writeFileSync("Main.dc.html", page({ theme: "light", w: W, h: H, body: lightBody }));

// ----------------------------------------------------------------- dark

const outage = {};
for (let i = 92; i < 96; i++) outage[i] = { shape: "bar", hue: "grey", depth: "out" };
outage[90] = { shape: "bar", hue: "grey", depth: "degraded" };
outage[91] = { shape: "bar", hue: "grey", depth: "degraded" };
outage[44] = { shape: "dot", hue: "warm", depth: "glitch" };

const unreachRow = (depth, name, addr) =>
  ({ depth, name, addr, state: "Unreachable", rate: null, rtt: null, hatched: true });

const darkBody = [
  appbar({ active: "dashboard", verdict: "linkdown", theme: "dark" }),
  pageband("Dashboard", "Live reachability &mdash; what your connection is doing right now."),
  wrap([
    verdictCard("linkdown", eventStrip(96, outage)),
    `<div>${section("Reachability", "click a row to pin it open")}${tree([
      { depth: 0, name: "Router", addr: "192.168.1.1", state: "Responding", rate: "100%", rtt: "1 ms" },
      { depth: 1, name: "Synology NAS", addr: "192.168.1.20", state: "Responding", rate: "100%", rtt: "1 ms" },
      { depth: 1, name: "Home Assistant", addr: "192.168.1.24", state: "Responding", rate: "100%", rtt: "2 ms" },
      { depth: 1, name: "Cable Modem", addr: "192.168.100.1", state: "Responding", rate: "100%", rtt: "9 ms" },
      { depth: 2, name: "Comcast Edge", addr: "198.51.100.14", state: "Down", rate: "0%", rtt: "—", pinned: true },
      unreachRow(3, "Comcast Core", "198.51.100.9"),
      unreachRow(4, "Google DNS", "8.8.8.8"),
      unreachRow(4, "Cloudflare DNS", "1.1.1.1"),
      unreachRow(4, "Quad9", "9.9.9.9"),
      unreachRow(4, "OpenDNS", "208.67.222.222"),
    ])}</div>`,
    `<div>${section("Pools")}${pools([
      { name: "Internet anchors", color: "var(--unreach)", members: "Google DNS, Cloudflare DNS, Quad9, OpenDNS" },
    ])}</div>`,
    `<div>${section("What happened", "most recent")}${ledger([
      { mark: { shape: "bar", hue: "grey", depth: "out" }, when: "Sun, Sep 13, 2:08 PM", lead: "Your link is down — 4 min so far.", body: "LinkDown: Comcast — Comcast Edge is Down; the modem still answers." },
      { mark: { shape: "bar", hue: "grey", depth: "degraded" }, when: "Sun, Sep 13, 2:06 PM", lead: "Your link degraded for 2 min.", body: "Comcast Edge lost 40% of its pings before going fully down." },
      { mark: { shape: "dot", hue: "warm", depth: "glitch" }, when: "Sun, Sep 13, 1:24 PM", lead: "Quad9 glitched for 6 s.", body: "2 of 20 pings lost — no other anchor saw it." },
      { mark: { shape: "bar", hue: "grey", depth: "out" }, when: "Fri, Sep 11, 3:02 AM", lead: "Your link went down for 4 min.", body: "LinkDown: Comcast — Comcast Edge is Down." },
    ])}</div>`,
    `<div>${section("Recent glitches", "all targets, last 24 h")}${glitchTable([
      { when: "9/13 14:06:12", subject: "Comcast Edge", cls: "target", lost: "8/20", dur: "14 s", detail: "burst loss 40% — escalated to Down" },
      { when: "9/13 13:24:40", subject: "Quad9", cls: "internet", lost: "2/20", dur: "6 s", detail: "burst loss 10%" },
      { when: "9/13 09:11:58", subject: "Comcast Core", cls: "internet", lost: "3/20", dur: "9 s", detail: "burst loss 15%" },
    ])}</div>`,
  ].join("")),
  statusBar("14:08:31&nbsp;&nbsp;Comcast Edge: Faltering &rarr; Down (burst loss 100%)"),
].join("\n");

writeFileSync("DashboardDark.dc.html", page({ theme: "dark", w: W, h: H, body: darkBody }));

console.log("wrote Main.dc.html, DashboardDark.dc.html");
