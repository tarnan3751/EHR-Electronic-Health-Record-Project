// Server-backed only: PHI stays in this form's memory, never browser storage.
(() => {
    const editor = document.querySelector('[data-note-editor]');
    const signature = document.querySelector('[data-note-sign]');
    const form = editor || signature;
    if (!form) return;

    const status = document.getElementById('note-save-status');
    const errors = document.getElementById('note-errors');
    const fields = [...form.querySelectorAll('textarea')];
    const version = form.querySelector('[name="Version"]');
    let revision = 0;
    let savedRevision = 0;
    let timer;
    let pending;
    let reviewing = false;
    let conflict = false;

    function showErrors(messages) {
        errors.replaceChildren(...messages.map(message => {
            const p = document.createElement('p');
            p.textContent = message;
            return p;
        }));
        errors.hidden = messages.length === 0;
    }

    function setStatus(text) { status.textContent = text; }
    function buttons(disabled) {
        form.querySelectorAll('button').forEach(button => { button.disabled = disabled; });
    }
    function schedule() {
        clearTimeout(timer);
        if (!conflict && !reviewing && revision !== savedRevision) {
            timer = setTimeout(() => { void save(false); }, 750);
        }
    }

    async function request(url, data) {
        const controller = new AbortController();
        const timeout = setTimeout(() => controller.abort(), 35000);
        try {
            const response = await fetch(url, {
                method: 'POST', body: data, credentials: 'same-origin',
                headers: { 'HX-Request': 'true', 'Accept': 'application/json' },
                signal: controller.signal
            });
            if (response.redirected || !response.headers.get('Content-Type')?.includes('application/json')) {
                throw new Error('Unconfirmed response');
            }
            return { response, result: await response.json() };
        } finally {
            clearTimeout(timeout);
        }
    }

    async function save(review) {
        clearTimeout(timer);
        // Never overlap saves. Each subsequent request uses the version acknowledged by the previous one.
        if (pending) {
            await pending;
            if (!review) { schedule(); return; }
        }
        if (!review && revision === savedRevision) return;
        const sentRevision = revision;
        const data = new FormData(form);
        const target = new URL(form.action);
        if (review) target.searchParams.set('handler', 'Review');
        setStatus('Saving…');
        const operation = (async () => {
            try {
                const { response, result } = await request(target, data);
                if (Number.isSafeInteger(result.version)) {
                    version.value = result.version;
                    savedRevision = sentRevision;
                }
                if (!response.ok) {
                    conflict = response.status === 409 || response.status === 404;
                    showErrors(result.errors || ['Not saved. Try again.']);
                    setStatus(result.version ? 'Saved' : 'Not saved');
                    return;
                }
                showErrors([]);
                if (review && result.url && revision === sentRevision) {
                    window.location.assign(result.url);
                    return;
                }
                setStatus(revision === savedRevision ? 'Saved' : 'Unsaved changes');
            } catch {
                setStatus('Not saved');
                showErrors(['The clinic\'s server didn\'t confirm this save. Select Save draft to try again.']);
            }
        })();
        pending = operation;
        await operation;
        pending = null;
        // Don't endlessly retry failed requests; a new edit or explicit save retries them.
        if (savedRevision === sentRevision && revision !== savedRevision) schedule();
    }

    if (editor) {
        fields.forEach(field => field.addEventListener('input', () => {
            revision++;
            setStatus('Unsaved changes');
            schedule();
        }));
        // The visit type (step 14) is saved with the draft, and its prompts, kept on its option, shown under the sections.
        const visitType = form.querySelector('[data-note-template]');
        visitType?.addEventListener('change', () => {
            const chosen = visitType.selectedOptions[0];
            form.querySelectorAll('[data-note-prompt]').forEach(prompt => {
                const text = chosen?.dataset[prompt.dataset.notePrompt] || '';
                prompt.textContent = text;
                prompt.hidden = text === '';
            });
            revision++;
            setStatus('Unsaved changes');
            schedule();
        });
        // The latest day's vital signs for Objective (step 14): fetched only when asked, shown as text, and added to the
        // end of Objective only by "Add to Objective", which then saves as typing does. Never anything without asking.
        const vitals = form.querySelector('[data-note-vitals]');
        const offer = form.querySelector('[data-note-vitals-offer]');
        const objective = form.querySelector('textarea[name="Objective"]');
        const vitalsStatus = form.querySelector('[data-note-vitals-status]');
        if (vitals && offer && objective && vitalsStatus) {
            const day = offer.querySelector('[data-note-vitals-day]');
            const text = offer.querySelector('[data-note-vitals-text]');
            const add = offer.querySelector('[data-note-vitals-add]');
            vitals.hidden = false;
            vitals.addEventListener('click', async () => {
                vitalsStatus.textContent = 'Loading vital signs…';
                try {
                    const url = new URL(window.location.href);
                    url.search = '?handler=Vitals';
                    const response = await fetch(url, { credentials: 'same-origin', headers: { 'Accept': 'application/json' } });
                    if (!response.ok || !response.headers.get('Content-Type')?.includes('application/json')) throw new Error('Unconfirmed response');
                    const result = await response.json();
                    text.textContent = result.text || '';
                    add.hidden = !result.text;
                    day.textContent = !result.text ? 'No vital signs to add.'
                        : result.today ? '' : `Not today. These were taken on ${result.day}.`;
                    day.hidden = day.textContent === '';
                    offer.hidden = false;
                    vitalsStatus.textContent = '';
                    (result.text ? add : vitals).focus();
                } catch {
                    vitalsStatus.textContent = 'The vital signs couldn\'t be loaded. Try again.';
                }
            });
            add.addEventListener('click', () => {
                objective.value = objective.value === '' ? text.textContent : `${objective.value}\n\n${text.textContent}`;
                objective.dispatchEvent(new Event('input'));
                offer.hidden = true;
                objective.focus();
                objective.setSelectionRange(objective.value.length, objective.value.length);
                vitalsStatus.textContent = 'Vital signs added to Objective.';
            });
        }
        form.addEventListener('submit', async event => {
            event.preventDefault();
            const review = event.submitter?.hasAttribute('data-review') === true;
            if (reviewing) return;
            reviewing = true;
            buttons(true);
            try { await save(review); }
            finally { reviewing = false; buttons(false); schedule(); }
        });
        form.addEventListener('keydown', event => {
            if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 's') {
                event.preventDefault();
                // The Save draft button by name: the form has buttons that don't submit it ("Add vital signs to Objective…").
                form.requestSubmit(form.querySelector('[data-note-save]'));
            }
        });
        window.addEventListener('beforeunload', event => {
            if (revision !== savedRevision) {
                event.preventDefault();
                event.returnValue = '';
            }
        });
    } else {
        form.addEventListener('submit', async event => {
            event.preventDefault();
            if (pending) return;
            const data = new FormData(form);
            buttons(true);
            setStatus('Signing…');
            pending = (async () => {
                try {
                    const { response, result } = await request(form.action, data);
                    if (response.ok && result.url) {
                        window.location.assign(result.url);
                        return;
                    }
                    showErrors(result.errors || ['Not signed. Go back to the draft and check it.']);
                    setStatus('Not signed');
                } catch {
                    showErrors(['The clinic\'s server didn\'t confirm the signature. Try again, or check the chart to see if the note was signed.']);
                    setStatus('Signature not confirmed');
                }
            })();
            await pending;
            pending = null;
            buttons(false);
        });
    }
})();
