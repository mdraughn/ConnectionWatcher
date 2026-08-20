export function StatsRow({ uptime, incidentCount, longestMs, avgRtt }: {
  uptime: number | null;
  incidentCount: number;
  longestMs: number;
  avgRtt: number | null;
}) {
  const longest =
    longestMs <= 0 ? "—"
      : longestMs < 90_000 ? `${Math.round(longestMs / 1000)} s`
      : longestMs < 90 * 60_000 ? `${Math.round(longestMs / 60_000)} min`
      : `${(longestMs / 3_600_000).toFixed(1)} h`;
  return (
    <div className="cw-stats-row">
      <div className="cw-stat"><b>{uptime === null ? "—" : `${uptime.toFixed(2)}%`}</b>uptime</div>
      <div className="cw-stat"><b>{incidentCount}</b>incidents</div>
      <div className="cw-stat"><b>{longest}</b>longest outage</div>
      <div className="cw-stat"><b>{avgRtt === null ? "—" : `${avgRtt.toFixed(1)} ms`}</b>avg RTT</div>
    </div>
  );
}
