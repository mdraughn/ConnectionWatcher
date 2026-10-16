// DNS config page (spec 2026-10-10 §5.3): writes through the existing
// admin-locked settings and targets endpoints. `window.dnsLookupNames` is the
// parsed stored list (seeded entries carry their expected addresses);
// buildLookupNamesJson comes from dns-lookup-names.js.

async function saveLookupNames(names) {
    const errors = document.getElementById("name-errors");
    clearErrors(errors);
    const result = await apiFetch("PUT", "/api/v1/settings", {
        "Dns.LookupNames": buildLookupNamesJson(window.dnsLookupNames ?? [], names),
    });
    if (result.ok) location.reload(); else showErrors(errors, result);
}

function currentNames() {
    return [...document.querySelectorAll("#name-list li")].map((li) => li.dataset.name);
}

// The Watch switch saves on change, like the resolver switches. A failed save puts the
// switch back where it was and shows why; a successful one reloads the page.
document.getElementById("dns-enabled").addEventListener("change", async (e) => {
    const errors = document.getElementById("watch-errors");
    clearErrors(errors);
    const hidden = document.querySelector("#watch-form [name='Dns.Enabled']");
    const previous = hidden.value;
    hidden.value = e.target.checked ? "true" : "false";
    const result = await apiFetch("PUT", "/api/v1/settings", { "Dns.Enabled": hidden.value });
    if (result.ok) { location.reload(); return; }
    hidden.value = previous;
    e.target.checked = previous === "true";
    showErrors(errors, result);
});
document.getElementById("watch-form").addEventListener("submit", (e) => e.preventDefault());

document.querySelectorAll("[data-toggle-resolver]").forEach((box) => box.addEventListener("change", async (e) => {
    const errors = document.getElementById("resolver-errors");
    clearErrors(errors);
    const row = e.target.closest("tr");
    const dto = JSON.parse(row.dataset.json);
    dto.enabled = e.target.checked;
    const result = await apiFetch("PUT", `/api/v1/targets/${row.dataset.resolverId}`, dto);
    if (result.ok) location.reload(); else { e.target.checked = !e.target.checked; showErrors(errors, result); }
}));

document.getElementById("upstream-form").addEventListener("submit", async (e) => {
    e.preventDefault();
    const errors = document.getElementById("upstream-errors");
    clearErrors(errors);
    const address = e.target.elements.address.value.trim();
    const name = e.target.elements.name.value.trim() || `${address} directly`;
    const result = await apiFetch("POST", "/api/v1/targets", {
        name, address, profile: "Resolver", probe: "dns", scope: "Resolver",
    });
    if (result.ok) location.reload(); else showErrors(errors, result);
});

document.querySelectorAll("[data-delete-resolver]").forEach((btn) => btn.addEventListener("click", async () => {
    if (!(await confirmDialog(`Remove ${btn.dataset.name}?`, { confirmLabel: "Remove" }))) return;
    const result = await apiFetch("DELETE", `/api/v1/targets/${btn.dataset.deleteResolver}`);
    if (result.ok) location.reload(); else showErrors(document.getElementById("upstream-errors"), result);
}));

document.getElementById("name-form").addEventListener("submit", (e) => {
    e.preventDefault();
    const name = e.target.elements.name.value.trim();
    if (!name) return;
    if (currentNames().some((n) => n.toLowerCase() === name.toLowerCase())) {
        showErrors(document.getElementById("name-errors"), { body: { errors: ["That name is already in the list."] } });
        return;
    }
    saveLookupNames([...currentNames(), name]);
});
document.querySelectorAll("[data-remove-name]").forEach((btn) => btn.addEventListener("click", () =>
    saveLookupNames(currentNames().filter((n) => n !== btn.dataset.removeName))));
