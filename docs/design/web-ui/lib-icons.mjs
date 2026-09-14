// Verdict icon exploration: four metaphors for the major state (up /
// struggling / down), each composed with the same minor badge.
//
// Two rules hold across every direction:
//   1. The three major states differ in FORM, not only colour, so they
//      survive greyscale, CVD and a monochrome tray.
//   2. The minor badge reuses the app's existing encoding law from
//      ClientApp/src/lib/encoding.ts - dot = one site, bar = shared path -
//      so it needs no new learning.
import { MONO, section } from "./lib-shell.mjs";

const SW = 2;
const cap = 'stroke-linecap="round" stroke-linejoin="round"';

/** Down is one shared convention: the direction's own glyph, dimmed, under a
 *  bold slash. Universal, and the only treatment that survives 16px. */
const slash = (c) => `<path d="M5 19 19 5" stroke="${c}" stroke-width="2.6" ${cap}></path>`;

// ------------------------------------------------------------ A. Signal

const SIG = {
  dot: `<circle cx="12" cy="12" r="2.6"></circle>`,
  inner: `<path d="M16.6 8.7a5.2 5.2 0 0 1 0 6.6" fill="none"></path><path d="M7.4 15.3a5.2 5.2 0 0 1 0-6.6" fill="none"></path>`,
  outer: `<path d="M19.4 5.9a8.6 8.6 0 0 1 0 12.2" fill="none"></path><path d="M4.6 18.1a8.6 8.6 0 0 1 0-12.2" fill="none"></path>`,
};

function signal(state, c) {
  const g = (inner) => `<g stroke="${c}" fill="${c}" stroke-width="${SW}" ${cap}>${inner}</g>`;
  if (state === "up") return g(SIG.dot + SIG.inner + SIG.outer);
  if (state === "struggling") return g(SIG.dot + SIG.inner);
  return `<g opacity="0.32">${g(SIG.dot + SIG.inner + SIG.outer)}</g>${slash(c)}`;
}

// -------------------------------------------------------------- B. Path

const node = (x, filled, c) => filled
  ? `<circle cx="${x}" cy="12" r="2.5" fill="${c}" stroke="none"></circle>`
  : `<circle cx="${x}" cy="12" r="2.5" fill="none" stroke="${c}" stroke-width="${SW}"></circle>`;

function path(state, c) {
  const link = (x1, x2, dashed) =>
    `<path d="M${x1} 12h${x2 - x1}" stroke="${c}" stroke-width="2.2" ${cap}${dashed ? ' stroke-dasharray="1.5 1.8"' : ""}></path>`;
  if (state === "up")
    return `${link(7, 9.5)}${link(14.5, 17)}${node(4, true, c)}${node(12, true, c)}${node(20, true, c)}`;
  if (state === "struggling")
    return `${link(7, 9.5)}${link(14.5, 17, true)}${node(4, true, c)}${node(12, true, c)}${node(20, false, c)}`;
  const full = `${link(7, 9.5)}${link(14.5, 17)}${node(4, true, c)}${node(12, true, c)}${node(20, true, c)}`;
  return `<g opacity="0.32">${full}</g>${slash(c)}`;
}

// ------------------------------------------------------------- C. Globe

const GLOBE = {
  rim: `<circle cx="12" cy="12" r="8.6" fill="none"></circle>`,
  meridian: `<path d="M12 3.4a3.7 8.6 0 0 1 0 17.2a3.7 8.6 0 0 1 0-17.2" fill="none"></path>`,
  equator: (dashed) => `<path d="M3.4 12h17.2" fill="none"${dashed ? ' stroke-dasharray="1.6 1.9"' : ""}></path>`,
};

function globe(state, c) {
  const g = (inner) => `<g stroke="${c}" stroke-width="${SW}" ${cap}>${inner}</g>`;
  if (state === "up") return g(GLOBE.rim + GLOBE.meridian + GLOBE.equator(false));
  if (state === "struggling") return g(GLOBE.rim + GLOBE.equator(true) + `<path d="M12 3.4a3.7 8.6 0 0 1 2.4 8" fill="none" stroke-dasharray="1.6 1.9"></path><path d="M12 20.6a3.7 8.6 0 0 1-2.4-8" fill="none"></path>`);
  return `<g opacity="0.32">${g(GLOBE.rim + GLOBE.meridian + GLOBE.equator(false))}</g>${slash(c)}`;
}

// ----------------------------------------------------------- D. Weather

const RAYS = [[12, 2.6, 12, 4.6], [12, 19.4, 12, 21.4], [2.6, 12, 4.6, 12], [19.4, 12, 21.4, 12],
  [5.4, 5.4, 6.9, 6.9], [17.1, 17.1, 18.6, 18.6], [18.6, 5.4, 17.1, 6.9], [6.9, 17.1, 5.4, 18.6]];

const CLOUD = `<path d="M7.6 19.4h9a3.9 3.9 0 0 0 .3-7.8 5.3 5.3 0 0 0-10.2-1 3.5 3.5 0 0 0 .9 8.8z" fill="none"></path>`;

function weather(state, c) {
  const g = (inner, sw = SW) => `<g stroke="${c}" fill="none" stroke-width="${sw}" ${cap}>${inner}</g>`;
  if (state === "up")
    return g(`<circle cx="12" cy="12" r="4.3"></circle>` + RAYS.map(([a, b, d, e]) => `<path d="M${a} ${b}L${d} ${e}"></path>`).join(""));
  if (state === "struggling")
    return g(`<circle cx="9" cy="8.4" r="3.2"></circle><path d="M9 2.9v1.5M3.5 8.4H5M5.1 4.5l1.1 1.1M12.9 4.5l-1.1 1.1"></path>` + CLOUD);
  return g(CLOUD + `<path d="M9 21.6l-.9 1.6M12.5 21.6l-.9 1.6M16 21.6l-.9 1.6"></path>`);
}

export const DIRECTIONS = {
  signal: {
    key: "signal", letter: "A", name: "Signal",
    draw: signal,
    motive: "The app&rsquo;s own brand mark, carrying state. One icon is both identity and verdict, so the favicon always looks like ConnectionWatcher.",
    tradeoff: "Rings read as <b>signal strength</b>, which risks implying your Wi-Fi rather than your uplink &mdash; the exact distinction this product exists to make. The symmetric left/right arcs push against that, but don&rsquo;t erase it. Check the 16&nbsp;px row too: with the outer pair gone, <i>struggling</i> thins out to almost nothing.",
    ladder: "Reach shrinks: two ring pairs &rarr; one &rarr; none.",
  },
  path: {
    key: "path", letter: "B", name: "Path",
    draw: path,
    motive: "What the app actually measures: you &rarr; your gear &rarr; the world. The same trunk metaphor as the Targets pipeline, and the only direction that can express <i>where</i> the break is.",
    tradeoff: "Five features across 16&nbsp;px is the tightest of the four. Up and struggling differ by a dashed segment and an open ring &mdash; legible at 24&nbsp;px, marginal in a tray.",
    ladder: "The far hop degrades: solid link &rarr; dashed link + hollow world &rarr; severed.",
  },
  globe: {
    key: "globe", letter: "C", name: "Globe",
    draw: globe,
    motive: "Zero learning curve. Everyone reads a globe as &ldquo;the internet&rdquo; and a slashed globe as &ldquo;no internet&rdquo;, with no legend and no onboarding.",
    tradeoff: "Generic &mdash; it says nothing about whose fault it is, which is the whole thesis. Rim plus two meridians is also the busiest glyph here and turns to mush below 20&nbsp;px. <b>And it fails the greyscale row:</b> up and struggling differ only by a dashed equator, which is invisible without colour &mdash; so this direction leans on hue alone exactly where it must not.",
    ladder: "The sphere frays: solid meridians &rarr; dashed &rarr; slashed.",
  },
  weather: {
    key: "weather", letter: "D", name: "Weather",
    draw: weather,
    motive: "Leans on vocabulary the product already uses out loud. The severity ladder is instantly intuitive and by far the most legible of the four at 16&nbsp;px.",
    tradeoff: "<b>It steals the minor axis&rsquo;s name.</b> &ldquo;Internet weather&rdquo; is a <i>minor</i> status here, so using weather for the <i>major</i> state blurs exactly the split you are trying to draw. Shown because your own wording invites it &mdash; but I&rsquo;d not ship it.",
    ladder: "Sun &rarr; sun behind cloud &rarr; rain. No slash: the storm is the down state.",
  },
};

export const MAJOR = [
  { state: "up", label: "Your internet is up", color: "var(--ok)" },
  { state: "struggling", label: "Your internet is struggling", color: "var(--warn)" },
  { state: "down", label: "Your internet is down", color: "var(--down)" },
];

/** One glyph at a given pixel size. */
export function icon(dir, state, size, color) {
  return `<svg width="${size}" height="${size}" viewBox="0 0 24 24" style="display:block;flex-shrink:0">${DIRECTIONS[dir].draw(state, color)}</svg>`;
}

/** Glyph plus the minor badge. The badge is tucked INTO the glyph's own
 *  lower-right, punching a plate-coloured hole through whatever it overlaps,
 *  so the pair reads as one layered mark rather than two loose marks. */
export function iconWithBadge(dir, state, size, color, minor, ring = "var(--surface)") {
  const plate = minor === "site"
    ? `<circle cx="18.4" cy="18.4" r="5.1" fill="${ring}"></circle>`
    : `<rect x="12.7" y="15.5" width="11.6" height="5.8" rx="2.9" fill="${ring}"></rect>`;
  const badge = minor === "site"
    ? `<circle cx="18.4" cy="18.4" r="3.1" fill="var(--warm-glitch)"></circle>`
    : `<rect x="14.1" y="16.9" width="8.8" height="3" rx="1.5" fill="var(--warm-degraded)"></rect>`;
  return `<svg width="${size}" height="${size}" viewBox="0 0 24 24" style="display:block;flex-shrink:0">
      ${DIRECTIONS[dir].draw(state, color)}${minor ? plate + badge : ""}
    </svg>`;
}

export const label = (t) => `<div style="font-size:0.735rem;color:var(--muted);text-align:center;line-height:1.35;margin-top:9px">${t}</div>`;

export { section };
