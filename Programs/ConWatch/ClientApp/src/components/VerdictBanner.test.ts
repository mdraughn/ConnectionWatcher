import { describe, expect, it } from "vitest";
import { slabFor } from "./VerdictBanner";

const v = (state: string, operator: string | null = null) =>
  ({ state, detail: "d", operator });

describe("slabFor", () => {
  it("answers UP for healthy, site incidents, AND internet weather", () => {
    expect(slabFor(v("Healthy")).headline).toContain("UP");
    expect(slabFor(v("SiteIncident")).headline).toContain("UP");
    expect(slabFor(v("InternetWeather")).headline).toContain("UP");
    for (const s of ["Healthy", "SiteIncident", "InternetWeather"])
      expect(slabFor(v(s)).cls).toBe("healthy"); // the slab stays green
  });

  it("keeps the caveats jargon-free", () => {
    for (const s of ["Healthy", "SiteIncident", "InternetWeather", "LinkDegraded", "LinkDown"]) {
      const caveat = slabFor(v(s)).caveat ?? "";
      expect(caveat).not.toMatch(/anchor|probe|target|verdict/i);
    }
  });

  it("marks whose gear the caveat is about", () => {
    expect(slabFor(v("InternetWeather")).mark).toEqual(
      { shape: "bar", hue: "warm", depth: "degraded" });
    expect(slabFor(v("LinkDegraded")).mark).toEqual(
      { shape: "bar", hue: "grey", depth: "degraded" });
    expect(slabFor(v("LinkDown")).mark).toEqual(
      { shape: "bar", hue: "grey", depth: "out" });
  });

  it("struggling and down are the only non-green slabs", () => {
    expect(slabFor(v("LinkDegraded"))).toMatchObject(
      { cls: "linkdegraded", headline: "Your internet is STRUGGLING" });
    expect(slabFor(v("LinkDown"))).toMatchObject(
      { cls: "linkdown", headline: "Your internet is DOWN" });
  });

  it("names the ISP when the down verdict blames one", () => {
    expect(slabFor(v("LinkDown", "Comcast")).caveat).toBe("Comcast is not carrying your traffic");
    expect(slabFor(v("LinkDown")).caveat).toContain("your connection");
  });
});
