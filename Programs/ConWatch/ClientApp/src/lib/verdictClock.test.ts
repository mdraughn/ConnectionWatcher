import { describe, expect, it } from "vitest";
import { answerClock } from "./verdictClock";
import type { TransitionEvent } from "./types";

const NOW = "2026-09-16T12:00:00Z";
const ago = (h: number) => new Date(Date.parse(NOW) - h * 3_600_000).toISOString();
const hours = (h: number) => h * 3_600_000;
// A verdict transition row switching TO `verdict` at `timestamp` (trail is
// newest first, like the API returns).
const v = (verdict: string, timestamp: string): TransitionEvent =>
  ({ timestamp, subjectId: "i", subjectName: "Internet", subjectKind: "Verdict",
     from: "x", to: "y", trigger: "t", detail: null, verdict,
     successRate: null, sampleSize: null, burstLossRate: null });

describe("answerClock", () => {
  it("times the up streak from the end of the last down", () => {
    const trail = [v("Healthy", ago(1)), v("LinkDown", ago(2)), v("Healthy", ago(72))];
    const c = answerClock("Healthy", trail, NOW, true);
    expect(c.cls).toBe("up");
    expect(c.sinceIso).toBe(ago(1)); // the recovery, not the ancient start
    expect(c.upStreakMs).toBeNull();
  });

  it("weather and site incidents never reset the streak", () => {
    const trail = [v("Healthy", ago(1)), v("InternetWeather", ago(2)),
      v("Healthy", ago(3)), v("SiteIncident", ago(4)), v("Healthy", ago(72))];
    expect(answerClock("Healthy", trail, NOW, true).sinceIso).toBe(ago(72));
  });

  it("struggling blemishes the streak but does not reset it", () => {
    const trail = [v("Healthy", ago(1)), v("LinkDegraded", ago(2)), v("Healthy", ago(72))];
    expect(answerClock("Healthy", trail, NOW, true).sinceIso).toBe(ago(72));
  });

  it("while struggling, shows the stretch AND the still-counting streak", () => {
    const trail = [v("LinkDegraded", ago(0.2)), v("Healthy", ago(72))];
    const c = answerClock("LinkDegraded", trail, NOW, true);
    expect(c.cls).toBe("struggling");
    expect(c.sinceIso).toBe(ago(0.2));
    expect(c.upStreakMs).toBeCloseTo(hours(72), -2); // counts through the struggle
  });

  it("down freezes the streak the outage ended, through any struggling prelude", () => {
    const trail = [v("LinkDown", ago(0.1)), v("LinkDegraded", ago(0.2)), v("Healthy", ago(72))];
    const c = answerClock("LinkDown", trail, NOW, true);
    expect(c.cls).toBe("down");
    expect(c.sinceIso).toBe(ago(0.1));
    expect(c.upStreakMs).toBeCloseTo(hours(71.9), -2); // ended when DOWN began
  });

  it("flags an unbounded streak when the trail was truncated", () => {
    expect(answerClock("Healthy", [v("Healthy", ago(72))], NOW, false).atLeast).toBe(true);
    expect(answerClock("Healthy", [v("Healthy", ago(72))], NOW, true).atLeast).toBe(false);
    expect(answerClock("Healthy",
      [v("Healthy", ago(1)), v("LinkDown", ago(2)), v("Healthy", ago(72))], NOW, false).atLeast).toBe(false);
  });

  it("goes quiet while checking, and starts at now when the trail lags the live verdict", () => {
    expect(answerClock("Starting", [], NOW, true).sinceIso).toBeNull();
    const lagged = answerClock("LinkDown", [v("Healthy", ago(72))], NOW, true);
    expect(lagged.sinceIso).toBe(NOW);
    expect(lagged.upStreakMs).toBeNull();
  });
});
