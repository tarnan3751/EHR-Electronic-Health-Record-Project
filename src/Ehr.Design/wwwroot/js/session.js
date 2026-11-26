// The end of a session, on every signed-in page (the expansion plan's step 18; UC 11, proposed FR-63). The server decides
// every end (Ehr.Domain's SessionTimes): 15 minutes after the last request, 12 hours after signing in. This script only
// asks it, at the session page the layout names, and never trusts the device's clock to end anything:
// - Activity: a key, a click or tap, the wheel, or typing (never mouse movement) sends a keep-alive, at most once a
//   minute. Any other request (a save, a dictation's audio) renews the session on the server by itself.
// - A recording: while the page says one is being recorded, sent or drafted (data-session-recording="recording" on
//   <body>, which dictation.js and ambient.js set), nothing is warned or ended, and a keep-alive goes every minute.
//   Paused, or waiting for its voices to be named ("waiting"), it may be warned, and the warning says what the dictation
//   service does with it.
// - The warning: when 2 minutes are left, a check (a request that renews nothing) confirms it, and a dialog takes the
//   focus. "Stay signed in" sends a keep-alive; the page isn't reloaded; if the server can't be reached, the dialog stays
//   and says so. At the 12-hour end nothing moves the end, and the dialog says so; once closed, it isn't shown again.
// - The end: once the server says the session is over, the page is taken off the screen. What was typed on it, or a
//   recording in it, is kept in this tab's memory, out of sight, never in browser storage; the same account signing in
//   again, in the frame the page shows or in another tab, gets it back as it was. Anyone else signing in clears it and
//   goes to their home page; closing the tab clears it. A page with nothing typed is reloaded instead.
// - Signing out on purpose with something typed does the same, so a sign-out never loses typed text.
// - Tabs tell each other through a BroadcastChannel, which stores nothing: a keep-alive, a sign-out, a sign-in.
// The page itself carries nothing of the session's: no account, no record, no time left, so two requests for a page
// give the same markup. The script asks for them as the page loads, with a check that renews nothing.
"use strict";

(() => {
    const dialog = document.querySelector("[data-session-warning]");
    const ended = document.querySelector("template[data-session-ended]");
    if (!dialog || !ended || typeof dialog.showModal !== "function") {
        return;
    }

    const body = document.body;
    const settings = dialog.dataset;
    const url = settings.sessionUrl;
    const idle = Number(settings.sessionIdle);
    const warn = Number(settings.sessionWarn);
    // One channel for staff pages and one for the portal's, which can share a host in development.
    const channel = "BroadcastChannel" in window ? new BroadcastChannel(`swiftchart-session-${settings.sessionKind}`) : null;
    const minute = 60000;
    const part = (name) => dialog.querySelector(`[data-session-${name}]`);

    // Until the server answers: the least a page just served can have left, the renewal coming once a minute.
    let known = { left: idle - 60, final: false, at: Date.now() };
    // Whose page this is, and which record a proxy's page is about, from the server's first answer while the page is on
    // screen; never from a later one, which another tab's choice of record or sign-in may have changed.
    let account = null;
    let acting = null;
    let lastKeepAlive = Date.now();
    let kept = null;
    let asking = false;
    let opener = null;
    let countdown = 0;
    // The 12-hour warning, once dismissed ("Keep working", or Escape), isn't opened again for this sign-in: nothing
    // moves that end, and the person is finishing their work. The end itself still ends the page.
    let finalDismissed = false;
    const typedFields = new Set();

    const remaining = () => known.left - (Date.now() - known.at) / 1000;
    const recording = () => body.dataset.sessionRecording || "";
    const typed = () => [...typedFields].some((field) => field.isConnected);

    // The server's answer: the session's state, {ended: true}, or null when it couldn't be asked.
    async function ask(check) {
        let response;
        try {
            response = await fetch(url, {
                credentials: "same-origin", cache: "no-store", redirect: "manual",
                headers: check ? { "Ehr-Session-Check": "1", Accept: "application/json" } : { Accept: "application/json" },
            });
        } catch {
            return null;
        }

        if (response.type === "opaqueredirect" || response.status === 401) {
            return { ended: true };
        }

        if (!response.ok) {
            return null;
        }

        const answer = await response.json();
        known = { left: answer.left, final: answer.final, at: Date.now() };
        if (account === null && !kept) {
            account = answer.account;
            acting = answer.acting ?? null;
            channel?.postMessage({ type: "signed-in", account });
        }

        return answer;
    }

    async function keepAlive() {
        lastKeepAlive = Date.now();
        const answer = await ask(false);
        if (answer && !answer.ended) {
            channel?.postMessage({ type: "alive", account, left: answer.left, final: answer.final });
        }

        return answer;
    }

    // At most once a minute, and only when the server's last renewal is a minute old.
    function due() {
        return Date.now() - lastKeepAlive >= minute && idle - remaining() >= 60;
    }

    function activity(event) {
        if (event.type === "input" || event.type === "change") {
            const field = event.target;
            if (field instanceof HTMLElement && field.matches("input:not([type=hidden]), textarea, select") && !dialog.contains(field)) {
                typedFields.add(field);
            }
        }

        if (!kept && !dialog.open && due()) {
            void keepAlive();
        }
    }

    async function tick() {
        if (kept || asking) {
            return;
        }

        asking = true;
        try {
            if (recording() === "recording") {
                if (dialog.open) {
                    close();
                }

                if (due()) {
                    await keepAlive();
                }

                return;
            }

            if (dialog.open) {
                showCountdown();
                if (remaining() <= 0) {
                    await settle();
                }

                return;
            }

            if (remaining() <= warn && !(known.final && finalDismissed && remaining() > 0)) {
                const answer = await ask(true);
                if (answer?.ended) {
                    end(known.final ? "longest" : "idle");
                } else if (answer === null && remaining() <= 0) {
                    end(known.final ? "longest" : "idle");
                } else if (remaining() <= warn && !(known.final && finalDismissed)) {
                    open();
                }
            }
        } finally {
            asking = false;
        }
    }

    // At the end the warning gave: only the server's answer ends the page, unless it can't be asked.
    async function settle() {
        const answer = await ask(true);
        if (answer?.ended || (answer === null && remaining() <= 0)) {
            end(known.final ? "longest" : "idle");
        } else if (remaining() > warn) {
            close();
        }
    }

    function minutes(seconds) {
        const whole = Math.max(0, Math.ceil(seconds));
        return `${Math.floor(whole / 60)}:${String(whole % 60).padStart(2, "0")}`;
    }

    function showCountdown() {
        dialog.querySelectorAll("[data-session-countdown]").forEach((place) => { place.textContent = minutes(remaining()); });
    }

    function open() {
        part("idle-text").hidden = known.final;
        part("final-text").hidden = !known.final;
        part("stay").hidden = known.final;
        part("carry-on").hidden = !known.final;
        part("typed").hidden = !typed();
        part("waiting").hidden = recording() !== "waiting";
        part("unreachable").hidden = true;
        showCountdown();
        opener = document.activeElement;
        dialog.showModal();
        (known.final ? part("carry-on") : part("stay")).focus();
        countdown = setInterval(showCountdown, 1000);
    }

    function close() {
        clearInterval(countdown);
        if (dialog.open) {
            dialog.close();
        }

        opener?.focus?.({ preventScroll: true });
        opener = null;
    }

    async function stay() {
        const answer = await keepAlive();
        if (answer?.ended) {
            end("idle");
        } else if (answer === null) {
            // The server couldn't be asked: the warning stays, and says so, rather than closing on a session it can't vouch for.
            part("unreachable").hidden = false;
        } else {
            close();
        }
    }

    function keepWorking() {
        finalDismissed = true;
        close();
    }

    // Takes the page off the screen. What was typed, or a recording, stays in this tab's memory until the same account
    // signs in again; otherwise nothing is kept.
    function end(why) {
        if (kept) {
            return;
        }

        close();
        const keep = typed() || recording() !== "";
        kept = {
            nodes: keep ? [...body.childNodes] : null,
            title: document.title,
            scroll: [window.scrollX, window.scrollY],
            focus: document.activeElement,
        };
        const panel = ended.content.cloneNode(true);
        panel.querySelectorAll("[data-session-why]").forEach((reason) => { reason.hidden = reason.dataset.sessionWhy !== why; });
        panel.querySelector("[data-session-kept]").hidden = !keep;
        body.replaceChildren(panel);
        document.title = "Signed out";
        body.querySelector("[data-session-sign-in]").addEventListener("click", showSignIn);
        const heading = body.querySelector("[data-session-ended-heading]");
        heading.tabIndex = -1;
        heading.focus();
        channel?.postMessage({ type: "ended", account });
    }

    // The sign-in, in a frame of this page, so nothing kept leaves the tab. It ends on the session page, which says whose
    // sign-in it is.
    function showSignIn() {
        const frame = body.querySelector("[data-session-frame]");
        frame.addEventListener("load", () => {
            let path = "";
            try {
                path = frame.contentWindow.location.pathname;
            } catch {
                return;
            }

            if (path === new URL(url, window.location.href).pathname) {
                void signedIn();
            } else if (document.activeElement !== frame) {
                // Into the frame's first field, unless someone is already typing in it.
                frame.contentDocument?.querySelector("input:not([type=hidden]), button")?.focus();
            }
        });
        frame.src = settings.sessionSignIn;
        frame.hidden = false;
        body.querySelector("[data-session-sign-in]").hidden = true;
    }

    async function signedIn() {
        const answer = await ask(true);
        if (!answer || answer.ended || !kept) {
            return;
        }

        // Whose page it was is unknown when the server couldn't be asked as it loaded: cleared, as for anyone else.
        if (account === null || answer.account !== account) {
            leave();
            return;
        }

        // A proxy's page goes back only to the record it was about: the same grant chosen again, if it's still theirs.
        if (acting && answer.acting !== acting && !(await choose(acting))) {
            leave(settings.sessionChoose);
            return;
        }

        restore();
    }

    async function choose(grant) {
        const token = kept.nodes?.map((node) => node.querySelector?.("input[name=__RequestVerificationToken]")).find((input) => input)?.value
            ?? body.querySelector("[data-session-frame]")?.contentDocument?.querySelector("input[name=__RequestVerificationToken]")?.value;
        try {
            await fetch(settings.sessionActingUrl, {
                method: "POST", credentials: "same-origin", redirect: "manual",
                body: new URLSearchParams({ grantId: grant, __RequestVerificationToken: token ?? "" }),
            });
        } catch {
            return false;
        }

        const answer = await ask(true);
        return answer?.acting === grant;
    }

    function restore() {
        if (!kept.nodes) {
            window.location.reload();
            return;
        }

        const { nodes, title, scroll, focus } = kept;
        kept = null;
        // A new sign-in: its own 12-hour warning will be shown.
        finalDismissed = false;
        body.replaceChildren(...nodes);
        document.title = title;
        window.scrollTo(scroll[0], scroll[1]);
        focus?.focus?.({ preventScroll: true });
        lastKeepAlive = Date.now();
        channel?.postMessage({ type: "signed-in", account });
    }

    // Someone else is signed in now, or the page can't go back: whatever was kept is cleared, and the browser goes to the
    // home page of whoever is signed in.
    function leave(to = settings.sessionHome) {
        kept = { nodes: null };
        body.replaceChildren();
        window.location.replace(to);
    }

    async function signOut(form) {
        try {
            await fetch(form.action, { method: "POST", credentials: "same-origin", redirect: "manual", body: new FormData(form) });
        } finally {
            end("signed-out");
        }
    }

    document.addEventListener("submit", (event) => {
        const form = event.target;
        if (kept || !(form instanceof HTMLFormElement)) {
            return;
        }

        if (new URL(form.action, window.location.href).pathname.endsWith("/sign-out")) {
            if (typed() || recording() !== "") {
                event.preventDefault();
                event.stopImmediatePropagation();
                void signOut(form);
            } else {
                channel?.postMessage({ type: "ended", account });
            }
        } else if (remaining() <= 0) {
            // The estimate says the session may be over: ask before sending, so typing isn't lost to the sign-in page. The
            // form's own handlers wait too, and run once, when it is sent again.
            event.preventDefault();
            event.stopImmediatePropagation();
            void ask(true).then((answer) => (answer?.ended ? end(known.final ? "longest" : "idle") : form.requestSubmit(event.submitter)));
        }
    }, true);

    for (const type of ["keydown", "pointerdown", "wheel", "input", "change"]) {
        document.addEventListener(type, activity, { capture: true, passive: true });
    }

    part("stay").addEventListener("click", () => void stay());
    part("carry-on").addEventListener("click", keepWorking);
    dialog.addEventListener("cancel", (event) => {
        event.preventDefault();
        if (known.final) {
            keepWorking();
        } else {
            void stay();
        }
    });

    channel?.addEventListener("message", ({ data }) => {
        if (data.type === "signed-in" && account !== null && data.account !== account) {
            leave();
        } else if (data.type === "signed-in" && kept) {
            void signedIn();
        } else if (data.type === "alive" && data.account === account && !kept) {
            known = { left: data.left, final: data.final, at: Date.now() };
            if (dialog.open && data.left > warn) {
                close();
            }
        } else if (data.type === "ended" && !kept) {
            // Told as the other tab sends its sign-out, which may not have arrived yet: asked again a little later too.
            for (const wait of [0, 1000, 3000]) {
                setTimeout(() => void (kept || ask(true).then((answer) => answer?.ended && end("ended"))), wait);
            }
        }
    });

    window.addEventListener("pageshow", (event) => {
        if (event.persisted) {
            known.left = 0;
            void tick();
        }
    });
    document.addEventListener("visibilitychange", () => void tick());
    window.addEventListener("focus", () => void tick());
    setInterval(() => void tick(), 5000);
    void ask(true);
})();
