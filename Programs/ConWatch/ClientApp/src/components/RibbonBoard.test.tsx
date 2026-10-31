import { afterEach, describe, expect, it, vi } from "vitest";
import { act } from "react";
import { createRoot } from "react-dom/client";
import { RibbonBoard, type BoardSubject } from "./RibbonBoard";
import { RAMP } from "../lib/encoding";

(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;

const T0 = Date.UTC(2026, 9, 5, 0, 0);
const at = (min: number) => new Date(T0 + min * 60_000).toISOString();
const tr = (min: number, from: string, to: string) => ({
  id: min, timestamp: at(min), subjectId: "s1", subjectName: "Example", subjectKind: "target",
  from, to, trigger: "Ping", detail: null, verdict: null,
  successRate: null, sampleSize: null, burstLossRate: null,
});

afterEach(() => vi.unstubAllGlobals());

describe("RibbonBoard", () => {
  it("draws a subject's segments with the shared hatch and its own title format", async () => {
    // newest first, as the API sends them
    const items = [tr(70, "Unreachable", "Down"), tr(40, "Responding", "Unreachable")];
    vi.stubGlobal("fetch", vi.fn(async () => ({ ok: true, json: async () => ({ items }) }) as Response));
    const subjects: BoardSubject[] = [
      { id: "s1", name: "Example", kind: "target", parent: null, role: "other" }];
    const host = document.createElement("div");
    document.body.appendChild(host);
    const root = createRoot(host);
    await act(async () => {
      root.render(<RibbonBoard subjects={subjects} rangeStart={new Date(T0)}
        rangeEnd={new Date(T0 + 100 * 60_000)} />);
    });
    await vi.waitFor(() => expect(host.querySelectorAll("rect[fill]").length).toBeGreaterThan(0));
    const rects = [...host.querySelectorAll("rect[fill]")].filter((r) => r.querySelector("title"));
    expect(rects.map((r) => r.getAttribute("fill")))
      .toEqual(["var(--healthy)", "url(#cw-hatch)", RAMP.warm.out]);
    expect(rects.map((r) => r.getAttribute("opacity") ?? "1")).toEqual(["1", "1", "1"]);
    expect(host.querySelector("pattern#cw-hatch")).not.toBeNull();
    const titles = rects.map((r) => r.querySelector("title")!.textContent!.split("\n")[0]);
    expect(titles[0]).toBe("Example — Responding");
    expect(titles[1]).toBe("Example — Unreachable behind a down parent (no data, no blame)");
    await act(async () => root.unmount());
    host.remove();
  });
});
