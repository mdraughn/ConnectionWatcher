// Shared helpers for the config pages live in a plain script (wwwroot/lib/site-config.js).
// Load its text and run it so the real file is what is tested.
import { readFileSync } from "fs";
import { resolve } from "path";
import { afterEach, describe, expect, it, vi } from "vitest";

// __dirname, not import.meta.url: under happy-dom the module URL is not a file: URL.
const source = readFileSync(resolve(__dirname, "../../../wwwroot/lib/site-config.js"), "utf-8");
const load = () => new Function(`${source}\nreturn { apiFetch, withBusy };`)();

afterEach(() => vi.unstubAllGlobals());

describe("site-config.js", () => {
  it("apiFetch turns a network failure into a failed result", async () => {
    vi.stubGlobal("fetch", vi.fn(async () => { throw new TypeError("Failed to fetch"); }));
    const result = await load().apiFetch("PUT", "/api/v1/settings", {});
    expect(result.ok).toBe(false);
    expect(result.status).toBe(0);
    expect(result.body.errors[0]).toContain("Could not reach the server");
  });

  it("withBusy clears aria-busy when the call throws", async () => {
    const button = document.createElement("button");
    await expect(load().withBusy(button, async () => { throw new Error("boom"); })).rejects.toThrow("boom");
    expect(button.hasAttribute("aria-busy")).toBe(false);
  });

  it("withBusy is busy during the call and returns its result", async () => {
    const button = document.createElement("button");
    let during: string | null = null;
    const value = await load().withBusy(button, async () => { during = button.getAttribute("aria-busy"); return 42; });
    expect(during).toBe("true");
    expect(value).toBe(42);
    expect(button.hasAttribute("aria-busy")).toBe(false);
  });
});
