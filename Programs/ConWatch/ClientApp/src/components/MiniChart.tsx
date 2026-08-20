// 60-minute latency/loss chart for an expanded dashboard tile.
import { useEffect, useState } from "react";
import type { RollupPoint } from "../lib/types";
import { TimeChart } from "./TimeChart";

async function fetchLastHour(targetId: string): Promise<RollupPoint[]> {
  const to = new Date();
  const from = new Date(to.getTime() - 60 * 60_000);
  const url = `/api/v1/rollups/${targetId}?from=${from.toISOString()}&to=${to.toISOString()}&resolution=minute`;
  const response = await fetch(url);
  if (!response.ok) return [];
  const body = await response.json();
  return body.points as RollupPoint[];
}

export function MiniChart({ targetId }: { targetId: string }) {
  const [points, setPoints] = useState<RollupPoint[]>([]);
  const [error, setError] = useState(false);

  useEffect(() => {
    let live = true;
    const load = () =>
      fetchLastHour(targetId)
        .then((p) => { if (live) { setPoints(p); setError(false); } })
        .catch(() => { if (live) setError(true); });
    load();
    const timer = setInterval(load, 60_000);
    return () => { live = false; clearInterval(timer); };
  }, [targetId]);

  if (error) return <p style={{ fontSize: "0.8rem" }}>chart unavailable</p>;

  const latency = points.map((p) => ({ x: new Date(p.periodStart), y: p.rttAvg }));
  const loss = points.map((p) => ({
    x: new Date(p.periodStart),
    y: p.pingCount > 0 ? (100 * (p.pingCount - p.successCount)) / p.pingCount : null,
  }));
  return (
    <div>
      <div className="chart-box">
        <TimeChart
          series={[
            { label: "RTT ms", data: latency },
            { label: "loss %", data: loss, secondaryAxisId: "loss" },
          ]}
          lossAxis
        />
      </div>
      <div className="axis"><span>-60 min</span><span>latency / loss</span><span>now</span></div>
    </div>
  );
}
