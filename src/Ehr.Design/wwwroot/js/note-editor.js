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
        setStatus('Saving');
        const operation = (async () => {
            try {
                const { response, result } = await request(target, data);
                if (Number.isSafeInteger(result.version)) {
                    version.value = result.version;
                    savedRevision = sentRevision;
                }
                if (!response.ok) {
                    conflict = response.status === 409 || response.status === 404;
                    showErrors(result.errors || ['The request could not be completed.']);
                    setStatus(result.version ? 'Saved' : 'Save Failed');
                    return;
                }
                showErrors([]);
                if (review && result.url && revision === sentRevision) {
                    window.location.assign(result.url);
                    return;
                }
                setStatus(revision === savedRevision ? 'Saved' : 'Unsaved');
            } catch {
                setStatus('Save Failed');
                showErrors(['The clinic server could not confirm this save. Your text is still here. Retry Save draft when connected; if it conflicts, compare with the saved draft in another tab.']);
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
            setStatus('Unsaved');
            schedule();
        }));
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
                form.requestSubmit(form.querySelector('button'));
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
            setStatus('Signing');
            pending = (async () => {
                try {
                    const { response, result } = await request(form.action, data);
                    if (response.ok && result.url) {
                        window.location.assign(result.url);
                        return;
                    }
                    showErrors(result.errors || ['The note could not be signed. Return to the editor and check the saved draft.']);
                    setStatus('Not signed');
                } catch {
                    showErrors(['The clinic server could not confirm the signature. This note is still displayed. Retry when connected or check the chart to see whether signing completed.']);
                    setStatus('Signature not confirmed');
                }
            })();
            await pending;
            pending = null;
            buttons(false);
        });
    }
})();
