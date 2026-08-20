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
  secondaryAxisId?: string;
}

export function TimeChart({ series, height, lossAxis }: {
  series: ChartSeries[];
  height?: number;
  lossAxis?: boolean; // adds a right-hand 0-100% axis with id "loss" (area)
}) {
  const primaryAxis = useMemo<AxisOptions<ChartPoint>>(
    () => ({ getValue: (d) => d.x, scaleType: "time" }),
    []
  );
  const secondaryAxes = useMemo<AxisOptions<ChartPoint>[]>(() => {
    const axes: AxisOptions<ChartPoint>[] = [
      { getValue: (d) => d.y, elementType: "line", min: 0 },
    ];
    if (lossAxis) {
      axes.push({
        id: "loss",
        getValue: (d) => d.y,
        elementType: "area",
        min: 0,
        max: 100,
        position: "right",
      });
    }
    return axes;
  }, [lossAxis]);

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
