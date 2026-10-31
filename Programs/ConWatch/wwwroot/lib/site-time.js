// Localizes server-rendered UTC instants in the browser (plain JS, no build step).
"use strict";

// For each element matching selector, reads the instant from `attribute` and, when it parses,
// replaces the element's text with the browser-local rendering. Returns how many were localized.
function localizeTimes(selector, attribute, options) {
    let count = 0;
    document.querySelectorAll(selector).forEach((el) => {
        const when = new Date(el.getAttribute(attribute));
        if (isNaN(when)) return;
        el.textContent = when.toLocaleString(undefined, options);
        count++;
    });
    return count;
}

globalThis.localizeTimes = localizeTimes;
