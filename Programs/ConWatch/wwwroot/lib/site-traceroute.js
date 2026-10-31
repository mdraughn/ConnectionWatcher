// Traceroute assist page logic (plain JS, no build step).
"use strict";

async function runTraceroute() {
    const host = document.getElementById("host").value.trim();
    const button = document.getElementById("run");
    const errors = document.getElementById("errors");
    const tbody = document.querySelector("#hops tbody");
    clearErrors(errors);
    tbody.innerHTML = "";
    button.disabled = true;

    try {
        await withBusy(button, async () => {
            // Existing targets, keyed by address, for "you are here" + parent defaults.
            const targets = await apiFetch("GET", "/api/v1/targets");
            if (!targets.ok) { showErrors(errors, targets); return; }
            const byAddress = new Map(targets.body.map((t) => [t.address, t]));

            const trace = await apiFetch("POST", "/api/v1/traceroute", { host });
            if (!trace.ok) { showErrors(errors, trace); return; }
            const body = trace.body;

            const profileFor = { Internal: "Internal", Path: "Path", Destination: "Public" };
            document.getElementById("operator-head").hidden = !body.asnLookupEnabled;

            let nearestKnownParent = null;
            for (const hop of body.hops) {
                const known = hop.address ? byAddress.get(hop.address) : undefined;
                const row = document.createElement("tr");

                const addCell = document.createElement("td");
                if (known) {
                    addCell.textContent = `already monitored as '${known.name}'`;
                } else if (hop.address) {
                    const link = document.createElement("a");
                    link.setAttribute("role", "button");
                    link.className = "outline";
                    const name = hop.reachedDestination ? host : `hop-${hop.ttl}`;
                    const params = new URLSearchParams({ address: hop.address, name });
                    if (nearestKnownParent) params.set("parent", nearestKnownParent);
                    if (hop.kind) params.set("profile", profileFor[hop.kind]);
                    if (hop.asName) params.set("operator", hop.asName);
                    link.href = `/Config/Targets?${params}`;
                    link.textContent = "Add as target";
                    addCell.appendChild(link);
                }

                const cells = [
                    hop.ttl,
                    hop.address ?? "*",
                    hop.hostname ?? "",
                    hop.rttMs != null ? hop.rttMs + " ms" : "—",
                    hop.kind ?? "",
                ];
                if (body.asnLookupEnabled) cells.push(hop.asName ?? "");
                const kindIndex = 4;
                for (const [i, text] of cells.entries()) {
                    const td = document.createElement("td");
                    td.textContent = String(text);
                    if (i === kindIndex) td.className = "cw-muted";
                    row.appendChild(td);
                }
                row.appendChild(addCell);
                tbody.appendChild(row);

                if (known) nearestKnownParent = known.name;
            }
        });
    } catch (e) {
        showErrors(errors, { body: { errors: [`Traceroute failed: ${e}`] } });
    } finally {
        button.disabled = false;
    }
}

document.getElementById("trace-form").addEventListener("submit", (e) => {
    e.preventDefault();
    runTraceroute();
});
