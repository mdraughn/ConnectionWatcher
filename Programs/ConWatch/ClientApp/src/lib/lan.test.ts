import { describe, expect, it } from "vitest";
import { isLocal, sinceLabel, summarizeLan } from "./lan";
import type { TargetStatus } from "./types";

const t = (over: Partial<TargetStatus>): TargetStatus => ({
  id: "x", name: "X", address: "203.0.113.9", state: "Responding",
  successRate: 1, avgRtt: 1, sampleSize: 10, ...over,
});

describe("isLocal", () => {
  it("is true only for scope Local; a missing scope is Verdict", () => {
    expect(isLocal(t({ scope: "Local" }))).toBe(true);
    expect(isLocal(t({ scope: "Verdict" }))).toBe(false);
    expect(isLocal(t({}))).toBe(false);
  });
});

describe("summarizeLan", () => {
  it("counts only Local targets", () => {
    const s = summarizeLan([
      t({ id: "a", scope: "Local", state: "Responding" }),
      t({ id: "b", scope: "Local", state: "Down" }),
      t({ id: "c", scope: "Local", state: "Unreachable" }),
      t({ id: "d", scope: "Local", state: "Starting" }),
      t({ id: "e", scope: "Verdict", state: "Down" }),
    ]);
    expect(s).toEqual({ total: 4, up: 1, down: 2 });
  });
  it("is all zeros with no Local targets", () => {
    expect(summarizeLan([t({})])).toEqual({ total: 0, up: 0, down: 0 });
  });
});

describe("sinceLabel", () => {
  const now = new Date("2026-10-02T12:00:00Z");
  it("is an em dash when there is no recorded change (a just-added device)", () => {
    expect(sinceLabel(undefined, now)).toBe("—");
    expect(sinceLabel(null, now)).toBe("—");
    expect(sinceLabel("", now)).toBe("—");
  });
  it("is an em dash for an unparseable timestamp, never NaN", () => {
    expect(sinceLabel("not a date", now)).toBe("—");
  });
  it("formats elapsed time", () => {
    expect(sinceLabel("2026-10-02T11:59:50Z", now)).toBe("just now");
    expect(sinceLabel("2026-10-02T11:55:00Z", now)).toBe("5 min");
    expect(sinceLabel("2026-10-02T09:00:00Z", now)).toBe("3 h");
    expect(sinceLabel("2026-09-29T12:00:00Z", now)).toBe("3 d");
  });
  it("treats a timestamp slightly in the future as just now", () => {
    expect(sinceLabel("2026-10-02T12:00:30Z", now)).toBe("just now");
  });
});
