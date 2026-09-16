import { describe, expect, it } from "vitest";
import { HATCH, HEALTHY, HEALTHY_VIVID, markCss, markRank, RAMP, ribbonFill, verdictFill } from "./encoding";
import {
  buildLedger, glitchMark, humanDuration, punchcardCells, stripBins, verdictWindows, windowMark,
} from "./incidents";
import type { GlitchItem } from "./glitchData";
import type { TransitionEvent } from "./types";

describe("humanDuration", () => {
  it("keeps seconds on short events and never blurs them into round minutes", () => {
    expect(humanDuration(26_000)).toBe("26 s");
    expect(humanDuration(115_000)).toBe("115 s");
    expect(humanDuration(4 * 60_000 + 10_000)).toBe("4 min 10 s");
    expect(humanDuration(9 * 60_000 + 40_000)).toBe("9 min 40 s");
    expect(humanDuration(4 * 60_000)).toBe("4 min");
    expect(humanDuration(45 * 60_000 + 40_000)).toBe("46 min");
    expect(humanDuration(150 * 60_000)).toBe("2.5 h");
  });
});

function vt(timestamp: string, from: string, to: string, verdict: string, detail = ""): TransitionEvent {
  return {
    timestamp, from, to, verdict, detail,
    subjectId: "v", subjectName: "Internet", subjectKind: "Verdict",
    trigger: "VerdictChanged", successRate: null, sampleSize: null, burstLossRate: null,
  };
}

function glitch(over: Partial<GlitchItem>): GlitchItem {
  return {
    id: 1, targetId: "t", targetName: "Google DNS", class: "Target",
    startUtc: "2026-09-08T08:42:00Z", endUtc: "2026-09-08T08:42:20Z",
    lostCount: 2, sampleCount: 8, detail: null, ...over,
  };
}

const TRUNK = new Set(["Router", "External Router", "Comcast Edge"]);

describe("verdictWindows", () => {
  it("derives attributed windows and closes the last at range end", () => {
    const windows = verdictWindows([
      vt("2026-09-09T05:38:00Z", "Responding", "Down", "LinkDown", "LinkDown: Comcast — Comcast Edge is Down"),
      vt("2026-09-09T05:42:00Z", "Down", "Responding", "Healthy", "Healthy: 4 anchors responding"),
      vt("2026-09-09T06:00:00Z", "Responding", "Faltering", "InternetWeather", "2 of 4 anchors degraded"),
    ], "2026-09-09T06:30:00Z");
    expect(windows).toEqual([
      { kind: "LinkDown", from: "2026-09-09T05:38:00Z", to: "2026-09-09T05:42:00Z", detail: "LinkDown: Comcast — Comcast Edge is Down" },
      { kind: "InternetWeather", from: "2026-09-09T06:00:00Z", to: "2026-09-09T06:30:00Z", detail: "2 of 4 anchors degraded" },
    ]);
  });
});

describe("windowMark", () => {
  it("maps link verdicts to grey bars and weather to warm bars by anchor damage", () => {
    const down = { kind: "LinkDown" as const, from: "", to: "", detail: null };
    expect(windowMark(down, 0, 2)).toEqual({ shape: "bar", hue: "grey", depth: "out" });
    const degraded = { ...down, kind: "LinkDegraded" as const };
    expect(windowMark(degraded, 0, 2)).toEqual({ shape: "bar", hue: "grey", depth: "degraded" });
    const weather = { ...down, kind: "InternetWeather" as const };
    expect(windowMark(weather, 1, 2)).toEqual({ shape: "bar", hue: "warm", depth: "degraded" });
    expect(windowMark(weather, 3, 2)).toEqual({ shape: "bar", hue: "warm", depth: "out" });
  });
});

describe("glitchMark", () => {
  it("distinguishes internet, trunk, and single-target glitches", () => {
    expect(glitchMark(glitch({ class: "Internet", targetName: "Internet" }), TRUNK))
      .toEqual({ shape: "bar", hue: "warm", depth: "glitch", ambiguous: false });
    expect(glitchMark(glitch({
      class: "Internet", targetName: "Internet",
      detail: "anchors: Google, Quad9; trunk affected (likely link/ISP)",
    }), TRUNK).ambiguous).toBe(true);
    expect(glitchMark(glitch({ targetName: "Comcast Edge" }), TRUNK))
      .toEqual({ shape: "bar", hue: "grey", depth: "glitch", ambiguous: false });
    expect(glitchMark(glitch({}), TRUNK))
      .toEqual({ shape: "dot", hue: "warm", depth: "glitch", ambiguous: false });
  });
});

describe("markRank and markCss", () => {
  it("ranks trunk-out above weather-out above degradations above glitches above dots", () => {
    const ranks = [
      markRank({ shape: "bar", hue: "grey", depth: "out" }),
      markRank({ shape: "bar", hue: "warm", depth: "out" }),
      markRank({ shape: "bar", hue: "grey", depth: "degraded" }),
      markRank({ shape: "bar", hue: "warm", depth: "degraded" }),
      markRank({ shape: "bar", hue: "warm", depth: "glitch" }),
      markRank({ shape: "dot", hue: "warm", depth: "out" }),
    ];
    expect([...ranks].sort((a, b) => b - a)).toEqual(ranks);
  });

  it("renders solid fills, ambiguous gradients, and exposes the hatch", () => {
    expect(markCss({ shape: "bar", hue: "warm", depth: "out" }).background).toBe(RAMP.warm.out);
    expect(markCss({ shape: "bar", hue: "warm", depth: "glitch", ambiguous: true }).background)
      .toContain("linear-gradient(to bottom right");
    expect(HATCH).toContain("repeating-linear-gradient(45deg");
  });
});

describe("ribbonFill", () => {
  it("colors trunk rows grey, others warm, hatches Unreachable", () => {
    expect(ribbonFill("trunk", "Down")).toBe(RAMP.grey.out);
    expect(ribbonFill("trunk", "Faltering")).toBe(RAMP.grey.degraded);
    expect(ribbonFill("anchor", "Down")).toBe(RAMP.warm.out);
    expect(ribbonFill("other", "Recovering")).toBe(RAMP.warm.degraded);
    expect(ribbonFill("anchor", "Unreachable")).toBe("hatch");
    expect(ribbonFill("anchor", "Responding")).toBe("var(--healthy)");
  });
});

describe("verdictFill", () => {
  it("attributes by verdict name: link = grey, weather/site = warm, healthy = quiet", () => {
    expect(verdictFill("LinkDown", "Down")).toBe(RAMP.grey.out);
    expect(verdictFill("LinkDegraded", "Faltering")).toBe(RAMP.grey.degraded);
    expect(verdictFill("InternetWeather", "Faltering")).toBe(RAMP.warm.degraded);
    expect(verdictFill("SiteIncident", "Responding")).toBe(RAMP.warm.glitch);
    expect(verdictFill("Healthy", "Responding")).toBe(HEALTHY);
  });

  it("falls back to the plain state mapping without a recognizable name", () => {
    expect(verdictFill(null, "Down")).toBe(RAMP.warm.out);
    expect(verdictFill("something else", "Responding")).toBe(HEALTHY);
    expect(verdictFill(null, "Starting")).toBe("var(--ribbon-starting)");
  });
});

describe("healthy tokens", () => {
  it("keeps the pale surface tint and the vivid small-mark green distinct", () => {
    expect(HEALTHY).not.toBe(HEALTHY_VIVID);
    // Theme-aware: colors are CSS variables so dark mode can re-point them.
    expect(HEALTHY_VIVID).toMatch(/^var\(--[a-z-]+\)$/);
    expect(RAMP.grey.out).toMatch(/^var\(--[a-z-]+\)$/);
  });
});

describe("punchcardCells", () => {
  it("buckets locally and keeps one winning mark per cell with counts", () => {
    // Build times FROM local components so hour mapping is TZ-proof.
    const at = (d: number, h: number) => new Date(2026, 8, d, h, 30).toISOString();
    const now = new Date(2026, 8, 30, 12);
    const windows = [
      { kind: "LinkDown" as const, from: at(9, 0), to: at(9, 0), detail: null },
      { kind: "InternetWeather" as const, from: at(9, 0), to: at(9, 0), detail: null }, // same cell: loses to LinkDown
    ];
    const cells = punchcardCells(windows, new Map([
      [windows[0], { shape: "bar" as const, hue: "grey" as const, depth: "out" as const }],
      [windows[1], { shape: "bar" as const, hue: "warm" as const, depth: "degraded" as const }],
    ]), [
      glitch({ startUtc: at(9, 0), endUtc: at(9, 0) }),      // dot loses to bars in same cell
      glitch({ startUtc: at(15, 3), endUtc: at(15, 3) }),    // its own cell: dot wins there
    ], TRUNK, 30, now);

    const day9 = cells.get("2026-9-9-0")!;
    expect(day9.mark).toEqual({ shape: "bar", hue: "grey", depth: "out" });
    expect(day9.count).toBe(3);
    expect(cells.get("2026-9-15-3")!.mark.shape).toBe("dot");
  });
});

describe("stripBins", () => {
  const MIN = 60_000;
  const now = new Date("2026-09-11T12:00:00Z");
  const iso = (minutesAgo: number) => new Date(now.getTime() - minutesAgo * MIN).toISOString();

  it("paints every bin an incident overlaps, newest bin at the end", () => {
    const w = { kind: "LinkDown" as const, from: iso(5), to: iso(2), detail: null };
    const mark = { shape: "bar" as const, hue: "grey" as const, depth: "out" as const };
    const bins = stripBins([w], new Map([[w, mark]]), [], TRUNK, MIN, 10, now);
    expect(bins).toHaveLength(10);
    // bins are oldest→newest: minutes 9..0 ago; the outage covers minutes 5..2 ago.
    expect(bins[0]).toBeNull(); // 9 min ago: healthy
    expect(bins[4]).toEqual(mark); // 5 min ago
    expect(bins[7]).toEqual(mark); // 2 min ago (inclusive end bin)
    expect(bins[9]).toBeNull(); // now: recovered
  });

  it("an ongoing incident paints through the newest bin", () => {
    const w = { kind: "LinkDegraded" as const, from: iso(3), to: now.toISOString(), detail: null };
    const mark = { shape: "bar" as const, hue: "grey" as const, depth: "degraded" as const };
    const bins = stripBins([w], new Map([[w, mark]]), [], TRUNK, MIN, 10, now);
    expect(bins[9]).toEqual(mark);
  });

  it("glitches paint at least one bin and lose to bars in the same bin", () => {
    const w = { kind: "InternetWeather" as const, from: iso(4), to: iso(4), detail: null };
    const weather = { shape: "bar" as const, hue: "warm" as const, depth: "degraded" as const };
    const bins = stripBins([w], new Map([[w, weather]]),
      [glitch({ startUtc: iso(4), endUtc: iso(4) }),      // same bin as weather: loses
       glitch({ startUtc: iso(8), endUtc: iso(8) })],     // its own bin: dot wins
      TRUNK, MIN, 10, now);
    expect(bins[5]).toEqual(weather);
    expect(bins[1]!.shape).toBe("dot");
  });
});

describe("buildLedger", () => {
  it("writes leads by kind, passes operator detail through, humanizes durations", () => {
    const w: any = { kind: "LinkDown", from: "2026-09-09T05:38:00Z", to: "2026-09-09T05:42:00Z",
      detail: "LinkDown: Comcast — Comcast Edge is Down" };
    const items = buildLedger([w],
      new Map([[w, { shape: "bar" as const, hue: "grey" as const, depth: "out" as const }]]),
      [glitch({ startUtc: "2026-09-10T01:00:00Z", endUtc: "2026-09-10T01:00:45Z" })], TRUNK);
    expect(items[0].at).toBe("2026-09-10T01:00:00Z"); // newest first
    expect(items[0].lead).toBe("Google DNS dropped 2 pings (45 s).");
    expect(items[1].lead).toBe("Your link went down for 4 min.");
    expect(items[1].body).toContain("Comcast");
    expect(items[1].mark.hue).toBe("grey");
  });
});
