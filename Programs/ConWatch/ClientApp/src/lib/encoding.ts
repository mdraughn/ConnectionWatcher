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

export const RAMP: Record<Hue, Record<Depth, string>> = {
  warm: { glitch: "#eda94f", degraded: "#e08a00", out: "#cc3333" },
  grey: { glitch: "#d4d4d4", degraded: "#7d7d7d", out: "#2b2b2b" },
};

/** Unreachable behind a down parent: inherited, no data, no blame. */
export const HATCH = "repeating-linear-gradient(45deg, #c9c9c9 0 3px, #f2f2f2 3px 6px)";

/** Healthy ribbon baseline. */
export const HEALTHY = "#eaf3ec";

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
 *  "hatch" for Unreachable (inherited, no data, no blame). */
export function ribbonFill(role: RowRole, state: string): string {
  const ramp = role === "trunk" ? RAMP.grey : RAMP.warm;
  switch (state) {
    case "Down": return ramp.out;
    case "Faltering":
    case "Recovering": return ramp.degraded;
    case "Unreachable": return "hatch";
    case "Responding": return HEALTHY;
    case "Starting":
    case "Initializing": return "#dfe8e2";
    default: return "#e6e6e6"; // Unknown
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
