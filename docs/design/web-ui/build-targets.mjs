import { writeFileSync } from "node:fs";
import { ICON, MONO, appbar, card, page, pageband, primaryButton, section } from "./lib-shell.mjs";

const W = 1440, H = 1540;

function subnav(items, activeIndex) {
  return `<div style="display:flex;border:1px solid var(--border-strong);border-radius:6px;overflow:hidden;background:var(--surface);width:max-content">
    ${items.map((o, i) => `<a href="#" style="padding:0 16px;height:33px;display:flex;align-items:center;font-size:0.835rem;font-weight:${i === activeIndex ? 600 : 450};color:var(--${i === activeIndex ? "primary" : "muted"});background:${i === activeIndex ? "var(--primary-soft)" : "transparent"};${i > 0 ? "border-left:1px solid var(--border-strong);" : ""}text-decoration:none">${o}</a>`).join("")}
  </div>`;
}

function healthBanner(text) {
  return `<div style="display:flex;gap:12px;align-items:flex-start;border:1px solid var(--border);border-left:4px solid var(--warn);border-radius:8px;background:color-mix(in srgb, var(--warn) 7%, var(--surface));padding:13px 16px">
      <span style="color:var(--warn);display:flex;flex-shrink:0;margin-top:1px">${ICON.alert}</span>
      <div>
        <div style="font-size:0.86rem;font-weight:600;color:var(--text)">1 configuration warning</div>
        <div style="font-size:0.83rem;color:var(--muted);margin-top:3px;line-height:1.45">${text}</div>
      </div>
    </div>`;
}

/** The path to the internet as a stepper: the trunk is the spine, branches
 *  hang off it, and the insert affordance sits between consecutive hops. */
function pipeline(stages) {
  const insert = `<div style="display:flex;align-items:center;gap:10px;padding:3px 0 3px 9px">
      <span style="width:2px;height:22px;background:var(--border-strong);flex-shrink:0"></span>
      <span style="display:inline-flex;align-items:center;gap:5px;font-size:0.72rem;color:var(--muted);border:1px dashed var(--border-strong);border-radius:4px;padding:2px 8px">${ICON.plus}insert a hop here</span>
    </div>`;
  const rows = stages.map((s, i) => {
    const node = `<div style="display:flex;align-items:center;gap:12px">
        <span style="width:20px;height:20px;border-radius:50%;border:2px solid ${s.anchor ? "var(--warm-degraded)" : "var(--primary)"};background:var(--surface);flex-shrink:0;display:flex;align-items:center;justify-content:center">
          <span style="width:7px;height:7px;border-radius:50%;background:${s.anchor ? "var(--warm-degraded)" : "var(--primary)"}"></span>
        </span>
        <span style="font-size:0.885rem;font-weight:580;color:var(--text);white-space:nowrap">${s.name}</span>
        <span style="font-family:${MONO};font-size:0.755rem;color:var(--faint);white-space:nowrap">${s.addr}</span>
        ${s.isp ? `<span style="font-size:0.7rem;font-weight:600;color:var(--warm-degraded);background:color-mix(in srgb, var(--warm-degraded) 13%, transparent);border-radius:4px;padding:2px 8px;white-space:nowrap">${s.isp}</span>` : ""}
      </div>`;
    const branches = s.branches
      ? `<div style="display:flex;align-items:center;gap:8px;padding:5px 0 1px 42px">
           <span style="font-size:0.735rem;color:var(--faint)">also behind this hop</span>
           ${s.branches.map((b) => `<span style="font-size:0.74rem;color:var(--muted);background:var(--surface-2);border:1px solid var(--border);border-radius:4px;padding:2px 8px;white-space:nowrap">${b}</span>`).join("")}
         </div>`
      : "";
    return node + branches + (i < stages.length - 1 ? insert : "");
  }).join("");
  return card(`<div style="padding:18px 20px">${rows}</div>`);
}

const th = (t) => `<th style="text-align:left;font-size:0.675rem;font-weight:650;letter-spacing:0.08em;text-transform:uppercase;color:var(--faint);padding:0 14px 10px">${t}</th>`;
const td = (t, extra = "") => `<td style="font-size:0.845rem;color:var(--text);padding:10px 14px;border-top:1px solid var(--border);${extra}">${t}</td>`;

function targetsTable(rows) {
  const yes = `<span style="display:inline-flex;align-items:center;gap:6px;font-size:0.78rem;color:var(--muted)"><span style="width:7px;height:7px;border-radius:50%;background:var(--ok)"></span>enabled</span>`;
  const no = `<span style="display:inline-flex;align-items:center;gap:6px;font-size:0.78rem;color:var(--faint)"><span style="width:7px;height:7px;border-radius:50%;background:var(--unreach)"></span>paused</span>`;
  const act = `<span style="display:flex;gap:7px;justify-content:flex-end">
      <span style="font-size:0.765rem;color:var(--muted);border:1px solid var(--border-strong);border-radius:4px;padding:2px 10px">Edit</span>
      <span style="font-size:0.765rem;color:var(--down);border:1px solid color-mix(in srgb, var(--down) 35%, var(--border));border-radius:4px;padding:2px 10px">Delete</span>
    </span>`;
  const body = rows.map((r) => `<tr>
      ${td(`<b style="font-weight:570">${r.name}</b>`)}
      ${td(r.addr, `font-family:${MONO};font-size:0.775rem;color:var(--muted);`)}
      ${td(r.parent === "—" ? `<span style="color:var(--faint)">—</span>` : `<span style="color:var(--muted)">${r.parent}</span>`)}
      ${td(`<span style="color:var(--muted)">${r.profile}</span>`)}
      ${td(r.isp ? `<span style="color:var(--muted)">${r.isp}</span>` : `<span style="color:var(--faint)">—</span>`)}
      ${td(r.enabled ? yes : no)}
      ${td(act)}
    </tr>`).join("");
  return card(`<div style="padding:14px 6px 6px">
    <table><thead><tr>${th("Name")}${th("Address")}${th("Parent")}${th("Profile")}${th("ISP / operator")}${th("Status")}${th("")}</tr></thead>
    <tbody>${body}</tbody></table></div>`);
}

function field(label, value, hint = "", isSelect = false, placeholder = "") {
  return `<label style="display:flex;flex-direction:column;gap:6px">
      <span style="font-size:0.785rem;font-weight:560;color:var(--text)">${label}</span>
      <span style="display:flex;align-items:center;gap:9px;height:35px;padding:0 11px;border:1px solid var(--border-strong);background:var(--bg);border-radius:5px">
        <span style="flex:1;font-size:0.845rem;color:var(--${value ? "text" : "faint"});font-family:${label === "Address" ? MONO : "inherit"}">${value || placeholder}</span>
        ${isSelect ? `<span style="color:var(--muted);display:flex">${ICON.chevron}</span>` : ""}
      </span>
      ${hint ? `<span style="font-size:0.735rem;color:var(--faint)">${hint}</span>` : ""}
    </label>`;
}

const body = [
  appbar({ active: "targets", verdict: "healthy", theme: "light" }),
  pageband(
    "Targets",
    "Everything ConnectionWatcher pings, and how the hops chain together. Changes apply within seconds.",
    primaryButton("Add target")),
  `<main style="flex:1;padding:0 26px 30px">
    <div style="max-width:1250px;margin:0 auto;display:flex;flex-direction:column;gap:24px">
      <div style="display:flex;align-items:center;gap:16px">
        ${subnav(["Targets", "Profiles", "Pools"], 0)}
        <span style="flex:1"></span>
        <a href="#" style="font-size:0.815rem;color:var(--primary)">Run quick setup</a>
        <span style="color:var(--border-strong)">|</span>
        <a href="#" style="font-size:0.815rem;color:var(--primary)">Export config</a>
        <a href="#" style="font-size:0.815rem;color:var(--primary)">Import</a>
      </div>
      ${healthBanner("Synology NAS and Home Assistant sit off the trunk, so their outages are recorded but never change the internet verdict.")}
      <div>${section("Path to the internet", "the trunk your verdict is built on")}
        ${pipeline([
          { name: "Router", addr: "192.168.1.1", branches: ["Synology NAS", "Home Assistant"] },
          { name: "Cable Modem", addr: "192.168.100.1" },
          { name: "Comcast Edge", addr: "198.51.100.14", isp: "Comcast" },
          { name: "Comcast Core", addr: "198.51.100.9", isp: "Comcast" },
          { name: "Internet anchors", addr: "Google DNS, Cloudflare DNS, Quad9, OpenDNS", anchor: true },
        ])}
      </div>
      <div>${section("All targets", "10 configured · 10 enabled")}
        ${targetsTable([
          { name: "Router", addr: "192.168.1.1", parent: "—", profile: "LAN", isp: "", enabled: true },
          { name: "Synology NAS", addr: "192.168.1.20", parent: "Router", profile: "LAN", isp: "", enabled: true },
          { name: "Home Assistant", addr: "192.168.1.24", parent: "—", profile: "LAN", isp: "", enabled: true },
          { name: "Cable Modem", addr: "192.168.100.1", parent: "Router", profile: "Trunk", isp: "Comcast", enabled: true },
          { name: "Comcast Edge", addr: "198.51.100.14", parent: "Cable Modem", profile: "Trunk", isp: "Comcast", enabled: true },
          { name: "Comcast Core", addr: "198.51.100.9", parent: "Comcast Edge", profile: "Trunk", isp: "Comcast", enabled: true },
          { name: "Google DNS", addr: "8.8.8.8", parent: "Comcast Core", profile: "Anchor", isp: "", enabled: true },
          { name: "Cloudflare DNS", addr: "1.1.1.1", parent: "Comcast Core", profile: "Anchor", isp: "", enabled: true },
          { name: "Quad9", addr: "9.9.9.9", parent: "Comcast Core", profile: "Anchor", isp: "", enabled: true },
          { name: "OpenDNS", addr: "208.67.222.222", parent: "Comcast Core", profile: "Anchor", isp: "", enabled: true },
        ])}
      </div>
      <div>${section("Add target")}
        ${card(`<div style="padding:20px">
          <div style="display:grid;grid-template-columns:repeat(3, minmax(0, 1fr));gap:18px">
            ${field("Name", "", "", false, "e.g. Level3 Chicago")}
            ${field("Address", "", "", false, "IP or hostname")}
            ${field("Parent", "Comcast Core", "", true)}
            ${field("Profile", "Anchor", "", true)}
            ${field("ISP / operator", "", "Optional — link verdicts and the ISP report use this name.", false, "e.g. Comcast")}
            <label style="display:flex;flex-direction:column;gap:6px">
              <span style="font-size:0.785rem;font-weight:560;color:var(--text)">Enabled</span>
              <span style="display:flex;align-items:center;gap:10px;height:35px">
                <span style="width:34px;height:20px;border-radius:999px;background:var(--primary);display:flex;align-items:center;padding:0 2px;justify-content:flex-end">
                  <span style="width:16px;height:16px;border-radius:50%;background:#fff"></span>
                </span>
                <span style="font-size:0.83rem;color:var(--muted)">Ping this target</span>
              </span>
            </label>
          </div>
          <div style="display:flex;gap:10px;margin-top:22px">
            ${primaryButton("Save target")}
            <button style="height:32px;padding:0 14px;border:1px solid var(--border-strong);background:var(--surface);color:var(--muted);border-radius:5px;font-size:0.815rem;font-weight:520;cursor:pointer;font-family:inherit">Cancel</button>
          </div>
        </div>`)}
      </div>
    </div>
  </main>`,
].join("\n");

writeFileSync("Targets.dc.html", page({ theme: "light", w: W, h: H, body }));
console.log("wrote Targets.dc.html");
