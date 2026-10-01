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
 * Wires a <dialog> so every way out — a [data-cancel] button, Escape, a
 * click on the dimmed backdrop, or the returned close(returnValue) — closes
 * it AND runs onClose(returnValue) once. The dialog's own `close` event
 * would do the same job, but it is queued as a separate task and at least
 * one embedded Chromium build never delivers it, so nothing here waits on it.
 */
function wireDialog(dialog, onClose) {
    const close = (returnValue = "") => {
        if (!dialog.open) return;
        dialog.close(returnValue);
        onClose(returnValue);
    };
    dialog.querySelectorAll("[data-cancel]").forEach((btn) =>
        btn.addEventListener("click", () => close()));
    // Escape: handled here so the browser's own cancel path (which would
    // close without telling us) never runs; `cancel` covers other UA
    // gestures such as a phone's back button.
    dialog.addEventListener("keydown", (e) => {
        if (e.key === "Escape") { e.preventDefault(); close(); }
    });
    dialog.addEventListener("cancel", (e) => { e.preventDefault(); close(); });
    // Backdrop: both mousedown and mouseup must land on the backdrop. A drag
    // that starts in a field and ends outside the card must not throw the
    // form away, and native <select> popups never deliver a mousedown to the
    // dialog at all.
    let armed = false;
    dialog.addEventListener("mousedown", (e) => { armed = e.target === dialog; });
    dialog.addEventListener("mouseup", (e) => {
        if (armed && e.target === dialog) close();
        armed = false;
    });
    return close;
}

/**
 * Styled replacement for window.confirm. Resolves true when the user picks
 * the confirming button; false on Cancel, Escape, or a backdrop click.
 */
let confirmState = null; // { dialog, close, settle } — one dialog per page
function confirmDialog(message, { confirmLabel = "OK" } = {}) {
    if (!confirmState) {
        const dialog = document.createElement("dialog");
        dialog.id = "cw-confirm";
        dialog.innerHTML =
            '<article><p></p><footer>' +
            '<button type="button" class="secondary outline" data-cancel>Cancel</button>' +
            '<button type="button" data-ok></button>' +
            "</footer></article>";
        document.body.appendChild(dialog);
        const close = wireDialog(dialog, (rv) => confirmState.settle?.(rv === "ok"));
        dialog.querySelector("[data-ok]").addEventListener("click", () => close("ok"));
        confirmState = { dialog, close, settle: null };
    }
    const { dialog } = confirmState;
    dialog.querySelector("p").textContent = message;
    dialog.querySelector("[data-ok]").textContent = confirmLabel;
    return new Promise((resolve) => {
        confirmState.settle = resolve;
        dialog.showModal();
        dialog.querySelector("[data-ok]").focus();
    });
}

/**
 * Wires a config page whose add/edit form lives in a <dialog>.
 *
 *   form          the <form> inside the dialog
 *   dialog        the <dialog>; must contain [data-dialog-title], the form's
 *                 submit button, and a [data-cancel] button
 *   errorList     <ul> inside the dialog for save errors
 *   pageErrorList <ul> on the page for delete errors
 *   noun          "target" | "pool" | "profile" — titles and button labels
 *   endpoint      REST base URL
 *   buildDto()    reads the form into the request body
 *   fillForm(dto) fills the form for an edit
 *   resetForm()   optional; clears page-specific state the form's own reset
 *                 cannot reach (e.g. the pool member list)
 *   beforeSave({isEdit, dto, form}) optional; false aborts, { query: "?k=v" }
 *                 is appended to the PUT/POST URL
 *
 * [data-add] buttons open the dialog empty; [data-edit] buttons (carrying
 * data-json) open it filled; [data-delete] buttons (carrying data-name)
 * confirm and DELETE. Returns { open } so a page can open the dialog in a
 * mode of its own (the Targets page's "insert here") and { close }.
 */
function wireConfigPage({ form, dialog, errorList, pageErrorList, noun, endpoint,
    buildDto, fillForm, resetForm, beforeSave }) {
    const title = dialog.querySelector("[data-dialog-title]");
    const submit = form.querySelector("[type=submit]");

    function open({ title: text, submitLabel }) {
        clearErrors(errorList);
        title.textContent = text;
        submit.textContent = submitLabel;
        dialog.showModal();
        form.querySelector("input:not([type=hidden]), select, textarea")?.focus();
    }

    // Closing by any route (Cancel, Escape, backdrop) drops whatever mode
    // the dialog was in, so the next open starts clean.
    const close = wireDialog(dialog, () => {
        form.reset();
        delete form.dataset.editingId;
        delete form.dataset.insertChildIds;
        delete form.dataset.originalAddress;
        resetForm?.();
    });

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

    document.querySelectorAll("[data-add]").forEach((btn) =>
        btn.addEventListener("click", () =>
            open({ title: `Add ${noun}`, submitLabel: `Add ${noun}` })));

    document.querySelectorAll("[data-edit]").forEach((btn) =>
        btn.addEventListener("click", () => {
            const dto = JSON.parse(btn.dataset.json);
            form.dataset.editingId = btn.dataset.edit;
            fillForm(dto);
            open({ title: `Edit ${dto.name}`, submitLabel: "Save changes" });
        }));

    document.querySelectorAll("[data-delete]").forEach((btn) =>
        btn.addEventListener("click", async () => {
            const name = btn.dataset.name;
            if (!await confirmDialog(`Delete ${noun} '${name}'?`, { confirmLabel: "Delete" })) return;
            clearErrors(pageErrorList);
            const result = await apiFetch("DELETE", `${endpoint}/${btn.dataset.delete}`);
            if (result.ok) location.reload();
            else showErrors(pageErrorList, result);
        }));

    return { open, close };
}
