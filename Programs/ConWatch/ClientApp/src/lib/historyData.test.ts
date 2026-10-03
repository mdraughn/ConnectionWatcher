import { describe, expect, it } from "vitest";
import { clipAtReset, incidents, ribbonSegments, subjectsForScope, uptimePercent } from "./historyData";
import type { RollupPoint, TransitionEvent } from "./types";

function point(success: number, count: number): RollupPoint {
  return { periodStart: "2026-08-20T00:00:00Z", pingCount: count, successCount: success, rttAvg: 10, rttMin: 5, rttMax: 20 };
}

function t(iso: string, to: string, from = "Responding", trigger = "ThresholdCrossed"): TransitionEvent {
  return {
    timestamp: iso, subjectId: "s", subjectName: "S", subjectKind: "Target",
    from, to, trigger, detail: "d", verdict: null,
    successRate: null, sampleSize: null, burstLossRate: null,
  };
}

describe("uptimePercent", () => {
  it("weights by ping count", () => {
    expect(uptimePercent([point(50, 100), point(100, 100)])).toBeCloseTo(75);
  });
  it("null when no pings", () => {
    expect(uptimePercent([])).toBeNull();
  });
});

describe("incidents (split semantics)", () => {
  it("a Down window is an outage, measured to recovery", () => {
    const stats = incidents([
      t("2026-08-20T01:00:00Z", "Down"),
      t("2026-08-20T01:10:00Z", "Recovering", "Down"),
      t("2026-08-20T01:12:00Z", "Responding", "Recovering"),
    ], "2026-08-20T00:00:00Z", "2026-08-20T06:00:00Z");
    expect(stats.outageCount).toBe(1);
    expect(stats.longestOutageMs).toBe(12 * 60_000);
    expect(stats.unreachableMs).toBe(0);
  });

  it("unreachable time ends at the release, not at re-prove", () => {
    const stats = incidents([
      t("2026-08-20T05:00:00Z", "Unreachable", "Responding", "ParentDown"),
      t("2026-08-20T05:01:00Z", "Starting", "Unreachable", "ParentRecovered"),
      t("2026-08-20T05:03:00Z", "Responding", "Starting"),
    ], "2026-08-20T00:00:00Z", "2026-08-20T06:00:00Z");
    expect(stats.outageCount).toBe(0);
    expect(stats.unreachableMs).toBe(60_000);
  });

  it("an open outage runs to range end", () => {
    const stats = incidents([t("2026-08-20T01:00:00Z", "Down")], "2026-08-20T00:00:00Z", "2026-08-20T02:00:00Z");
    expect(stats.outageCount).toBe(1);
    expect(stats.longestOutageMs).toBe(60 * 60_000);
  });

  it("an outage superseded by unreachable splits into both buckets", () => {
    const stats = incidents([
      t("2026-08-20T01:00:00Z", "Down"),
      t("2026-08-20T01:05:00Z", "Unreachable", "Down", "ParentDown"),
      t("2026-08-20T01:10:00Z", "Starting", "Unreachable", "ParentRecovered"),
      t("2026-08-20T01:12:00Z", "Responding", "Starting"),
    ], "2026-08-20T00:00:00Z", "2026-08-20T06:00:00Z");
    expect(stats.outageCount).toBe(1);
    expect(stats.longestOutageMs).toBe(5 * 60_000);
    expect(stats.unreachableMs).toBe(5 * 60_000);
  });

  it("a renumber marker (AddressChanged) does not end an open outage", () => {
    const stats = incidents([
      t("2026-08-20T01:00:00Z", "Down"),
      t("2026-08-20T01:03:00Z", "Starting", "Starting", "AddressChanged"),
      t("2026-08-20T01:05:00Z", "Responding", "Starting"),
    ], "2026-08-20T00:00:00Z", "2026-08-20T06:00:00Z");
    expect(stats.outageCount).toBe(1);
    expect(stats.longestOutageMs).toBe(5 * 60_000);
  });

  it("an outage already open at the range start counts from the range start", () => {
    const stats = incidents([
      t("2026-08-20T01:00:00Z", "Responding", "Down"),
    ], "2026-08-20T00:00:00Z", "2026-08-20T06:00:00Z");
    expect(stats.outageCount).toBe(1);
    expect(stats.longestOutageMs).toBe(60 * 60_000);
  });

  it("an unreachable window already open at the range start counts from the range start", () => {
    const stats = incidents([
      t("2026-08-20T00:30:00Z", "Starting", "Unreachable", "ParentRecovered"),
    ], "2026-08-20T00:00:00Z", "2026-08-20T06:00:00Z");
    expect(stats.outageCount).toBe(0);
    expect(stats.unreachableMs).toBe(30 * 60_000);
  });
});

describe("clipAtReset", () => {
  const list = [
    t("2026-08-20T01:00:00Z", "Down"),
    t("2026-08-20T01:30:00Z", "Starting", "Starting", "HistoryReset"),
    t("2026-08-20T01:35:00Z", "Responding", "Starting"),
  ];

  it("returns only transitions after the latest reset", () => {
    const { transitions, resetAt } = clipAtReset(list);
    expect(resetAt).toBe("2026-08-20T01:30:00Z");
    expect(transitions.length).toBe(1);
    expect(transitions[0].to).toBe("Responding");
  });

  it("no reset passes everything through", () => {
    const { transitions, resetAt } = clipAtReset([list[0], list[2]]);
    expect(resetAt).toBeNull();
    expect(transitions.length).toBe(2);
  });

  it("stats after clipping exclude the pre-reset outage", () => {
    const { transitions } = clipAtReset(list);
    const stats = incidents(transitions, "2026-08-20T00:00:00Z", "2026-08-20T06:00:00Z");
    expect(stats.outageCount).toBe(0);
    expect(stats.longestOutageMs).toBe(0);
  });
});

describe("ribbonSegments", () => {
  it("builds contiguous segments across the range", () => {
    const segments = ribbonSegments(
      [t("2026-08-20T06:00:00Z", "Down"), t("2026-08-20T09:00:00Z", "Responding", "Down")],
      "2026-08-20T00:00:00Z", "2026-08-20T12:00:00Z");
    expect(segments.length).toBe(3);
    expect(segments[0]).toMatchObject({ state: "Responding", from: "2026-08-20T00:00:00Z" });
    expect(segments[1]).toMatchObject({ state: "Down", from: "2026-08-20T06:00:00Z", to: "2026-08-20T09:00:00Z" });
    expect(segments[2].state).toBe("Responding");
    expect(segments[2].to).toBe("2026-08-20T12:00:00Z");
  });

  it("uses the first transition's from-state before any transition", () => {
    const segments = ribbonSegments(
      [t("2026-08-20T06:00:00Z", "Faltering")],
      "2026-08-20T00:00:00Z", "2026-08-20T12:00:00Z");
    expect(segments[0].state).toBe("Responding");
    expect(segments[1].state).toBe("Faltering");
  });

  it("empty transitions yields one unknown segment", () => {
    const segments = ribbonSegments([], "2026-08-20T00:00:00Z", "2026-08-20T12:00:00Z");
    expect(segments.length).toBe(1);
    expect(segments[0].state).toBe("Unknown");
  });
});

describe("subjectsForScope", () => {
  const subjects = [
    { id: "r", name: "Router", scope: "Verdict" },
    { id: "n", name: "NAS", scope: "Local" },
    { id: "p", name: "Internet" }, // no scope: an older page payload means Verdict
  ];
  it("internet keeps everything that is not Local", () => {
    expect(subjectsForScope(subjects, "internet").map((s) => s.id)).toEqual(["r", "p"]);
  });
  it("local keeps only Local targets", () => {
    expect(subjectsForScope(subjects, "local").map((s) => s.id)).toEqual(["n"]);
  });
  it("is empty rather than throwing when nothing matches", () => {
    expect(subjectsForScope([], "local")).toEqual([]);
  });
});
