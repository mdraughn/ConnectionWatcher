import { describe, expect, it } from "vitest";
import { renderToStaticMarkup } from "react-dom/server";
import { Punchcard } from "./Punchcard";
import { punchcardCells } from "../lib/incidents";

describe("Punchcard", () => {
  it("draws the cell punchcardCells filled", () => {
    const now = new Date(2026, 9, 5, 12, 0);
    const g = {
      id: 1, targetId: "t", targetName: "Site", class: "Target" as const,
      startUtc: new Date(2026, 9, 5, 0, 10).toISOString(),
      endUtc: new Date(2026, 9, 5, 0, 11).toISOString(),
      lostCount: 2, sampleCount: 10, detail: null,
    };
    const cells = punchcardCells([], new Map(), [g], new Set(), 30, now);
    const html = renderToStaticMarkup(<Punchcard cells={cells} days={30} now={now} />);
    const titles = [...html.matchAll(/title="([^"]*)"/g)].map((m) => m[1]);
    expect(titles).toHaveLength(1);
    expect(titles[0]).toMatch(/^10\/5 0:00 — one site: glitch/);
  });
});
