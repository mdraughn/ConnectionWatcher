import { readFileSync } from "fs";
import { resolve } from "path";
import { describe, expect, it } from "vitest";

describe("site.css", () => {
  it("keeps the explicit and the OS-preference dark token blocks identical", () => {
    const css = readFileSync(resolve(__dirname, "../../../wwwroot/css/site.css"), "utf8");
    const explicit = /\[data-theme="dark"\]\s*\{([^}]*)\}/.exec(css)![1];
    const os = /:root:not\(\[data-theme="light"\]\):not\(\[data-theme="dark"\]\)\s*\{([^}]*)\}/.exec(css)![1];
    const norm = (s: string) => s.split(";").map((d) => d.trim()).filter(Boolean);
    expect(norm(os)).toEqual(norm(explicit));
  });
});
