// Tiny external store for the dashboard island (React useSyncExternalStore).
import type { Snapshot, TransitionEvent } from "./types";

const MAX_RECENT = 50;

export interface StoreState {
  verdict: Snapshot["verdict"];
  targets: Snapshot["targets"];
  pools: Snapshot["pools"];
  recentTransitions: TransitionEvent[];
  latestTransition: TransitionEvent | null;
  connected: boolean;
  disabled: boolean;
  /** Last-change ISO time per Local target id. */
  localChanges: Record<string, string>;
}

function laterOf(map: Record<string, string>, id: string, timestamp: string): Record<string, string> {
  const existing = map[id];
  return existing && Date.parse(existing) >= Date.parse(timestamp) ? map : { ...map, [id]: timestamp };
}

export function createStatusStore() {
  let state: StoreState = {
    verdict: null, targets: [], pools: [],
    recentTransitions: [], latestTransition: null, connected: false,
    disabled: false, localChanges: {},
  };
  const listeners = new Set<() => void>();

  function notify() {
    state = { ...state };
    listeners.forEach((l) => l());
  }

  return {
    subscribe(listener: () => void): () => void {
      listeners.add(listener);
      return () => listeners.delete(listener);
    },
    getState(): StoreState {
      return state;
    },
    applySnapshot(snap: Snapshot): void {
      state.verdict = snap.verdict;
      state.targets = snap.targets;
      state.pools = snap.pools;
      state.disabled = snap.enabled === false;
      notify();
    },
    applyTransition(t: TransitionEvent): void {
      // A LAN device's blip is not internet news: it never becomes the status
      // line's latest event or lands in the recent-events panel.
      if (state.targets.some((x) => x.id === t.subjectId && x.scope === "Local")) {
        state.localChanges = laterOf(state.localChanges, t.subjectId, t.timestamp);
        notify();
        return;
      }
      state.latestTransition = t;
      state.recentTransitions = [t, ...state.recentTransitions].slice(0, MAX_RECENT);
      notify();
    },
    /** History for Local targets (newest first): the last-change time per device. */
    seedLocalChanges(transitions: TransitionEvent[]): void {
      let next = state.localChanges;
      for (const t of transitions) next = laterOf(next, t.subjectId, t.timestamp);
      state.localChanges = next;
      notify();
    },
    seedRecent(transitions: TransitionEvent[]): void {
      state.recentTransitions = transitions.slice(0, MAX_RECENT);
      state.latestTransition = transitions[0] ?? state.latestTransition;
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
