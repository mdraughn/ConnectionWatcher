import { describe, expect, it } from "vitest";
import { formatClock, formatProse } from "./format";

describe("formatClock", () => {
  it("shows seconds up to an hour and minutes forever — the clock must visibly tick", () => {
    expect(formatClock(42_000)).toBe("42s");
    expect(formatClock(5 * 60_000 + 12_000)).toBe("5m 12s");
    expect(formatClock(3 * 3_600_000 + 24 * 60_000)).toBe("3h 24m");
    expect(formatClock(6 * 86_400_000 + 4 * 3_600_000 + 12 * 60_000)).toBe("6d 4h 12m");
    // Hundreds of days in, the minutes still change every minute.
    expect(formatClock(247 * 86_400_000 + 13 * 3_600_000 + 22 * 60_000)).toBe("247d 13h 22m");
  });
});

describe("formatProse", () => {
  it("keeps seconds on short events and never blurs them into round minutes", () => {
    expect(formatProse(26_000)).toBe("26 s");
    expect(formatProse(115_000)).toBe("115 s");
    expect(formatProse(4 * 60_000 + 10_000)).toBe("4 min 10 s");
    expect(formatProse(9 * 60_000 + 40_000)).toBe("9 min 40 s");
    expect(formatProse(4 * 60_000)).toBe("4 min");
    expect(formatProse(45 * 60_000 + 40_000)).toBe("46 min");
    expect(formatProse(150 * 60_000)).toBe("2.5 h");
  });
});

describe("the two styles", () => {
  it("the same 100 s reads one way per style", () => {
    expect(formatClock(100_000)).toBe("1m 40s");
    expect(formatProse(100_000)).toBe("100 s");
  });
});
