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
    for (const anchor of result.body ?? []) {
        const label = document.createElement("label");
        const box = document.createElement("input");
        box.type = "checkbox";
        box.checked = true;
        box.dataset.address = anchor.address;
        label.appendChild(box);
        label.append(` ${anchor.name} (${anchor.address})`);
        list.appendChild(label);
    }
}

document.getElementById("wan-probe").addEventListener("click", async () => {
    const button = document.getElementById("wan-probe");
    const out = document.getElementById("wan-result");
    button.setAttribute("aria-busy", "true");
    const result = await apiFetch("POST", "/api/v1/setup/wan-probe");
    button.removeAttribute("aria-busy");
    const probe = result.body;
    if (!result.ok || !probe?.address) {
        out.textContent = `Skipped: ${probe?.failureReason ?? "probe failed"}. ` +
            "You can add it later from the Traceroute or Config pages.";
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
    };
    const button = document.getElementById("apply");
    button.setAttribute("aria-busy", "true");
    const result = await apiFetch("POST", "/api/v1/setup/apply", body);
    button.removeAttribute("aria-busy");
    if (result.ok) location.href = "/";
    else showErrors(errors, result);
});

loadGateways();
loadAnchors();
