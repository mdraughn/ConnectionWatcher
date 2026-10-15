// The DNS page's lookup-name list builder, kept in its own plain script so a
// Node test (ClientApp/src/lib/dnsLookupNames.test.ts) can load and check it.

/** Keeps every existing entry still named (so a seeded name keeps its expected
 *  addresses), appends plain entries for new names, in the given order.
 *  Existing entries that are null or lack a string name are skipped; names
 *  match case-insensitively and ignoring surrounding spaces. */
function buildLookupNamesJson(existing, names) {
    const byName = new Map();
    for (const e of existing ?? []) {
        if (e && typeof e.name === "string" && e.name.trim())
            byName.set(e.name.trim().toLowerCase(), e);
    }
    return JSON.stringify(names.map((n) => {
        const found = byName.get(n.trim().toLowerCase());
        return found ? { ...found, name: found.name.trim() } : { name: n };
    }));
}
