import type { VerdictStatus } from "../lib/types";

const LABELS: Record<string, string> = {
  Healthy: "INTERNET: HEALTHY",
  SiteIncident: "INTERNET: SITE INCIDENT",
  InternetWeather: "INTERNET: WEATHER",
  LinkDegraded: "INTERNET: LINK DEGRADED",
  LinkDown: "INTERNET: LINK DOWN",
  Unknown: "INTERNET: MEASURING…",
};

export function VerdictBanner({ verdict, connected }: {
  verdict: VerdictStatus | null;
  connected: boolean;
}) {
  if (!verdict) {
    return (
      <div className={`verdict-banner unknown ${connected ? "" : "reconnecting"}`}>
        &#9675; VERDICT OFF — configure an anchor pool to enable internet-weather detection
        {!connected && <span className="pill">reconnecting…</span>}
      </div>
    );
  }
  const cls = verdict.state.toLowerCase();
  return (
    <div className={`verdict-banner ${cls} ${connected ? "" : "reconnecting"}`}>
      &#9679; {LABELS[verdict.state] ?? verdict.state}
      {verdict.detail ? <span style={{ fontWeight: 400 }}> — {verdict.detail}</span> : null}
      {!connected && <span className="pill">reconnecting…</span>}
    </div>
  );
}
