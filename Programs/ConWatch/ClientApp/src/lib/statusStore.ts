// Tiny external store for the dashboard island (React useSyncExternalStore).
import type { DnsStatus, ModelWarning, Snapshot, TransitionEvent } from "./types";
import { isLocal } from "./scope";

const MAX_RECENT = 50;

export interface StoreState {
  verdict: Snapshot["verdict"];
  targets: Snapshot["targets"];
  pools: Snapshot["pools"];
  recentTransitions: TransitionEvent[];
  latestTransition: TransitionEvent | null;
  connected: boolean;
  disabled: boolean;
  warnings: ModelWarning[];
  dns: DnsStatus | null;
}

export function createStatusStore() {
  let state: StoreState = {
    verdict: null, targets: [], pools: [],
    recentTransitions: [], latestTransition: null, connected: false,
    disabled: false, warnings: [], dns: null,
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
      state.warnings = snap.warnings ?? [];
      state.dns = snap.dns ?? null;
      notify();
    },
    applyTransition(t: TransitionEvent): void {
      // A LAN device's blip is not internet news: it never becomes the status
      // line's latest event or lands in the recent-events panel.
      if (state.targets.some((x) => x.id === t.subjectId && isLocal(x))) {
        return;
      }
      state.latestTransition = t;
      state.recentTransitions = [t, ...state.recentTransitions].slice(0, MAX_RECENT);
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
