// Quick-setup wizard logic (plain JS, no build step).
"use strict";

let wanOffer = null; // { name, address } when the probe succeeded and is accepted

async function loadGateways() {
    const list = document.getElementById("gateway-list");
    const result = await apiFetch("GET", "/api/v1/setup/gateway");
    list.removeAttribute("aria-busy");
    if (!result.ok || result.body.length === 0) {
        list.textContent = "No gateway detected — enter your router's address below.";
        return;
    }
    list.innerHTML = "";
    result.body.forEach((candidate, index) => {
        const label = document.createElement("label");
        const radio = document.createElement("input");
        radio.type = "radio";
        radio.name = "gateway";
        radio.value = candidate.address;
        radio.checked = index === 0;
        radio.addEventListener("change", () => {
            document.getElementById("router-address").value = candidate.address;
        });
        label.appendChild(radio);
        label.append(` ${candidate.address} — ${candidate.source}` +
            (candidate.reachable ? ` · answering (${candidate.rttMs} ms)` : " · not answering"));
        list.appendChild(label);
    });
    document.getElementById("router-address").value = result.body[0].address;
}

async function loadAnchors() {
    const list = document.getElementById("anchor-list");
    const result = await apiFetch("GET", "/api/v1/setup/anchors");
    list.removeAttribute("aria-busy");
    list.innerHTML = "";
    const anchors = result.ok ? result.body : [];
    if (!result.ok) list.textContent = "Could not load the suggested targets — you can add them later from the Targets page.";
    for (const anchor of anchors) {
        const label = document.createElement("label");
        const box = document.createElement("input");
        box.type = "checkbox";
        box.checked = true;
        box.dataset.address = anchor.address;
        label.appendChild(box);
        label.append(` ${anchor.name} (${anchor.address})`);
        list.appendChild(label);
    }

    const resolvers = document.getElementById("resolver-list");
    resolvers.removeAttribute("aria-busy");
    resolvers.innerHTML = "";
    const addRadio = (value, text, checked) => {
        const label = document.createElement("label");
        const radio = document.createElement("input");
        radio.type = "radio";
        radio.name = "resolver";
        radio.value = value;
        radio.checked = checked;
        label.appendChild(radio);
        label.append(` ${text}`);
        resolvers.appendChild(label);
    };
    for (const anchor of anchors)
        addRadio(anchor.address, `${anchor.name} (${anchor.address})`, false);
    addRadio("", "I don't know", true);
}

document.getElementById("wan-probe").addEventListener("click", async () => {
    const button = document.getElementById("wan-probe");
    const out = document.getElementById("wan-result");
    const result = await withBusy(button, () => apiFetch("POST", "/api/v1/setup/wan-probe"));
    const probe = result.body;
    if (!result.ok || !probe?.address) {
        out.textContent = `Skipped: ${probe?.failureReason ?? "probe failed"}. ` +
            "You can add it later from the Traceroute or Targets pages.";
        wanOffer = null;
        return;
    }
    if (!probe.pingOk) {
        out.textContent = `Found ${probe.address}, but your router doesn't answer pings on its ` +
            "public address from inside the network (no hairpin) — skipped.";
        wanOffer = null;
        return;
    }
    wanOffer = { name: "Outer IP", address: probe.address };
    out.innerHTML = "";
    const label = document.createElement("label");
    const box = document.createElement("input");
    box.type = "checkbox";
    box.checked = true;
    box.addEventListener("change", () => {
        wanOffer = box.checked ? { name: "Outer IP", address: probe.address } : null;
    });
    label.appendChild(box);
    label.append(` Monitor ${probe.address} (answers in ${probe.rttMs} ms)`);
    out.appendChild(label);
});

document.getElementById("apply").addEventListener("click", async () => {
    const errors = document.getElementById("errors");
    clearErrors(errors);
    const anchors = [...document.querySelectorAll("#anchor-list input:checked")]
        .map((box) => box.dataset.address);
    const body = {
        router: {
            name: document.getElementById("router-name").value || "Router",
            address: document.getElementById("router-address").value.trim(),
        },
        outerIp: wanOffer,
        anchorAddresses: anchors,
        upstreamResolverAddress: document.querySelector("#resolver-list input:checked")?.value || null,
        ispName: document.getElementById("isp-name").value.trim() || null,
    };
    const button = document.getElementById("apply");
    const result = await withBusy(button, () => apiFetch("POST", "/api/v1/setup/apply", body));
    if (result.ok) location.href = "/";
    else showErrors(errors, result);
});

loadGateways();
loadAnchors();

// --- Clone from a running node -------------------------------------------
// Transport policy lives on THIS (requesting) node: plain HTTP to a
// non-loopback host with secrets involved needs the explicit override below.
function cloneNeedsWarning(url, password) {
    let parsed;
    try { parsed = new URL(url); } catch { return false; } // server rejects it
    if (parsed.protocol === "https:") return false;
    const host = parsed.hostname;
    if (host === "localhost" || host === "127.0.0.1" || host === "[::1]" || host === "::1")
        return false;
    return true; // secrets are requested by default; password may also be set
}

async function runClone(includeSecrets, allowInsecure) {
    const errors = document.getElementById("clone-errors");
    const progress = document.getElementById("clone-progress");
    clearErrors(errors);
    document.getElementById("clone-insecure").hidden = true;
    progress.textContent = "cloning…";
    const result = await apiFetch("POST", "/api/v1/setup/clone", {
        sourceUrl: document.getElementById("clone-url").value.trim(),
        sourcePassword: document.getElementById("clone-password").value || null,
        includeSecrets,
        allowInsecure,
        includeLocalDevices: document.getElementById("clone-local").checked,
    });
    progress.textContent = "";
    if (result.ok) {
        progress.textContent = "Cloned. Opening the dashboard…";
        location.href = "/";
        return;
    }
    if (result.body?.code === "insecure-transport") {
        document.getElementById("clone-insecure").hidden = false;
        return;
    }
    showErrors(errors, result);
}

document.getElementById("clone-btn").addEventListener("click", () => {
    const url = document.getElementById("clone-url").value.trim();
    const password = document.getElementById("clone-password").value;
    if (cloneNeedsWarning(url, password)) {
        document.getElementById("clone-insecure").hidden = false;
        return;
    }
    runClone(true, false);
});
document.getElementById("clone-insecure-anyway").addEventListener("click",
    () => runClone(true, true));
document.getElementById("clone-no-secrets").addEventListener("click",
    () => runClone(false, false));
