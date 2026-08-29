// Administration page logic (plain JS, no build step).
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

function wireSettingsForm(formId, errorsId) {
    document.getElementById(formId).addEventListener("submit", async (e) => {
        e.preventDefault();
        const errors = document.getElementById(errorsId);
        clearErrors(errors);
        const values = {};
        for (const field of e.target.querySelectorAll("[name]"))
            values[field.name] = field.value === "" ? null : field.value;
        const result = await apiFetch("PUT", "/api/v1/settings", values);
        if (result.ok) location.reload();
        else showErrors(errors, result);
    });
}
wireSettingsForm("settings-form", "settings-errors");
wireSettingsForm("notify-form", "notify-errors");

document.getElementById("mqtt-test-btn").addEventListener("click", async () => {
    const form = document.getElementById("notify-form");
    const value = (name) => form.querySelector(`[name="Mqtt.${name}"]`).value;
    const out = document.getElementById("mqtt-test-result");
    out.textContent = "testing…";
    const result = await apiFetch("POST", "/api/v1/admin/mqtt-test", {
        host: value("Host") || null,
        port: value("Port") ? Number(value("Port")) : null,
        username: value("Username") || null,
        password: value("Password") || null,
    });
    if (result.ok && result.body.ok) out.textContent = `✓ ${result.body.message}`;
    else if (result.ok) out.textContent = `✗ ${result.body.message}`;
    else out.textContent = `✗ ${(result.body?.errors ?? ["request failed"]).join("; ")}`;
});

function wireDanger(inputId, buttonId, word, url) {
    const input = document.getElementById(inputId);
    const button = document.getElementById(buttonId);
    input.addEventListener("input", () => { button.disabled = input.value !== word; });
    button.addEventListener("click", async () => {
        const errors = document.getElementById("danger-errors");
        clearErrors(errors);
        const result = await apiFetch("POST", url, { confirm: input.value });
        if (result.ok) location.href = "/";
        else showErrors(errors, result);
    });
}

wireDanger("clear-confirm", "clear-btn", "CLEAR", "/api/v1/admin/clear-history");
wireDanger("reset-confirm", "reset-btn", "RESET", "/api/v1/admin/factory-reset");
loadStats();
