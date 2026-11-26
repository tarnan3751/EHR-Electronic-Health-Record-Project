// Dictating a note (UC 9 Transcribe dictated audio, UC 10 Structure and validate note fields; Samir Mukshe). The script
// for Areas/Clinical/Pages/Patients/Dictate: it records the microphone, sends the recording to the clinician dictation
// API (Ehr.Web, Api/Dictation/DictationApi.cs, whose header is the contract) in numbered chunks, and shows the
// transcript as the clinician speaks. After Stop it asks for the result and shows the Subjective, Objective, Assessment
// and Plan the model proposed and validation let through, each awaiting review, or says why the fields couldn't be
// sorted.
//
// What it handles, from UC 9 and UC 10's alternative courses:
// - no microphone, or one the browser blocks: says how to fix it, and the page stays usable;
// - a dropped connection: recording carries on, the chunks wait here in order, and they are sent again with backoff,
//   from the one the API expects next (409 out_of_order), so the transcript resumes where it paused;
// - a passage too quiet, too noisy or too uncertain to trust, or in Spanish not heard as Spanish: marked in the
//   transcript, to say again;
// - the model unavailable or too slow: the transcript stays as plain text, with the API's message;
// - an order or allergy change dictated: listed as not carried over, never filled into a field (FR-14);
// - the dictation's limit, 3 minutes or 2 in Spanish as the service gives it: the time left shown beside Stop, the
//   status line saying a minute ahead that it stops then, at its limit, and when 30 and 10 seconds are left, and at the limit the recording stopped, the page's
//   own words for it shown, said once with the transcript's finishing, and what was recorded taken on to the review;
// - Cancel: the session is deleted, so its audio and transcript are discarded, and the screen is cleared.
// The language is chosen for each dictation, English first (the expansion plan's step 17): sent with the start as
// spokenLanguage, never remembered, and fixed while a dictation is under way; a Spanish transcript is marked lang="es".
// Finished results are staged through the server-owned review binding; no AI text is approved here (FR-13). The audio goes up in whatever format the browser records (AGENTS.md: never
// hard-code one), and every piece of text is set with textContent, never parsed as HTML. Wired here rather than in
// attributes, so a strict CSP can be added later.
"use strict";

(() => {
    const root = document.querySelector("[data-dictation]");
    if (!root) {
        return;
    }

    const part = (name) => root.querySelector(`[data-dictation-${name}]`);
    const api = root.dataset.dictationApi;
    const token = root.querySelector("input[name=__RequestVerificationToken]")?.value ?? "";
    const startButton = part("start");
    const languageChoice = part("language");
    const languageHint = part("language-hint");
    const stopButton = part("stop");
    const cancelButton = part("cancel");
    const timeLeft = part("time-left");
    const timeLeftValue = part("time-left-value");
    const statusArea = part("status");
    const limitNotice = part("limit");
    const recordingMark = part("recording");
    const transcriptArea = part("transcript");
    const unclearNotice = part("unclear");
    const sortMessage = part("sort-message");
    const notCarried = part("not-carried");
    const notCarriedList = part("not-carried-list");
    // The fields the page shows (the expansion plan's step 14: subjective, objective, assessment and plan).
    const fieldNames = [...root.querySelectorAll("[data-dictation-field]")].map((field) => field.dataset.dictationField);

    const view = new window.SwiftChartDictationView(root);
    const renderTranscript = (...args) => view.renderTranscript(...args);
    const renderResult = (...args) => view.renderResult(...args);
    const reviewButton = part("review");
    const manualLink = part("manual");

    // How long to wait before sending again after a failure: doubling, up to the most.
    const firstRetryMs = 1000;
    const mostRetryMs = 10000;
    // What the status line says as the time left reaches each point, in seconds, given the limit in minutes: a minute
    // ahead, in the Listen page's words for a visit ("Listening stops in a minute, at its 20-minute limit."), then twice
    // near the end, whatever the limit. The time left itself is shown beside Stop.
    const leftToSay = [
        [60, (limit) => `Dictation stops in a minute, at its ${limit}-minute limit.`],
        [30, () => "30 seconds of dictation left. Stop soon, then dictate the rest separately."],
        [10, () => "10 seconds of dictation left. Stop soon, then dictate the rest separately."],
    ];

    let state = fresh();

    function fresh() {
        return {
            session: null,
            stream: null,
            recorder: null,
            queue: [],
            nextSeq: 0,
            sending: false,
            paused: false,
            retryMs: firstRetryMs,
            retryTimer: 0,
            stopRequested: false,
            // The service refused audio past the dictation's limit, so nothing more is sent.
            atLimit: false,
            // At the limit (the service's too_long), the page's words for it, said again as the transcript is finished.
            limitWords: null,
            // The last of leftToSay the time left has reached.
            leftSaid: Infinity,
            stopping: false,
            ended: false,
            cancelled: false,
            transcript: null,
            persisted: false,
            staging: false,
        };
    }

    // "Cancel" stops a dictation under way; once there is a transcript it becomes "Discard", which throws work away
    // and is outlined in danger (docs/brand: Button).
    function cancelAs(text) {
        cancelButton.textContent = text;
        cancelButton.classList.toggle("sc-btn--danger", text === "Discard");
        cancelButton.classList.toggle("sc-btn--quiet", text !== "Discard");
    }

    function say(text, isError = false) {
        statusArea.textContent = text;
        statusArea.classList.toggle("sc-status--error", isError);
    }

    // The language can be changed only while a dictation can be started, never in the middle of one.
    function startable(enabled) {
        startButton.disabled = !enabled;
        languageChoice.disabled = !enabled;
    }

    // The hint, read with the choice, says what a dictation in Spanish is.
    function showLanguage() {
        const spanish = languageChoice.value === "es";
        languageHint.hidden = !spanish;
        if (spanish) {
            languageChoice.setAttribute("aria-describedby", languageHint.id);
        } else {
            languageChoice.removeAttribute("aria-describedby");
        }
    }

    // While recording, the status line carries a dot and the transcript's head says "Recording": a word beside each.
    function buttons(recording) {
        statusArea.dataset.state = recording ? "recording" : "idle";
        // For session.js (the expansion plan's step 18): no session warning or end while a dictation is recorded, sent or
        // transcribed, from Dictate until its result, a failure or Cancel.
        if (recording) {
            document.body.dataset.sessionRecording = "recording";
        } else {
            delete document.body.dataset.sessionRecording;
        }

        recordingMark.hidden = !recording;
        startable(!recording);
        stopButton.disabled = !recording;
        cancelButton.disabled = !recording && state.session === null;
    }

    async function call(method, path, body, type) {
        const headers = { RequestVerificationToken: token };
        if (type) {
            headers["Content-Type"] = type;
        }

        return fetch(api + path, { method, headers, body, credentials: "same-origin" });
    }

    async function json(response) {
        try {
            return await response.json();
        } catch {
            return {};
        }
    }

    // The API's own words when it gives them; a status-based fallback otherwise.
    async function refusal(response) {
        const body = await json(response);
        return body.errors?.join(" ") || body.message || `Dictation failed (${response.status}). Try again in a moment.`;
    }

    // A failure worth sending again: no connection, the service busy, unavailable or slow, or the cloud app unable to
    // reach the on-prem app for a moment.
    function transient(status) {
        return status === 0 || status === 429 || status >= 500;
    }

    // UC 9 step 1: the microphone first, so nothing starts on the server when it can't be recorded.
    async function start() {
        if (!navigator.mediaDevices?.getUserMedia || !window.MediaRecorder) {
            say("This browser can't record audio. Use a current version of Chrome, Edge, Firefox or Safari, or type the note.", true);
            return;
        }

        if (state.session) {
            say("Discard this unfinished dictation before starting another. Its transcript is still displayed.", true);
            return;
        }
        // Reuse the request ID when start's acknowledgement was lost: the server returns its saved binding.
        const requestId = state.requestId || crypto.randomUUID();
        state = fresh();
        state.requestId = requestId;
        const spokenLanguage = languageChoice.value === "es" ? "es" : "en";
        startable(false);
        resetScreen();
        if (spokenLanguage === "es") {
            transcriptArea.lang = "es";
        }

        cancelAs("Cancel");
        try {
            state.stream = await navigator.mediaDevices.getUserMedia({ audio: true });
        } catch (error) {
            buttons(false);
            say(microphoneProblem(error), true);
            return;
        }

        let response;
        try {
            response = await call("POST", "/for-note", JSON.stringify({
                reviewId: state.requestId, patientId: root.dataset.patientId,
                draftId: root.dataset.draftId || null, version: Number(root.dataset.draftVersion) || null, spokenLanguage,
            }), "application/json");
        } catch {
            endRecording();
            buttons(false);
            say("The dictation service can't be reached. Check your connection and try again.", true);
            return;
        }

        if (response.status !== 201) {
            endRecording();
            buttons(false);
            say(await refusal(response), true);
            return;
        }

        state.session = await json(response);
        root.dataset.draftId = state.session.draftId;
        root.dataset.draftVersion = state.session.version;
        manualLink.href = state.session.editorUrl;
        manualLink.hidden = false;
        root.querySelector("[data-dictation-new-manual]").hidden = true;
        if (state.session.state !== "recording") {
            endRecording();
            state.persisted = true;
            window.location.assign(state.session.url);
            return;
        }
        // No mimeType: the browser picks the format it records (WebM in Chromium, MP4 in WebKit), and the server
        // decodes either.
        const recorder = new MediaRecorder(state.stream);
        state.recorder = recorder;
        recorder.addEventListener("dataavailable", (event) => {
            if (event.data && event.data.size > 0 && !state.cancelled && !state.atLimit) {
                state.queue.push({ seq: state.nextSeq++, blob: event.data, type: event.data.type || recorder.mimeType || "audio/*" });
                pump();
            }
        });
        recorder.addEventListener("stop", () => {
            stopTracks();
            timeLeft.hidden = true;
            if (!state.cancelled) {
                state.stopRequested = true;
                pump();
            }
        });
        recorder.start(1000);
        // The whole limit, until the service says how much audio it holds.
        timeLeftValue.textContent = clock(state.session.maxSeconds);
        timeLeft.hidden = false;
        buttons(true);
        say("Recording. Speak your note, then select Stop.");
    }

    function microphoneProblem(error) {
        switch (error?.name) {
            case "NotAllowedError":
            case "SecurityError":
                return "The microphone is blocked. Allow it for this site in the browser's settings (the icon beside the address), then select Dictate again. You can still type the note.";
            case "NotFoundError":
            case "OverconstrainedError":
                return "No microphone was found. Connect one, then select Dictate again. You can still type the note.";
            case "NotReadableError":
                return "The microphone is in use by another app. Close it, then select Dictate again.";
            default:
                return "The microphone couldn't be started. You can still type the note.";
        }
    }

    // Sends the waiting chunks in order, one at a time. On a failure worth retrying the chunks stay here and are sent
    // again later; the recording carries on meanwhile (UC 9: the connection drops).
    async function pump() {
        if (state.sending || state.cancelled || state.ended || !state.session) {
            return;
        }

        state.sending = true;
        try {
            while (state.queue.length > 0 && !state.cancelled) {
                const chunk = state.queue[0];
                let response;
                try {
                    response = await call("POST", `/${state.session.sessionId}/audio?seq=${chunk.seq}`, chunk.blob, chunk.type);
                } catch {
                    pauseAndRetry();
                    return;
                }

                if (state.cancelled) {
                    return;
                }

                if (response.ok) {
                    const received = await json(response);
                    state.queue.shift();
                    resumed();
                    renderTranscript(received.transcript);
                    showTimeLeft(received.audioSeconds);
                    continue;
                }

                const body = await json(response);
                if (response.status === 409 && body.error === "out_of_order" && typeof body.expectedSeq === "number") {
                    // The API already has everything before expectedSeq: send from there.
                    state.queue = state.queue.filter((waiting) => waiting.seq >= body.expectedSeq);
                    continue;
                }

                if (response.status === 409 && body.error === "stopped") {
                    state.queue = [];
                    break;
                }

                if (transient(response.status)) {
                    pauseAndRetry();
                    return;
                }

                if (response.status === 413) {
                    // Over the dictation's limit: what was sent is kept, and the dictation stops here. Nothing more is
                    // sent, not even the last of the audio the recorder hands over as it stops, which would only be
                    // refused as out of order, again and again.
                    state.queue = [];
                    state.atLimit = true;
                    stopButton.disabled = true;
                    if (body.error === "too_long") {
                        // The page's own words, naming the limit, shown until the dictation goes on to its review. The
                        // notice isn't a live region: the status line says the limit once, as it says the transcript
                        // is being finished, which follows at once (finish()), rather than a moment before it.
                        state.limitWords = `The dictation stopped at its ${state.session.maxSeconds / 60}-minute limit.`;
                        limitNotice.textContent = `${state.limitWords} What you recorded is kept and goes on to the review. `
                            + "Start another dictation for the rest once you've reviewed it.";
                        limitNotice.hidden = false;
                    } else {
                        say(body.message || "The dictation is over its time limit, so it has been stopped.", true);
                    }

                    if (state.recorder?.state === "recording") {
                        state.recorder.stop();
                    }

                    break;
                }

                fail(body.message || `Dictation failed (${response.status}).`);
                return;
            }
        } finally {
            state.sending = false;
        }

        if (state.stopRequested && state.queue.length === 0) {
            finish();
        }
    }

    function pauseAndRetry() {
        if (!state.paused) {
            state.paused = true;
            say("The connection to the dictation service dropped. Still recording on this device; the transcript is paused and catches up when the connection returns.", true);
        }

        clearTimeout(state.retryTimer);
        state.retryTimer = setTimeout(pump, state.retryMs);
        state.retryMs = Math.min(state.retryMs * 2, mostRetryMs);
    }

    function resumed() {
        state.retryMs = firstRetryMs;
        if (state.paused) {
            state.paused = false;
            say(state.stopRequested ? "Connection back. Sending the rest of the recording." : "Connection back. Recording; the transcript has caught up.");
        }
    }

    // The time left beside Stop: the dictation's limit (the session's maxSeconds, 3 minutes or 2 in Spanish) less the
    // audio the service holds. It changes with every chunk, so it isn't a live region: the status line says each of
    // leftToSay as the time left reaches it, once, and only while recording. Points passed together, as the chunks
    // held back by a dropped connection go up, are said as the last of them.
    function showTimeLeft(audioSeconds) {
        const left = Math.floor(state.session.maxSeconds - audioSeconds);
        timeLeftValue.textContent = clock(left);
        const reached = leftToSay.filter(([at]) => left <= at && at < state.leftSaid).at(-1);
        if (reached) {
            state.leftSaid = reached[0];
            if (state.recorder?.state === "recording") {
                say(reached[1](state.session.maxSeconds / 60));
            }
        }
    }

    // Minutes and seconds, as the time left is shown: 2:00, 0:09.
    function clock(seconds) {
        return `${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, "0")}`;
    }

    function stop() {
        stopButton.disabled = true;
        if (state.recorder?.state === "recording") {
            say("Stopping…");
            state.recorder.stop();
        }
    }

    // UC 9 step 3 and UC 10: stop, then ask for the result until the fields are sorted, or can't be.
    async function finish() {
        if (state.stopping || state.cancelled) {
            return;
        }

        state.stopping = true;
        const finishing = "Finishing the transcript and proposing the fields. This can take up to a minute.";
        say(state.limitWords ? `${state.limitWords} ${finishing}` : finishing);
        let result = await ask("stop");
        while (result && result.sorting === "pending" && !state.cancelled) {
            result = await ask("result");
        }

        if (result && !state.cancelled) {
            state.ended = true;
            buttons(false);
            cancelButton.disabled = false;
            cancelAs("Discard");
            startable(false);
            say(result.sorting === "done"
                ? "Done. Review each proposed field; nothing is in the note until you approve it."
                : "Done, but the fields couldn't be sorted. The transcript is kept as plain text.");
            await stageReview();
        }
    }

    async function stageReview() {
        if (!state.session?.reviewId || state.staging) return;
        state.staging = true;
        reviewButton.hidden = false;
        reviewButton.disabled = true;
        try {
            const response = await call("POST", `/review/${state.session.reviewId}/stage`,
                JSON.stringify({ version: state.session.version }), "application/json");
            const body = await json(response);
            if (!response.ok || !body.url) {
                if (Number.isSafeInteger(body.version)) state.session.version = body.version;
                say(body.errors?.join(" ") || body.message || "The proposals were not saved for review. Your transcript is still here; compare the saved draft and retry, or continue manually.", true);
                return;
            }
            state.persisted = true;
            window.location.assign(body.url);
        } catch {
            say("The server could not confirm saving the proposals. Your transcript is still here. Retry saving them; a completed save will not be duplicated.", true);
        } finally {
            state.staging = false;
            reviewButton.disabled = false;
        }
    }

    // One stop or result call, sent again with backoff while the connection is down. Null when it can't go on.
    async function ask(route) {
        for (;;) {
            if (state.cancelled) {
                return null;
            }

            let response;
            try {
                response = await call("POST", `/${state.session.sessionId}/${route}`);
            } catch {
                response = { ok: false, status: 0 };
            }

            if (response.ok) {
                state.retryMs = firstRetryMs;
                const result = await json(response);
                renderResult(result);
                return result;
            }

            if (!transient(response.status)) {
                fail(await refusal(response));
                return null;
            }

            say("Waiting for the dictation service to answer…", true);
            await new Promise((resolve) => setTimeout(resolve, state.retryMs));
            state.retryMs = Math.min(state.retryMs * 2, mostRetryMs);
        }
    }

    function fail(message) {
        endRecording();
        state.ended = true;
        buttons(false);
        startable(state.session === null);
        cancelButton.disabled = state.session === null;
        say(message, true);
    }

    // UC 9 and UC 10, any step: the clinician cancels. The session is deleted, discarding its audio, transcript and
    // proposed fields; a session whose delete doesn't arrive is discarded by the service after ten idle minutes.
    async function cancel() {
        const session = state.session;
        state.cancelled = true;
        clearTimeout(state.retryTimer);
        endRecording();
        if (session?.reviewId) {
            try {
                const response = await call("POST", `/review/${session.reviewId}/discard`);
                if (!response.ok) {
                    const body = await json(response);
                    throw new Error(body.errors?.join(" ") || body.message || "Discard was not confirmed.");
                }
            } catch (error) {
                state.cancelled = false;
                state.ended = true;
                buttons(false);
                startable(false);
                cancelButton.disabled = false;
                say(`${error.message} Your transcript is still displayed; saved proposals must be reviewed individually.`, true);
                return;
            }
        }
        state.queue = [];
        resetScreen();
        buttons(false);
        cancelButton.disabled = true;
        cancelAs("Cancel");
        if (session) {
            try {
                await call("DELETE", `/${session.sessionId}`);
            } catch {
                // Nothing to do: the service discards it on its own.
            }
        }

        state = fresh();
        say("Temporary dictation discarded. The manual draft was kept; no AI text was approved.");
    }

    function endRecording() {
        if (state.recorder && state.recorder.state !== "inactive") {
            state.recorder.stop();
        }

        stopTracks();
    }

    function stopTracks() {
        state.stream?.getTracks().forEach((track) => track.stop());
    }

    function resetScreen() {
        state.transcript = null;
        reviewButton.hidden = true;
        limitNotice.hidden = true;
        transcriptArea.removeAttribute("lang");
        transcriptArea.replaceChildren(element("p", "Your words appear here as you speak.", "sc-transcript__placeholder"));
        unclearNotice.hidden = true;
        sortMessage.textContent = "The fields are proposed once you stop.";
        for (const name of fieldNames) {
            const field = root.querySelector(`[data-dictation-field="${name}"]`);
            const status = field.querySelector("[data-field-status]");
            status.textContent = "Not dictated yet";
            status.className = "sc-tag";
            field.querySelector("[data-field-value]").replaceChildren();
            field.classList.remove("sc-proposal--pending", "sc-proposal--empty", "sc-proposal--selected");
        }

        notCarried.hidden = true;
        notCarriedList.replaceChildren();
    }

    function element(tag, text, className) {
        const node = document.createElement(tag);
        node.textContent = text;
        node.className = className || "";
        return node;
    }

    languageChoice.value = "en";
    showLanguage();
    languageChoice.addEventListener("change", showLanguage);
    // A page the browser kept and shows again (Back) starts in English too, if no dictation is under way.
    window.addEventListener("pageshow", (event) => {
        if (event.persisted && !languageChoice.disabled) {
            languageChoice.value = "en";
            showLanguage();
        }
    });
    reviewButton.addEventListener("click", stageReview);
    startButton.addEventListener("click", start);
    stopButton.addEventListener("click", stop);
    cancelButton.addEventListener("click", cancel);

    // Keyboard first (NFR-12): Alt+Shift+D starts dictation, and stops it while recording. Not Alt+D, which Chrome,
    // Edge and Firefox use on Windows to jump to the address bar: a page can take it over, but then the clinician loses
    // it, and Firefox can be set to stop pages overriding its shortcuts. No browser assigns Alt+Shift+D. By key
    // position, since Option changes the letter typed on a Mac.
    document.addEventListener("keydown", (event) => {
        if (event.altKey && event.shiftKey && !event.ctrlKey && !event.metaKey && event.code === "KeyD") {
            event.preventDefault();
            if (!stopButton.disabled) {
                stop();
            } else if (!startButton.disabled) {
                start();
            }
        }
    });

    window.addEventListener("beforeunload", event => {
        if (state.session && !state.persisted && !state.cancelled) {
            event.preventDefault(); event.returnValue = "";
        }
    });

    // Leaving the page mid-dictation discards it, as Cancel would.
    window.addEventListener("pagehide", () => {
        if (state.session && !state.cancelled) {
            endRecording();
            if (!state.persisted && state.session.reviewId) {
                fetch(`${api}/review/${state.session.reviewId}/discard`, {
                    method: "POST", headers: { RequestVerificationToken: token }, credentials: "same-origin", keepalive: true,
                }).catch(() => { });
            }
            fetch(`${api}/${state.session.sessionId}`, {
                method: "DELETE",
                headers: { RequestVerificationToken: token },
                credentials: "same-origin",
                keepalive: true,
            }).catch(() => { });
        }
    });
})();
