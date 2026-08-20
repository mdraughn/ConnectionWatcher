import { describe, expect, it } from "vitest";
import { incidents, ribbonSegments, uptimePercent } from "./historyData";
import type { RollupPoint, TransitionEvent } from "./types";

function point(success: number, count: number): RollupPoint {
  return { periodStart: "2026-08-20T00:00:00Z", pingCount: count, successCount: success, rttAvg: 10, rttMin: 5, rttMax: 20 };
}

function t(iso: string, to: string, from = "Responding"): TransitionEvent {
  return {
    timestamp: iso, subjectId: "s", subjectName: "S", subjectKind: "Target",
    from, to, trigger: "ThresholdCrossed", detail: "d", verdict: null,
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

describe("incidents", () => {
  it("counts down/unreachable entries and measures the longest", () => {
    const list = [
      t("2026-08-20T01:00:00Z", "Down"),
      t("2026-08-20T01:10:00Z", "Recovering", "Down"),
      t("2026-08-20T01:12:00Z", "Responding", "Recovering"),   // 12-minute incident
      t("2026-08-20T05:00:00Z", "Unreachable"),
      t("2026-08-20T05:05:00Z", "Responding", "Unreachable"),  // 5-minute incident
    ];
    const result = incidents(list, "2026-08-20T06:00:00Z");
    expect(result.count).toBe(2);
    expect(result.longestMs).toBe(12 * 60_000);
  });

  it("open incident runs to range end", () => {
    const result = incidents([t("2026-08-20T01:00:00Z", "Down")], "2026-08-20T02:00:00Z");
    expect(result.count).toBe(1);
    expect(result.longestMs).toBe(60 * 60_000);
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
    expect(segments[0].state).toBe("Responding");   // the transition's `from`
    expect(segments[1].state).toBe("Faltering");
  });

  it("empty transitions yields one unknown segment", () => {
    const segments = ribbonSegments([], "2026-08-20T00:00:00Z", "2026-08-20T12:00:00Z");
    expect(segments.length).toBe(1);
    expect(segments[0].state).toBe("Unknown");
  });
});
