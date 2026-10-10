import { describe, expect, it } from "vitest";
import { renderToStaticMarkup } from "react-dom/server";
import { TargetRow } from "./TargetRow";
import type { TargetStatus } from "../lib/types";

const t = (over: Partial<TargetStatus> = {}): TargetStatus => ({
  id: "x", name: "Router", address: "203.0.113.9", state: "Down",
  successRate: 0.5, avgRtt: 12, sampleSize: 10, pingCount: 7, ...over,
});

describe("TargetRow", () => {
  it("by default shows chip, pct, rtt and LED", () => {
    const html = renderToStaticMarkup(<TargetRow target={t()} />);
    expect(html).toContain("statechip");
    expect(html).toContain(">Down<");
    expect(html).toContain("50%");
    expect(html).toContain("12 ms");
    expect(html).toContain("cw-led");
  });
  it("LAN options drop chip and rtt, keep pct and LED", () => {
    const html = renderToStaticMarkup(
      <TargetRow target={t()} show={{ stateChip: false, rtt: false }} />);
    expect(html).not.toContain("statechip");
    expect(html).not.toContain("Down</");
    expect(html).not.toContain(" ms");
    expect(html).toContain("50%");
    expect(html).toContain("cw-led");
  });
  it("keeps the assumed suffix", () => {
    const html = renderToStaticMarkup(<TargetRow target={t({ assumed: true })} />);
    expect(html).toContain("(assumed)");
  });
  it("marks a critical target and always shows its state chip", () => {
    const html = renderToStaticMarkup(
      <TargetRow target={t({ critical: true })} show={{ stateChip: false, rtt: false }} />);
    expect(html).toContain("cw-critical");
    expect(html).toContain(">critical<");
    expect(html).toContain("statechip"); // the LAN section hides chips, but not for critical
  });
  it("leaves non-critical rows unmarked", () => {
    expect(renderToStaticMarkup(<TargetRow target={t()} />)).not.toContain("cw-critical");
  });
});
