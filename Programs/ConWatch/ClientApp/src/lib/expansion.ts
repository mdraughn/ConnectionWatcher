// Rule C expansion state machine (pure; unit-tested).
// Quiet states: Responding and Initializing always; Starting is quiet unless
// it directly follows Unreachable (the release/re-prove path) — so a fresh
// boot or an engine rebuild does not expand healthy rows. Leaving quiet
// expands; a return to quiet holds the tile open through a cool-down; pins
// override everything.

export interface RowUi {
  expanded: boolean;
  pinned: boolean;
  cooldownUntil: number | null;
  lastState: string | null;
}

export const COOLDOWN_MS = 5 * 60_000;

function isQuiet(state: string, lastState: string | null): boolean {
  if (state === "Responding" || state === "Initializing") return true;
  if (state === "Starting") return lastState !== "Unreachable";
  return false;
}

export function initialRow(): RowUi {
  return { expanded: false, pinned: false, cooldownUntil: null, lastState: null };
}

export function onStateChange(row: RowUi, state: string, nowMs: number): RowUi {
  if (state === row.lastState) return row;
  if (!isQuiet(state, row.lastState)) {
    return { ...row, lastState: state, expanded: true, cooldownUntil: null };
  }
  if (row.expanded && row.cooldownUntil === null) {
    // Just returned to quiet: hold open through the cool-down.
    return { ...row, lastState: state, cooldownUntil: nowMs + COOLDOWN_MS };
  }
  return { ...row, lastState: state };
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
