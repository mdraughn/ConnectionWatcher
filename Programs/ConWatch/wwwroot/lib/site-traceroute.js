// Traceroute assist page logic (plain JS, no build step).
"use strict";

async function runTraceroute() {
    const host = document.getElementById("host").value.trim();
    const button = document.getElementById("run");
    const errors = document.getElementById("errors");
    const tbody = document.querySelector("#hops tbody");
    errors.innerHTML = "";
    tbody.innerHTML = "";
    button.setAttribute("aria-busy", "true");
    button.disabled = true;

    try {
        // Existing targets, keyed by address, for "you are here" + parent defaults.
        const targets = await (await fetch("/api/v1/targets")).json();
        const byAddress = new Map(targets.map((t) => [t.address, t]));

        const response = await fetch("/api/v1/traceroute", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ host }),
        });
        const body = await response.json();
        if (!response.ok) {
            for (const message of body.errors ?? ["Traceroute failed"]) {
                const li = document.createElement("li");
                li.textContent = message;
                errors.appendChild(li);
            }
            return;
        }

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
                link.href = `/Config/Targets?${params}`;
                link.textContent = "Add as target";
                addCell.appendChild(link);
            }

            row.innerHTML =
                `<td>${hop.ttl}</td>` +
                `<td>${hop.address ?? "*"}</td>` +
                `<td>${hop.hostname ?? ""}</td>` +
                `<td>${hop.rttMs != null ? hop.rttMs + " ms" : "—"}</td>`;
            row.appendChild(addCell);
            tbody.appendChild(row);

            if (known) nearestKnownParent = known.name;
        }
    } catch (e) {
        const li = document.createElement("li");
        li.textContent = `Traceroute failed: ${e}`;
        errors.appendChild(li);
    } finally {
        button.removeAttribute("aria-busy");
        button.disabled = false;
    }
}

document.getElementById("trace-form").addEventListener("submit", (e) => {
    e.preventDefault();
    runTraceroute();
});
