// Shared shell behavior on every page: theme cycling, the nav drawer, and the
// verdict pip — the bar answers "is it up?" on every page, not just the
// dashboard. Plain JS, no build step.
"use strict";

// --- theme: system -> dark -> light -> system ------------------------------
(function () {
    function currentChoice() {
        try { return localStorage.getItem("cw-theme") || "system"; } catch { return "system"; }
    }
    function apply(choice) {
        if (choice === "light" || choice === "dark")
            document.documentElement.setAttribute("data-theme", choice);
        else
            document.documentElement.removeAttribute("data-theme");
        try {
            if (choice === "system") localStorage.removeItem("cw-theme");
            else localStorage.setItem("cw-theme", choice);
        } catch { }
        for (const btn of document.querySelectorAll("#cw-theme-btn, #cw-theme-btn-drawer"))
            btn.title = `Theme: ${choice === "system" ? "follows your system" : choice} — click to change`;
    }
    function cycle() {
        const next = { system: "dark", dark: "light", light: "system" }[currentChoice()] ?? "system";
        apply(next);
    }
    apply(currentChoice());
    for (const btn of document.querySelectorAll("#cw-theme-btn, #cw-theme-btn-drawer"))
        btn.addEventListener("click", cycle);
})();

// --- drawer -----------------------------------------------------------------
(function () {
    const drawer = document.getElementById("cw-drawer");
    const burger = document.getElementById("cw-burger");
    if (!drawer || !burger) return;
    const set = (open) => drawer.classList.toggle("open", open);
    burger.addEventListener("click", () => set(true));
    document.getElementById("cw-drawer-close")?.addEventListener("click", () => set(false));
    document.getElementById("cw-drawer-backdrop")?.addEventListener("click", () => set(false));
})();

// --- verdict pip -------------------------------------------------------------
// Same answer language as the dashboard slab (see VerdictBanner.tsx): green
// Up / amber Struggling / red Down / grey Checking / grey Paused (archive).
(function () {
    const pip = document.getElementById("cw-pip");
    if (!pip) return;
    const dot = pip.querySelector(".dot");
    const label = pip.querySelector(".label");

    const notes = document.getElementById("cw-notes");
    function renderNotes(warnings) {
        if (!notes) return;
        const n = (warnings || []).filter((w) => !w.acknowledged).length;
        notes.hidden = n === 0;
        notes.querySelector(".count").textContent = String(n);
        notes.title = n === 1 ? "1 ConnectionWatcher note" : `${n} ConnectionWatcher notes`;
    }
    document.addEventListener("cw:warnings", (e) => renderNotes(e.detail));

    function render(status) {
        let color = "var(--cw-unreach)", text = "Checking";
        if (status && status.enabled === false) {
            text = "Paused";
        } else if (status && status.verdict) {
            const map = {
                Healthy: ["var(--cw-ok)", "Up"],
                SiteIncident: ["var(--cw-ok)", "Up"],
                InternetWeather: ["var(--cw-ok)", "Up"],
                LinkDegraded: ["var(--cw-warn)", "Struggling"],
                LinkDown: ["var(--cw-down)", "Down"],
            };
            const m = map[status.verdict.state];
            if (m) [color, text] = m;
        }
        dot.style.background = color;
        label.textContent = text;
        pip.title = status?.verdict?.detail ?? "";
        pip.hidden = false;
        renderNotes(status?.enabled === false ? [] : status?.warnings);
    }

    async function poll() {
        try {
            const r = await fetch("/api/v1/status");
            render(r.ok ? await r.json() : null);
        } catch { /* keep the last answer */ }
    }
    poll();
    setInterval(poll, 30000);
})();
