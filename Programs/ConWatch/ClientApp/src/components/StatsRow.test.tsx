import { describe, expect, it } from "vitest";
import { renderToStaticMarkup } from "react-dom/server";
import { StatsRow } from "./StatsRow";

const textOf = (html: string) => html.replace(/<[^>]*>/g, "");

describe("StatsRow", () => {
  it("writes durations in prose and a dash for none", () => {
    const text = textOf(renderToStaticMarkup(
      <StatsRow uptime={99.5} outageCount={1} longestOutageMs={100_000}
        unreachableMs={0} avgRtt={12} />));
    expect(text).toContain("longest 100 s");
    expect(text).toContain("—unreachable — no data");
  });
});
