import { readFileSync } from "fs";
import { resolve } from "path";
import { describe, expect, it } from "vitest";
import { answerClassOf, VERDICTS, verdictLook } from "./verdicts";

const vocabulary = JSON.parse(readFileSync(
  resolve(__dirname, "../../../../../Tests/contracts/vocabulary.json"), "utf-8")) as Record<string, string[]>;

describe("verdict table", () => {
  it("covers every server VerdictState except Unknown", () => {
    expect(Object.keys(VERDICTS).sort()).toEqual(vocabulary.VerdictState.filter((s) => s !== "Unknown").sort());
  });
  it("an unknown name has no look and reads as checking", () => {
    expect(verdictLook("Unknown")).toBeNull();
    expect(verdictLook("toString")).toBeNull();
    expect(answerClassOf(undefined)).toBe("checking");
  });
});
