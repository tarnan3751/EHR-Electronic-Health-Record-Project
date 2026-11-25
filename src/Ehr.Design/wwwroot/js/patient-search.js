// The patient search from the keyboard (Areas/Clinical/Pages/Patients). "/" outside a field goes to the search: on the
// patients page it focuses the name, on a chart it follows the link back to the search, anywhere else the Patients
// link. From the name or MRN field, the down arrow moves into the results, the arrows move through them, Escape goes
// back to the name, and Enter opens a chart, since each result is a link. Listened for on the document, so results
// htmx swaps in need nothing more, and wired here rather than in attributes, so a strict CSP can be added later.
// Nothing here opens or prefetches a chart by itself: each one opened is a recorded read, and one off the clinician's
// panel asks for emergency access. It also keeps a submitted search from listing its patients twice. And, since it is
// on every clinical page, it moves the focus to a notice marked data-focus-on-load (tabindex="-1"), such as the chart's
// "Saved:" after a save, so the keyboard and a screen reader start from what just happened; unless something on the
// page already took the focus (autofocus).
"use strict";

(() => {
    const notice = document.querySelector("[data-focus-on-load]");
    if (notice && (document.activeElement === null || document.activeElement === document.body)) {
        notice.focus();
    }

    function isTyping(element) {
        return element instanceof HTMLElement
            && (element.isContentEditable || element.closest("input, textarea, select") !== null);
    }

    function nameField() {
        return document.querySelector("[data-search-field]");
    }

    // Enter or Search submits the form as a page. htmx doesn't drop the results request still waiting out the pause
    // after the last keystroke, which would list the same patients again, each a recorded read, for a page on its way
    // out: so, once the form is submitted, no results request leaves. A page brought back from the browser's history
    // starts listening again.
    let submitted = false;
    document.addEventListener("submit", (event) => {
        if (event.target instanceof HTMLFormElement && event.target.matches("form[role=search]")) {
            submitted = true;
        }
    });
    document.addEventListener("htmx:beforeRequest", (event) => {
        if (submitted && event.target instanceof HTMLFormElement && event.target.matches("form[role=search]")) {
            event.preventDefault();
        }
    });
    window.addEventListener("pageshow", () => {
        submitted = false;
    });

    document.addEventListener("keydown", (event) => {
        if (event.defaultPrevented || event.altKey || event.ctrlKey || event.metaKey) {
            return;
        }

        const target = event.target;
        if (event.key === "/" && !isTyping(target)) {
            const field = nameField();
            const link = document.querySelector("a[data-search-back]") || document.querySelector("a[data-search-shortcut]");
            if (field) {
                event.preventDefault();
                field.focus();
                field.select();
            } else if (link) {
                event.preventDefault();
                window.location.assign(link.href);
            }

            return;
        }

        const results = [...document.querySelectorAll("#search-results tbody a")];
        const index = results.indexOf(target);
        // Not from the date field, whose arrows change the date.
        const fromField = target instanceof HTMLInputElement && target.type === "search" && target.closest("form[role=search]") !== null;
        let next = null;
        if (event.key === "ArrowDown") {
            next = fromField ? results[0] : index >= 0 ? results[index + 1] : null;
        } else if (event.key === "ArrowUp" && index >= 0) {
            next = index > 0 ? results[index - 1] : nameField();
        } else if (event.key === "Escape" && index >= 0) {
            next = nameField();
        }

        if (next) {
            event.preventDefault();
            next.focus();
        }
    });
})();
