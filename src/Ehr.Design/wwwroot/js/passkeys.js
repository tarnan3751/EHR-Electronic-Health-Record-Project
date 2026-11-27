// Staff passkeys (the expansion plan's step 19, UC 11): adding one on the passkeys page, and using one in place of the
// code on the sign-in's code page. The browser makes or uses the passkey (navigator.credentials); the server gives the
// options and checks what comes back (StaffPasskeys), and the person is asked for their PIN, fingerprint or face each
// time. On the code page the code stays below: a passkey that fails or is cancelled says so, and the code still works. The options and the passkey travel as WebAuthn's own JSON, through
// the browser's parse...FromJSON and toJSON: a browser without those, or without JavaScript, isn't offered a passkey and
// keeps the authenticator code. Nothing is kept in browser storage, and no data is written into the page's markup.
"use strict";

(() => {
    const supported = typeof window.PublicKeyCredential === "function"
        && typeof PublicKeyCredential.parseCreationOptionsFromJSON === "function"
        && typeof PublicKeyCredential.parseRequestOptionsFromJSON === "function";

    // A notice that starts with its state, a sentence of its own ("Not saved. ..."), marked up as the notice's leading
    // words (docs/brand: Notice), and shown; or hidden, with no message.
    function say(area, message) {
        if (!message) {
            area.hidden = true;
            area.replaceChildren();
            return;
        }

        const stop = message.indexOf(". ");
        if (stop > 0) {
            const state = document.createElement("span");
            state.className = "sc-notice__state";
            state.textContent = message.slice(0, stop + 1);
            area.replaceChildren(state, message.slice(stop + 1));
        } else {
            area.textContent = message;
        }

        area.hidden = false;
    }

    // The form posted as the browser would, with its form token, for JSON back. Null when the server can't be reached
    // or doesn't answer with JSON (off-site, the clinic's server cut off, the cloud app's own answer).
    async function post(form) {
        try {
            const response = await fetch(form.action, { method: "POST", body: new FormData(form), credentials: "same-origin" });
            const type = response.headers.get("Content-Type") || "";
            return type.startsWith("application/json") ? { ok: response.ok, body: await response.json() } : null;
        } catch {
            return null;
        }
    }

    // The passkey made, sent back with the state in a plain form, so the page after it is the server's.
    function finish(form, credential, state) {
        form.elements.credential.value = JSON.stringify(credential);
        form.elements.state.value = state;
        form.submit();
    }

    const add = document.querySelector("[data-passkey-add]");
    if (add && supported) {
        document.querySelector("[data-passkey-unsupported]").hidden = true;
        add.hidden = false;
        const problem = add.querySelector("[data-passkey-problem]");
        const button = add.querySelector("button");
        add.addEventListener("submit", async (event) => {
            event.preventDefault();
            say(problem, null);
            for (const field of add.querySelectorAll("input[aria-invalid]")) {
                field.removeAttribute("aria-invalid");
            }

            button.disabled = true;
            try {
                const answer = await post(add);
                if (!answer) {
                    say(problem, "Not saved. The clinic's server couldn't be reached. Try again in a moment.");
                    return;
                }

                if (!answer.ok) {
                    say(problem, answer.body.problem);
                    const field = answer.body.field && add.elements[answer.body.field.toLowerCase()];
                    if (field) {
                        field.setAttribute("aria-invalid", "true");
                        field.focus();
                    }

                    return;
                }

                let credential;
                try {
                    credential = await navigator.credentials.create({ publicKey: PublicKeyCredential.parseCreationOptionsFromJSON(answer.body.options) });
                } catch {
                    say(problem, "Not saved. No passkey was made. It may have been cancelled, or this device couldn't check your PIN, fingerprint or face. Try again.");
                    return;
                }

                finish(document.querySelector("[data-passkey-finish]"), credential, answer.body.state);
            } finally {
                button.disabled = false;
                // A disabled button loses the focus; it goes back there unless a field that was wrong has it.
                if (!add.contains(document.activeElement)) {
                    button.focus();
                }
            }
        });
    }

    // The code page: "Use a passkey" first, with the focus, where this browser can use one.
    const use = document.querySelector("[data-passkey-use]");
    if (use && supported) {
        use.hidden = false;
        const problem = use.querySelector("[data-passkey-problem]");
        const button = use.querySelector("button");
        button.focus();
        use.addEventListener("submit", async (event) => {
            event.preventDefault();
            say(problem, null);
            button.disabled = true;
            try {
                const answer = await post(use);
                if (!answer) {
                    say(problem, "Not signed in. The clinic's server couldn't be reached. Try again in a moment.");
                    return;
                }

                if (!answer.ok) {
                    say(problem, answer.body.problem);
                    return;
                }

                let credential;
                try {
                    credential = await navigator.credentials.get({ publicKey: PublicKeyCredential.parseRequestOptionsFromJSON(answer.body.options) });
                } catch {
                    say(problem, "Not signed in. The passkey wasn't used. It may have been cancelled, or this device couldn't check your PIN, fingerprint or face. Try again, or enter a code below.");
                    return;
                }

                finish(document.querySelector("[data-passkey-finish]"), credential, answer.body.state);
            } finally {
                button.disabled = false;
                button.focus();
            }
        });
    }
})();
