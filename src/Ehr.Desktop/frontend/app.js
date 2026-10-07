// The desktop screen. It makes no network calls (AGENTS.md): it asks the Rust side through its commands
// (src-tauri/src/main.rs), and redraws when the Rust side says something changed. Phrases go in as text, never HTML.
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

function button(label, onClick) {
  const element = el('button', { type: 'button', textContent: label });
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
  if (view) {
    renderStatus(view.status, view.waiting);
    renderConflicts(view.conflicts);
    renderPhrases(view.phrases);
  }
}

function renderStatus(status, waiting) {
  const checked = status.checkedAt != null;
  const online = status.server != null;
  const connection = byId('connection');
  connection.classList.toggle('online', checked && online);
  connection.classList.toggle('offline', checked && !online);
  connection.textContent = !checked ? 'Connecting…' : online ? `Online: ${host(status.server)}` : 'Offline';

  byId('waiting').textContent =
    waiting === 0 ? 'Everything is sent.' : `${waiting} ${waiting === 1 ? 'change' : 'changes'} waiting to send.`;
  byId('checked').textContent = checked ? `Checked at ${new Date(status.checkedAt).toLocaleTimeString()}.` : '';
  show(byId('sync-problem'), status.problem);
  show(byId('notice'), status.notice);
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
    el('span', { className: 'shortcut', textContent: phrase.shortcut }),
    ' ',
    el('span', { className: 'body', textContent: phrase.body }),
  ];
}

function bothVersions(theirs, mine) {
  return el(
    'dl',
    {},
    el('dt', { textContent: 'Theirs (saved)' }),
    el('dd', {}, ...phraseText(theirs)),
    el('dt', { textContent: 'Mine (not saved)' }),
    el('dd', {}, ...phraseText(mine)),
  );
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
        el('p', { textContent: 'Someone else saved this phrase before your change reached the server. Choose which version to keep.' }),
        bothVersions(theirs, conflict),
        button('Keep mine', resolve(true)),
        ' ',
        button('Keep theirs', resolve(false)),
      ]
    : [
        el('p', {
          textContent: theirs
            ? `${theirs.shortcut} is already in use by another phrase, so your change wasn't saved.`
            : `The server didn't accept your change. ${conflict.message ?? ''}`,
        }),
        el('dl', {}, el('dt', { textContent: 'Mine (not saved)' }), el('dd', {}, ...phraseText(conflict))),
        button('Discard mine', resolve(false)),
      ];
  return el('li', {}, el('div', { className: 'conflict' }, ...content));
}

// The phrases. Redrawn only when they change; a phrase being edited keeps its form and what's typed in it.
let shownPhrases = '';

function renderPhrases(phrases) {
  const json = JSON.stringify(phrases);
  if (json === shownPhrases) return;
  shownPhrases = json;

  const list = byId('quick-texts');
  const editing = new Map([...list.querySelectorAll(':scope > li.editing')].map((li) => [li.dataset.id, li]));
  const focused = list.contains(document.activeElement) ? document.activeElement : null;
  list.replaceChildren(...phrases.map((phrase) => editing.get(phrase.id) ?? phraseItem(phrase)));
  focused?.focus();
}

function phraseItem(phrase) {
  const li = el('li', {}, ...phraseText(phrase));
  li.dataset.id = phrase.id;
  if (phrase.pending) li.append(el('span', { className: 'pending', textContent: 'Not sent yet' }));
  li.append(
    button('Edit', () => {
      li.classList.add('editing');
      showEditForm(li, phrase.id, phrase.version, phrase);
    }),
  );
  return li;
}

function errorFor(name) {
  const span = el('span', { className: 'error' });
  span.dataset.for = name;
  return span;
}

function phraseForm(values, submitLabel) {
  return el(
    'form',
    { className: 'quick-text-form' },
    el('label', {}, 'Shortcut ', el('input', { name: 'shortcut', autocomplete: 'off', value: values.shortcut })),
    errorFor('shortcut'),
    el('label', {}, 'Text ', el('textarea', { name: 'body', rows: 2, value: values.body })),
    errorFor('body'),
    el('button', { textContent: submitLabel }),
  );
}

function formValues(form) {
  return { shortcut: form.elements.shortcut.value, body: form.elements.body.value };
}

function showProblems(form, problems) {
  for (const span of form.querySelectorAll('.error[data-for]')) {
    span.textContent = problems[span.dataset.for] ?? '';
  }
}

// `version` is the phrase as the person saw it when they began; the save is refused if it has changed since.
function showEditForm(li, id, version, values) {
  const form = phraseForm(values, 'Save');
  form.append(' ', button('Cancel', () => closeEdit(li)));
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
          { className: 'conflict' },
          el('p', { textContent: 'Someone else saved this phrase while you were editing it. Choose which version to keep.' }),
          bothVersions(answer.current, mine),
          button('Keep mine', () => saveEdit(li, id, answer.current.version, mine)),
          ' ',
          button('Keep theirs', () => closeEdit(li)),
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

byId('sync-now').addEventListener('click', () => call('sync_now'));
listen('changed', refresh);
refresh();
