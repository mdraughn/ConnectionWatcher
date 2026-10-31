// "Name resolution": the host's resolvers and the check names, beside the LAN
// section (spec 2026-10-10 §5.2, D5). It never colours the verdict or the tray.
import { dnsHeadline, troubledNames } from "../lib/dnsSummary";
import { StateChip } from "./TargetRow";
import type { DnsResolverStatus, DnsStatus } from "../lib/types";

function ResolverRow({ r }: { r: DnsResolverStatus }) {
  return (
    <li>
      <div className="cw-row">
        <span className={`dot ${r.state.toLowerCase()}`}></span>
        <span>{r.label}</span>
        <span className="addr">{r.address}</span>
        <span className="stats">
          <StateChip state={r.state} always />
          {r.state !== "Responding" && r.detail && <span className="sep"> · </span>}
          {r.state !== "Responding" && r.detail && <small className="detail">{r.detail}</small>}
        </span>
      </div>
    </li>
  );
}

export function NameResolutionSection({ dns, verdictState }: {
  dns: DnsStatus | null; verdictState: string | null;
}) {
  if (!dns) return null;
  const headline = dnsHeadline(dns, verdictState);
  if (!dns.watched) {
    return (
      <div id="dns-section" style={{ marginTop: "1.5rem" }}>
        <h4>Name resolution <small>({headline})</small></h4>
        <p>Name resolution isn't being watched.</p>
      </div>
    );
  }
  const names = troubledNames(dns);
  return (
    <div id="dns-section" style={{ marginTop: "1.5rem" }}>
      <h4>Name resolution <small>({headline})</small></h4>
      <ul className="cw-tree">
        {dns.systemResolvers.map((r) => <ResolverRow key={r.id} r={r} />)}
        {dns.upstreamResolvers.map((r) => <ResolverRow key={r.id} r={r} />)}
      </ul>
      {names.length > 0 && (
        <ul className="cw-names">
          {names.map((n) => (
            <li key={n.name}>
              <span>{n.name}</span>
              <small className="detail"> — {n.lastResult === "filtered" ? "blocked by your resolver" : "not resolving"}
                {n.detail ? ` (${n.detail})` : ""}</small>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
