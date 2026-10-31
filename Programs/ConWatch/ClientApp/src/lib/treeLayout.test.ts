import { describe, expect, it } from "vitest";
import { groupByParent, layoutTree } from "./treeLayout";
import type { TargetStatus } from "./types";

const t = (id: string, name: string): TargetStatus =>
  ({ id, name, address: null, state: "Responding", successRate: 1, avgRtt: 5, sampleSize: 20 });

// Mark's real home: Router -> {Business Router (off-path), External Router ->
// Comcast Edge -> 4 anchors}.
const targets = [
  t("r", "Router"), t("b", "Business Router"), t("x", "External Router"),
  t("c", "Comcast Edge"), t("a1", "Cloudflare DNS"), t("a2", "Google DNS"),
  t("a3", "OpenDNS"), t("a4", "Quad9 DNS"),
];
const parents: Record<string, string | null> = {
  r: null, b: "r", x: "r", c: "x", a1: "c", a2: "c", a3: "c", a4: "c",
};
const trunkNames = ["Comcast Edge", "External Router", "Router"]; // server order is leaf-first
const anchorNames = ["Cloudflare DNS", "Google DNS", "OpenDNS", "Quad9 DNS"];
const topo = { parents, poolMembers: { p1: ["a1", "a2", "a3", "a4"] } };

describe("layoutTree", () => {
  it("splits into path and internet sections with the trunk flush left", () => {
    const l = layoutTree(topo, targets, trunkNames, anchorNames);
    expect(l.mode).toBe("trunk");
    const path = l.rows.filter((r) => r.section === "path");
    const net = l.rows.filter((r) => r.section === "internet");
    expect(path.map((r) => r.target.name)).toEqual(
      ["Router", "Business Router", "External Router", "Comcast Edge"]);
    expect(net.map((r) => r.target.name)).toEqual(anchorNames);
    // Trunk rows carry no rails (flush left); the branch gets one elbow cell.
    expect(path.filter((r) => r.kind === "trunk").every((r) => r.rails.length === 0)).toBe(true);
    expect(path.find((r) => r.target.name === "Business Router")!.rails).toEqual(["elbow"]);
    // Anchors are flat.
    expect(net.every((r) => r.rails.length === 0)).toBe(true);
  });

  it("draws the spine from the first trunk row to the last, passing through branches", () => {
    const l = layoutTree(topo, targets, trunkNames, anchorNames);
    const byName = new Map(l.rows.map((r) => [r.target.name, r]));
    expect(byName.get("Router")!.spine).toBe("bottom");
    expect(byName.get("Business Router")!.spine).toBe("pass");
    expect(byName.get("External Router")!.spine).toBe("both");
    expect(byName.get("Comcast Edge")!.spine).toBe("top");
    expect(byName.get("Cloudflare DNS")!.spine).toBe("none");
  });

  it("falls back to plain mode without trunk/anchor knowledge", () => {
    expect(layoutTree(topo, targets, undefined, undefined).mode).toBe("plain");
    expect(layoutTree(topo, targets, [], []).mode).toBe("plain");
  });

  it("renders deeper off-path subtrees with tee/pipe rails like a file tree", () => {
    const deep = [...targets, t("cam", "Camera"), t("nvr", "NVR")];
    const deepParents = { ...parents, cam: "b", nvr: "b" };
    const l = layoutTree({ ...topo, parents: deepParents }, deep, trunkNames, anchorNames);
    const path = l.rows.filter((r) => r.section === "path");
    expect(path.map((r) => r.target.name)).toEqual(
      ["Router", "Business Router", "Camera", "NVR", "External Router", "Comcast Edge"]);
    const cam = path.find((r) => r.target.name === "Camera")!;
    const nvr = path.find((r) => r.target.name === "NVR")!;
    // Business Router still elbows off the spine; its children carry a blank
    // continuation cell (their parent was the last branch at that level).
    expect(cam.rails).toEqual(["blank", "tee"]);
    expect(nvr.rails).toEqual(["blank", "elbow"]);
    // Everything between Router and Comcast Edge lets the spine pass through.
    expect(cam.spine).toBe("pass");
  });

  it("keeps targets hung under an anchor in the internet section, railed", () => {
    const extra = [...targets, t("svc", "Some Service")];
    const extraParents = { ...parents, svc: "a2" };
    const l = layoutTree({ ...topo, parents: extraParents }, extra, trunkNames, anchorNames);
    const net = l.rows.filter((r) => r.section === "internet");
    expect(net.map((r) => r.target.name)).toEqual(
      ["Cloudflare DNS", "Google DNS", "Some Service", "OpenDNS", "Quad9 DNS"]);
    expect(net.find((r) => r.target.name === "Some Service")!.rails).toEqual(["elbow"]);
  });

  it("puts unrelated subtrees in the path section after the trunk, without spine", () => {
    const extra = [...targets, t("z", "Lab Switch"), t("z2", "Lab Host")];
    const extraParents = { ...parents, z: null, z2: "z" };
    const l = layoutTree({ ...topo, parents: extraParents }, extra, trunkNames, anchorNames);
    const path = l.rows.filter((r) => r.section === "path");
    expect(path.map((r) => r.target.name).slice(-2)).toEqual(["Lab Switch", "Lab Host"]);
    expect(path.find((r) => r.target.name === "Lab Switch")!.spine).toBe("none");
    expect(path.find((r) => r.target.name === "Lab Host")!.rails).toEqual(["elbow"]);
  });
});

describe("groupByParent", () => {
  it("adopts orphans as roots in input order", () => {
    const a = t("a", "a"), b = t("b", "b"), c = t("c", "c");
    const g = groupByParent([a, b, c], { a: "missing", b: null, c: "b" });
    expect(g.get(null)!.map((x) => x.id)).toEqual(["a", "b"]);
    expect(g.get("b")!.map((x) => x.id)).toEqual(["c"]);
  });
});
