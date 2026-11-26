// The desktop screen. It makes no network calls (AGENTS.md): it asks the Rust side through its commands
// (src-tauri/src/main.rs), and redraws when the Rust side says something changed. Phrases go in as text, never HTML.
// The lock (the expansion plan's step 19a, at the end) covers it without clearing anything; Rust decides when. After
// the remote wipe (step 20), it shows only that everything here was deleted.
'use strict';

const { invoke } = window.__TAURI__.core;
const { listen } = window.__TAURI__.event;

const byId = (id) => document.getElementById(id);

function el(tag, properties = {}, ...children) {
  const element = document.createElement(tag);
  Object.assign(element, properties);
  element.append(...children);
  return element;
}

// A button is secondary unless it says otherwise (docs/brand: Button).
function button(label, onClick, className = 'sc-btn') {
  const element = el('button', { type: 'button', textContent: label, className });
  element.addEventListener('click', onClick);
  return element;
}

function show(element, text) {
  element.hidden = !text;
  element.textContent = text ?? '';
}

// Runs a command. If it fails, says why and returns undefined.
async function call(command, args) {
  try {
    const answer = await invoke(command, args);
    show(byId('request-error'), null);
    return answer;
  } catch (problem) {
    show(byId('request-error'), String(problem));
    return undefined;
  }
}

async function refresh() {
  const view = await call('view');
  if (view?.deleted) {
    showDeleted(view.deletedProblem);
    return;
  }
  if (view) {
    serverAnswered = view.status.server != null;
    applyLock(view.lock);
    // Under the lock, and while a sign-in the server ended waits for the lock (after a recording), the page stays
    // exactly as it is, for the same clinician to come back to.
    if (locked || view.lock.sessionOver) return;
    const signedIn = view.status.account != null;
    renderStatus(view.status, view.waiting);
    renderAccount(view.status.account);
    show(byId('sign-in-notice'), view.signedOutBecause);
    byId('sign-in-servers').textContent = `Signs in to: ${view.servers.map(host).join(', ')}`;
    renderOfflineAccess(view.offlineAvailable, signedIn);
    chartsUpdated = view.chartsUpdated;
    renderChartsUpdated();
    byId('clinical-waiting').textContent = view.clinicalWaiting
      ? `${view.clinicalWaiting} draft or recording changes need sync or review.`
      : 'No draft or recording changes waiting.';
    if (selectedPatient) void openChart(selectedPatient, false);
    renderConflicts(view.conflicts);
    renderPhrases(view.phrases, signedIn);
  }
}

function renderStatus(status, waiting) {
  const signedIn = status.account != null;
  const checked = status.checkedAt != null;
  const online = status.server != null;
  // A dot and the words (docs/brand: ConnectionStatus): the words always show, and the server is in mono.
  const connection = byId('connection');
  connection.classList.toggle('sc-conn--signedout', !signedIn);
  connection.classList.toggle('sc-conn--online', signedIn && checked && online);
  connection.classList.toggle('sc-conn--offline', signedIn && checked && !online);
  const words = !signedIn ? ['Signed out'] : !checked ? ['Connecting…'] : !online ? ['Offline']
    : ['Online: ', el('span', { className: 'sc-conn__server', textContent: host(status.server) })];
  connection.replaceChildren(el('span', { className: 'sc-conn__dot' }), el('span', {}, ...words));

  byId('waiting').textContent =
    waiting === 0 ? 'No Quick text changes waiting.' : `${waiting} ${waiting === 1 ? 'change' : 'changes'} waiting to send.`;
  byId('checked').textContent = checked ? `Checked at ${new Date(status.checkedAt).toLocaleTimeString()}.` : '';
  show(byId('sync-problem'), status.problem);
}

// Signed out, the page offers to sign in, and the phrases can be read but not changed (renderPhrases).
function renderAccount(account) {
  const signedIn = account != null;
  byId('account').hidden = !signedIn;
  byId('account-name').textContent = account ?? '';
  byId('sign-in').hidden = signedIn;
  byId('add-quick-text').hidden = !signedIn;
}

function host(url) {
  try {
    return new URL(url).host;
  } catch {
    return url;
  }
}

function phraseText(phrase) {
  return [
    el('span', { className: 'sc-quicktext__shortcut', textContent: phrase.shortcut }),
    ' ',
    el('span', { className: 'sc-quicktext__body', textContent: phrase.body }),
  ];
}

// One version of a phrase or a draft, under its label.
function version(label, ...content) {
  return el('div', { className: 'sc-version' }, el('dt', { className: 'sc-version__label', textContent: label }), el('dd', {}, ...content));
}

// Both versions side by side; nothing is merged silently (docs/brand).
function bothVersions(theirs, mine) {
  return el('dl', { className: 'sc-versions' }, version('Theirs (saved)', ...phraseText(theirs)), version('Mine (not saved)', ...phraseText(mine)));
}

function actions(...buttons) {
  return el('div', { className: 'sc-actions' }, ...buttons);
}

function notice(text, tone = 'warning') {
  return el('p', { className: `sc-notice sc-notice--${tone}`, textContent: text });
}

// Changes the server refused, for the person to choose. Redrawn only when they change.
let shownConflicts = '';

function renderConflicts(conflicts) {
  const json = JSON.stringify(conflicts);
  if (json === shownConflicts) return;
  shownConflicts = json;
  byId('conflicts').hidden = conflicts.length === 0;
  byId('conflict-list').replaceChildren(...conflicts.map(conflictItem));
}

function conflictItem(conflict) {
  const { theirs } = conflict;
  const resolve = (keepMine) => async () => {
    await call('resolve', { key: conflict.key, keepMine });
    await refresh();
  };

  const content = conflict.canKeepMine
    ? [
        notice('Someone else saved this phrase before your change reached the server. Choose which version to keep.'),
        bothVersions(theirs, conflict),
        actions(button('Keep mine', resolve(true)), button('Keep theirs', resolve(false))),
      ]
    : [
        notice(theirs
          ? `${theirs.shortcut} is already in use by another phrase, so your change wasn't saved.`
          : `The server didn't accept your change. ${conflict.message ?? ''}`),
        el('dl', { className: 'sc-versions' }, version('Mine (not saved)', ...phraseText(conflict))),
        actions(button('Discard mine', resolve(false), 'sc-btn sc-btn--danger')),
      ];
  return el('li', {}, el('div', { className: 'sc-conflict' }, ...content));
}

// The phrases. Redrawn only when they change; a phrase being edited keeps its form and what's typed in it. Signed
// out, they can't be changed, since a change has to be sent under someone's session: no Edit buttons, no open forms.
let shownPhrases = '';

function renderPhrases(phrases, signedIn) {
  const json = JSON.stringify({ phrases, signedIn });
  if (json === shownPhrases) return;
  shownPhrases = json;

  const list = byId('quick-texts');
  const editing = new Map(
    signedIn ? [...list.querySelectorAll(':scope > li.editing')].map((li) => [li.dataset.id, li]) : [],
  );
  const focused = list.contains(document.activeElement) ? document.activeElement : null;
  list.replaceChildren(...phrases.map((phrase) => editing.get(phrase.id) ?? phraseItem(phrase, signedIn)));
  focused?.focus();
}

function phraseItem(phrase, signedIn) {
  const li = el('li', {}, ...phraseText(phrase));
  li.dataset.id = phrase.id;
  if (phrase.pending) li.append(el('span', { className: 'sc-tag sc-tag--warning sc-quicktext__pending', textContent: 'Not sent yet' }));
  if (signedIn) {
    li.append(
      button('Edit', () => {
        li.classList.add('editing');
        showEditForm(li, phrase.id, phrase.version, phrase);
      }, 'sc-btn sc-btn--compact'),
    );
  }
  return li;
}

// A labeled field with the place for its error, tied to it for assistive technology (docs/brand: TextField). Each
// form's fields get ids of their own.
let fieldCount = 0;

function field(label, name, control) {
  const id = `field-${++fieldCount}`;
  control.id = id;
  control.name = name;
  control.setAttribute('aria-describedby', `${id}-error`);
  const error = el('span', { className: 'sc-field__error', id: `${id}-error` });
  error.dataset.for = name;
  return el('div', { className: 'sc-field' }, el('label', { className: 'sc-field__label', htmlFor: id, textContent: label }), control, error);
}

// The form's buttons go in its last element, the row of actions.
function phraseForm(values, submitLabel) {
  return el(
    'form',
    { className: 'sc-quicktext-form' },
    field('Shortcut', 'shortcut', el('input', { className: 'sc-field__input sc-field__input--mono', autocomplete: 'off', value: values.shortcut })),
    field('Text', 'body', el('textarea', { className: 'sc-field__input', rows: 2, value: values.body })),
    actions(el('button', { className: 'sc-btn', textContent: submitLabel })),
  );
}

function formValues(form) {
  return { shortcut: form.elements.shortcut.value, body: form.elements.body.value };
}

function showProblems(form, problems) {
  for (const span of form.querySelectorAll('[data-for]')) {
    const problem = problems[span.dataset.for] ?? '';
    span.textContent = problem;
    const control = form.elements[span.dataset.for];
    if (problem) control?.setAttribute('aria-invalid', 'true');
    else control?.removeAttribute('aria-invalid');
  }
}

// `version` is the phrase as the person saw it when they began; the save is refused if it has changed since.
function showEditForm(li, id, version, values) {
  const form = phraseForm(values, 'Save');
  form.lastElementChild.append(button('Cancel', () => closeEdit(li), 'sc-btn sc-btn--quiet'));
  form.addEventListener('submit', (event) => {
    event.preventDefault();
    saveEdit(li, id, version, formValues(form));
  });
  li.replaceChildren(form);
  form.elements.shortcut.focus();
  return form;
}

async function saveEdit(li, id, version, mine) {
  const answer = await call('edit', { id, version, ...mine });
  switch (answer?.outcome) {
    case 'saved':
      closeEdit(li);
      break;
    case 'problems':
      showProblems(li.querySelector('form') ?? showEditForm(li, id, version, mine), answer.problems);
      break;
    case 'changedSince':
      // As on the web page: nothing was saved, so show both versions and let the person choose.
      li.replaceChildren(
        el(
          'div',
          { className: 'sc-conflict' },
          notice('Someone else saved this phrase while you were editing it. Choose which version to keep.'),
          bothVersions(answer.current, mine),
          actions(button('Keep mine', () => saveEdit(li, id, answer.current.version, mine)), button('Keep theirs', () => closeEdit(li))),
        ),
      );
      break;
  }
}

function closeEdit(li) {
  li.classList.remove('editing');
  shownPhrases = '';
  refresh();
}

const addForm = byId('add-quick-text');
addForm.addEventListener('submit', async (event) => {
  event.preventDefault();
  const answer = await call('add', formValues(addForm));
  if (!answer) return;
  showProblems(addForm, answer.problems ?? {});
  if (answer.outcome === 'saved') {
    addForm.reset();
    addForm.elements.shortcut.focus();
    await refresh();
  }
});

const signInForm = byId('sign-in');
signInForm.addEventListener('submit', async (event) => {
  event.preventDefault();
  const { email, password, code } = signInForm.elements;
  // One at a time: sending the same code again would count as another attempt.
  const submit = signInForm.querySelector('button');
  submit.disabled = true;
  try {
    await invoke('sign_in', { email: email.value, password: password.value, code: code.value });
    signInForm.reset();
    show(byId('sign-in-problem'), null);
  } catch (problem) {
    show(byId('sign-in-problem'), String(problem));
  } finally {
    submit.disabled = false;
  }
  await refresh();
});

// Signing out closes the offline charts, and anything typed on them goes. The lock offers it too.
async function signOut() {
  offlineSigningOut = true;
  renderOfflineAccess(false, false);
  try {
    await call('sign_out');
  } finally {
    offlineSigningOut = false;
    // The answer to the sign-out the person asked for, so it may take the lock away.
    applyLock(await invoke('lock_state'), true);
    await refresh();
  }
}

byId('sign-out').addEventListener('click', signOut);

const downloadPanel = byId('download-panel');
const draftForm = byId('clinical-draft');
let offlineAvailable = false;
let offlineSigningOut = false;
let selectedPatient = null;
let chartRequest = 0;
let panelRequest = 0;
let editingDraft = null;
let recording = null;
// When the offline charts were last downloaded (view.chartsUpdated), and how many there are.
let chartsUpdated = null;
let panelSize = 0;

function resetDraftEditor() {
  editingDraft = null;
  draftForm.reset();
  byId('draft-label').textContent = 'New draft';
  byId('cancel-draft-edit').hidden = true;
}

byId('cancel-draft-edit').addEventListener('click', resetDraftEditor);

// A recording holds the lock off while it lasts (src-tauri/src/lock.rs), and counts as activity.
function holdLock(active) {
  invoke('recording', { active }).catch(() => {});
}

function cancelRecording() {
  if (!recording) return;
  holdLock(false);
  recording.cancelled = true;
  if (recording.recorder && recording.recorder.state !== 'inactive') recording.recorder.stop();
  recording = null;
  byId('record-audio').textContent = 'Record audio';
  byId('record-audio').disabled = false;
  byId('recording-message').textContent = '';
}

function clearChart() {
  chartRequest++;
  selectedPatient = null;
  byId('offline-chart').hidden = true;
  byId('chart-name').textContent = '';
  byId('chart-demographics').textContent = '';
  byId('chart-sections').replaceChildren();
  byId('saved-drafts').replaceChildren();
  byId('saved-recordings').replaceChildren();
  cancelRecording();
  resetDraftEditor();
  draftForm.querySelector('button').disabled = false;
}

function renderOfflineAccess(available, signedIn) {
  if (offlineSigningOut) {
    available = false;
    signedIn = false;
  }
  byId('offline-sign-in').hidden = available || !signedIn;
  byId('offline-charts').hidden = !available;
  if (available === offlineAvailable) return;
  offlineAvailable = available;
  if (available) {
    loadPanel();
  } else {
    panelRequest++;
    panelSize = 0;
    byId('offline-panel').replaceChildren();
    byId('offline-message').textContent = '';
    clearChart();
  }
}

async function loadPanel() {
  if (locked) return;
  const request = ++panelRequest;
  const panel = await call('offline_panel');
  if (!offlineAvailable || request !== panelRequest || !panel) return;
  panelSize = panel.length;
  renderChartsUpdated();
  const list = byId('offline-panel');
  list.replaceChildren(...panel.map((patient) =>
    el('li', {}, button(`${patient.name} · MRN ${patient.mrn}`, () => openChart(patient.id), 'sc-btn sc-btn--quiet')),
  ));
  if (panel.length === 0) {
    list.append(el('li', { className: 'sc-muted', textContent: 'No charts downloaded. Add patients to your panel in the web app, then download here.' }));
  }
  if (selectedPatient && !panel.some((patient) => patient.id === selectedPatient)) clearChart();
}

// "Offline charts last updated …", on the clinic's clock when the charts named it. Only Download my charts refreshes
// them, so this says how old the copy is; charts downloaded by a build that didn't record it say so.
function chartsUpdatedText(updated) {
  if (updated) return `Offline charts last updated ${clinicStamp(new Date(updated.at).toISOString(), updated.clinicTimeZone)}.`;
  return panelSize ? 'When these charts were downloaded isn\'t recorded: download them again to record it.' : '';
}

function renderChartsUpdated() {
  byId('charts-updated').textContent = chartsUpdatedText(chartsUpdated);
}

function chartSection(title, rows, none = 'None recorded.') {
  return el('section', {}, el('h4', { textContent: title }),
    rows.length ? el('ul', { className: 'sc-list' }, ...rows.map((text) => el('li', { textContent: text })))
      : el('p', { className: 'sc-muted', textContent: none }));
}

// A time from the offline copy on the clinic's clock, labeled: "2026-11-15 14:05 CST". The copy says which zone;
// one made before it did, or naming a zone this machine doesn't know, is shown in UTC, labeled, as before.
function clinicStamp(text, zone, withTime = true) {
  const utc = withTime ? `${text.slice(0, 10)} ${text.slice(11, 16)} UTC` : text.slice(0, 10);
  if (!zone) return utc;
  try {
    const parts = Object.fromEntries(new Intl.DateTimeFormat('en-US', {
      timeZone: zone, year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit',
      hourCycle: 'h23', timeZoneName: 'short',
    }).formatToParts(new Date(text)).map((part) => [part.type, part.value]));
    const day = `${parts.year}-${parts.month}-${parts.day}`;
    return withTime ? `${day} ${parts.hour}:${parts.minute} ${parts.timeZoneName}` : day;
  } catch {
    return utc;
  }
}

// Signed notes, read-only, each followed by its addenda in the order they were signed. Sensitive notes and addenda
// are never kept on this device; the line above the visits says how many items were left out. A note signed in four
// sections (the expansion plan's step 14) is shown in them; one signed before, in History, Assessment and Plan.
function signedNotes(notes, zone) {
  const stamp = (text) => clinicStamp(text, zone);
  const part = (title, text) => [el('h5', { textContent: title }), el('p', { className: 'sc-note__text', textContent: text })];
  return el('section', {}, el('h4', { textContent: 'Signed notes' }),
    ...(notes.length ? notes.map((note) => el('details', { className: 'sc-result' },
      el('summary', { textContent: `Signed note · ${stamp(note.signedAt)} · ${note.signer}${note.addenda.length ? ` · ${note.addenda.length} ${note.addenda.length === 1 ? 'addendum' : 'addenda'}` : ''}` }),
      ...(note.subjective != null
        ? [...part('Subjective', note.subjective), ...part('Objective', note.objective ?? '')]
        : part('History', note.history)),
      ...part('Assessment', note.assessment), ...part('Plan', note.plan),
      ...note.addenda.flatMap((addendum) => part(`${addendum.notNoteAuthor ? `Addendum by ${addendum.author}, not the note's author` : `Addendum · ${addendum.author}`} · ${stamp(addendum.signedAt)}`, addendum.text)),
      ...(note.sensitiveAddendaNotKept
        ? [el('p', { className: 'sc-muted', textContent: `Sensitive addenda not kept offline: ${note.sensitiveAddendaNotKept}. Open the note on the clinic's site to see them.` })]
        : []),
    )) : [el('p', { className: 'sc-muted', textContent: 'None signed.' })]));
}

async function openChart(patient, reset = true) {
  if (locked) return;
  if (reset) clearChart();
  const request = ++chartRequest;
  const result = await call('offline_chart', { patient });
  if (!offlineAvailable || request !== chartRequest || !result) return;
  selectedPatient = patient;
  const { chart, drafts, recordings } = result;
  byId('offline-chart').hidden = false;
  byId('chart-name').textContent = chart.name;
  byId('chart-demographics').textContent = `MRN ${chart.mrn} · Born ${chart.birthDate} · ${chart.gender}`;
  const details = (main, ...parts) => [main, ...parts.filter(Boolean)].join(' · ');
  byId('chart-sections').replaceChildren(
    // "No known allergies" was asked and recorded; it isn't the same as nothing recorded.
    chartSection('Allergies', chart.allergies.map((a) => details(a.display, a.reaction, a.criticality)),
      chart.allergyStatus === 'none-known' ? 'No known allergies.' : 'No allergy information recorded.'),
    chartSection('Medications', chart.medications.map((m) => details(m.display, m.dosage))),
    chartSection('Conditions', chart.conditions.map((c) => details(c.display, c.onsetDate))),
    // Sensitive diagnoses and medications are never kept on this device: say so, rather than look complete.
    ...(chart.sensitiveNotKept
      ? [el('p', { className: 'sc-muted', textContent: `Sensitive items not kept offline: ${chart.sensitiveNotKept}. Open the chart on the clinic's site to see them.` })]
      : []),
    chartSection('Recent visits', chart.visits.map((v) => details(clinicStamp(v.periodStart, chart.clinicTimeZone, false), v.reason,
      v.systolicMmHg != null && v.diastolicMmHg != null ? `${v.systolicMmHg}/${v.diastolicMmHg} mmHg` : '',
      v.heartRatePerMinute != null ? `${v.heartRatePerMinute} bpm` : ''))),
    signedNotes(chart.notes ?? [], chart.clinicTimeZone),
  );
  byId('saved-drafts').replaceChildren(...drafts.map((draft) => renderDraft(patient, draft)));
  if (!drafts.length) byId('saved-drafts').append(el('li', { className: 'sc-muted', textContent: 'No saved drafts.' }));
  byId('saved-recordings').replaceChildren(...recordings.map(({ problem }) =>
    el('li', { textContent: problem ?? 'Recording saved on this device · Pending transcription' }),
  ));
}

downloadPanel.addEventListener('click', async () => {
  downloadPanel.disabled = true;
  byId('offline-message').textContent = 'Downloading charts…';
  try {
    const answer = await call('download_panel');
    if (!offlineAvailable) return;
    byId('offline-message').textContent = answer === undefined ? 'Download failed.' : 'Downloaded charts updated.';
    if (answer !== undefined) await loadPanel();
  } finally {
    downloadPanel.disabled = false;
  }
});

function renderDraft(patient, draft) {
  const labels = {
    synced: 'Synced · Unsigned draft',
    pending: 'Saved on this device · Pending sync',
    conflict: 'A newer version is on the server. Choose which to keep.',
    invalid: draft.problem ?? 'This draft needs review.',
    // Signed as a sensitive note: its text was removed from this device, and it can't be edited here.
    withheld: 'Signed as a sensitive note · Not kept on this device',
  };
  if (draft.state === 'withheld') return el('li', {}, el('p', { className: 'desktop-draft__state', textContent: labels.withheld }));
  const row = el('li', {}, el('p', { className: 'desktop-draft__state', textContent: labels[draft.state] }),
    el('pre', { className: 'desktop-draft__body', textContent: draft.body }));
  if (draft.state === 'synced' || draft.state === 'invalid') {
    row.append(button('Edit draft', () => {
      editingDraft = { id: draft.id, version: draft.version };
      draftForm.elements.body.value = draft.body;
      byId('draft-label').textContent = 'Edit draft';
      byId('cancel-draft-edit').hidden = false;
      draftForm.elements.body.focus();
    }, 'sc-btn sc-btn--compact'));
  }
  if (draft.state === 'conflict') {
    // Both versions side by side, in the words the phrases' conflicts use.
    const theirs = version('Theirs (saved)', el('pre', { className: 'desktop-draft__body', textContent: draft.theirs }));
    const mine = version('Mine (not saved)', row.querySelector('pre'));
    const choose = async (keepMine) => {
      const answer = await call('resolve_clinical', {
        patient, id: draft.id, version: draft.theirVersion, keepMine,
      });
      if (answer !== undefined && selectedPatient === patient) await openChart(patient, false);
    };
    row.append(el('dl', { className: 'sc-versions' }, theirs, mine),
      actions(button('Keep mine', () => choose(true)), button('Keep theirs', () => choose(false))));
  }
  return row;
}

draftForm.addEventListener('submit', async (event) => {
  event.preventDefault();
  const patient = selectedPatient;
  if (!offlineAvailable || !patient) return;
  const edit = editingDraft;
  const submit = draftForm.querySelector('button');
  submit.disabled = true;
  try {
    const args = { patient, body: draftForm.elements.body.value, ...edit };
    const answer = await call(edit ? 'edit_clinical' : 'save_clinical_draft', args);
    if (!offlineAvailable || selectedPatient !== patient || answer === undefined) return;
    resetDraftEditor();
    byId('offline-message').textContent = 'Draft saved on this device.';
    await openChart(patient, false);
  } finally {
    submit.disabled = false;
  }
});

byId('record-audio').addEventListener('click', async () => {
  const control = byId('record-audio');
  const message = byId('recording-message');
  if (recording?.recorder?.state === 'recording') {
    recording.recorder.stop();
    return;
  }
  const patient = selectedPatient;
  if (!offlineAvailable || !patient || recording) return;
  const capture = { cancelled: false, recorder: null };
  recording = capture;
  control.disabled = true;
  let stream;
  try {
    stream = await navigator.mediaDevices.getUserMedia({ audio: true });
    if (capture.cancelled || selectedPatient !== patient) {
      stream.getTracks().forEach((track) => track.stop());
      return;
    }

    const recorder = new MediaRecorder(stream);
    capture.recorder = recorder;
    const parts = [];
    recorder.addEventListener('dataavailable', ({ data }) => {
      if (data.size) parts.push(data);
    });
    recorder.addEventListener('error', () => {
      capture.cancelled = true;
      message.textContent = 'The microphone recording failed.';
    });
    let timer;
    recorder.addEventListener('stop', async () => {
      clearTimeout(timer);
      stream.getTracks().forEach((track) => track.stop());
      if (recording === capture) control.disabled = true;
      try {
        if (capture.cancelled || selectedPatient !== patient) return;
        const type = parts[0]?.type || recorder.mimeType;
        const audio = new Blob(parts, { type });
        if (audio.size === 0 || audio.size > 8 * 1024 * 1024) {
          message.textContent = 'The recording is empty or too large. Try a shorter recording.';
          return;
        }
        message.textContent = 'Saving recording…';
        const bytes = Array.from(new Uint8Array(await audio.arrayBuffer()));
        if (capture.cancelled || selectedPatient !== patient) return;
        const saved = await call('save_recording', { patient, mediaType: type, audio: bytes });
        if (recording !== capture) return;
        message.textContent = saved === undefined ? 'Recording could not be saved.' : 'Recording saved on this device.';
        if (saved !== undefined) await openChart(patient, false);
      } finally {
        holdLock(false);
        if (recording === capture) {
          recording = null;
          control.textContent = 'Record audio';
          control.disabled = false;
        }
      }
    });
    recorder.start();
    holdLock(true);
    timer = setTimeout(() => {
      if (recorder.state === 'recording') recorder.stop();
    }, 60_000);
    control.textContent = 'Stop and save';
    control.disabled = false;
    message.textContent = 'Recording for up to 60 seconds.';
  } catch {
    holdLock(false);
    stream?.getTracks().forEach((track) => track.stop());
    if (recording === capture) {
      recording = null;
      control.disabled = false;
      message.textContent = 'The microphone could not be opened. Check your microphone permissions.';
    }
  }
});

byId('sync-now').addEventListener('click', () => call('sync_now'));

// The lock (the expansion plan's step 19a; src-tauri/src/lock.rs decides when, and main.rs refuses the offline charts
// while it's up). Locked, the app bar and the page are inert and out of sight, kept exactly as they were, typed text,
// scroll and focus and all, and the lock panel fills the window; nothing under it redraws until it opens. Signing out
// from it is the one thing that clears the page.
const lockForm = byId('lock-form');
const warning = byId('lock-warning');
let locked = false;
let focusBeforeLock = null;
let countdown = null;
// Whether the last sync round reached a server, for the words of the banner after the 12 hours.
let serverAnswered = false;
// The newest view of the lock shown (LockView.generation): an older one, computed before a lock, is dropped.
let lockGeneration = -1;
// Everything here was deleted (showDeleted): nothing else is ever shown again.
let deleted = false;

// The lock's view from the Rust side (LockView in main.rs). Anything may lock the window, but only `uncover` may open
// it: the lock's own events, and the answers to unlocking, locking now and signing out (step 19a's final review). A
// view the page asked for along the way, such as the one with each redraw, never does.
function applyLock(view, uncover = false) {
  if (deleted || view.generation < lockGeneration) return;
  lockGeneration = view.generation;
  renderSignInNeeded(view);
  if (view.state === 'locked') {
    closeWarning();
    if (!locked) lockWindow();
    renderLock(view);
    return;
  }
  if (locked) {
    if (!uncover) return;
    unlockWindow();
  }
  if (view.state === 'warning') openWarning(view);
  else closeWarning();
}

// applyLock with the right to open the window, and a redraw if it did.
function applyLockAndRedraw(view) {
  const wasLocked = locked;
  applyLock(view, true);
  if (wasLocked && !locked) refresh();
}

function lockWindow() {
  locked = true;
  focusBeforeLock = document.activeElement;
  for (const part of [byId('app-bar'), byId('page')]) part.inert = true;
  document.body.classList.add('desktop-locked');
  byId('lock').hidden = false;
  lockForm.reset();
  show(byId('lock-problem'), null);
  lockForm.elements.password.focus();
}

function unlockWindow() {
  locked = false;
  byId('lock').hidden = true;
  lockForm.reset();
  document.body.classList.remove('desktop-locked');
  for (const part of [byId('app-bar'), byId('page')]) part.inert = false;
  if (focusBeforeLock?.isConnected) focusBeforeLock.focus();
  focusBeforeLock = null;
}

// Why it locked, as the lock says it: each starts with its state word (docs/brand).
const lockReasons = {
  idle: (view) => ['Locked:', ` nothing was done here for ${view.idleMinutes} minutes.`],
  started: () => ['Locked:', ' SwiftChart opens locked while a clinician is signed in.'],
  asked: () => ['Locked:', ' this window was locked.'],
  longest: () => ['Locked:', ' a sign-in lasts at most 12 hours.'],
  wrongPasswords: () => ['Locked:', ' too many wrong passwords were typed here.'],
  ended: () => ['Signed out:', ' the server ended this sign-in: your password was changed, your account was locked or disabled, or it is 30 days since you signed in.'],
  offlineEnded: () => ['Locked:', ' the offline charts open for 30 days after a sign-in, and yours have passed. Nothing on this computer was deleted.'],
};

const lockHow = {
  password: 'Enter your password to unlock. What you typed is kept, and sync carries on.',
  signIn: 'Enter your password and the code from your authenticator app. If the clinic\'s server can\'t be reached, your password alone opens the offline charts. What you typed is kept.',
  signedOut: 'Sign in again with your password and the code from your authenticator app to come back to this window as you left it.',
};

function renderLock(view) {
  const [state, words] = lockReasons[view.why](view);
  const why = byId('lock-why');
  why.className = `sc-notice sc-notice--${view.need === 'signedOut' ? 'warning' : 'info'}`;
  why.replaceChildren(el('span', { className: 'sc-notice__state', textContent: state }), words);
  byId('lock-title').textContent = view.why === 'ended' ? 'Signed out' : 'Locked';
  byId('lock-how').textContent = view.why === 'wrongPasswords'
    ? 'Enter your password and the code from your authenticator app, once the clinic\'s server can be reached. Nothing on this computer was deleted, and what you typed is kept.'
    : lockHow[view.need];
  const full = view.need !== 'password';
  byId('lock-code-field').hidden = !full;
  byId('lock-submit').textContent = full ? 'Sign in' : 'Unlock';
  byId('lock-account').textContent = view.account ?? 'a clinician';
  // How old the offline copy is, prominently when nothing will refresh it until a full sign-in.
  const updated = chartsUpdatedText(view.chartsUpdated);
  const charts = byId('lock-charts');
  charts.hidden = !updated;
  charts.className = full ? 'sc-notice sc-notice--warning' : 'sc-status';
  charts.textContent = full && updated ? `${updated} Nothing is brought back until you sign in again.` : updated;
}

// After the 12 hours, opened with the password alone: say a full sign-in is needed, and how old the charts are.
function renderSignInNeeded(view) {
  const needed = view.signInEnded && (view.state === 'unlocked' || view.state === 'warning');
  byId('sign-in-needed').hidden = !needed;
  if (!needed) return;
  byId('sign-in-needed-text').replaceChildren(
    el('span', { className: 'sc-notice__state', textContent: serverAnswered ? 'Sign in again:' : 'Offline:' }),
    serverAnswered
      ? ' a sign-in lasts at most 12 hours, and a server is answering. If signing in says the clinic\'s server can\'t be reached, your password alone still opens the offline charts.'
      : ' a sign-in with your code is needed as soon as the clinic\'s server can be reached.',
    ` ${chartsUpdatedText(view.chartsUpdated)} Drafts and recordings are saved on this computer and sent after you sign in.`,
  );
}

function openWarning(view) {
  byId('lock-warning-idle').hidden = view.last;
  byId('lock-warning-last').hidden = !view.last;
  byId('lock-stay').hidden = view.last;
  byId('lock-keep-working').hidden = !view.last;
  // The countdown is shown, not announced; the Rust side decides the end.
  const ends = Date.now() + view.left * 1000;
  const tick = () => {
    const left = Math.max(0, Math.round((ends - Date.now()) / 1000));
    const shown = `${Math.floor(left / 60)}:${String(left % 60).padStart(2, '0')}`;
    for (const place of warning.querySelectorAll('[data-countdown]')) place.textContent = shown;
  };
  tick();
  clearInterval(countdown);
  countdown = setInterval(tick, 1000);
  if (!warning.open) {
    warning.showModal();
    byId(view.last ? 'lock-keep-working' : 'lock-stay').focus();
  }
}

function closeWarning() {
  clearInterval(countdown);
  countdown = null;
  if (warning.open) warning.close();
}

// Activity: a key, a click or tap, the wheel or typing, never mouse movement; told to the Rust side at most once every
// 10 seconds, so the lock may come up to 10 seconds early.
let activityTold = 0;
for (const type of ['keydown', 'pointerdown', 'wheel', 'input']) {
  document.addEventListener(type, () => {
    if (locked || Date.now() - activityTold < 10_000) return;
    activityTold = Date.now();
    invoke('activity').catch(() => {});
  }, { capture: true, passive: true });
}

async function stay() {
  activityTold = Date.now();
  await invoke('activity');
  applyLock(await invoke('lock_state'));
}

byId('lock-stay').addEventListener('click', stay);
byId('lock-keep-working').addEventListener('click', stay);
warning.addEventListener('cancel', (event) => {
  event.preventDefault();
  stay();
});
byId('lock-now').addEventListener('click', async () => applyLockAndRedraw(await invoke('lock_now')));
byId('sign-in-now').addEventListener('click', async () => applyLockAndRedraw(await invoke('lock_now')));
byId('lock-sign-out').addEventListener('click', signOut);

// An administrator revoked this computer's sign-in, and the Rust side deleted everything SwiftChart kept here, then
// loaded this page again, so nothing it showed is kept (the expansion plan's step 20; src-tauri/src/wipe.rs). Only this
// fills the window, as the lock does, and quitting is all there is to do. `problem` says what couldn't be deleted:
// starting again finishes it.
function showDeleted(problem) {
  deleted = true;
  closeWarning();
  for (const part of [byId('app-bar'), byId('page')]) part.inert = true;
  document.body.classList.add('desktop-locked');
  byId('lock').hidden = true;
  byId('deleted-title').textContent = problem ? 'Not finished' : 'Deleted';
  byId('deleted-text').replaceChildren(
    el('span', { className: 'sc-notice__state', textContent: problem ? 'Not finished:' : 'Deleted:' }),
    problem
      ? ` an administrator revoked this computer's sign-in, and SwiftChart couldn't delete everything it keeps here (${problem}). Quit SwiftChart and start it again to finish.`
      : ' an administrator revoked this computer\'s sign-in, so SwiftChart deleted everything it kept here: the offline charts, drafts and recordings not yet sent, and its key. Quit SwiftChart; when it starts again it is empty, and you can sign in.',
  );
  byId('deleted').hidden = false;
  byId('quit').focus();
}

byId('quit').addEventListener('click', () => invoke('quit'));

lockForm.addEventListener('submit', async (event) => {
  event.preventDefault();
  const submit = byId('lock-submit');
  submit.disabled = true;
  try {
    const view = await invoke('unlock', { password: lockForm.elements.password.value, code: lockForm.elements.code.value });
    show(byId('lock-problem'), null);
    applyLockAndRedraw(view);
  } catch (problem) {
    lockForm.elements.password.value = '';
    show(byId('lock-problem'), String(problem));
    // What opens it may have changed: the code too, after the 12 hours.
    applyLock(await invoke('lock_state'));
    lockForm.elements.password.focus();
  } finally {
    submit.disabled = false;
  }
});

// Coming back to the window: the Rust side may have locked it meanwhile. Asking can only lock it.
const askLock = async () => applyLock(await invoke('lock_state'));
document.addEventListener('visibilitychange', () => {
  if (!document.hidden) askLock();
});
window.addEventListener('focus', askLock);
listen('lock', ({ payload }) => applyLockAndRedraw(payload));
listen('changed', refresh);
// A page loaded again holds no recording.
holdLock(false);
refresh();
