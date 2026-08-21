// Shared helpers for the config editor pages (plain JS, no build step).
"use strict";

async function apiFetch(method, url, body) {
    const options = { method, headers: {} };
    if (body !== undefined) {
        options.headers["Content-Type"] = "application/json";
        options.body = JSON.stringify(body);
    }
    const response = await fetch(url, options);
    let payload = null;
    try { payload = await response.json(); } catch { /* empty body */ }
    return { ok: response.ok, status: response.status, body: payload };
}

function showErrors(listEl, result) {
    listEl.innerHTML = "";
    const errors = result?.body?.errors ?? [`Request failed (HTTP ${result?.status ?? "?"})`];
    for (const message of errors) {
        const li = document.createElement("li");
        li.textContent = message;
        listEl.appendChild(li);
    }
}

function clearErrors(listEl) {
    listEl.innerHTML = "";
}

/**
 * Wires a config form: submit -> POST/PUT, delete buttons, edit buttons.
 * Optional beforeSave({isEdit, dto, form}) may return false to abort, or
 * { query: "?k=v" } to append to the PUT/POST URL.
 */
function wireConfigPage({ form, errorList, buildDto, fillForm, endpoint, beforeSave }) {
    form.addEventListener("submit", async (e) => {
        e.preventDefault();
        clearErrors(errorList);
        const dto = buildDto();
        const id = form.dataset.editingId;
        let query = "";
        if (beforeSave) {
            const verdict = await beforeSave({ isEdit: Boolean(id), dto, form });
            if (verdict === false) return;
            query = verdict?.query ?? "";
        }
        const result = id
            ? await apiFetch("PUT", `${endpoint}/${id}${query}`, dto)
            : await apiFetch("POST", `${endpoint}${query}`, dto);
        if (result.ok) location.reload();
        else showErrors(errorList, result);
    });

    document.querySelectorAll("[data-edit]").forEach((btn) =>
        btn.addEventListener("click", () => {
            form.dataset.editingId = btn.dataset.edit;
            fillForm(JSON.parse(btn.dataset.json));
            form.querySelector("[type=submit]").textContent = "Save changes";
        }));

    document.querySelectorAll("[data-delete]").forEach((btn) =>
        btn.addEventListener("click", async () => {
            if (!confirm(`Delete '${btn.dataset.name}'?`)) return;
            clearErrors(errorList);
            const result = await apiFetch("DELETE", `${endpoint}/${btn.dataset.delete}`);
            if (result.ok) location.reload();
            else showErrors(errorList, result);
        }));
}
