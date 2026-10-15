// The DNS config page's lookup-name builder lives in a plain script
// (wwwroot/lib/dns-lookup-names.js, a global function). Load its text and
// evaluate it so the real file is what is tested.
import { readFileSync } from "fs";
import { resolve } from "path";
import { describe, expect, it } from "vitest";

type Entry = { name: string; expected?: string[] } | null;
type Builder = (existing: Entry[] | null | undefined, names: string[]) => string;

// __dirname, not import.meta.url: under happy-dom the module URL is not a file: URL.
const source = readFileSync(resolve(__dirname, "../../../wwwroot/lib/dns-lookup-names.js"), "utf-8");
const buildLookupNamesJson = new Function(`${source}\nreturn buildLookupNamesJson;`)() as Builder;

const build = (existing: Entry[] | null | undefined, names: string[]) =>
  JSON.parse(buildLookupNamesJson(existing, names)) as { name: string; expected?: string[] }[];

describe("buildLookupNamesJson", () => {
  it("keeps a padded existing entry's expected addresses, under its trimmed name", () => {
    const out = build([{ name: "  dns.google  ", expected: ["8.8.8.8", "8.8.4.4"] }], ["dns.google"]);
    expect(out).toEqual([{ name: "dns.google", expected: ["8.8.8.8", "8.8.4.4"] }]);
  });

  it("matches existing names case-insensitively", () => {
    const out = build([{ name: "dns.google", expected: ["8.8.8.8"] }], ["DNS.Google"]);
    expect(out).toEqual([{ name: "dns.google", expected: ["8.8.8.8"] }]);
  });

  it("skips null elements and entries without a string name", () => {
    const existing = [null, { name: 42 } as unknown as Entry, { expected: ["1.1.1.1"] } as unknown as Entry,
      { name: "router.lan" }];
    expect(build(existing, ["router.lan"])).toEqual([{ name: "router.lan" }]);
  });

  it("tolerates a missing existing list", () => {
    expect(build(undefined, ["a.lan"])).toEqual([{ name: "a.lan" }]);
  });

  it("turns a new name into a plain entry", () => {
    expect(build([], ["router.lan"])).toEqual([{ name: "router.lan" }]);
  });

  it("follows the order of the given names and drops names no longer given", () => {
    const existing = [
      { name: "dns.google", expected: ["8.8.8.8"] },
      { name: "one.one.one.one", expected: ["1.1.1.1"] },
      { name: "dns.quad9.net", expected: ["9.9.9.9"] },
    ];
    const out = build(existing, ["router.lan", "dns.quad9.net", "dns.google"]);
    expect(out).toEqual([
      { name: "router.lan" },
      { name: "dns.quad9.net", expected: ["9.9.9.9"] },
      { name: "dns.google", expected: ["8.8.8.8"] },
    ]);
  });
});
