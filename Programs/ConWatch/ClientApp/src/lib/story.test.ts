// HA12: the story's weather mark counts DISTINCT anchors that went Down inside
// a verdict window, not Down rows. One anchor flapping twice is still one
// site's trouble (the window stays "degraded" weather); two different anchors
// meet the weather threshold and the window reads as the internet going out.
import { afterEach, describe, expect, it, vi } from "vitest";
import { fetchStory } from "./story";
import type { TransitionEvent } from "./types";

const MINUTE = 60_000;
/** The server's encoding: an ISO instant at offset zero written as +00:00. */
const at = (ms: number) => new Date(ms).toISOString().replace("Z", "+00:00");

function row(t: number, subjectKind: string, subjectName: string, from: string, to: string,
  verdict: string | null = null): TransitionEvent {
  return {
    timestamp: at(t), subjectId: `${subjectName}-id`, subjectName, subjectKind, from, to,
    trigger: subjectKind === "Verdict" ? "VerdictChanged" : "ThresholdCrossed",
    detail: null, verdict, successRate: null, sampleSize: null, burstLossRate: null,
  };
}

/** A weather window an hour ago, ten minutes long, with the two given anchor Downs inside it. */
function weatherWindow(firstDown: string, secondDown: string): TransitionEvent[] {
  const t0 = Date.now() - 60 * MINUTE;
  const chronological = [
    row(t0, "Verdict", "Internet", "Healthy", "Degraded", "InternetWeather"),
    row(t0 + 1 * MINUTE, "Target", firstDown, "Responding", "Down"),
    row(t0 + 2 * MINUTE, "Target", firstDown, "Down", "Responding"),
    row(t0 + 3 * MINUTE, "Target", secondDown, "Responding", "Down"),
    row(t0 + 4 * MINUTE, "Target", secondDown, "Down", "Responding"),
    row(t0 + 10 * MINUTE, "Verdict", "Internet", "Degraded", "Healthy", "Healthy"),
  ];
  return chronological.reverse(); // the API answers newest first
}

function stubApi(transitions: TransitionEvent[]) {
  vi.stubGlobal("fetch", vi.fn(async (url: string) => {
    const items = url.startsWith("/api/v1/transitions") ? transitions : [];
    return { ok: true, json: async () => ({ items }) } as Response;
  }));
}

const topology = { anchorNames: ["Google", "Cloudflare"], trunkNames: ["Router"], weatherThreshold: 2 };

describe("fetchStory", () => {
  afterEach(() => vi.unstubAllGlobals());

  it("one anchor going down twice stays one site's trouble: degraded weather", async () => {
    stubApi(weatherWindow("Google", "Google"));
    const story = await fetchStory(topology, 7);
    expect(story.ledger).toHaveLength(1);
    expect(story.ledger[0].mark).toMatchObject({ shape: "bar", hue: "warm", depth: "degraded" });
    expect(story.ledger[0].lead).toMatch(/^Internet weather for /);
  });

  it("two distinct anchors going down meet the weather threshold: the internet went out", async () => {
    stubApi(weatherWindow("Google", "Cloudflare"));
    const story = await fetchStory(topology, 7);
    expect(story.ledger).toHaveLength(1);
    expect(story.ledger[0].lead).toMatch(/^The internet went out for .* not your line\.$/);
    expect(story.ledger[0].mark).toMatchObject({ shape: "bar", hue: "warm", depth: "out" });
  });
});
