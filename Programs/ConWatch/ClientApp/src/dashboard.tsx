// Dashboard island: hydrates the server-rendered tree with live behavior.
import { useEffect, useSyncExternalStore } from "react";
import { createRoot } from "react-dom/client";
import { StatusBar } from "./components/StatusBar";
import { Tree, type Topology } from "./components/Tree";
import { VerdictBanner } from "./components/VerdictBanner";
import { startHub } from "./lib/hub";
import { createStatusStore, type StatusStore } from "./lib/statusStore";
import type { Snapshot, TransitionEvent } from "./lib/types";

interface InitialData {
  snapshot: Snapshot | null;
  parents: Record<string, string | null>;
  poolMembers: Record<string, string[]>;
}

function App({ store, topology }: { store: StatusStore; topology: Topology }) {
  const state = useSyncExternalStore(store.subscribe, store.getState);

  useEffect(() => {
    const timer = setInterval(() => store.tickAll(Date.now()), 5000);
    return () => clearInterval(timer);
  }, [store]);

  return (
    <div>
      <VerdictBanner verdict={state.verdict} connected={state.connected} />
      <Tree store={store} topology={topology} targets={state.targets}
        pools={state.pools} recent={state.recentTransitions} />
      <StatusBar latest={state.latestTransition} recent={state.recentTransitions}
        connected={state.connected} />
    </div>
  );
}

const el = document.getElementById("dashboard-root");
const dataEl = document.getElementById("initial-data");
if (el && dataEl) {
  const initial = JSON.parse(dataEl.textContent ?? "{}") as InitialData;
  const store = createStatusStore();
  if (initial.snapshot) store.applySnapshot(initial.snapshot, Date.now());

  fetch("/api/v1/transitions?limit=50")
    .then((r) => (r.ok ? r.json() : null))
    .then((body) => {
      if (body?.items) store.seedRecent(body.items as TransitionEvent[]);
    })
    .catch(() => undefined);

  startHub({
    onSnapshot: (s) => store.applySnapshot(s, Date.now()),
    onTransition: (t) => store.applyTransition(t, Date.now()),
    onConnectionChange: (connected) => store.setConnected(connected),
  });

  const topology: Topology = { parents: initial.parents, poolMembers: initial.poolMembers };
  createRoot(el).render(<App store={store} topology={topology} />);
}
