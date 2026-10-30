import { describe, expect, it } from "vitest";
import { isLocal, isResolver, isTreeTarget, summarizeLan } from "./scope";
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

describe("isResolver", () => {
  it("is true only for scope Resolver", () => {
    expect(isResolver(t({ scope: "Resolver" }))).toBe(true);
    expect(isResolver(t({ scope: "Local" }))).toBe(false);
    expect(isResolver(t({}))).toBe(false);
  });

  it("a resolver is not a LAN device", () => {
    expect(isLocal(t({ scope: "Resolver" }))).toBe(false);
    expect(summarizeLan([t({ scope: "Resolver" })]).total).toBe(0);
  });
});

describe("isTreeTarget", () => {
  it("is the server's IsTree(): Verdict and a missing scope only", () => {
    expect(isTreeTarget(t({ scope: "Verdict" }))).toBe(true);
    expect(isTreeTarget(t({}))).toBe(true);
    expect(isTreeTarget(t({ scope: "Local" }))).toBe(false);
    expect(isTreeTarget(t({ scope: "Resolver" }))).toBe(false);
  });
});
