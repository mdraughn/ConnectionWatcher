// Pure helpers for the "Name resolution" section (unit-tested).
import type { DnsLookupNameStatus, DnsStatus } from "./types";

/** The heading's parenthetical, in a person's words (spec 2026-10-10 §5.2; ruling 2026-10-15 for Down-while-healthy). */
export function dnsHeadline(dns: DnsStatus | null, verdictState: string | null): string | null {
  if (!dns) return null;
  if (!dns.watched) return "isn't being watched";
  switch (dns.state) {
    case "Ok": return "working";
    case "Filtered": return "working, but your resolver blocks the check names";
    case "Degraded": return "one resolver isn't answering";
    case "Down": return verdictState === "DnsDown" ? "not working" : "re-checking";
    default: return "checking";
  }
}

/** Lookup names worth a line: a miss or a block. Healthy and never-asked names stay quiet. */
export function troubledNames(dns: DnsStatus): DnsLookupNameStatus[] {
  return dns.lookupNames.filter((n) => n.lastResult === "miss" || n.lastResult === "filtered");
}
