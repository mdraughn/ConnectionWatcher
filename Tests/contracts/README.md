# Contract fixtures

Golden files that pin wire formats. The `*.keys.json` files hold the key set of every JSON object the dashboard reads, as `{ "<object path>": ["key", ...] }`, with arrays of objects written as `path[]` (for example `status.dns.lookupNames[]`). `Tests/ConWatch.Tests/ContractShapeTests.cs` seeds a fully populated server (a system and an upstream resolver, DnsDown with its blame, a held model warning, a glitch and a minute rollup), walks the live JSON of `/api/v1/status`, `/api/v1/transitions`, `/api/v1/glitches`, `/api/v1/rollups/{id}` and the Index page's `initial-data` island, and compares it with these files. `Programs/ConWatch/ClientApp/src/lib/contracts.test.ts` checks the TypeScript key lists against the same files, and types each list with `satisfies` so `tsc` fails when `types.ts` drifts. `vocabulary.json` (enum names) and `config-export.keys.json` are checked the same way. A mismatch fails and prints the new content. Nothing rewrites a fixture unless the tests run with `UPDATE_CONTRACTS=1`, and a regenerated fixture must then be reviewed against the client.

## Client and server differences

Each line is a known difference between `ClientApp/src/lib/types.ts` (or the named client type) and what the server sends. `contracts.test.ts` lists the same exceptions, so a new difference fails until it is added here.

// AUDIT: status.targets[].operator: the server sends it; TargetStatus does not declare it (the dashboard does not show a target's operator).
// AUDIT: transition[].ancestorId: the server sends it; TransitionEvent does not declare it.
// AUDIT: transition[].avgRtt: the server sends it; TransitionEvent does not declare it.
// AUDIT: transitions.nextBefore: the server sends the paging cursor; the client never reads it (it asks for one page with a limit).
// AUDIT: glitch[].scope: the server sends it; GlitchItem (types.ts) does not declare it.
// AUDIT: rollups.resolution: the server sends it; history.tsx reads only `points`.
// AUDIT: live transitions (SignalR "transition") carry `dns`, the DnsDown blame, on a DnsDown verdict row; TransitionEvent does not declare it. History rows never carry it, so no fixture holds it.
// AUDIT: ModelWarning.kind, DnsBlame.kind, DnsStatus.state and DnsLookupNameStatus.lastResult end in `| string`, which widens each union to string; a `satisfies` against them cannot catch a missing literal. The ModelWarningKind check in contracts.test.ts relies on set equality with vocabulary.json instead.

Not counted as differences: the client marks several keys optional (`Snapshot.enabled`, `warnings`, `dns`; most of TargetStatus's later fields; `TransitionEvent.id` and `scope`) that the server's REST answers always send. Live SignalR transitions omit `id` and `scope`, which is why those two are optional.
