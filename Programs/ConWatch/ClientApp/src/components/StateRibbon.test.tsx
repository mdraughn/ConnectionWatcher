import { describe, expect, it } from "vitest";
import { renderToStaticMarkup } from "react-dom/server";
import { StateRibbon } from "./StateRibbon";
import type { RibbonSegment } from "../lib/historyData";

const T0 = Date.UTC(2026, 9, 5, 0, 0);
const at = (min: number) => new Date(T0 + min * 60_000).toISOString();
const segments: RibbonSegment[] = [
  { state: "Responding", from: at(0), to: at(40), evidence: null },
  { state: "Unreachable", from: at(40), to: at(70), evidence: null },
  { state: "Down", from: at(70), to: at(100), evidence: "no replies" },
];

function rectsOf(html: string) {
  const doc = new DOMParser().parseFromString(`<body>${html}</body>`, "text/html");
  return [...doc.querySelectorAll("rect")].filter((r) => r.hasAttribute("opacity"));
}

describe("StateRibbon", () => {
  const html = renderToStaticMarkup(
    <StateRibbon segments={segments} rangeStart={new Date(T0)} rangeEnd={new Date(T0 + 100 * 60_000)}
      resetAt={at(10)} kind="target" role="trunk" />);
  const rects = rectsOf(html);
  const titles = rects.map((r) => r.querySelector("title")!.textContent!.split("\n")[0]);

  it("draws one rect per segment at its share of the range", () => {
    expect(rects.map((r) => [r.getAttribute("x"), r.getAttribute("width")]))
      .toEqual([["0", "40"], ["40", "30"], ["70", "30"]]);
  });
  it("fills by state: healthy, hatch pattern, grey out", () => {
    expect(rects.map((r) => r.getAttribute("fill")))
      .toEqual(["var(--healthy)", "url(#cw-hatch-timeline)", "var(--grey-out)"]);
    expect(html).toContain('id="cw-hatch-timeline"');
  });
  it("dims only the segment that starts before the reset", () => {
    expect(rects.map((r) => r.getAttribute("opacity"))).toEqual(["0.35", "1", "1"]);
  });
  it("titles each segment, naming Unreachable in full", () => {
    expect(titles[0]).toMatch(/^Responding: /);
    expect(titles[1]).toMatch(/^Unreachable behind a down parent \(no data, no blame\): /);
    expect(titles[2]).toMatch(/^Down: /);
    expect(rects[0].querySelector("title")!.textContent).toContain("(before address change");
    expect(rects[2].querySelector("title")!.textContent).toContain("\nno replies");
  });
});
