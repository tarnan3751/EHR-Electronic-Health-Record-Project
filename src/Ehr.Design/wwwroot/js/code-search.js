// The search for a reference code as a clinician types: a diagnosis, a medication or an allergen. An input marked
// data-code-search="<kind>" gets its list's options from /codes, a moment after the typing stops, so the browser's own
// list (arrow keys, Enter) offers the matches. The form checks the name again on the server. Without JavaScript the
// name is typed in full. Nothing about the patient is sent, and nothing is kept in browser storage.
//
// The field's [data-code-search-status], a polite live region, says when nothing matches, and when the session has
// ended: the search was then sent to the sign-in page, which mustn't end up in the list.
"use strict";

(() => {
    let timer = 0;
    let latest = 0;
    document.addEventListener("input", (event) => {
        const input = event.target.closest("input[data-code-search]");
        const list = input?.list;
        if (!list) {
            return;
        }

        const status = input.closest(".sc-field")?.querySelector("[data-code-search-status]");
        const say = (text) => {
            if (status && status.textContent !== text) {
                status.textContent = text;
            }
        };

        clearTimeout(timer);
        const typed = input.value.trim();
        if (typed.length < 2) {
            list.replaceChildren();
            say("");
            return;
        }

        timer = setTimeout(async () => {
            const request = ++latest;
            try {
                const address = `/codes?kind=${encodeURIComponent(input.dataset.codeSearch)}&q=${encodeURIComponent(typed)}`;
                const response = await fetch(address, { credentials: "same-origin", headers: { Accept: "text/html" } });
                if (request !== latest) {
                    return;
                }

                if (response.redirected) {
                    // Signed out: the answer is the sign-in page, not suggestions.
                    list.replaceChildren();
                    say(" Signed out. Sign in again to see suggestions. What you typed is still here.");
                    return;
                }

                if (response.ok) {
                    list.innerHTML = await response.text();
                    say(list.options.length === 0 ? " No matches." : "");
                }
            } catch {
                // No suggestions this time: the name can still be typed in full.
            }
        }, 120);
    });
})();
