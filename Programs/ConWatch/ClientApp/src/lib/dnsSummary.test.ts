import { describe, expect, it } from "vitest";
import { dnsHeadline, troubledNames } from "./dnsSummary";
import type { DnsStatus } from "./types";

const dns = (over: Partial<DnsStatus> = {}): DnsStatus => ({
  watched: true, state: "Ok", systemResolvers: [], upstreamResolvers: [], blame: null, lookupNames: [], ...over,
});

describe("dnsHeadline", () => {
  it("words each state for a person", () => {
    expect(dnsHeadline(null, "Healthy")).toBeNull();
    expect(dnsHeadline(dns({ watched: false, state: "Unknown" }), "Healthy")).toBe("isn't being watched");
    expect(dnsHeadline(dns({ state: "Unknown" }), "Healthy")).toBe("checking");
    expect(dnsHeadline(dns(), "Healthy")).toBe("working");
    expect(dnsHeadline(dns({ state: "Filtered" }), "Healthy")).toBe("working, but your resolver blocks the check names");
    expect(dnsHeadline(dns({ state: "Degraded" }), "Healthy")).toBe("one resolver isn't answering");
    expect(dnsHeadline(dns({ state: "Down" }), "DnsDown")).toBe("not working");
    expect(dnsHeadline(dns({ state: "Down" }), "Healthy")).toBe("re-checking");
    expect(dnsHeadline(dns({ state: "Down" }), "LinkDown")).toBe("re-checking");
  });
});

describe("troubledNames", () => {
  it("lists only names with a miss or a filtered result", () => {
    const d = dns({ lookupNames: [
      { name: "dns.google", lastResult: "ok", detail: null },
      { name: "one.one.one.one", lastResult: "filtered", detail: "Filtered: 0.0.0.0 (one.one.one.one)" },
      { name: "example.org", lastResult: "miss", detail: "ServFail (example.org)" },
      { name: "never.test", lastResult: "none", detail: null },
    ] });
    expect(troubledNames(d).map((n) => n.name)).toEqual(["one.one.one.one", "example.org"]);
  });
});
