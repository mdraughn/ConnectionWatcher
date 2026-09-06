import { describe, expect, it } from "vitest";
import { bucketByHourLocal, chronicTargets, type GlitchItem } from "./glitchData";

// Timestamps are built FROM local wall-clock components so the expected
// hour mapping holds in any timezone the test machine runs in.
function atLocal(y: number, m: number, d: number, h: number, min = 0): string {
  return new Date(y, m - 1, d, h, min).toISOString();
}

function glitch(start: string, name = "Google", cls: "Target" | "Internet" = "Target"): GlitchItem {
  return {
    id: Math.random(), targetId: name, targetName: name, class: cls,
    startUtc: start, endUtc: start, lostCount: 2, sampleCount: 8, detail: null,
  };
}

describe("bucketByHourLocal", () => {
  it("buckets counts into [day][hour] cells ending at 'now'", () => {
    const now = new Date(2026, 8, 6, 12); // Sep 6 local noon
    const grid = bucketByHourLocal(
      [atLocal(2026, 9, 4, 3, 34), atLocal(2026, 9, 4, 3, 48), atLocal(2026, 9, 5, 22)],
      3, now);
    expect(grid.length).toBe(3); // 3 day columns, oldest first
    expect(grid[0][3]).toBe(2);  // Sep 4, 03:xx local — the nightly stripe
    expect(grid[1][22]).toBe(1); // Sep 5, 22:xx
    expect(grid.flat().reduce((a, b) => a + b, 0)).toBe(3);
  });

  it("ignores events outside the window", () => {
    const now = new Date(2026, 8, 6, 12);
    const grid = bucketByHourLocal([atLocal(2026, 8, 1, 5)], 3, now);
    expect(grid.flat().reduce((a, b) => a + b, 0)).toBe(0);
  });

  it("spans a DST boundary without losing events", () => {
    // US DST fall-back 2026: Nov 1. Window covers Oct 31–Nov 2.
    const now = new Date(2026, 10, 2, 12);
    const grid = bucketByHourLocal(
      [atLocal(2026, 10, 31, 23), atLocal(2026, 11, 1, 1), atLocal(2026, 11, 2, 3)],
      3, now);
    expect(grid.flat().reduce((a, b) => a + b, 0)).toBe(3);
  });
});

describe("chronicTargets", () => {
  it("flags a target with glitches in three distinct hours of the last 24", () => {
    const now = new Date(2026, 8, 6, 12);
    const items = [
      glitch(atLocal(2026, 9, 6, 2)),
      glitch(atLocal(2026, 9, 6, 3)),
      glitch(atLocal(2026, 9, 6, 4)),
      glitch(atLocal(2026, 9, 6, 5), "Quad9"),
      glitch(atLocal(2026, 9, 6, 5, 30), "Quad9"), // same hour: counts once
    ];
    expect(chronicTargets(items, now)).toEqual(["Google"]);
  });

  it("ignores internet-class glitches and old events", () => {
    const now = new Date(2026, 8, 6, 12);
    const items = [
      glitch(atLocal(2026, 9, 6, 2), "Internet", "Internet"),
      glitch(atLocal(2026, 9, 6, 3), "Internet", "Internet"),
      glitch(atLocal(2026, 9, 6, 4), "Internet", "Internet"),
      glitch(atLocal(2026, 9, 3, 2)), // outside 24h
      glitch(atLocal(2026, 9, 3, 3)),
      glitch(atLocal(2026, 9, 3, 4)),
    ];
    expect(chronicTargets(items, now)).toEqual([]);
  });
});
