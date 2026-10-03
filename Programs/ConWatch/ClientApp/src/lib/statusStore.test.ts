import { describe, expect, it, vi } from "vitest";
import { createStatusStore } from "./statusStore";
import type { Snapshot, TransitionEvent } from "./types";

const snapshot: Snapshot = {
  verdict: { state: "Healthy", detail: "3 anchors responding" },
  targets: [
    { id: "t1", name: "Router", address: "192.168.1.1", state: "Responding", successRate: 1, avgRtt: 1, sampleSize: 10 },
    { id: "t2", name: "Google", address: "8.8.8.8", state: "Responding", successRate: 1, avgRtt: 22, sampleSize: 10 },
  ],
  pools: [{ id: "p1", name: "Internet", state: "Responding" }],
};

function transition(overrides: Partial<TransitionEvent>): TransitionEvent {
  return {
    timestamp: "2026-08-20T12:00:00Z", subjectId: "t2", subjectName: "Google",
    subjectKind: "Target", from: "Responding", to: "Faltering",
    trigger: "ThresholdCrossed", detail: null, verdict: null,
    successRate: 0.6, sampleSize: 10, burstLossRate: null,
    ...overrides,
  };
}

describe("statusStore", () => {
  it("applies snapshots", () => {
    const store = createStatusStore();
    store.applySnapshot(snapshot);
    expect(store.getState().targets.length).toBe(2);
    expect(store.getState().verdict?.state).toBe("Healthy");
  });

  it("records transitions", () => {
    const store = createStatusStore();
    store.applySnapshot(snapshot);
    store.applyTransition(transition({}));
    expect(store.getState().latestTransition?.subjectName).toBe("Google");
    expect(store.getState().recentTransitions.length).toBe(1);
  });

  it("caps recent transitions at 50", () => {
    const store = createStatusStore();
    store.applySnapshot(snapshot);
    for (let i = 0; i < 60; i++)
      store.applyTransition(transition({ timestamp: `2026-08-20T12:00:${String(i % 60).padStart(2, "0")}Z` }));
    expect(store.getState().recentTransitions.length).toBe(50);
  });

  it("seeds recent transitions from the API", () => {
    const store = createStatusStore();
    store.seedRecent([transition({ subjectName: "A" }), transition({ subjectName: "B" })]);
    expect(store.getState().recentTransitions.length).toBe(2);
    expect(store.getState().latestTransition?.subjectName).toBe("A");
  });

  it("notifies subscribers", () => {
    const store = createStatusStore();
    const spy = vi.fn();
    store.subscribe(spy);
    store.applySnapshot(snapshot);
    expect(spy).toHaveBeenCalled();
  });

  const lanSnapshot: Snapshot = {
    ...snapshot,
    targets: [
      ...snapshot.targets,
      { id: "t9", name: "NAS", address: "203.0.113.9", state: "Responding", successRate: 1, avgRtt: 1, sampleSize: 10, scope: "Local" },
    ],
  };

  it("keeps a live Local transition out of the latest/recent events (it is not internet news)", () => {
    const store = createStatusStore();
    store.applySnapshot(lanSnapshot);
    store.applyTransition(transition({ subjectId: "t9", subjectName: "NAS", to: "Down", timestamp: "2026-10-02T10:00:00Z" }));
    expect(store.getState().latestTransition).toBeNull();
    expect(store.getState().recentTransitions).toHaveLength(0);
    expect(store.getState().localChanges["t9"]).toBe("2026-10-02T10:00:00Z");
  });

  it("still records verdict-scope transitions as before", () => {
    const store = createStatusStore();
    store.applySnapshot(lanSnapshot);
    store.applyTransition(transition({}));
    expect(store.getState().latestTransition?.subjectName).toBe("Google");
    expect(store.getState().localChanges).toEqual({});
  });

  it("seeds last-change times from history, newest wins, live is never overwritten by older", () => {
    const store = createStatusStore();
    store.applySnapshot(lanSnapshot);
    store.applyTransition(transition({ subjectId: "t9", timestamp: "2026-10-02T11:00:00Z" }));
    store.seedLocalChanges([ // newest first, as the API returns them
      transition({ subjectId: "t9", timestamp: "2026-10-02T09:00:00Z" }),
      transition({ subjectId: "t8", timestamp: "2026-10-02T08:00:00Z" }),
      transition({ subjectId: "t8", timestamp: "2026-10-01T08:00:00Z" }),
    ]);
    expect(store.getState().localChanges["t9"]).toBe("2026-10-02T11:00:00Z");
    expect(store.getState().localChanges["t8"]).toBe("2026-10-02T08:00:00Z");
  });
});
