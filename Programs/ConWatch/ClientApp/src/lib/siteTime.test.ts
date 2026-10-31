// Local-time rendering for the plain-JS pages lives in wwwroot/lib/site-time.js.
// Load its text and run it so the real file is what is tested.
import { readFileSync } from "fs";
import { resolve } from "path";
import { describe, expect, it } from "vitest";

// __dirname, not import.meta.url: under happy-dom the module URL is not a file: URL.
const source = readFileSync(resolve(__dirname, "../../../wwwroot/lib/site-time.js"), "utf-8");
new Function(source)();
const localizeTimes = (globalThis as any).localizeTimes as
  (selector: string, attribute: string, options?: Intl.DateTimeFormatOptions) => number;

describe("site-time.js", () => {
  it("localizes each valid instant and leaves a bad one alone", () => {
    document.body.innerHTML = `<small data-utc="2026-10-29T10:00:00.0000000Z">u1</small><small data-utc="nope">u2</small>`;
    const n = localizeTimes("[data-utc]", "data-utc");
    expect(n).toBe(1);
    expect(document.body.children[0].textContent).toBe(new Date("2026-10-29T10:00:00Z").toLocaleString());
    expect(document.body.children[1].textContent).toBe("u2");
  });

  it("passes the page's format options through", () => {
    document.body.innerHTML = `<time datetime="2026-10-29T15:04:05Z">x</time>`;
    const options: Intl.DateTimeFormatOptions = { hour12: false, hour: "2-digit", minute: "2-digit" };
    expect(localizeTimes("time", "datetime", options)).toBe(1);
    expect(document.body.children[0].textContent)
      .toBe(new Date("2026-10-29T15:04:05Z").toLocaleString(undefined, options));
  });
});
