// HA1: the client's view of the server JSON, checked against the golden key
// sets the .NET ContractShapeTests write (Tests/contracts/*.keys.json).
// Each key list is typed with `satisfies` against the type the dashboard reads
// through, plus a Covers<> check for the other direction, so tsc fails when
// types.ts drifts from the list and this test fails when the server drifts
// from the fixture. Known differences are listed per path and mirrored as
// `// AUDIT` lines in Tests/contracts/README.md.
import { readFileSync } from "fs";
import { resolve } from "path";
import { describe, expect, it } from "vitest";
import type { GlitchItem } from "./glitchData";
import type {
  DnsBlame, DnsLookupNameStatus, DnsResolverStatus, DnsStatus, ModelWarning, PoolStatus,
  RollupPoint, Snapshot, TargetScope, TargetStatus, TransitionEvent, VerdictStatus,
} from "./types";
import { VERDICT_SUBJECT_ID } from "./verdictSubject";
import { answerClassOf, type AnswerClass } from "./verdictClock";

// __dirname, not import.meta.url: under happy-dom the module URL is not a file: URL.
const contracts = resolve(__dirname, "../../../../../Tests/contracts");
const fixture = (name: string) =>
  JSON.parse(readFileSync(resolve(contracts, name), "utf-8")) as Record<string, string[]>;

/** Compile time: the list names every key of T (`satisfies` proves it names nothing else). */
type Covers<T, L extends readonly PropertyKey[]> =
  [Exclude<keyof T, L[number]>] extends [never] ? true : { missingFromList: Exclude<keyof T, L[number]> };

const snapshotKeys = ["enabled", "verdict", "targets", "pools", "warnings", "dns"] as const satisfies readonly (keyof Snapshot)[];
const verdictKeys = ["state", "detail", "operator"] as const satisfies readonly (keyof VerdictStatus)[];
const targetKeys = ["id", "name", "address", "probe", "state", "assumed", "successRate", "avgRtt", "sampleSize",
  "pingCount", "scope", "critical", "systemManaged", "explainedBy"] as const satisfies readonly (keyof TargetStatus)[];
const poolKeys = ["id", "name", "state"] as const satisfies readonly (keyof PoolStatus)[];
const warningKeys = ["key", "kind", "targetId", "targetName", "message", "since", "acknowledged"] as const satisfies readonly (keyof ModelWarning)[];
const dnsKeys = ["watched", "state", "systemResolvers", "upstreamResolvers", "blame", "lookupNames"] as const satisfies readonly (keyof DnsStatus)[];
const blameKeys = ["kind", "message", "resolvers"] as const satisfies readonly (keyof DnsBlame)[];
const resolverKeys = ["id", "address", "label", "state", "detail", "enabled"] as const satisfies readonly (keyof DnsResolverStatus)[];
const lookupNameKeys = ["name", "lastResult", "detail"] as const satisfies readonly (keyof DnsLookupNameStatus)[];
const transitionKeys = ["id", "timestamp", "subjectId", "subjectName", "subjectKind", "from", "to", "trigger", "detail",
  "verdict", "successRate", "sampleSize", "burstLossRate", "scope", "opening"] as const satisfies readonly (keyof TransitionEvent)[];
const glitchKeys = ["id", "targetId", "targetName", "class", "startUtc", "endUtc", "lostCount", "sampleCount",
  "detail"] as const satisfies readonly (keyof GlitchItem)[];
const rollupKeys = ["periodStart", "pingCount", "successCount", "rttAvg", "rttMin", "rttMax"] as const satisfies readonly (keyof RollupPoint)[];

// The page wrappers have no client type: the client reads one key inline (`.items`, `.points`).
const transitionsPageKeys = ["items"] as const;
const glitchesPageKeys = ["items"] as const;
const rollupsPageKeys = ["points"] as const;
// AUDIT (README): InitialData is module-private in dashboard.tsx, so this list cannot be typed against it.
const islandKeys = ["snapshot", "parents", "poolMembers", "trunkNames", "anchorNames", "weatherThreshold",
  "engineDisabled", "cloneSource", "admin"] as const;

// Referenced so tsc evaluates every Covers<> (an unsatisfied one is not assignable from `true`).
const covered: true[] = [
  true satisfies Covers<Snapshot, typeof snapshotKeys>,
  true satisfies Covers<VerdictStatus, typeof verdictKeys>,
  true satisfies Covers<TargetStatus, typeof targetKeys>,
  true satisfies Covers<PoolStatus, typeof poolKeys>,
  true satisfies Covers<ModelWarning, typeof warningKeys>,
  true satisfies Covers<DnsStatus, typeof dnsKeys>,
  true satisfies Covers<DnsBlame, typeof blameKeys>,
  true satisfies Covers<DnsResolverStatus, typeof resolverKeys>,
  true satisfies Covers<DnsLookupNameStatus, typeof lookupNameKeys>,
  true satisfies Covers<TransitionEvent, typeof transitionKeys>,
  true satisfies Covers<GlitchItem, typeof glitchKeys>,
  true satisfies Covers<RollupPoint, typeof rollupKeys>,
];

interface Audit {
  /** Keys the server sends that the client type does not declare. */
  serverOnly?: readonly string[];
  /** Keys the client type declares that the server does not send here. */
  clientOnly?: readonly string[];
}

/** The fixture's key set is the client's list, adjusted by the audited differences. */
function expectContract(file: string, path: string, clientKeys: readonly string[], audit: Audit = {}) {
  const serverKeys = fixture(file)[path];
  expect(serverKeys, `${file} has no path ${path}`).toBeDefined();
  for (const k of audit.serverOnly ?? []) expect(clientKeys, `${path}: ${k} is audited server-only`).not.toContain(k);
  for (const k of audit.clientOnly ?? []) expect(clientKeys, `${path}: ${k} is audited client-only`).toContain(k);
  const expected = [...clientKeys.filter((k) => !(audit.clientOnly ?? []).includes(k)), ...(audit.serverOnly ?? [])];
  expect([...serverKeys].sort(), `${file} ${path}`).toEqual([...new Set(expected)].sort());
}

/** Every path in a fixture is checked by some case below: a new server object fails until it is covered. */
function expectPaths(file: string, paths: string[]) {
  expect(Object.keys(fixture(file)).sort()).toEqual([...paths].sort());
}

describe("server JSON contracts", () => {
  it("covers every Covers<> check", () => expect(covered.every((c) => c)).toBe(true));

  it("status matches the Snapshot types", () => {
    const f = "status.keys.json";
    expectPaths(f, ["status", "status.verdict", "status.targets[]", "status.pools[]", "status.warnings[]", "status.dns",
      "status.dns.blame", "status.dns.systemResolvers[]", "status.dns.upstreamResolvers[]", "status.dns.lookupNames[]"]);
    expectContract(f, "status", snapshotKeys);
    expectContract(f, "status.verdict", verdictKeys);
    expectContract(f, "status.targets[]", targetKeys, { serverOnly: ["operator"] });
    expectContract(f, "status.pools[]", poolKeys);
    expectContract(f, "status.warnings[]", warningKeys);
    expectContract(f, "status.dns", dnsKeys);
    expectContract(f, "status.dns.blame", blameKeys);
    expectContract(f, "status.dns.systemResolvers[]", resolverKeys);
    expectContract(f, "status.dns.upstreamResolvers[]", resolverKeys);
    expectContract(f, "status.dns.lookupNames[]", lookupNameKeys);
  });

  it("transitions match TransitionEvent", () => {
    const f = "transition.keys.json";
    expectPaths(f, ["transitions", "transition[]", "transition.opening[]"]);
    expectContract(f, "transitions", transitionsPageKeys, { serverOnly: ["nextBefore"] });
    // `opening` is sent only with ?opening=true, so the plain rows never carry it.
    expectContract(f, "transition[]", transitionKeys, { serverOnly: ["ancestorId", "avgRtt"], clientOnly: ["opening"] });
    // ?opening=true rows: the same keys plus the marker.
    expectContract(f, "transition.opening[]", transitionKeys, { serverOnly: ["ancestorId", "avgRtt"] });
  });

  it("glitches match GlitchItem", () => {
    const f = "glitch.keys.json";
    expectPaths(f, ["glitches", "glitch[]"]);
    expectContract(f, "glitches", glitchesPageKeys);
    expectContract(f, "glitch[]", glitchKeys, { serverOnly: ["scope"] });
  });

  it("rollups match RollupPoint", () => {
    const f = "rollup.keys.json";
    expectPaths(f, ["rollups", "rollup[]"]);
    expectContract(f, "rollups", rollupsPageKeys, { serverOnly: ["resolution"] });
    expectContract(f, "rollup[]", rollupKeys);
  });

  it("the Index island matches InitialData", () => {
    const f = "index-island.keys.json";
    expectPaths(f, ["index.island"]);
    expectContract(f, "index.island", islandKeys);
  });
});

describe("server vocabulary", () => {
  const vocabulary = fixture("vocabulary.json");

  it("the dashboard's verdict trail fetches the id the server gives the verdict", () => {
    const frozen = JSON.parse(readFileSync(resolve(contracts, "vocabulary.json"), "utf-8")) as { VerdictSubjectId: string };
    expect(VERDICT_SUBJECT_ID).toBe(frozen.VerdictSubjectId);
  });

  it("every VerdictState has an answer class, and only Unknown is still checking", () => {
    const expected = {
      Healthy: "up", SiteIncident: "up", InternetWeather: "up",
      LinkDegraded: "struggling", LinkDown: "down", DnsDown: "down", Unknown: "checking",
    } as const satisfies Record<string, AnswerClass>;
    expect([...vocabulary.VerdictState].sort()).toEqual(Object.keys(expected).sort());
    for (const state of vocabulary.VerdictState)
      expect(answerClassOf(state), state).toBe(expected[state as keyof typeof expected]);
  });

  it("every ModelWarningKind is a ModelWarning kind the client names", () => {
    // AUDIT (README): ModelWarning.kind ends in `| string`, which widens the union to string,
    // so this `satisfies` cannot catch a missing literal; the set equality below does.
    const kinds = ["HopBypassed", "NeverAnswered", "UnprovableUp", "ProbeOnlyLoss", "ResolverPartial", "ResolverLying",
      "ResolverFiltered", "UpstreamResolverDown", "NameUnresolvable"] as const satisfies readonly ModelWarning["kind"][];
    expect([...vocabulary.ModelWarningKind].sort()).toEqual([...kinds].sort());
  });

  it("TargetScope is the server's vocabulary", () => {
    const targetScopes = ["Local", "Resolver", "Verdict"] as const satisfies readonly TargetScope[];
    const all: true = true satisfies Covers<Record<TargetScope, 0>, typeof targetScopes>;
    expect(all).toBe(true);
    expect([...targetScopes].sort()).toEqual([...vocabulary.TargetScope].sort());
  });

  it("every GlitchClass is in GlitchItem.class", () => {
    const classes = ["Target", "Internet"] as const satisfies readonly GlitchItem["class"][];
    const all: true = true satisfies Covers<Record<GlitchItem["class"], 0>, typeof classes>;
    expect(all).toBe(true);
    const glitchClass = vocabulary.GlitchClass as unknown as Record<string, number>;
    expect(Object.keys(glitchClass).sort()).toEqual([...classes].sort());
  });
});
