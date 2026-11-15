// A link marked data-back goes back in the browser's history instead of to its address, to the form a failed save came
// from, often still with what was typed in it (the cloud app's "Not saved" page, Forwarding/CloudForwardingMiddleware).
// Wired here rather than in an onclick attribute, so a strict CSP can be added later.
"use strict";

document.addEventListener("click", (event) => {
    const link = event.target.closest("a[data-back]");
    if (link) {
        event.preventDefault();
        history.back();
    }
});
