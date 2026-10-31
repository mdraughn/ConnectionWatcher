import { describe, expect, it } from "vitest";
import { chronicTargets } from "./glitchData";
import type { GlitchItem } from "./types";

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
