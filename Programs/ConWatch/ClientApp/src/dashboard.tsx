// Dashboard island: hydrates the server-rendered tree with live behavior.
import { useEffect, useState, useSyncExternalStore } from "react";
import { createRoot } from "react-dom/client";
import { EventStrip } from "./components/EventStrip";
import { GlitchSection } from "./components/GlitchSection";
import { Ledger } from "./components/Ledger";
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
  /** Master enable switch off: archive mode — history serves, nothing pings. */
  engineDisabled?: boolean;
  cloneSource?: string | null;
}

// The dashboard ledger is the 10 most recent events, looked for across the
// last 90 days (the glitch retention window); the History page has the rest.
const STORY_DAYS = 90;
const STORY_EVENTS = 10;

function RecentEvents({ initial }: { initial: InitialData }) {
  const [story, setStory] = useState<Story | null>(null);
  useEffect(() => {
    let live = true;
    fetchStory(initial, STORY_DAYS).then((s) => { if (live) setStory(s); }).catch(() => undefined);
    return () => { live = false; };
  }, []);
  if (!story) return null;
  return (
    <div id="story-section" style={{ marginTop: "1.5rem" }}>
      <h4>What happened — the last {STORY_EVENTS} events</h4>
      <Ledger items={story.ledger} days={story.days} maxItems={STORY_EVENTS} moreHref="/History" />
    </div>
  );
}

function App({ store, topology, initial }: {
  store: StatusStore; topology: Topology; initial: InitialData;
}) {
  const state = useSyncExternalStore(store.subscribe, store.getState);

  // Recent glitches cover the last 24 h, fixed at page load.
  const [glitchRange] = useState(() => {
    const to = new Date();
    return { from: new Date(to.getTime() - 24 * 3_600_000), to };
  });

  if (initial.engineDisabled) {
    // Archive mode: everything DB-backed keeps working; no live states, no
    // pretense of them.
    return (
      <div>
        <div className="verdict-banner unknown">
          &#9208; MONITORING DISABLED — archive mode
          <span style={{ fontWeight: 400 }}>
            {" "}— history, the report, and exports stay available; nothing is
            pinged or notified. {initial.cloneSource
              ? "Finish the migration or re-enable on the "
              : "Re-enable on the "}
            <a href="/Admin">Admin page</a>.
          </span>
        </div>
        <EventStrip topology={initial} />
        <RecentEvents initial={initial} />
        <div style={{ marginTop: "1.5rem" }}>
          <GlitchSection from={glitchRange.from} to={glitchRange.to}
            subtitle="all targets, last 24 h" />
        </div>
      </div>
    );
  }

  return (
    <div>
      <VerdictBanner verdict={state.verdict} connected={state.connected} />
      <EventStrip topology={initial} />
      <Tree topology={topology} targets={state.targets} pools={state.pools}
        trunkNames={initial.trunkNames} anchorNames={initial.anchorNames} />
      <RecentEvents initial={initial} />
      <div style={{ marginTop: "1.5rem" }}>
        <GlitchSection from={glitchRange.from} to={glitchRange.to}
          subtitle="all targets, last 24 h" />
      </div>
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
  if (initial.snapshot) store.applySnapshot(initial.snapshot);

  fetch("/api/v1/transitions?limit=50")
    .then((r) => (r.ok ? r.json() : null))
    .then((body) => {
      if (body?.items) store.seedRecent(body.items as TransitionEvent[]);
    })
    .catch(() => undefined);

  startHub({
    onSnapshot: (s) => store.applySnapshot(s),
    onTransition: (t) => store.applyTransition(t),
    onConnectionChange: (connected) => store.setConnected(connected),
  });

  const topology: Topology = { parents: initial.parents, poolMembers: initial.poolMembers };
  createRoot(el).render(<App store={store} topology={topology} initial={initial} />);
}
