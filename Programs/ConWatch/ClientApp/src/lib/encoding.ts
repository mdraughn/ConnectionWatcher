// Mark's shared incident encoding (spec 2026-09-10):
//   shape = scope (bar = shared path, dot = one target)
//   hue   = whose gear (warm = out there, grey = your equipment)
//   depth = severity (pale glitch → mid degraded → deep out)
// Colorblind-validated: the warm/grey pairs pass CVD separation at every tier.

export type Depth = "glitch" | "degraded" | "out";
export type Hue = "warm" | "grey";

export interface Mark {
  shape: "bar" | "dot";
  hue: Hue;
  depth: Depth;
  /** Evidence splits between your gear and the outside world: half/half fill. */
  ambiguous?: boolean;
}

// Colors are CSS variables (defined in wwwroot/css/site.css) so dark mode can
// re-point them. The dark GREY ramp INVERTS — pale->black is invisible on a
// dark page, so there "deeper = worse" runs toward light instead; the warm
// ramp keeps its direction, preserving warm-vs-grey separation at every tier.
export const RAMP: Record<Hue, Record<Depth, string>> = {
  warm: { glitch: "var(--warm-glitch)", degraded: "var(--warm-degraded)", out: "var(--warm-out)" },
  grey: { glitch: "var(--grey-glitch)", degraded: "var(--grey-degraded)", out: "var(--grey-out)" },
};

/** Unreachable: own Down explained by an upstream failure; no data
 *  recorded, no blame. */
export const HATCH =
  "repeating-linear-gradient(45deg, var(--grey-glitch) 0 3px, var(--surface-2) 3px 6px)";

/** Healthy ribbon baseline — deliberately pale for large surfaces (punchcard,
 *  ribbon boards) where a vivid green would overwhelm. */
export const HEALTHY = "var(--healthy)";

/** Healthy for SMALL marks (the event strip's 10px bins), where the pale
 *  tint is illegible and "all green" is the whole message. */
export const HEALTHY_VIVID = "var(--healthy-vivid)";

export function markCss(mark: Mark): { background: string } {
  if (mark.ambiguous) {
    const warm = RAMP.warm[mark.depth];
    const grey = RAMP.grey[mark.depth];
    // Split on the ascending diagonal: grey (your gear) upper-left,
    // warm (out there) lower-right.
    return { background: `linear-gradient(to bottom right, ${grey} 49.5%, ${warm} 50.5%)` };
  }
  return { background: RAMP[mark.hue][mark.depth] };
}

export type RowRole = "trunk" | "anchor" | "other";

/** Ribbon fill for a target's state, colored by whose gear the row is:
 *  trunk rows use the grey ramp, everything else the warm ramp. Returns
 *  "hatch" for Unreachable (explained by an upstream failure; no data
 *  recorded, no blame). */
export function ribbonFill(role: RowRole, state: string): string {
  const ramp = role === "trunk" ? RAMP.grey : RAMP.warm;
  switch (state) {
    case "Down": return ramp.out;
    case "Faltering":
    case "Recovering": return ramp.degraded;
    case "Unreachable": return "hatch";
    case "Responding": return HEALTHY;
    case "Starting":
    case "Initializing": return "var(--ribbon-starting)";
    default: return "var(--ribbon-unknown)"; // Unknown
  }
}

/** Ribbon fill for the VERDICT subject's timeline. The verdict subject logs
 *  HostStates with the verdict NAME alongside (segment evidence), and the name
 *  carries the attribution: Link* = your gear (grey ramp), weather = out there
 *  (warm), a site incident = pale warm, healthy = quiet. Without a
 *  recognizable name (e.g. the residual segment before the first in-range
 *  transition) fall back to the plain state mapping. */
export function verdictFill(verdictName: string | null, state: string): string {
  switch (verdictName) {
    case "LinkDown": return RAMP.grey.out;
    case "DnsDown": return RAMP.grey.out; // your resolver: your gear, name lookups out
    case "LinkDegraded": return RAMP.grey.degraded;
    case "InternetWeather": return RAMP.warm.degraded;
    case "SiteIncident": return RAMP.warm.glitch;
    case "Healthy": return HEALTHY;
    default: return ribbonFill("other", state);
  }
}

/** Cell precedence when several marks land in one punchcard cell:
 *  your-gear outages outrank internet outages outrank degradations outrank
 *  glitches; any bar (shared path) outranks any dot (one target). */
export function markRank(mark: Mark): number {
  const depthRank = { out: 3, degraded: 2, glitch: 1 }[mark.depth];
  const hueRank = mark.hue === "grey" ? 1 : 0;
  const shapeRank = mark.shape === "bar" ? 10 : 0;
  return shapeRank + depthRank * 2 + hueRank;
}
