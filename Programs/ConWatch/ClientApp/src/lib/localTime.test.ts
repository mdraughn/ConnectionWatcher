import { describe, expect, it, vi } from "vitest";
import { DAY_MS, HOUR_MS, isoCompare, isoMs, localDayStart, localHourAt, localHourKey } from "./localTime";

describe("localTime", () => {
  it("has the unit constants", () => {
    expect(HOUR_MS).toBe(3_600_000);
    expect(DAY_MS).toBe(24 * HOUR_MS);
  });
  it("keys an hour the way the punchcard always has", () => {
    expect(localHourKey(new Date(2026, 9, 5, 7, 30))).toBe("2026-10-5-7");
  });
  it("starts a local day daysBack days ago", () => {
    expect(localDayStart(new Date(2026, 9, 5, 7, 30), 2)).toEqual(new Date(2026, 9, 3));
    expect(localDayStart(new Date(2026, 9, 5, 7, 30))).toEqual(new Date(2026, 9, 5));
  });
  it("isoMs parses an instant", () => {
    expect(isoMs("2026-10-29T10:00:00Z")).toBe(Date.UTC(2026, 9, 29, 10));
  });
  it("isoCompare treats +00:00 and Z, 1 and 7 fraction digits, as the same clock", () => {
    expect(isoCompare("2026-10-29T10:00:00.9+00:00", "2026-10-29T10:00:00.900Z")).toBe(0);
    expect(isoCompare("2026-10-29T10:00:00.1234567+00:00", "2026-10-29T10:00:00.123Z")).toBe(0);
    expect(isoCompare("2026-10-29T10:00:00+00:00", "2026-10-29T10:00:00.500Z")).toBeLessThan(0);
  });
  it("localHourAt builds each hour of an ordinary day", () => {
    const day = new Date(2026, 5, 15);
    for (const h of [0, 2, 3, 12, 23]) expect(localHourAt(day, h)!.getHours()).toBe(h);
  });
  it("localHourAt is null when the wall-clock hour does not exist (zone-independent stub)", () => {
    const RealDate = Date;
    // A clock whose constructor skips hour 2, as a spring-forward day does.
    class Gap extends RealDate {
      constructor(y: number, m: number, d: number, h = 0) {
        super(y, m, d, h === 2 ? 3 : h);
      }
    }
    vi.stubGlobal("Date", Gap);
    try {
      expect(localHourAt(new RealDate(2026, 2, 8), 2)).toBeNull();
      expect(localHourAt(new RealDate(2026, 2, 8), 3)!.getHours()).toBe(3);
    } finally {
      vi.unstubAllGlobals();
    }
  });
});
