// Dashboard island: hydrates the server-rendered tree with live behavior.
import { useEffect, useState, useSyncExternalStore } from "react";
import { createRoot } from "react-dom/client";
import { EventStrip } from "./components/EventStrip";
import { GlitchSection } from "./components/GlitchSection";
import { LanSection } from "./components/LanSection";
import { NameResolutionSection } from "./components/NameResolutionSection";
import { Ledger } from "./components/Ledger";
import { NotesPanel, acknowledgeWarning } from "./components/NotesPanel";
import { StatusBar } from "./components/StatusBar";
import { Tree, type Topology } from "./components/Tree";
import { VerdictBanner } from "./components/VerdictBanner";
import { startHub } from "./lib/hub";
import { isTreeTarget } from "./lib/scope";
import { createStatusStore, type StatusStore } from "./lib/statusStore";
import { answerClock } from "./lib/verdictClock";
import { VERDICT_SUBJECT_ID } from "./lib/verdictSubject";
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
  admin?: boolean;
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

// The slab clock's trail: verdict transitions, fetched once and kept
// current by the live stream. 500 reaches years of verdict history.
const TRAIL_LIMIT = 500;

function App({ store, topology, initial }: {
  store: StatusStore; topology: Topology; initial: InitialData;
}) {
  const state = useSyncExternalStore(store.subscribe, store.getState);

  const [trail, setTrail] = useState<{ items: TransitionEvent[]; complete: boolean } | null>(null);
  useEffect(() => {
    let live = true;
    fetch(`/api/v1/transitions?subjectId=${VERDICT_SUBJECT_ID}&limit=${TRAIL_LIMIT}`)
      .then((r) => (r.ok ? r.json() : null))
      .then((body) => {
        if (live && body?.items)
          setTrail({ items: body.items, complete: body.items.length < TRAIL_LIMIT });
      })
      .catch(() => undefined);
    return () => { live = false; };
  }, []);
  useEffect(() => {
    const t = state.latestTransition;
    if (!t?.verdict) return; // only verdict-subject rows carry a verdict
    setTrail((prev) => prev && prev.items[0]?.timestamp !== t.timestamp
      ? { ...prev, items: [t, ...prev.items] } : prev);
  }, [state.latestTransition]);
  const clock = trail && state.verdict
    ? answerClock(state.verdict.state, trail.items, new Date().toISOString(), trail.complete)
    : null;

  // Keep the header's wrench badge (site-shell.js) live between its polls.
  useEffect(() => {
    document.dispatchEvent(new CustomEvent("cw:warnings", { detail: state.warnings }));
  }, [state.warnings]);

  // Recent glitches cover the last 24 h, fixed at page load.
  const [glitchRange] = useState(() => {
    const to = new Date();
    return { from: new Date(to.getTime() - 24 * 3_600_000), to };
  });

  if (initial.engineDisabled || state.disabled) {
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
            subtitle="Internet targets, last 24 h" />
        </div>
      </div>
    );
  }

  return (
    <div>
      <VerdictBanner verdict={state.verdict} dns={state.dns} connected={state.connected} clock={clock} />
      <EventStrip topology={initial} />
      <Tree topology={topology} targets={state.targets.filter(isTreeTarget)} pools={state.pools}
        trunkNames={initial.trunkNames} anchorNames={initial.anchorNames} />
      <LanSection targets={state.targets} />
      <NameResolutionSection dns={state.dns} verdictState={state.verdict?.state ?? null} />
      {initial.admin && (
        <NotesPanel warnings={state.warnings}
          onAcknowledge={(key) => { void acknowledgeWarning(key); }} />
      )}
      <RecentEvents initial={initial} />
      <div style={{ marginTop: "1.5rem" }}>
        <GlitchSection from={glitchRange.from} to={glitchRange.to}
          subtitle="Internet targets, last 24 h" />
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

  fetch("/api/v1/transitions?limit=50&scope=internet")
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
