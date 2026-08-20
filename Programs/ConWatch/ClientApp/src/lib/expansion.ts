// Rule C expansion state machine (pure; unit-tested).
// Quiet states: Responding always; Starting/Initializing only until the row
// has ever left the starting family (fresh boot). Leaving quiet expands; a
// return to Responding holds the tile open through a cool-down; pins override.

export interface RowUi {
  expanded: boolean;
  pinned: boolean;
  cooldownUntil: number | null;
  everLeftStarting: boolean;
}

export const COOLDOWN_MS = 5 * 60_000;

const STARTING_FAMILY = new Set(["Starting", "Initializing"]);

function isQuiet(state: string, everLeftStarting: boolean): boolean {
  if (state === "Responding") return true;
  if (STARTING_FAMILY.has(state) && !everLeftStarting) return true;
  return false;
}

export function initialRow(): RowUi {
  return { expanded: false, pinned: false, cooldownUntil: null, everLeftStarting: false };
}

export function onStateChange(row: RowUi, state: string, nowMs: number): RowUi {
  const everLeftStarting = row.everLeftStarting || !STARTING_FAMILY.has(state);
  if (!isQuiet(state, everLeftStarting)) {
    return { ...row, everLeftStarting, expanded: true, cooldownUntil: null };
  }
  if (row.expanded && row.cooldownUntil === null) {
    // Just recovered: hold open through the cool-down.
    return { ...row, everLeftStarting, cooldownUntil: nowMs + COOLDOWN_MS };
  }
  return { ...row, everLeftStarting };
}

export function tick(row: RowUi, nowMs: number): RowUi {
  if (row.expanded && row.cooldownUntil !== null && nowMs > row.cooldownUntil) {
    return { ...row, expanded: false, cooldownUntil: null };
  }
  return row;
}

export function togglePin(row: RowUi): RowUi {
  return { ...row, pinned: !row.pinned };
}

export function isExpanded(row: RowUi): boolean {
  return row.pinned || row.expanded;
}
