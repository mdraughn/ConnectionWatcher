import { describe, expect, it } from "vitest";
import { COOLDOWN_MS, initialRow, isExpanded, onStateChange, tick, togglePin } from "./expansion";

describe("expansion rule C", () => {
  it("fresh-boot Starting stays collapsed", () => {
    let row = initialRow();
    row = onStateChange(row, "Starting", 0);
    expect(isExpanded(row)).toBe(false);
  });

  it("Faltering expands", () => {
    let row = initialRow();
    row = onStateChange(row, "Responding", 0);
    row = onStateChange(row, "Faltering", 1000);
    expect(isExpanded(row)).toBe(true);
  });

  it("Starting after leaving the starting family expands (release path)", () => {
    let row = initialRow();
    row = onStateChange(row, "Starting", 0);
    row = onStateChange(row, "Responding", 1000);   // left starting family
    row = onStateChange(row, "Unreachable", 2000);  // parent down
    expect(isExpanded(row)).toBe(true);
    row = onStateChange(row, "Starting", 3000);     // released, re-proving
    expect(isExpanded(row)).toBe(true);
  });

  it("recovery holds through cooldown then collapses on tick", () => {
    let row = initialRow();
    row = onStateChange(row, "Responding", 0);
    row = onStateChange(row, "Down", 1000);
    row = onStateChange(row, "Responding", 2000);
    expect(isExpanded(row)).toBe(true);
    row = tick(row, 2000 + COOLDOWN_MS - 1);
    expect(isExpanded(row)).toBe(true);
    row = tick(row, 2000 + COOLDOWN_MS + 1);
    expect(isExpanded(row)).toBe(false);
  });

  it("re-degrading during cooldown clears the cooldown", () => {
    let row = initialRow();
    row = onStateChange(row, "Responding", 0);
    row = onStateChange(row, "Down", 1000);
    row = onStateChange(row, "Responding", 2000);
    row = onStateChange(row, "Faltering", 3000);
    row = tick(row, 3000 + COOLDOWN_MS * 2);
    expect(isExpanded(row)).toBe(true); // still bad, cooldown irrelevant
  });

  it("pin forces expansion and survives ticks; unpin collapses a quiet row", () => {
    let row = initialRow();
    row = onStateChange(row, "Responding", 0);
    expect(isExpanded(row)).toBe(false);
    row = togglePin(row);
    expect(isExpanded(row)).toBe(true);
    row = tick(row, COOLDOWN_MS * 10);
    expect(isExpanded(row)).toBe(true);
    row = togglePin(row);
    row = tick(row, COOLDOWN_MS * 10 + 1);
    expect(isExpanded(row)).toBe(false);
  });
});
