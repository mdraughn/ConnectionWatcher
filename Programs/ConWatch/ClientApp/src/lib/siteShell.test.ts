// The header pip on every non-dashboard page is drawn by a plain script
// (wwwroot/lib/site-shell.js). Load its text and run it against a happy-dom
// page so the real file is what is tested. Every VerdictState from the server
// vocabulary must give the pip the colour of the dashboard's answer class.
import { readFileSync } from "fs";
import { resolve } from "path";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { answerClassOf, type AnswerClass } from "./verdictClock";

// __dirname, not import.meta.url: under happy-dom the module URL is not a file: URL.
const source = readFileSync(resolve(__dirname, "../../../wwwroot/lib/site-shell.js"), "utf-8");
const vocabulary = JSON.parse(readFileSync(
  resolve(__dirname, "../../../../../Tests/contracts/vocabulary.json"), "utf-8")) as Record<string, string[]>;

const colour: Record<AnswerClass, string> = {
  up: "var(--cw-ok)", struggling: "var(--cw-warn)", down: "var(--cw-down)", checking: "var(--cw-unreach)",
};
const defaultWord: Record<AnswerClass, string> = {
  up: "Up", struggling: "Struggling", down: "Down", checking: "Checking",
};
// Ruling D2: during DnsDown the header says the more specific "DNS down" (same red).
const wordOverride: Record<string, string> = { DnsDown: "DNS down" };

async function flush() {
  for (let i = 0; i < 10; i++) await Promise.resolve();
}

async function runShell(state: string) {
  document.body.innerHTML = `<div id="cw-pip" hidden><span class="dot"></span><span class="label"></span></div>`;
  vi.stubGlobal("fetch", vi.fn(async () => ({
    ok: true, json: async () => ({ enabled: true, verdict: { state }, warnings: [] }),
  })));
  new Function(source)();
  await flush();
  const pip = document.getElementById("cw-pip")!;
  return {
    background: (pip.querySelector(".dot") as HTMLElement).style.background,
    label: pip.querySelector(".label")!.textContent,
  };
}

describe("site-shell.js verdict pip", () => {
  beforeEach(() => {
    // The script polls on a 30 s interval; keep it from outliving a case.
    vi.spyOn(globalThis, "setInterval").mockImplementation((() => 0) as unknown as typeof setInterval);
  });
  afterEach(() => {
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
    document.body.innerHTML = "";
  });

  it.each(vocabulary.VerdictState)("%s draws the colour and word of its answer class", async (state) => {
    const cls = answerClassOf(state);
    const shown = await runShell(state);
    expect(shown.background).toBe(colour[cls]);
    expect(shown.label).toBe(wordOverride[state] ?? defaultWord[cls]);
  });
});
