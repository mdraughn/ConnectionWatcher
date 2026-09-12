// Data page logic (plain JS, no build step).
"use strict";

async function loadStats() {
    const el = document.getElementById("db-stats");
    const result = await apiFetch("GET", "/api/v1/admin/stats");
    el.removeAttribute("aria-busy");
    if (!result.ok) {
        el.textContent = "stats unavailable";
        return;
    }
    const s = result.body;
    const mb = (s.fileSizeBytes / 1048576).toFixed(2);
    const stat = (value, label) => `<div class="cw-stat"><b>${value}</b>${label}</div>`;
    el.innerHTML =
        stat(`${mb} MB`, "database size") +
        stat(s.tables.targets, "targets") +
        stat(s.tables.transitions.toLocaleString(), "transitions") +
        stat(s.tables.rollups.toLocaleString(), "rollup rows") +
        stat(s.oldestTransitionUtc ? new Date(s.oldestTransitionUtc).toLocaleDateString() : "—", "oldest data") +
        stat(s.lastRetentionRunUtc ? new Date(s.lastRetentionRunUtc).toLocaleString() : "—", "last retention run");
}

document.getElementById("retention-form").addEventListener("submit", async (e) => {
    e.preventDefault();
    const errors = document.getElementById("retention-errors");
    clearErrors(errors);
    const values = {};
    for (const field of e.target.querySelectorAll("[name]"))
        values[field.name] = field.value === "" ? null : field.value;
    const result = await apiFetch("PUT", "/api/v1/settings", values);
    if (result.ok) location.reload();
    else showErrors(errors, result);
});

loadStats();
