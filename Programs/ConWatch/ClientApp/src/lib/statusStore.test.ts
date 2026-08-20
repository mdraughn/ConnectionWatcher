import { beforeEach, describe, expect, it, vi } from "vitest";
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
  beforeEach(() => {
    localStorage.clear();
  });

  it("applies snapshots and updates row ui state", () => {
    const store = createStatusStore();
    store.applySnapshot(snapshot, 0);
    expect(store.getState().targets.length).toBe(2);
    expect(store.isExpanded("t1")).toBe(false);
  });

  it("a degrading transition expands the row and records the event", () => {
    const store = createStatusStore();
    store.applySnapshot(snapshot, 0);
    store.applyTransition(transition({}), 1000);
    expect(store.isExpanded("t2")).toBe(true);
    expect(store.getState().latestTransition?.subjectName).toBe("Google");
    expect(store.getState().recentTransitions.length).toBe(1);
  });

  it("caps recent transitions at 50", () => {
    const store = createStatusStore();
    store.applySnapshot(snapshot, 0);
    for (let i = 0; i < 60; i++)
      store.applyTransition(transition({ timestamp: `2026-08-20T12:00:${String(i % 60).padStart(2, "0")}Z` }), i);
    expect(store.getState().recentTransitions.length).toBe(50);
  });

  it("pins persist through localStorage", () => {
    const store = createStatusStore();
    store.applySnapshot(snapshot, 0);
    store.togglePin("t1");
    expect(store.isExpanded("t1")).toBe(true);

    const second = createStatusStore();
    second.applySnapshot(snapshot, 0);
    expect(second.isExpanded("t1")).toBe(true);
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
    store.applySnapshot(snapshot, 0);
    expect(spy).toHaveBeenCalled();
  });
});
