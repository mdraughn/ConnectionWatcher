import { describe, expect, it } from "vitest";
import { renderToStaticMarkup } from "react-dom/server";
import { NameResolutionSection } from "./NameResolutionSection";
import type { DnsStatus } from "../lib/types";

const dns = (over: Partial<DnsStatus> = {}): DnsStatus => ({
  watched: true, state: "Ok", blame: null, lookupNames: [], upstreamResolvers: [],
  systemResolvers: [{ id: "a", address: "10.0.0.1", label: "Your resolver 10.0.0.1", state: "Responding", detail: null, enabled: true }],
  ...over,
});

describe("NameResolutionSection", () => {
  it("renders nothing without a dns block", () => {
    expect(renderToStaticMarkup(<NameResolutionSection dns={null} verdictState="Healthy" />)).toBe("");
  });
  it("shows the not-watched line and nothing else when the watch is off", () => {
    const html = renderToStaticMarkup(<NameResolutionSection dns={dns({ watched: false, state: "Unknown", systemResolvers: [] })} verdictState="Healthy" />);
    expect(html).toContain("Name resolution isn&#x27;t being watched.");
    expect(html).not.toContain("cw-tree");
  });
  it("lists resolvers with a chip, the detail only when not Responding, and troubled names only", () => {
    const html = renderToStaticMarkup(<NameResolutionSection dns={dns({
      state: "Degraded",
      systemResolvers: [
        { id: "a", address: "10.0.0.1", label: "Your resolver 10.0.0.1", state: "Responding", detail: null, enabled: true },
        { id: "b", address: "10.0.0.2", label: "Your resolver 10.0.0.2", state: "Down", detail: "TimedOut (dns.google)", enabled: true },
      ],
      upstreamResolvers: [{ id: "u", address: "1.1.1.1", label: "1.1.1.1 directly", state: "Responding", detail: null, enabled: true }],
      lookupNames: [
        { name: "dns.google", lastResult: "ok", detail: null },
        { name: "example.org", lastResult: "miss", detail: "ServFail (example.org)" },
      ],
    })} verdictState="Healthy" />);
    expect(html).toContain("one resolver isn&#x27;t answering");
    expect(html).toContain("Your resolver 10.0.0.1");
    expect(html).toContain("1.1.1.1 directly");
    expect(html).toContain("TimedOut (dns.google)");
    expect(html).toContain("example.org");
    expect(html).not.toContain(">dns.google<"); // a healthy name is not listed
    expect(html).toContain("Down");
  });
});
