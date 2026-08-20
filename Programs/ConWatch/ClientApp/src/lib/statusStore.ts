// Tiny external store for the dashboard island (React useSyncExternalStore).
import { initialRow, isExpanded, onStateChange, tick, togglePin, type RowUi } from "./expansion";
import type { Snapshot, TransitionEvent } from "./types";

const PINS_KEY = "conwatch.pins";
const MAX_RECENT = 50;

export interface StoreState {
  verdict: Snapshot["verdict"];
  targets: Snapshot["targets"];
  pools: Snapshot["pools"];
  recentTransitions: TransitionEvent[];
  latestTransition: TransitionEvent | null;
  connected: boolean;
}

function loadPins(): Set<string> {
  try {
    return new Set(JSON.parse(localStorage.getItem(PINS_KEY) ?? "[]") as string[]);
  } catch {
    return new Set();
  }
}

function savePins(pins: Set<string>): void {
  try {
    localStorage.setItem(PINS_KEY, JSON.stringify([...pins]));
  } catch {
    // localStorage unavailable — pins just don't persist
  }
}

export function createStatusStore() {
  const rows = new Map<string, RowUi>();
  const pins = loadPins();
  let state: StoreState = {
    verdict: null, targets: [], pools: [],
    recentTransitions: [], latestTransition: null, connected: false,
  };
  const listeners = new Set<() => void>();

  function notify() {
    state = { ...state };
    listeners.forEach((l) => l());
  }

  function row(id: string): RowUi {
    let r = rows.get(id);
    if (!r) {
      r = initialRow();
      if (pins.has(id)) r = togglePin(r);
      rows.set(id, r);
    }
    return r;
  }

  return {
    subscribe(listener: () => void): () => void {
      listeners.add(listener);
      return () => listeners.delete(listener);
    },
    getState(): StoreState {
      return state;
    },
    isExpanded(id: string): boolean {
      return isExpanded(row(id));
    },
    applySnapshot(snap: Snapshot, nowMs: number): void {
      state.verdict = snap.verdict;
      state.targets = snap.targets;
      state.pools = snap.pools;
      for (const t of [...snap.targets, ...snap.pools]) {
        rows.set(t.id, onStateChange(row(t.id), t.state, nowMs));
      }
      notify();
    },
    applyTransition(t: TransitionEvent, nowMs: number): void {
      rows.set(t.subjectId, onStateChange(row(t.subjectId), t.to, nowMs));
      state.latestTransition = t;
      state.recentTransitions = [t, ...state.recentTransitions].slice(0, MAX_RECENT);
      notify();
    },
    tickAll(nowMs: number): void {
      let changed = false;
      for (const [id, r] of rows) {
        const next = tick(r, nowMs);
        if (next !== r) {
          rows.set(id, next);
          changed = true;
        }
      }
      if (changed) notify();
    },
    togglePin(id: string): void {
      const next = togglePin(row(id));
      rows.set(id, next);
      if (next.pinned) pins.add(id);
      else pins.delete(id);
      savePins(pins);
      notify();
    },
    setConnected(connected: boolean): void {
      if (state.connected !== connected) {
        state.connected = connected;
        notify();
      }
    },
  };
}

export type StatusStore = ReturnType<typeof createStatusStore>;
