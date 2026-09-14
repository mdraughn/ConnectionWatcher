// Regenerates every .dc.html artboard from the shared token blocks and
// component helpers. Run from this directory:
//
//   node build-all.mjs
//
// The artboards are the source of truth. The published design canvas is a
// separate packaging step — see ../README.md.
import { chdir } from "node:process";
import { dirname } from "node:path";
import { fileURLToPath } from "node:url";

// The builders read tokens-*.txt and write *.dc.html relative to the working
// directory, so anchor it here rather than wherever node was invoked.
chdir(dirname(fileURLToPath(import.meta.url)));

await import("./build-dash.mjs");
await import("./build-history.mjs");
await import("./build-targets.mjs");
await import("./build-mobile.mjs");
await import("./build-system.mjs");
