import { writeFileSync } from "node:fs";
import { MONO, card, page, section } from "./lib-shell.mjs";
import { DIRECTIONS, MAJOR, icon, iconWithBadge, label } from "./lib-icons.mjs";

const W = 720, H = 1010;

const head = (d) => `<div style="flex-shrink:0;padding:22px 24px 16px">
    <div style="display:flex;align-items:center;gap:11px">
      <span style="width:26px;height:26px;border-radius:6px;background:var(--primary-soft);color:var(--primary);display:flex;align-items:center;justify-content:center;font-size:0.82rem;font-weight:700">${d.letter}</span>
      <h1 style="margin:0;font-size:1.28rem;font-weight:650;letter-spacing:-0.015em;color:var(--text)">${d.name}</h1>
    </div>
    <p style="margin:11px 0 0;font-size:0.835rem;color:var(--text);line-height:1.5">${d.motive}</p>
    <p style="margin:8px 0 0;font-size:0.815rem;color:var(--muted);line-height:1.5"><b style="font-weight:600;color:var(--text)">Tradeoff.</b> ${d.tradeoff}</p>
  </div>`;

const majorRow = (k) => card(`<div style="display:grid;grid-template-columns:repeat(3, minmax(0, 1fr));gap:10px;padding:22px 14px 18px">
    ${MAJOR.map((m) => `<div style="display:flex;flex-direction:column;align-items:center">
      ${icon(k, m.state, 58, m.color)}${label(m.label)}
    </div>`).join("")}
  </div>
  <div style="border-top:1px solid var(--border);padding:11px 18px;font-size:0.775rem;color:var(--muted)">${DIRECTIONS[k].ladder}</div>`);

const ladder = (k) => {
  const row = (size, mono, note) => `<div style="display:grid;grid-template-columns:78px repeat(3, minmax(0, 1fr));gap:10px;align-items:center;padding:11px 0;border-top:1px solid var(--border)">
      <span style="font-family:${MONO};font-size:0.7rem;color:var(--faint)">${note}</span>
      ${MAJOR.map((m) => `<div style="display:flex;justify-content:center">${icon(k, m.state, size, mono ? "var(--text)" : m.color)}</div>`).join("")}
    </div>`;
  return card(`<div style="padding:4px 18px 14px">
      <div style="display:grid;grid-template-columns:78px repeat(3, minmax(0, 1fr));gap:10px;padding:12px 0 4px">
        <span></span>${MAJOR.map((m) => `<span style="font-size:0.715rem;color:var(--faint);text-align:center">${m.state}</span>`).join("")}
      </div>
      ${row(32, false, "32 px")}${row(24, false, "24 px")}${row(16, false, "16 px tab")}${row(24, true, "greyscale")}
    </div>`);
};

const minorRow = (k) => card(`<div style="display:grid;grid-template-columns:repeat(4, minmax(0, 1fr));gap:8px;padding:20px 12px 16px">
    ${[["up", "site", "Up &middot; one site down"], ["up", "wide", "Up &middot; internet-wide"],
       ["struggling", "site", "Struggling &middot; one site"], ["struggling", "wide", "Struggling &middot; wide"]]
      .map(([s, m, t]) => `<div style="display:flex;flex-direction:column;align-items:center">
        ${iconWithBadge(k, s, 46, MAJOR.find((x) => x.state === s).color, m)}${label(t)}
      </div>`).join("")}
  </div>`);

const inPlace = (k) => card(`<div style="padding:16px 18px">
    <div style="display:flex;align-items:center;gap:10px;border:1px solid var(--border);border-left:4px solid var(--warn);border-radius:7px;background:color-mix(in srgb, var(--warn) 7%, var(--surface));padding:11px 14px">
      ${iconWithBadge(k, "struggling", 30, "var(--warn)", "wide", "color-mix(in srgb, var(--warn) 7%, var(--surface))")}
      <span style="font-size:0.9rem;font-weight:660;color:var(--warn);white-space:nowrap">Your internet is struggling</span>
      <span style="font-size:0.815rem;color:var(--text);opacity:0.8">&mdash; and several sites are unreachable.</span>
    </div>
    <div style="display:flex;align-items:center;gap:18px;margin-top:16px">
      <span style="display:flex;align-items:center;gap:7px;background:var(--surface-2);border:1px solid var(--border);border-radius:999px;padding:3px 12px 3px 7px">
        ${icon(k, "up", 17, "var(--ok)")}<span style="font-size:0.775rem;font-weight:600;color:var(--text)">Up</span>
      </span>
      <span style="display:flex;align-items:center;gap:0;background:var(--surface-3);border-radius:8px 8px 0 0;padding:7px 12px 8px;min-width:190px">
        ${icon(k, "down", 16, "var(--down)")}
        <span style="font-size:0.755rem;color:var(--muted);margin-left:8px;white-space:nowrap">Dashboard &mdash; Connection&hellip;</span>
      </span>
    </div>
    <div style="font-size:0.75rem;color:var(--faint);margin-top:11px">Banner &middot; nav pip &middot; browser tab at true 16&nbsp;px</div>
  </div>`);

for (const d of Object.values(DIRECTIONS)) {
  const body = [
    head(d),
    `<main style="flex:1;padding:0 24px 26px;display:flex;flex-direction:column;gap:20px">
      <div>${section("Major states")}${majorRow(d.key)}</div>
      <div>${section("Size ladder", "form must survive colour loss")}${ladder(d.key)}</div>
      <div>${section("With a minor status")}${minorRow(d.key)}</div>
      <div>${section("In place")}${inPlace(d.key)}</div>
    </main>`,
  ].join("\n");
  const name = "Icon" + d.name.replace(/\s/g, "") + ".dc.html";
  writeFileSync(name, page({ theme: "light", w: W, h: H, body }));
}

// ------------------------------------------------------------ the system

const SW = 1440, SH = 1180;

const tile = (k, size, bg, color, radius) => `<div style="width:${size}px;height:${size}px;border-radius:${radius}px;background:${bg};display:flex;align-items:center;justify-content:center;flex-shrink:0">${icon(k, "down", Math.round(size * 0.58), color)}</div>`;

const appIcons = card(`<div style="padding:22px 20px 18px">
    <div style="display:grid;grid-template-columns:repeat(4, minmax(0, 1fr));gap:20px">
      ${Object.values(DIRECTIONS).map((d) => `<div style="display:flex;flex-direction:column;align-items:center;gap:12px">
        ${tile(d.key, 124, "color-mix(in srgb, var(--down) 12%, var(--surface))", "var(--down)", 28)}
        <div style="display:flex;align-items:flex-end;gap:12px">
          ${tile(d.key, 48, "color-mix(in srgb, var(--down) 12%, var(--surface))", "var(--down)", 11)}
          ${tile(d.key, 32, "color-mix(in srgb, var(--down) 12%, var(--surface))", "var(--down)", 7)}
          ${icon(d.key, "down", 16, "var(--down)")}
        </div>
        <div style="font-size:0.78rem;font-weight:600;color:var(--text)">${d.letter} &middot; ${d.name}</div>
      </div>`).join("")}
    </div>
    <p style="margin:18px 0 0;font-size:0.79rem;color:var(--muted);line-height:1.55;max-width:900px">
      Large sizes (PWA, apple-touch-icon, the ConTray companion) want a filled tile tinted with the
      verdict colour; 16&nbsp;px wants the bare glyph, because a tile at tab size eats the glyph.
      Shown in the down state, which is the one that has to be unmistakable from across the room.
    </p>
  </div>`);

const rules = card(`<div style="padding:20px;display:grid;grid-template-columns:repeat(3, minmax(0, 1fr));gap:24px">
    <div>
      <div style="font-size:0.86rem;font-weight:620;color:var(--text);margin-bottom:6px">Major carries colour and form</div>
      <p style="margin:0;font-size:0.8rem;color:var(--muted);line-height:1.55">Green / amber / red from the existing <code style="font-family:${MONO};font-size:0.75rem">--cw-*</code> ramp, but the three states are also three different <i>shapes</i>. The greyscale row on each direction is the test: if you cannot tell them apart there, the icon fails for eight percent of men and every monochrome tray.</p>
    </div>
    <div>
      <div style="font-size:0.86rem;font-weight:620;color:var(--text);margin-bottom:6px">Minor reuses your own law</div>
      <p style="margin:0;font-size:0.8rem;color:var(--muted);line-height:1.55">From <code style="font-family:${MONO};font-size:0.75rem">encoding.ts</code>: <b>dot = one site</b>, <b>bar = shared path</b>, warm = out there. So the badge is a warm dot for a site incident and a warm bar for internet-wide trouble &mdash; the same marks already used in the ledger and punchcard. Nothing new to learn.</p>
    </div>
    <div>
      <div style="font-size:0.86rem;font-weight:620;color:var(--text);margin-bottom:6px">The badge has a floor</div>
      <p style="margin:0;font-size:0.8rem;color:var(--muted);line-height:1.55">It is legible down to about 20&nbsp;px and no further. Below that, drop it and show the major state alone &mdash; at tab and tray size the major answer is the one anyone wants. Minor is also only observable while the link is up or struggling: with the link down, nothing beyond the modem can be measured.</p>
    </div>
  </div>`);

const badgeStrip = card(`<div style="padding:20px;display:flex;align-items:center;gap:34px;flex-wrap:wrap">
    ${[["site", "one site down", "var(--warm-glitch)", `<span style="display:inline-block;width:11px;height:11px;border-radius:50%;background:var(--warm-glitch)"></span>`],
       ["wide", "internet-wide trouble", "var(--warm-degraded)", `<span style="display:inline-block;width:20px;height:9px;border-radius:2px;background:var(--warm-degraded)"></span>`]]
      .map(([m, t, c, mark]) => `<div style="display:flex;align-items:center;gap:13px">
        ${iconWithBadge("signal", "up", 50, "var(--ok)", m)}
        <div>
          <div style="font-size:0.85rem;font-weight:600;color:var(--text)">${t}</div>
          <div style="display:flex;align-items:center;gap:8px;margin-top:5px">${mark}<span style="font-family:${MONO};font-size:0.73rem;color:var(--muted)">${c.replace("var(--", "").replace(")", "")}</span></div>
        </div>
      </div>`).join("")}
    <div style="flex:1;min-width:240px;font-size:0.79rem;color:var(--muted);line-height:1.55">
      The badge sits on a plate in the page colour so it separates cleanly from the glyph on any surface &mdash;
      banner tint, nav pill, or a browser tab.
    </div>
  </div>`);

const systemBody = [
  `<div style="flex-shrink:0;padding:22px 26px 16px">
     <h1 style="margin:0 0 4px;font-size:1.33rem;font-weight:650;letter-spacing:-0.015em;color:var(--text)">Verdict icons &mdash; how the set composes</h1>
     <p style="margin:0;font-size:0.835rem;color:var(--muted)">Three major states &times; an optional minor badge. Four metaphors for the major glyph; one shared rule for everything else.</p>
   </div>`,
  `<main style="flex:1;padding:0 26px 30px">
    <div style="max-width:1388px;margin:0 auto;display:flex;flex-direction:column;gap:22px">
      <div>${section("The rules")}${rules}</div>
      <div>${section("The minor badge", "identical across all four directions")}${badgeStrip}</div>
      <div>${section("As an app icon", "124 / 48 / 32 / 16 px")}${appIcons}</div>
    </div>
  </main>`,
].join("\n");

writeFileSync("IconSystem.dc.html", page({ theme: "light", w: SW, h: SH, body: systemBody }));

console.log("wrote IconSignal / IconPath / IconGlobe / IconWeather / IconSystem .dc.html");
