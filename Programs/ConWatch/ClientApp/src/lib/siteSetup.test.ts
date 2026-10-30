// The quick-setup wizard script (wwwroot/lib/site-setup.js) is a plain script; run its real text.
import { readFileSync } from "fs";
import { resolve } from "path";
import { afterEach, describe, expect, it } from "vitest";

const source = readFileSync(resolve(__dirname, "../../../wwwroot/lib/site-setup.js"), "utf-8");

const ids = ["gateway-list", "anchor-list", "resolver-list", "router-address", "router-name", "wan-probe",
  "wan-result", "apply", "errors", "isp-name", "clone-url", "clone-password", "clone-local", "clone-btn",
  "clone-insecure", "clone-insecure-anyway", "clone-no-secrets", "clone-errors", "clone-progress"];

const run = async (apiFetch: (method: string, url: string) => Promise<unknown>) => {
  document.body.innerHTML = ids.map((id) => `<div id="${id}" aria-busy="true"></div>`).join("");
  new Function("apiFetch", "withBusy", "clearErrors", "showErrors", source)(
    apiFetch, async (_b: unknown, f: () => unknown) => f(), () => {}, () => {});
  await new Promise((r) => setTimeout(r, 0));
};

afterEach(() => { document.body.innerHTML = ""; });

describe("site-setup.js", () => {
  it("a failed anchors load clears aria-busy and shows a message instead of throwing", async () => {
    await run(async (_m, url) => url.endsWith("/anchors")
      ? { ok: false, status: 500, body: { errors: ["boom"] } }
      : { ok: true, status: 200, body: [] });
    const list = document.getElementById("anchor-list")!;
    expect(list.hasAttribute("aria-busy")).toBe(false);
    expect(list.textContent).toContain("Could not load");
    expect(document.getElementById("resolver-list")!.querySelectorAll("input").length).toBe(1); // "I don't know"
  });
});
