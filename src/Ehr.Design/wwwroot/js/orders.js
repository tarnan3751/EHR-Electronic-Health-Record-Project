// UC 1 Place an order (Tahseen Arnan). On the chart, Alt+Shift+O follows the "New order" link: no major browser uses it,
// and it is clear of "/", Ctrl/Cmd+S and dictation's Alt+Shift+D. On the order form, "Sign order" posts with fetch, so that
// when the clinic's server can't be reached (off-site, the cloud app's 503, or no answer at all) the form stays on screen
// with what was entered and says nothing was saved. A form to fix comes back as a page, which takes this one's place.
// Without JavaScript the form posts as a normal form. Nothing is kept in browser storage.
"use strict";

(() => {
    document.addEventListener("keydown", (event) => {
        const link = document.querySelector("a[data-new-order]");
        if (link && event.altKey && event.shiftKey && !event.ctrlKey && !event.metaKey && event.code === "KeyO") {
            event.preventDefault();
            link.click();
        }
    });

    let sending = false;
    document.addEventListener("submit", async (event) => {
        const form = event.target.closest("form[data-order-form]");
        if (!form) {
            return;
        }

        event.preventDefault();
        if (sending) {
            return;
        }

        sending = true;
        const message = form.querySelector("[data-order-message]");
        let text = "Not saved: the clinic's server can't be reached. Your order is still here; sign it again once the server is back.";
        try {
            const response = await fetch(form.action, {
                method: "POST", body: new FormData(form), credentials: "same-origin", headers: { "HX-Request": "true" },
            });
            if (response.redirected) {
                window.location.assign(response.url);
                return;
            }

            const type = response.headers.get("Content-Type") || "";
            if (response.ok && type.includes("application/json")) {
                window.location.assign((await response.json()).url);
                return;
            }

            if ((response.status === 409 || response.status === 422) && type.includes("text/html")) {
                const page = new DOMParser().parseFromString(await response.text(), "text/html");
                document.querySelector("main").replaceWith(page.querySelector("main"));
                // The first field to fix; or, when the form is back only to show notes, the button that signs.
                (document.querySelector("form[data-order-form] [aria-invalid=true]")
                    ?? document.querySelector("form[data-order-form] [data-order-sign][autofocus]"))?.focus();
                return;
            }

            if (response.status < 500) {
                text = "Not saved. Reload the page and try again.";
            }
        } catch {
            // No answer at all: the message above.
        } finally {
            sending = false;
        }

        message.textContent = text;
        message.hidden = false;
        message.focus();
    });
})();
