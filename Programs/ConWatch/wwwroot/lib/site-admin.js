// Administration page logic (plain JS, no build step).
// Database stats, exports, and retention moved to the Data page (site-data.js).
"use strict";

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

const passwordForm = document.getElementById("password-form");
if (passwordForm) {
    passwordForm.addEventListener("submit", async (e) => {
        e.preventDefault();
        const errors = document.getElementById("password-errors");
        clearErrors(errors);
        const f = new FormData(passwordForm);
        if (f.get("password") !== f.get("confirm")) {
            showErrors(errors, { body: { errors: ["Passwords do not match."] } });
            return;
        }
        const result = await apiFetch("POST", "/api/v1/auth/set-password",
            { current: f.get("current") || null, password: f.get("password") });
        if (result.ok) location.reload();
        else showErrors(errors, result.status === 401
            ? { body: { errors: ["Current password is wrong."] } } : result);
    });
    document.getElementById("password-remove-btn")?.addEventListener("click", async () => {
        const errors = document.getElementById("password-errors");
        clearErrors(errors);
        const current = new FormData(passwordForm).get("current");
        const result = await apiFetch("POST", "/api/v1/auth/remove-password", { current });
        if (result.ok) location.reload();
        else showErrors(errors, { body: { errors: ["Enter the current password above, then click Remove."] } });
    });
}

wireDanger("clear-confirm", "clear-btn", "CLEAR", "/api/v1/admin/clear-history");
wireDanger("reset-confirm", "reset-btn", "RESET", "/api/v1/admin/factory-reset");

// Prune-by-date: the confirm-word pattern plus a cutoff date and an inline
// result (rows removed, space returned) instead of a redirect.
{
    const input = document.getElementById("prune-confirm");
    const button = document.getElementById("prune-btn");
    input.addEventListener("input", () => { button.disabled = input.value !== "PRUNE"; });
    button.addEventListener("click", async () => {
        const errors = document.getElementById("danger-errors");
        const out = document.getElementById("prune-result");
        clearErrors(errors);
        const date = document.getElementById("prune-date").value;
        if (!date) {
            showErrors(errors, { body: { errors: ["Pick a cutoff date first."] } });
            return;
        }
        button.setAttribute("aria-busy", "true");
        const result = await apiFetch("POST", "/api/v1/admin/prune",
            { beforeUtc: date + "T00:00:00Z", confirm: input.value });
        button.removeAttribute("aria-busy");
        if (!result.ok) { showErrors(errors, result); return; }
        const r = result.body;
        const freed = Math.max(0, r.fileSizeBefore - r.fileSizeAfter) / 1048576;
        out.textContent = `Removed ${r.transitions.toLocaleString()} transitions, ` +
            `${r.rollups.toLocaleString()} rollup rows, ${r.glitches.toLocaleString()} glitches; ` +
            `freed ${freed.toFixed(1)} MB.`;
        input.value = "";
        button.disabled = true;
    });
}

// Monitoring toggle (master enable switch).
const engineToggle = document.getElementById("engine-toggle-btn");
if (engineToggle) {
    engineToggle.addEventListener("click", async () => {
        const errors = document.getElementById("engine-errors");
        clearErrors(errors);
        engineToggle.setAttribute("aria-busy", "true");
        const result = await apiFetch("PUT", "/api/v1/settings", {
            "Engine.Enabled": engineToggle.dataset.enable,
        });
        engineToggle.removeAttribute("aria-busy");
        if (result.ok) location.reload();
        else showErrors(errors, result);
    });
}

// Take-over from the clone source.
const takeoverBtn = document.getElementById("takeover-btn");
if (takeoverBtn) {
    takeoverBtn.addEventListener("click", async () => {
        const errors = document.getElementById("takeover-errors");
        const out = document.getElementById("takeover-result");
        clearErrors(errors);
        out.textContent = "taking over — disabling source, final sync…";
        takeoverBtn.setAttribute("aria-busy", "true");
        const result = await apiFetch("POST", "/api/v1/setup/takeover", {
            sourcePassword: document.getElementById("takeover-password").value || null,
        });
        takeoverBtn.removeAttribute("aria-busy");
        if (result.ok) {
            out.textContent = "Done — this node is live; the source is a disabled archive.";
            setTimeout(() => location.href = "/", 1500);
        } else {
            out.textContent = "";
            showErrors(errors, result);
        }
    });
}
