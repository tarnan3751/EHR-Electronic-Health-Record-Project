// Shared UC 9/10 transcript/source renderer, reused by the persisted UC 13 review screen.
// Offsets remain half-open UTF-16; all supplied text is rendered as text nodes.
"use strict";
window.SwiftChartDictationView = class {
    constructor(root) {
        const part = name => root.querySelector(`[data-dictation-${name}]`);
        const transcriptArea = part("transcript"), unclearNotice = part("unclear");
        const sortMessage = part("sort-message"), notCarried = part("not-carried"), notCarriedList = part("not-carried-list");
        // The fields the page shows: four since the expansion plan's step 14, and on the review of a dictation sorted
        // before then, its History, Assessment and Plan, as it was saved.
        const fieldNames = [...root.querySelectorAll("[data-dictation-field]")].map((field) => field.dataset.dictationField);
        const state = { transcript: null };
        // Words for the clinician beside each marked passage and each code the API sends.
        const unclearWords = {
            too_quiet: "too quiet to hear clearly",
            too_noisy: "too much background noise",
            low_confidence: "the speech model wasn't sure",
        };
        const warningWords = {
            possible_correction: "You may have corrected this later in the dictation: check the highlighted words.",
            near_unclear_passage: "A passage beside its source was hard to hear: check the highlighted words.",
            one_field_holds_dictation: "This section holds the whole dictation. Sort it into the sections it belongs to before accepting it.",
            // A recorded visit's (the expansion plan's step 16).
            speaker_unsure: "Who said this couldn't be told with confidence: check the highlighted words.",
            other_speaker: "Someone else in the room said this: check that it is about the patient.",
            patients_own_words: "In the patient's own words. Reword it as your note before you accept it.",
            part_not_offered: "Part of the visit had something for this section that didn't pass the checks: read the highlighted part.",
        };
        // Who said each passage of a recorded visit, in words beside it, never by colour alone.
        const speakerWords = { clinician: "Clinician", patient: "Patient", other: "Other person", unsure: "Unsure" };
        const instructionWords = { order: "Order", allergy_change: "Allergy change" };
        // Those the dictation proposes (the expansion plan's step 15). The review lists them apart, with their actions, and
        // its not-carried-over list leaves them out; the dictation page marks them in its list, read-only.
        const proposalWords = { order: "Proposed order", allergy_addition: "Proposed allergy to add" };
        const listedApart = root.querySelector("[data-chart-proposals]") !== null;

        // A field's status as a tag (docs/brand: Tag): the words say it, the tone only supports them.
        function setStatus(status, text, tone) {
            status.textContent = text;
            status.className = tone ? `sc-tag sc-tag--${tone}` : "sc-tag";
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

        // The transcript, its segments marked: unclear passages (with why), words not final yet, and the ranges to
        // highlight (a field's sources, FR-08). Offsets are UTF-16, as JavaScript strings index.
        function renderTranscript(transcript, highlights = []) {
            if (!transcript) {
                return;
            }

            state.transcript = transcript;
            const text = transcript.text ?? "";
            if (text.length === 0) {
                transcriptArea.replaceChildren(element("p", "Listening…", "sc-transcript__placeholder"));
                return;
            }

            const segments = transcript.segments ?? [];
            const points = new Set([0, text.length]);
            for (const range of [...segments, ...highlights]) {
                points.add(Math.max(0, Math.min(text.length, range.start)));
                points.add(Math.max(0, Math.min(text.length, range.end)));
            }

            const sorted = [...points].sort((a, b) => a - b);
            // A recorded visit's transcript is a paragraph a turn, each led by who said it; a dictation's, one paragraph.
            const turns = segments.some((s) => s.speaker);
            const paragraphs = [];
            let paragraph = element("p");
            let speaker = null;
            for (let i = 0; i + 1 < sorted.length; i++) {
                const [from, to] = [sorted[i], sorted[i + 1]];
                const segment = segments.find((s) => s.start <= from && to <= s.end);
                if (turns && !segment && !text.slice(from, to).trim()) {
                    continue;
                }

                if (turns && segment && segment.speaker !== speaker) {
                    if (paragraph.childNodes.length > 0) {
                        paragraphs.push(paragraph);
                    }

                    speaker = segment.speaker;
                    paragraph = element("p", undefined, "sc-transcript__turn");
                    paragraph.append(element("span", speakerWords[speaker] ?? "Unsure", "sc-transcript__speaker"), document.createTextNode(" "));
                }

                const highlighted = highlights.some((h) => h.start <= from && to <= h.end);
                let node = document.createTextNode(text.slice(from, to));
                if (segment?.unclear) {
                    const mark = element("mark", undefined, "sc-transcript__unclear");
                    mark.title = `Say this again: ${unclearWords[segment.unclear] ?? "it may be wrong"}.`;
                    mark.append(node);
                    node = mark;
                } else if (segment && !segment.final) {
                    const span = element("span", undefined, "sc-transcript__tentative");
                    span.append(node);
                    node = span;
                }

                if (highlighted) {
                    const mark = element("mark", undefined, "sc-transcript__source");
                    mark.append(node);
                    node = mark;
                }

                paragraph.append(node);
            }

            transcriptArea.replaceChildren(...paragraphs, paragraph);
            unclearNotice.hidden = !segments.some((s) => s.unclear);
        }

        function renderResult(result) {
            renderTranscript(result.transcript);
            if (result.sorting === "pending") {
                sortMessage.textContent = "Proposing the fields…";
            } else if (result.sorting === "unavailable") {
                sortMessage.textContent = result.message || "The fields couldn't be sorted, so the transcript is kept as plain text.";
            } else {
                sortMessage.textContent = "Each proposed field is awaiting your review. Select one to see the words it came from.";
            }

            for (const name of fieldNames) {
                renderField(name, result.fields?.[name], result.sorting);
            }

            const proposed = new Map((result.proposals ?? []).map((proposal) => [`${proposal.start}:${proposal.end}`, proposal.kind]));
            const items = (result.notCarriedOver ?? []).filter((item) => !(listedApart && proposed.has(`${item.start}:${item.end}`)));
            notCarried.hidden = items.length === 0;
            notCarriedList.replaceChildren(...items.map((item) => {
                const text = (result.transcript?.text ?? "").slice(item.start, item.end);
                const kind = proposed.get(`${item.start}:${item.end}`);
                const li = element("li");
                li.append(element("strong", `${(kind ? proposalWords[kind] : instructionWords[item.reason]) ?? "Instruction"}: `), document.createTextNode(`“${text}”`));
                highlightOnFocus(li, [item]);
                return li;
            }));
        }

        function renderField(name, field, sorting) {
            const container = root.querySelector(`[data-dictation-field="${name}"]`);
            const status = container.querySelector("[data-field-status]");
            const value = container.querySelector("[data-field-value]");
            container.classList.toggle("sc-proposal--pending", field?.status === "pending");
            container.classList.toggle("sc-proposal--empty", field?.status === "empty");
            container.classList.remove("sc-proposal--selected");
            value.replaceChildren();
            if (field?.status === "pending") {
                setStatus(status, "Awaiting review", "warning");
                const text = element("p", field.value, "sc-proposal__value");
                highlightOnFocus(text, field.sources ?? []);
                value.append(text);
                for (const control of container.querySelectorAll("button, textarea, summary")) {
                    control.onfocus = () => renderTranscript(state.transcript, field.sources ?? []);
                    control.onblur = () => renderTranscript(state.transcript);
                }
                // A recorded visit's: who said the words it came from, which stays when the transcript has gone.
                const saidBy = [...new Set((field.sources ?? []).map((source) => source.speaker).filter(Boolean))];
                if (saidBy.length > 0) {
                    value.append(element("p", `Said by: ${saidBy.map((speaker) => speakerWords[speaker] ?? "Unsure").join(", ")}.`, "sc-proposal__message"));
                }

                for (const warning of field.warnings ?? []) {
                    const note = element("p", warningWords[warning.code] ?? "Check this field against the transcript.", "sc-proposal__warning");
                    highlightOnFocus(note, [warning]);
                    value.append(note);
                }
            } else if (field?.status === "empty") {
                setStatus(status, "Left empty");
                const notDictated = (field.reasons ?? []).includes("not_dictated");
                value.append(element("p", notDictated
                    ? "Nothing was dictated for this field. Fill it in during review if it's needed."
                    : "No suggestion: what was proposed didn't pass the automatic checks. Fill it in during review.", "sc-proposal__message"));
            } else if (sorting === "pending") {
                setStatus(status, "Proposing…", "info");
            } else {
                setStatus(status, sorting === "unavailable" ? "Not sorted" : "Not dictated yet");
            }
        }

        // Hovering or focusing the element highlights its ranges in the transcript; the keyboard reaches it with Tab.
        // A proposed field shows the same tint while its sources are shown.
        function highlightOnFocus(node, ranges) {
            node.tabIndex = 0;
            const selected = (isSelected) => node.closest(".sc-proposal")?.classList.toggle("sc-proposal--selected", isSelected);
            const on = () => { renderTranscript(state.transcript, ranges); selected(true); };
            const off = () => { renderTranscript(state.transcript); selected(false); };
            node.addEventListener("mouseenter", on);
            node.addEventListener("focus", on);
            node.addEventListener("mouseleave", off);
            node.addEventListener("blur", off);
        }

        // The review's proposed orders and allergies: moving into one's actions shows its words in the transcript.
        root.querySelectorAll("[data-chart-proposal-start]").forEach((node) => {
            const ranges = [{ start: Number(node.dataset.chartProposalStart), end: Number(node.dataset.chartProposalEnd) }];
            node.addEventListener("focusin", () => renderTranscript(state.transcript, ranges));
            node.addEventListener("focusout", () => renderTranscript(state.transcript));
        });

        this.renderTranscript = renderTranscript;
        this.renderResult = renderResult;
    }
};
