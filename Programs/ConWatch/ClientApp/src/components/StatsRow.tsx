function duration(ms: number): string {
  if (ms <= 0) return "—";
  if (ms < 90_000) return `${Math.round(ms / 1000)} s`;
  if (ms < 90 * 60_000) return `${Math.round(ms / 60_000)} min`;
  return `${(ms / 3_600_000).toFixed(1)} h`;
}

export function StatsRow({ uptime, outageCount, longestOutageMs, unreachableMs, avgRtt }: {
  uptime: number | null;
  outageCount: number;
  longestOutageMs: number;
  unreachableMs: number;
  avgRtt: number | null;
}) {
  return (
    <div className="cw-stats-row">
      <div className="cw-stat"><b>{uptime === null ? "—" : `${uptime.toFixed(2)}%`}</b>uptime (measured)</div>
      <div className="cw-stat">
        <b>{outageCount}</b>outages{outageCount > 0 ? ` · longest ${duration(longestOutageMs)}` : ""}
      </div>
      <div className="cw-stat"><b>{duration(unreachableMs)}</b>unreachable — no data</div>
      <div className="cw-stat"><b>{avgRtt === null ? "—" : `${avgRtt.toFixed(1)} ms`}</b>avg RTT</div>
    </div>
  );
}
