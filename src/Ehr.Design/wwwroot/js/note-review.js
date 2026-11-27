"use strict";
(() => {
    const root = document.querySelector("[data-note-review]");
    if (!root) return;
    const view = new window.SwiftChartDictationView(root);
    const status = document.getElementById("note-save-status");
    const errors = document.getElementById("note-errors");
    let snapshot = JSON.parse(root.querySelector("[data-review-snapshot]").textContent);
    // A recorded visit's transcript (step 16), beside the snapshot when the page read it; a decision's answer has none.
    const conversation = root.querySelector("[data-review-transcript]");
    const transcript = conversation ? JSON.parse(conversation.textContent) : null;
    let busy = false;
    const dirty = new Set();
    const buttons = () => root.querySelectorAll("button");
    const stateWords = { pending: "Awaiting review", accepted: "Accepted", edited: "Edited", rejected: "Rejected" };

    function showErrors(messages) {
        errors.replaceChildren(...messages.map(text => {
            const p = document.createElement("p"); p.textContent = text; return p;
        }));
        errors.hidden = messages.length === 0;
        if (messages.length) errors.focus();
    }

    function render() {
        if (snapshot.result) view.renderResult(transcript ? { ...snapshot.result, transcript } : snapshot.result);
        for (const proposal of snapshot.proposals) {
            const card = root.querySelector(`[data-proposal-id="${proposal.id}"]`);
            if (!card) continue;
            card.dataset.reviewState = proposal.state;
            card.classList.toggle("sc-proposal--pending", proposal.state === "pending");
            // The state as a tag: its words, and the tone that supports them (Review.cshtml renders the same).
            const tag = card.querySelector("[data-field-status]");
            tag.textContent = stateWords[proposal.state] ?? proposal.state;
            tag.className = proposal.state === "pending" ? "sc-tag sc-tag--warning"
                : proposal.state === "accepted" || proposal.state === "edited" ? "sc-tag sc-tag--success" : "sc-tag";
            card.querySelector("[data-approved-value]").textContent = proposal.state === "rejected"
                ? "Rejected." : proposal.approvedValue ? `Approved value: ${proposal.approvedValue}` : "";
            const controls = card.querySelector("[data-review-controls]");
            if (controls) controls.hidden = proposal.state !== "pending";
        }
        if (snapshot.state === "completed" && snapshot.proposals.length > 0)
            root.querySelector("[data-dictation-sort-message]").textContent = "Review complete. Continue to the note to edit or sign.";
        else if (snapshot.result?.sorting === "done" && snapshot.proposals.length === 0)
            root.querySelector("[data-dictation-sort-message]").textContent = "No usable proposals. Continue to the note to write it yourself.";
        const reviewed = snapshot.proposals.filter(p => p.state !== "pending").length;
        root.querySelector("[data-review-progress]").textContent = `${reviewed} of ${snapshot.proposals.length} ${snapshot.proposals.length === 1 ? "proposal" : "proposals"} reviewed.`;
        root.querySelectorAll('[name="Version"]').forEach(field => { field.value = snapshot.version; });
        const finish = root.querySelector("[data-review-complete] button");
        if (finish) finish.disabled = busy || reviewed !== snapshot.proposals.length;
    }

    root.querySelectorAll("textarea").forEach(field => field.addEventListener("input", () => dirty.add(field.id)));
    root.querySelectorAll("[data-review-submit]").forEach(form => form.addEventListener("submit", async event => {
        event.preventDefault();
        if (busy) return;
        const data = new FormData(form);
        if (event.submitter?.name) data.set(event.submitter.name, event.submitter.value);
        busy = true;
        buttons().forEach(button => { button.disabled = true; });
        status.textContent = "Saving decision…";
        let saved = false;
        try {
            const response = await fetch(form.action, {
                method: "POST", body: data, credentials: "same-origin", signal: AbortSignal.timeout(35000),
                headers: { "HX-Request": "true", Accept: "application/json" },
            });
            if (response.redirected || !response.headers.get("Content-Type")?.includes("application/json")) throw new Error();
            const result = await response.json();
            if (!response.ok) {
                showErrors(result.errors || ["Not saved. Check the saved draft and try again."]);
                status.textContent = "Not saved";
                return;
            }
            form.querySelectorAll("textarea").forEach(field => dirty.delete(field.id));
            showErrors([]);
            status.textContent = "Decision saved";
            if (result.url) { window.location.assign(result.url); return; }
            snapshot = result.snapshot;
            saved = true;
            render();
        } catch {
            status.textContent = "Not saved";
            showErrors(["The clinic's server didn't confirm this decision. Try again."]);
        } finally {
            busy = false;
            buttons().forEach(button => { button.disabled = false; });
            render();
            if (saved) {
                const next = root.querySelector('[data-review-state="pending"] [name="Decision"][value="accepted"]')
                    || root.querySelector("[data-review-complete] button");
                next?.focus();
            }
        }
    }));
    root.querySelector("[data-review-retrieve]")?.addEventListener("click", async () => {
        if (busy) return;
        busy = true;
        status.textContent = "Retrieving the finished dictation…";
        try {
            const response = await fetch(root.dataset.stageUrl, {
                method: "POST", credentials: "same-origin", signal: AbortSignal.timeout(35000),
                headers: { "Content-Type": "application/json", RequestVerificationToken: root.querySelector('[name="__RequestVerificationToken"]').value },
                body: JSON.stringify({ version: snapshot.version }),
            });
            const result = await response.json();
            if (response.ok && result.url) { window.location.assign(result.url); return; }
            showErrors(result.errors || [result.message || "The dictation couldn't be retrieved. Try again."]);
            status.textContent = "Not retrieved";
        } catch {
            status.textContent = "Not retrieved";
            showErrors(["The clinic's server couldn't retrieve the dictation. Try again, or discard it."]);
        } finally { busy = false; }
    });
    window.addEventListener("beforeunload", event => {
        if (dirty.size || busy) { event.preventDefault(); event.returnValue = ""; }
    });
    render();
    if (!errors.hidden) errors.focus();
    // Back from deciding a proposed order or allergy (step 15): the next one waiting, as the page marked it.
    else (root.querySelector("[data-chart-proposals] [autofocus]")
        || root.querySelector('[data-review-state="pending"] [name="Decision"][value="accepted"]')
        || root.querySelector("[data-review-complete] button"))?.focus();
})();
