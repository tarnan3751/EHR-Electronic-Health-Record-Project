// Listening to a visit to draft its note (ambient listening, the expansion plan's step 16; UC 23). The script for
// Areas/Clinical/Pages/Patients/Listen. Its order is the point:
// 1. The clinician confirms the consent in the page's form. The server records it and starts the session
//    (Ehr.Web, Api/Dictation/AmbientApi.cs, whose header is the contract). Only after its 201 is the microphone opened:
//    nothing is captured or kept in the browser before.
// 2. The visit's audio goes up in numbered five-second chunks, in whatever format the browser records (AGENTS.md: never
//    hard-code one). The page shows that it is recording, and for how long, the whole time, and so does the tab's title;
//    the clock that ticks is apart from the status line, a live region, which says each change and warning and keeps it
//    until the next. Pause, or the page going out of sight, even while listening starts, pauses the recorder and
//    switches the microphone's tracks off, and nothing is sent until the clinician resumes. It stops on its own at the
//    session's limit, saying so a minute before and saying at the limit what happens next, or after five minutes with
//    nobody speaking, warned at four, and at once, keeping nothing, if the clinician is signed out or the listening is
//    refused. A warning on the status line is in the guide's warning style, and like every message said once.
// 3. Stop and draft: the server transcribes the visit and tells its voices apart; the clinician says who each voice is,
//    with no role chosen for them, or that they can't tell, and the note is drafted and saved for review (UC 13), where
//    the page goes. Fetching the draft is tried again a few times while the service doesn't answer, then given up.
// Stop and discard, at any point before that, switches the microphone off first, then discards the session and its
// review: nothing recorded is kept. Leaving the page before the voices are handed over does the same, and leaving it at
// all ends listening here for good. When the visit can't be fetched or drafted, the page offers what can be done: try
// again, discard the recording, or once the voices are handed over, open the draft to retrieve it. A chunk the recorder hands over after its listening is discarded, stopped or ended is
// dropped. Every piece of text is set with textContent, never parsed as HTML.
"use strict";

(() => {
    const root = document.querySelector("[data-ambient]");
    if (!root) {
        return;
    }

    const part = (name) => root.querySelector(`[data-ambient-${name}]`);
    const api = root.dataset.ambientApi;
    const reviewApi = root.dataset.reviewApi;
    const token = root.querySelector("input[name=__RequestVerificationToken]")?.value ?? "";
    const consent = part("consent");
    const statusArea = part("status");
    const clock = part("clock");
    const capture = part("capture");
    const pauseButton = part("pause");
    const speakersArea = part("speakers");
    const voicesArea = part("voices");
    const draftButton = part("draft");
    const title = document.title;

    const chunkMs = 5000;
    const firstRetryMs = 1000;
    const mostRetryMs = 10000;
    const warnQuietSeconds = 240;
    const quietSeconds = 300;
    const roleWords = { clinician: "Me", patient: "The patient", other: "Someone else in the room", unsure: "I can't tell" };
    const mostAttempts = 4;
    // As AmbientApi.MaxVoices.
    const mostVoices = 9;
    const again = part("again");
    const retryButton = part("retry");
    const discardStuck = part("discard-stuck");
    const openDraft = part("open-draft");

    let state = fresh();

    function fresh() {
        return { requestId: null, session: null, consented: 0, stream: null, recorder: null, queue: [], nextSeq: 0, sending: false,
            paused: false, hidden: false, stopping: false, stopped: false, drafting: false, handedOff: false, ended: false, left: false,
            persisted: false, warned: false, nearLimit: false, retrying: false, retryMs: firstRetryMs, retryTimer: 0, clock: 0, recordedMs: 0,
            resumedAt: 0, voices: [] };
    }

    // The status line, a live region: each message stays until the next replaces it, an error in danger text and a
    // warning (nobody has spoken for 4 minutes, a minute left) in warning text.
    function say(text, isError = false, isWarning = false) {
        statusArea.textContent = text;
        statusArea.classList.toggle("sc-status--error", isError);
        statusArea.classList.toggle("sc-status--warning", isWarning);
    }

    // At the session's limit: the limit, and what happens next, said once.
    function atTheLimit() {
        return `Listening stopped at its ${Math.round((state.session?.maxSeconds ?? 0) / 60)}-minute limit. Transcribing the `
            + "visit and telling its voices apart: then say who each voice is to draft the note.";
    }

    function call(method, path, body, type) {
        const headers = { RequestVerificationToken: token };
        if (type) {
            headers["Content-Type"] = type;
        }

        return fetch(path, { method, headers, body, credentials: "same-origin" });
    }

    async function json(response) {
        try {
            return await response.json();
        } catch {
            return {};
        }
    }

    async function refusal(response) {
        const body = await json(response);
        return body.errors?.join(" ") || body.message || `Listening failed (${response.status}). Try again in a moment.`;
    }

    function transient(status) {
        return status === 0 || status === 429 || status >= 500;
    }

    function minutes(seconds) {
        const whole = Math.max(0, Math.floor(seconds));
        return `${Math.floor(whole / 60)}:${String(whole % 60).padStart(2, "0")}`;
    }

    function recordedSeconds() {
        return (state.recordedMs + (state.resumedAt ? Date.now() - state.resumedAt : 0)) / 1000;
    }

    // The clock, every second while recording, with its red dot: not a live region, so a screen reader isn't read it each
    // second; the tab's title says it too.
    function showRecording() {
        const max = state.session?.maxSeconds ?? 0;
        const elapsed = recordedSeconds();
        clock.textContent = `Recording the visit: ${minutes(elapsed)} of ${minutes(max)}.`;
        clock.dataset.state = "recording";
        document.title = `● Recording: ${title}`;
        if (max > 0 && elapsed >= max) {
            stopAndDraft(atTheLimit());
        } else if (max > 0 && max - elapsed <= 60 && !state.nearLimit) {
            // Once, a minute before the limit, not a countdown each second.
            state.nearLimit = true;
            say(`Listening stops in a minute, at its ${Math.round(max / 60)}-minute limit.`, false, true);
        }
    }

    // 1. The consent, then the session, then the microphone.
    async function start(event) {
        event.preventDefault();
        if (!consent.reportValidity()) {
            return;
        }

        if (!navigator.mediaDevices?.getUserMedia || !window.MediaRecorder) {
            say("This browser can't record audio. Use a current version of Chrome, Edge, Firefox or Safari, or type the note.", true);
            return;
        }

        const form = new FormData(consent);
        state.requestId ||= crypto.randomUUID();
        const requestId = state.requestId;
        consent.querySelector("button").disabled = true;
        let response;
        try {
            response = await call("POST", `${api}/start`, JSON.stringify({
                reviewId: requestId, patientId: root.dataset.patientId,
                draftId: root.dataset.draftId || null, version: Number(root.dataset.draftVersion) || null,
                speakers: Number(form.get("Speakers")), givenBy: form.get("GivenBy"), agreed: form.get("Agreed") === "true",
            }), "application/json");
        } catch {
            consent.querySelector("button").disabled = false;
            say("The clinic's server can't be reached. Check your connection and try again.", true);
            return;
        }

        if (response.status !== 201) {
            consent.querySelector("button").disabled = false;
            say(await refusal(response), true);
            return;
        }

        state = { ...fresh(), requestId, session: await json(response), consented: Number(form.get("Speakers")) };
        root.dataset.draftId = state.session.draftId;
        root.dataset.draftVersion = state.session.version;
        if (state.session.state !== "recording") {
            state.persisted = true;
            window.location.assign(state.session.url);
            return;
        }

        try {
            state.stream = await navigator.mediaDevices.getUserMedia({ audio: true });
        } catch (error) {
            await discard(microphoneProblem(error));
            return;
        }

        // No mimeType: the browser picks the format it records (WebM in Chromium, MP4 in WebKit), and the server decodes
        // either. Its chunks belong to this listening alone: one handed over once it was discarded, ended, or after the
        // recorder stopped (a real recorder's last comes after stop() returns) is dropped, never queued for the next.
        const own = state;
        const recorder = new MediaRecorder(own.stream);
        own.recorder = recorder;
        recorder.addEventListener("dataavailable", (event) => {
            if (event.data && event.data.size > 0 && own === state && !own.ended && !own.stopped) {
                own.queue.push({ seq: own.nextSeq++, blob: event.data, type: event.data.type || recorder.mimeType || "audio/*" });
                pump();
            }
        });
        recorder.addEventListener("stop", () => {
            own.stopped = true;
            stopTracks(own);
            if (own === state && !own.ended) {
                pump();
            }
        });
        recorder.start(chunkMs);
        state.resumedAt = Date.now();
        state.clock = setInterval(showRecording, 1000);
        consent.hidden = true;
        capture.hidden = false;
        showRecording();
        say("Listening: the visit is being recorded.");
        part("stop").focus();
        // Out of sight while listening started: paused at once, as it would be had it gone out of sight now.
        if (document.visibilityState === "hidden") {
            pause(hiddenMessage);
        }
    }

    const hiddenMessage = "Paused while this page was out of sight: nothing was recorded. Select Resume listening to carry on.";

    function microphoneProblem(error) {
        switch (error?.name) {
            case "NotAllowedError":
            case "SecurityError":
                return "The microphone is blocked, so nothing was recorded. Allow it for this site in the browser's settings, then start again.";
            case "NotFoundError":
            case "OverconstrainedError":
                return "No microphone was found, so nothing was recorded. Connect one, then start again.";
            default:
                return "The microphone couldn't be started, so nothing was recorded.";
        }
    }

    // 2. The chunks, in order, one at a time; none while paused or hidden. On a failure worth retrying they wait here.
    async function pump() {
        if (state.sending || state.ended || state.paused || !state.session) {
            return;
        }

        state.sending = true;
        try {
            while (state.queue.length > 0 && !state.ended && !state.paused) {
                const chunk = state.queue[0];
                let response;
                try {
                    response = await call("POST", `${api}/${state.session.sessionId}/audio?seq=${chunk.seq}`, chunk.blob, chunk.type);
                } catch {
                    retryLater();
                    return;
                }

                if (state.ended) {
                    return;
                }

                const body = await json(response);
                if (response.ok) {
                    state.queue.shift();
                    state.retryMs = firstRetryMs;
                    if (state.retrying) {
                        state.retrying = false;
                        say("Connected again: the audio recorded meanwhile has been sent.");
                    }

                    heard(body);
                    continue;
                }

                if (response.status === 409 && body.error === "out_of_order" && typeof body.expectedSeq === "number") {
                    state.queue = state.queue.filter((waiting) => waiting.seq >= body.expectedSeq);
                    continue;
                }

                if (response.status === 401) {
                    end("You were signed out, so listening stopped. Nothing recorded was kept.");
                    return;
                }

                if (response.status === 409 || response.status === 404) {
                    end(body.message || "This listening has ended. Nothing more is sent.");
                    return;
                }

                if (response.status === 413) {
                    state.queue = [];
                    stopAndDraft(atTheLimit());
                    return;
                }

                if (transient(response.status)) {
                    retryLater();
                    return;
                }

                end(body.message || `Listening failed (${response.status}). Nothing more is sent.`);
                return;
            }
        } finally {
            state.sending = false;
        }

        if (state.stopping && state.queue.length === 0 && state.recorder?.state === "inactive") {
            finishRecording();
        }
    }

    function retryLater() {
        state.retrying = true;
        say("The connection to the clinic's server dropped. Still recording here; the audio is sent when it's back.", true);
        clearTimeout(state.retryTimer);
        state.retryTimer = setTimeout(pump, state.retryMs);
        state.retryMs = Math.min(state.retryMs * 2, mostRetryMs);
    }

    // The warning stays until someone speaks, or something else is said.
    function heard(received) {
        const quiet = received.quietSeconds ?? 0;
        if (quiet >= quietSeconds) {
            stopAndDraft("Stopped: nobody has spoken for 5 minutes. Say who each voice is to draft the note.");
        } else if (quiet >= warnQuietSeconds && !state.stopping && !state.warned) {
            state.warned = true;
            say("Nobody has spoken for 4 minutes: listening stops in a minute unless someone speaks.", false, true);
        } else if (quiet < warnQuietSeconds && state.warned) {
            state.warned = false;
            say("Listening: someone spoke, so listening carries on.");
        }
    }

    function pause(reason) {
        if (state.paused || state.recorder?.state !== "recording") {
            return;
        }

        state.paused = true;
        state.recorder.pause();
        state.stream.getAudioTracks().forEach((track) => { track.enabled = false; });
        state.recordedMs += Date.now() - state.resumedAt;
        state.resumedAt = 0;
        clearInterval(state.clock);
        clock.textContent = `Paused at ${minutes(recordedSeconds())} of ${minutes(state.session?.maxSeconds ?? 0)}.`;
        clock.dataset.state = "idle";
        pauseButton.textContent = "Resume listening";
        document.title = `Paused: ${title}`;
        say(reason, false);
    }

    function resume() {
        if (!state.paused || state.recorder?.state !== "paused") {
            return;
        }

        state.paused = false;
        state.stream.getAudioTracks().forEach((track) => { track.enabled = true; });
        state.recorder.resume();
        state.resumedAt = Date.now();
        state.clock = setInterval(showRecording, 1000);
        pauseButton.textContent = "Pause listening";
        showRecording();
        say("Listening again: the visit is being recorded.");
        pump();
    }

    function stopTracks(of = state) {
        of.stream?.getTracks().forEach((track) => { track.enabled = false; track.stop(); });
    }

    // Ends the listening here at once and keeps nothing: the microphone first, then what was sent.
    function end(message) {
        const session = state.session;
        stopHere();
        document.title = title;
        capture.hidden = true;
        speakersArea.hidden = true;
        again.hidden = true;
        say(message, true);
        if (session) {
            forget(session);
        }
    }

    // The review, then the service's session: both discarded. Without an answer, the service discards it after ten idle
    // minutes, and the review stays to be discarded from the draft.
    function forget(session) {
        call("POST", `${reviewApi}/review/${session.reviewId}/discard`).catch(() => { });
        call("DELETE", `${api}/${session.sessionId}`).catch(() => { });
    }

    // Ends it here for good: nothing more recorded, queued or sent.
    function stopHere() {
        state.ended = true;
        state.queue = [];
        clearInterval(state.clock);
        clearTimeout(state.retryTimer);
        if (state.recorder && state.recorder.state !== "inactive") {
            state.recorder.stop();
        }

        stopTracks();
    }

    // Back to the consent, for a listening of its own, with the cursor on its heading: the button just used has gone.
    function askAgain() {
        state = fresh();
        consent.hidden = false;
        consent.reset();
        consent.querySelector("button").disabled = false;
        part("consent-heading").focus();
    }

    async function discard(message) {
        end(message || "Discarded: nothing recorded was kept.");
        statusArea.classList.remove("sc-status--error");
        askAgain();
    }

    // 3. Stop and draft: the rest of the audio goes up, and the server transcribes the visit and tells its voices apart.
    function stopAndDraft(message) {
        if (state.stopping || state.ended) {
            return;
        }

        state.stopping = true;
        state.paused = false;
        clearInterval(state.clock);
        document.title = title;
        capture.hidden = true;
        say(message || "Stopped. Transcribing the visit and telling its voices apart: this takes about a minute.");
        if (state.recorder && state.recorder.state !== "inactive") {
            state.recorder.stop();
        } else {
            stopTracks();
            pump();
        }
    }

    async function finishRecording() {
        if (state.ended || !state.stopping || state.voices.length > 0) {
            return;
        }

        let result = await ask("stop", undefined, beforeNaming);
        while (result && result.sorting === "pending" && !state.ended) {
            result = await ask("result", undefined, beforeNaming);
        }

        if (state.ended) {
            return;
        }

        if (!result) {
            stuck(true);
        } else if (result.sorting !== "awaiting_speakers") {
            // Not transcribed and its voices told apart: nothing a visit can be drafted from.
            say("This visit couldn't be transcribed and its voices told apart, so it can't be drafted. Nothing was added to the note. Discard the recording, and dictate or type the note.", true);
            stuck(false);
        } else if ((result.speakers ?? []).length > mostVoices) {
            say(`The dictation service told apart more voices than can be named (${mostVoices} at most), so this visit can't be drafted. Nothing was added to the note. Discard the recording, and dictate or type the note.`, true);
            stuck(false);
        } else {
            showVoices(result);
        }
    }

    const beforeNaming = "The visit couldn't be fetched: the dictation service isn't answering, so its voices can't be named yet. Nothing was added to the note. Try again in a moment, or discard the recording; the service keeps nothing for more than 10 minutes.";
    const afterNaming = "The draft couldn't be fetched: the dictation service isn't answering. Nothing was added to the note. Try again in a moment, or open the draft in a few minutes to retrieve it, or discard it there; the service keeps nothing for more than 10 minutes.";

    // Where the page waits when the visit can't be fetched or drafted, never a dead end. Before the voices are named:
    // try again (when it might work) or discard the recording, as leaving the page would. Once they have been handed
    // over: try again, or open the draft, where Retrieve stages it; leaving keeps it.
    function stuck(retry) {
        retryButton.hidden = !retry;
        discardStuck.hidden = state.handedOff;
        openDraft.hidden = !state.handedOff;
        if (state.session?.editorUrl) {
            openDraft.href = state.session.editorUrl;
        }

        again.hidden = false;
        (retry ? retryButton : state.handedOff ? openDraft : discardStuck).focus();
    }

    async function tryAgain() {
        again.hidden = true;
        state.retryMs = firstRetryMs;
        if (state.handedOff) {
            say("Fetching the draft again.");
            await fetchDraft(await ask("result", undefined, afterNaming));
        } else {
            say("Fetching the visit again.");
            await finishRecording();
        }
    }

    // One call that answers with the result, sent again with backoff while the connection is down or the service busy, a
    // few times at most. Null when it can't, having said why (gaveUp, when the service didn't answer) and what to do. A 502,
    // an answer this app couldn't use, isn't sent again: it would come back the same.
    async function ask(route, body, gaveUp) {
        for (let attempt = 1; ; attempt++) {
            if (state.ended) {
                return null;
            }

            let response;
            try {
                response = await call("POST", `${api}/${state.session.sessionId}/${route}`, body, body ? "application/json" : undefined);
            } catch {
                response = { ok: false, status: 0 };
            }

            if (response.ok) {
                state.retryMs = firstRetryMs;
                return json(response);
            }

            if (!transient(response.status) || response.status === 502) {
                say(await refusal(response), true);
                return null;
            }

            if (attempt >= mostAttempts) {
                say(gaveUp, true);
                return null;
            }

            await new Promise((resolve) => setTimeout(resolve, state.retryMs));
            state.retryMs = Math.min(state.retryMs * 2, mostRetryMs);
        }
    }

    // Each voice with its first words and how long it spoke, and no role chosen: the clinician chooses every one. Beyond
    // the two voices that must be the clinician and the patient, a voice may be someone else, if the consent was for three,
    // and "I can't tell" (a fragment of someone's, say) for any number; the page says so when the consent was for two.
    function showVoices(result) {
        const text = result.transcript?.text ?? "";
        const segments = result.transcript?.segments ?? [];
        state.voices = (result.speakers ?? []).map((voice) => voice.group);
        voicesArea.replaceChildren(...(result.speakers ?? []).map((voice) => {
            const fieldset = element("fieldset", undefined, "sc-stack");
            fieldset.append(element("legend", `Voice ${voice.group}: spoke for ${minutes(voice.seconds)}`));
            const words = segments.filter((segment) => segment.speakerGroup === voice.group).slice(0, 3)
                .map((segment) => text.slice(segment.start, segment.end)).join(" … ");
            fieldset.append(element("p", words ? `“${words}”` : "Nothing it said could be made out."));
            const field = element("div", undefined, "sc-field");
            const id = `ambient-voice-${voice.group}`;
            const label = element("label", `Who is voice ${voice.group}?`);
            label.htmlFor = id;
            const select = element("select", undefined, "sc-field__input");
            select.id = id;
            select.dataset.ambientVoice = String(voice.group);
            select.append(new Option("Choose who it is", ""));
            const beyond = state.voices.length > 2;
            for (const [role, words] of Object.entries(roleWords)) {
                if (role === "other" ? beyond && state.consented > 2 : role !== "unsure" || beyond) {
                    select.append(new Option(words, role));
                }
            }

            select.addEventListener("input", checkVoices);
            select.addEventListener("change", checkVoices);
            field.append(label, select);
            fieldset.append(field);
            return fieldset;
        }));
        part("voices-two").hidden = !(state.voices.length > 2 && state.consented < 3);
        speakersArea.hidden = false;
        say("Say who each voice is, then select Draft the note.");
        checkVoices();
        voicesArea.querySelector("select")?.focus();
    }

    function chosenRoles() {
        return [...voicesArea.querySelectorAll("[data-ambient-voice]")].map((select) => [select.dataset.ambientVoice, select.value]);
    }

    // As the API checks it (Api/Dictation/AmbientApi.cs): every voice, one clinician, one patient, someone else at most
    // once, and no more named, all but "I can't tell", than the consent said would speak.
    function checkVoices() {
        const roles = chosenRoles().map(([, role]) => role);
        const count = (name) => roles.filter((role) => role === name).length;
        const complete = roles.every((role) => role !== "")
            && count("clinician") === 1 && count("patient") === 1 && count("other") <= 1
            && roles.filter((role) => role !== "unsure").length <= state.consented;
        draftButton.disabled = !complete;
        return complete;
    }

    async function draft() {
        if (!checkVoices() || state.drafting) {
            return;
        }

        state.drafting = true;
        draftButton.disabled = true;
        speakersArea.hidden = true;
        say("Drafting the note. Keep this page open until it's ready: a few minutes for a long visit.");
        const named = await ask("speakers", JSON.stringify({ speakers: Object.fromEntries(chosenRoles()) }),
            "The voices couldn't be sent: the dictation service isn't answering. Select Draft the note to try again, or discard the recording.");
        if (!named) {
            if (!state.ended) {
                state.drafting = false;
                speakersArea.hidden = false;
                checkVoices();
            }

            return;
        }

        // Handed over: the service drafts the note from here, and the draft can be retrieved from its page, so leaving
        // this one keeps it.
        state.handedOff = true;
        await fetchDraft(named);
    }

    // Asks for the draft while it is being written, then saves it for review.
    async function fetchDraft(answer) {
        let result = answer;
        while (result && result.sorting === "pending" && !state.ended) {
            result = await ask("result", undefined, afterNaming);
        }

        if (result) {
            await stage();
        } else if (!state.ended) {
            stuck(true);
        }
    }

    // The draft is saved for review on the server, which fetches it from the service itself; the page then goes there.
    async function stage() {
        try {
            const response = await call("POST", `${reviewApi}/review/${state.session.reviewId}/stage`,
                JSON.stringify({ version: state.session.version }), "application/json");
            const body = await json(response);
            if (response.ok && body.url) {
                state.persisted = true;
                window.location.assign(body.url);
                return;
            }

            say(body.errors?.join(" ") || body.message || "The draft wasn't saved for review. Open the draft to retrieve it.", true);
        } catch {
            say("The server couldn't confirm saving the draft. Open the draft to retrieve it.", true);
        }
    }

    function element(tag, text, className) {
        const node = document.createElement(tag);
        if (text !== undefined) {
            node.textContent = text;
        }

        if (className) {
            node.className = className;
        }

        return node;
    }

    consent.addEventListener("submit", start);
    pauseButton.addEventListener("click", () => (state.paused ? resume() : pause("Paused: nothing is being recorded. Select Resume listening to carry on.")));
    part("stop").addEventListener("click", () => stopAndDraft());
    part("discard").addEventListener("click", () => discard());
    part("discard-recording").addEventListener("click", () => discard());
    discardStuck.addEventListener("click", () => {
        again.hidden = true;
        discard();
    });
    retryButton.addEventListener("click", tryAgain);
    draftButton.addEventListener("click", draft);

    // Out of sight, the recording pauses, and resumes only when the clinician says so.
    document.addEventListener("visibilitychange", () => {
        if (document.visibilityState === "hidden") {
            pause(hiddenMessage);
        }
    });

    window.addEventListener("beforeunload", (event) => {
        if (state.session && !state.persisted && !state.ended && !state.handedOff) {
            event.preventDefault();
            event.returnValue = "";
        }
    });

    // Leaving the page ends listening here for good: the microphone off, nothing waiting kept, nothing sent again. Until the
    // voices are handed over it discards it all too, as Stop and discard would; once the service has taken them, the
    // draft is kept, saved for review whenever it is retrieved from the draft.
    window.addEventListener("pagehide", () => {
        if (!state.session || state.persisted || state.ended) {
            return;
        }

        const session = state.session;
        stopHere();
        state.left = true;
        if (!state.handedOff) {
            const headers = { RequestVerificationToken: token };
            fetch(`${reviewApi}/review/${session.reviewId}/discard`, { method: "POST", headers, credentials: "same-origin", keepalive: true }).catch(() => { });
            fetch(`${api}/${session.sessionId}`, { method: "DELETE", headers, credentials: "same-origin", keepalive: true }).catch(() => { });
        }
    });

    // Back from the browser's back/forward cache: the listening ended when the page was left, and the page says so, never
    // "Recording".
    window.addEventListener("pageshow", (event) => {
        if (!event.persisted || !state.left) {
            return;
        }

        document.title = title;
        capture.hidden = true;
        speakersArea.hidden = true;
        if (state.handedOff) {
            say("Listening ended when you left this page while the note was being drafted. Open the draft to retrieve it.");
        } else {
            say("Listening ended when you left this page: nothing recorded was kept.");
            askAgain();
        }
    });
})();
