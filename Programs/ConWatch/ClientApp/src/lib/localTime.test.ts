import { describe, expect, it } from "vitest";
import { DAY_MS, HOUR_MS, isoCompare, isoMs, localDayStart, localHourKey } from "./localTime";

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
});
