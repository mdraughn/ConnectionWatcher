// Dashboard island: hydrates the server-rendered tree with live behavior.
import { useEffect, useState, useSyncExternalStore } from "react";
import { createRoot } from "react-dom/client";
import { Punchcard } from "./components/Punchcard";
import { StatusBar } from "./components/StatusBar";
import { Tree, type Topology } from "./components/Tree";
import { VerdictBanner } from "./components/VerdictBanner";
import { startHub } from "./lib/hub";
import { createStatusStore, type StatusStore } from "./lib/statusStore";
import { fetchStory, type Story } from "./lib/story";
import type { Snapshot, TransitionEvent } from "./lib/types";

interface InitialData {
  snapshot: Snapshot | null;
  parents: Record<string, string | null>;
  poolMembers: Record<string, string[]>;
  trunkNames?: string[];
  anchorNames?: string[];
  weatherThreshold?: number;
}

const STORY_DAYS = 30;

function LastThirtyDays({ initial }: { initial: InitialData }) {
  const [story, setStory] = useState<Story | null>(null);
  useEffect(() => {
    let live = true;
    fetchStory(initial, STORY_DAYS).then((s) => { if (live) setStory(s); }).catch(() => undefined);
    return () => { live = false; };
  }, []);
  if (!story) return null;
  return (
    <div id="story-section" style={{ marginTop: "1.5rem" }}>
      <h4>The last 30 days</h4>
      <Punchcard cells={story.cells} days={story.days} now={story.now} />
    </div>
  );
}

function App({ store, topology, initial }: {
  store: StatusStore; topology: Topology; initial: InitialData;
}) {
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
      <LastThirtyDays initial={initial} />
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
  createRoot(el).render(<App store={store} topology={topology} initial={initial} />);
}
