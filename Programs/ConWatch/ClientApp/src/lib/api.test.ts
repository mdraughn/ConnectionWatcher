import { afterEach, describe, expect, it, vi } from "vitest";
import {
  acknowledgeWarning, fetchGlitches, fetchRollups, fetchSnapshot, fetchTransitions,
  fetchTransitionsNewestFirst, transitionsUrl,
} from "./api";

afterEach(() => vi.unstubAllGlobals());

function stub(items: unknown[]) {
  vi.stubGlobal("fetch", vi.fn(async () => ({ ok: true, json: async () => ({ items }) }) as Response));
}

describe("api", () => {
  it("builds the transitions URL in a fixed key order", () => {
    expect(transitionsUrl({ subjectId: "s", from: new Date(Date.UTC(2026, 9, 29, 10)), limit: 1000, opening: true }))
      .toBe("/api/v1/transitions?subjectId=s&from=2026-10-29T10%3A00%3A00.000Z&limit=1000&opening=true");
    expect(transitionsUrl({ limit: 50, scope: "internet" })).toBe("/api/v1/transitions?limit=50&scope=internet");
  });

  it("fetchTransitions puts opening rows first, then in-range rows oldest first", async () => {
    stub([{ id: 2, opening: true }, { id: 1, opening: true }, { id: 5 }, { id: 4 }]);
    expect((await fetchTransitions({ limit: 10 })).map((t) => t.id)).toEqual([1, 2, 4, 5]);
  });

  it("fetchTransitionsNewestFirst keeps the API order", async () => {
    stub([{ id: 5 }, { id: 4 }]);
    expect((await fetchTransitionsNewestFirst({ limit: 10 }))?.map((t) => t.id)).toEqual([5, 4]);
  });

  it("fetchTransitions resolves [] when fetch rejects", async () => {
    vi.stubGlobal("fetch", vi.fn(async () => { throw new TypeError("network"); }));
    await expect(fetchTransitions({ limit: 10 })).resolves.toEqual([]);
    await expect(fetchTransitionsNewestFirst({ limit: 10 })).resolves.toBeNull();
    await expect(fetchGlitches({ from: new Date(), limit: 10 })).resolves.toEqual([]);
    await expect(fetchRollups("s", new Date(), new Date())).resolves.toEqual([]);
    await expect(fetchSnapshot()).resolves.toBeNull();
    await expect(acknowledgeWarning("k")).resolves.toBe(false);
  });

  it("a 500 is the same as no data", async () => {
    vi.stubGlobal("fetch", vi.fn(async () => ({ ok: false, status: 500, json: async () => ({}) }) as Response));
    await expect(fetchTransitions({ limit: 10 })).resolves.toEqual([]);
    await expect(fetchTransitionsNewestFirst({ limit: 10 })).resolves.toBeNull();
    await expect(fetchGlitches({ from: new Date(), limit: 10 })).resolves.toEqual([]);
    await expect(fetchRollups("s", new Date(), new Date())).resolves.toEqual([]);
    await expect(fetchSnapshot()).resolves.toBeNull();
    await expect(acknowledgeWarning("k")).resolves.toBe(false);
  });

  it("an unreadable or wrongly shaped body is no data", async () => {
    vi.stubGlobal("fetch", vi.fn(async () => ({ ok: true, json: async () => { throw new SyntaxError("bad"); } }) as unknown as Response));
    await expect(fetchSnapshot()).resolves.toBeNull();
    await expect(fetchTransitions({ limit: 10 })).resolves.toEqual([]);
    vi.stubGlobal("fetch", vi.fn(async () => ({ ok: true, json: async () => ({}) }) as Response));
    await expect(fetchGlitches({ from: new Date(), limit: 10 })).resolves.toEqual([]);
    await expect(fetchRollups("s", new Date(), new Date())).resolves.toEqual([]);
  });
});
