// Shows a message when an htmx request fails, so a save never fails silently: no connection, or an error from the
// server. The page provides the place for it, an element with id "request-error", and the next request that
// succeeds clears it.
"use strict";

(() => {
    function messageFor(detail) {
        const status = detail.xhr ? detail.xhr.status : 0;
        if (detail.requestConfig.verb === "get") {
            return "Couldn't load that. Try again in a moment.";
        }

        return status === 0 || status >= 500
            ? "Not saved. The server is unavailable, but your changes are still on screen. Try again in a moment."
            : "Not saved. Reload the page and try again.";
    }

    // A failed save starts with its state as a sentence of its own ("Not saved. ..."); the state is marked up as the
    // notice's leading words (docs/brand: Notice). The text is the same either way.
    const notSaved = "Not saved.";

    function show(event) {
        const area = document.getElementById("request-error");
        if (area) {
            const message = messageFor(event.detail);
            if (message.startsWith(notSaved)) {
                const state = document.createElement("span");
                state.className = "sc-notice__state";
                state.textContent = notSaved;
                area.replaceChildren(state, message.slice(notSaved.length));
            } else {
                area.textContent = message;
            }

            area.hidden = false;
        }
    }

    // No response at all, then a response with an error status.
    document.addEventListener("htmx:sendError", show);
    document.addEventListener("htmx:responseError", show);

    document.addEventListener("htmx:afterRequest", (event) => {
        const area = document.getElementById("request-error");
        if (area && event.detail.successful) {
            area.hidden = true;
        }
    });
})();
