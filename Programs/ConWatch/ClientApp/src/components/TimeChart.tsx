// Shared chart wrapper: the TanStack (react-charts) integration lives ONLY
// here, so a library swap (sanctioned fallback: uPlot) touches one file.
import { useMemo } from "react";
import { AxisOptions, Chart } from "react-charts";

export interface ChartPoint {
  x: Date;
  y: number | null;
}

export interface ChartSeries {
  label: string;
  data: ChartPoint[];
}

export function TimeChart({ series, height }: {
  series: ChartSeries[];
  height?: number;
}) {
  const primaryAxis = useMemo<AxisOptions<ChartPoint>>(
    () => ({ getValue: (d) => d.x, scaleType: "time" }),
    []
  );
  const secondaryAxes = useMemo<AxisOptions<ChartPoint>[]>(() => {
    return [{ getValue: (d) => d.y, elementType: "line", min: 0 }];
  }, []);

  const nonEmpty = series.filter((s) => s.data.length > 0);
  if (nonEmpty.length === 0) {
    return <p style={{ fontSize: "0.8rem", opacity: 0.6 }}>no data yet</p>;
  }
  return (
    <div style={{ height: height ?? 70 }}>
      <Chart options={{ data: nonEmpty, primaryAxis, secondaryAxes, tooltip: { show: false } }} />
    </div>
  );
}
