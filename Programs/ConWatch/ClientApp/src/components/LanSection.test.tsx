import { describe, expect, it } from "vitest";
import { renderToStaticMarkup } from "react-dom/server";
import { LanSection } from "./LanSection";
import type { TargetStatus } from "../lib/types";

const t = (over: Partial<TargetStatus> = {}): TargetStatus => ({
  id: "n", name: "NAS", address: "203.0.113.9", state: "Responding",
  successRate: 1, avgRtt: 3, sampleSize: 10, pingCount: 4, scope: "Local", ...over,
});

describe("LanSection", () => {
  it("shows LED and success %, no state word, rtt or since-time", () => {
    const html = renderToStaticMarkup(<LanSection targets={[t()]} />);
    expect(html).toContain("cw-led");
    expect(html).toContain("100%");
    expect(html).toContain("1 of 1 up");
    expect(html).not.toContain("Responding");
    expect(html).not.toContain(" ms");
    expect(html).not.toContain("min");
  });
  it("renders nothing without LAN targets", () => {
    expect(renderToStaticMarkup(<LanSection targets={[t({ scope: "Verdict" })]} />)).toBe("");
  });
});
