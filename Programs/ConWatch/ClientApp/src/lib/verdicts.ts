// One table for everything the client knows about a verdict: its answer
// class, slab colour class and mark, ribbon fill, and incident window.
// Adding a verdict means adding one row here (the coverage test in
// verdicts.test.ts fails until you do).
import { HEALTHY, RAMP, ribbonFill, type Mark } from "./encoding";

export type AnswerClass = "up" | "struggling" | "down" | "checking";
export type VerdictName =
  "Healthy" | "SiteIncident" | "InternetWeather" | "LinkDegraded" | "LinkDown" | "DnsDown";

export interface VerdictLook {
  answer: Exclude<AnswerClass, "checking">;
  slabCls: "healthy" | "linkdegraded" | "linkdown";
  slabMark: Mark | null;
  /** The fill verdictFill gives this verdict's ribbon segments. */
  ribbon: string;
  /** The incident-window mark; null = no ledger/strip/punchcard window. */
  window: ((anchorDowns: number, threshold: number) => Mark) | null;
}

export const VERDICTS: Record<VerdictName, VerdictLook> = {
  Healthy: {
    answer: "up", slabCls: "healthy", slabMark: null,
    ribbon: HEALTHY, window: null,
  },
  SiteIncident: {
    answer: "up", slabCls: "healthy",
    slabMark: { shape: "dot", hue: "warm", depth: "glitch" },
    ribbon: RAMP.warm.glitch, window: null,
  },
  InternetWeather: {
    answer: "up", slabCls: "healthy",
    slabMark: { shape: "bar", hue: "warm", depth: "degraded" },
    ribbon: RAMP.warm.degraded,
    // Weather depth escalates to "out" when enough anchors were fully Down.
    window: (downs, threshold) => ({
      shape: "bar", hue: "warm", depth: downs >= threshold ? "out" : "degraded",
    }),
  },
  LinkDegraded: {
    answer: "struggling", slabCls: "linkdegraded",
    slabMark: { shape: "bar", hue: "grey", depth: "degraded" },
    ribbon: RAMP.grey.degraded,
    window: () => ({ shape: "bar", hue: "grey", depth: "degraded" }),
  },
  LinkDown: {
    answer: "down", slabCls: "linkdown",
    slabMark: { shape: "bar", hue: "grey", depth: "out" },
    ribbon: RAMP.grey.out,
    window: () => ({ shape: "bar", hue: "grey", depth: "out" }),
  },
  // Your resolver: your gear, name lookups out.
  DnsDown: {
    answer: "down", slabCls: "linkdown",
    slabMark: { shape: "bar", hue: "grey", depth: "out" },
    ribbon: RAMP.grey.out, window: null,
  },
};

/** The look for a verdict name, or null for Unknown / anything unrecognized. */
export function verdictLook(name: string | null | undefined): VerdictLook | null {
  return name != null && Object.hasOwn(VERDICTS, name) ? VERDICTS[name as VerdictName] : null;
}

export function answerClassOf(name: string | null | undefined): AnswerClass {
  return verdictLook(name)?.answer ?? "checking";
}

/** Ribbon fill for the VERDICT subject's timeline. The verdict subject logs
 *  HostStates with the verdict NAME alongside (segment evidence), and the name
 *  carries the attribution. Without a recognizable name (e.g. the residual
 *  segment before the first in-range transition) fall back to the plain state
 *  mapping. */
export function verdictFill(verdictName: string | null, state: string): string {
  return verdictLook(verdictName)?.ribbon ?? ribbonFill("other", state);
}
