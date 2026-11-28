//! The SwiftChart desktop app: a window over an encrypted local database (store.rs) that syncs with the servers in the
//! background (sync.rs) while a clinician is signed in. The page (frontend/) calls the commands below and redraws on
//! the "changed" event.
//!
//! The window locks (the expansion plan's step 19a; ehr_desktop::lock decides when): after 15 idle minutes, at the
//! sign-in's 12-hour end, when the server ends the session, and at every launch with a sign-in kept. The page covers
//! itself and keeps everything typed (frontend/app.js); here, the chart reads are refused while it's locked, and sync
//! carries on. It opens with the password, checked by the server when one answers and otherwise against the hash in
//! the store (ehr_desktop::unlock); after the 12 hours, with a full sign-in, or the password alone while no server can
//! complete one; after the server ended the session, with a full sign-in only.
//!
//! When a server the sign-in was made with says an administrator revoked it, the app deletes everything it keeps on
//! this computer (the expansion plan's step 20, wipe.rs), says so, and refuses everything else until it's quit. It
//! offers updates from the same servers, never over the lock or a recording (updates.rs).

// Release builds on Windows open no console window alongside the app.
#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

mod api;
mod capture;
mod key;
mod rules;
mod store;
mod sync;
mod updates;
mod wipe;

use std::path::PathBuf;
use std::sync::{Mutex, MutexGuard, PoisonError};
use std::time::{Duration, SystemTime, UNIX_EPOCH};

use ehr_desktop::lock::{self, Lock, Moment, Need, Why};
use ehr_desktop::unlock::{self, Allowed};
use ehr_desktop::{charts, draft_sync, recordings};
use serde::Serialize;
use tauri::{AppHandle, Emitter, Manager, State};
use tokio::sync::Notify;
use uuid::Uuid;

use store::{Conflict, Phrase, Store};

/// How often the app syncs when nothing prompts it sooner.
const SYNC_EVERY: Duration = Duration::from_secs(10);
/// How often a session the server ended presents its token (sync.rs), only so that a revocation made afterwards still
/// reaches this computer: one refused request every 5 minutes, which records no audit event and counts towards no
/// lockout on the server (step 20's final review).
const ENDED_EVERY: Duration = Duration::from_secs(5 * 60);

struct App {
    /// Or why the local database couldn't be opened.
    store: Result<Mutex<Store>, String>,
    /// Or why the sync client couldn't be made.
    sync: Result<sync::Sync, String>,
    status: Mutex<Status>,
    /// Starts a sync round now: after a change, or when the person asks.
    wake: Notify,
    /// When the window locks, and what opens it.
    lock: Mutex<Lock>,
    /// Why the kept sign-in was forgotten at start, for the sign-in form; none once someone signs in.
    notice: Mutex<Option<&'static str>>,
    /// The local database's file; none if the app's data folder couldn't be made, when there is no store either.
    database: Option<PathBuf>,
    /// Once an administrator revoked the sign-in: whether everything here was deleted, or what couldn't be. From then
    /// on the app refuses everything, and says so.
    deleted: Mutex<Option<Result<(), String>>>,
    /// This build's version (Cargo.toml, or a release build's --config), which the page shows and every request says.
    version: String,
    updates: updates::Updates,
}

/// Said when the app starts with servers other than the ones the kept sign-in was made with (step 19a's final review).
const OTHER_SERVERS: &str = "Signed out. The servers changed since you signed in.";

/// What the commands that read the offline charts answer while the window is locked.
const LOCKED: &str = "Locked.";

impl App {
    fn store(&self) -> Result<MutexGuard<'_, Store>, String> {
        if self.deleted().is_some() {
            return Err(DELETED.to_owned());
        }
        self.store
            .as_ref()
            .map(store::lock)
            .map_err(|problem| format!("The local database couldn't be opened ({problem})."))
    }

    fn sync(&self) -> Result<&sync::Sync, String> {
        self.sync.as_ref().map_err(Clone::clone)
    }

    fn deleted(&self) -> Option<Result<(), String>> {
        self.deleted
            .lock()
            .unwrap_or_else(PoisonError::into_inner)
            .clone()
    }

    fn status(&self) -> MutexGuard<'_, Status> {
        self.status.lock().unwrap_or_else(PoisonError::into_inner)
    }

    /// Held on its own: never while the store or the session is held.
    fn lock(&self) -> MutexGuard<'_, Lock> {
        self.lock.lock().unwrap_or_else(PoisonError::into_inner)
    }

    /// Chart data goes to the page only while the window is unlocked.
    fn unlocked(&self) -> Result<(), String> {
        if self.lock().is_locked() {
            Err(LOCKED.to_owned())
        } else {
            Ok(())
        }
    }

    /// The lock as the page shows it, locking the window if it's time.
    fn lock_view(&self) -> LockView {
        let now = Moment::now();
        let (state, sign_in_ended, session_over, generation) = {
            let mut lock = self.lock();
            let state = lock.tick(now);
            (
                state,
                lock.sign_in_ended(now),
                lock.session_over(),
                lock.generation(),
            )
        };
        let (name, need, why, last, left) = match state {
            lock::State::SignedOut => ("signedOut", None, None, false, Duration::ZERO),
            lock::State::Unlocked => ("unlocked", None, None, false, Duration::ZERO),
            lock::State::Warning { last, left } => ("warning", None, None, last, left),
            lock::State::Locked { need, why } => {
                ("locked", Some(need), Some(why), false, Duration::ZERO)
            }
        };
        // The clinician the lock is for, from its own record: the session may be gone.
        let charts_updated = self.store().ok().and_then(|store| {
            let owner = store.unlock(unlock::account).ok()??.clinician_id;
            store.charts_updated(owner).ok()?
        });
        LockView {
            state: name,
            need,
            why,
            last,
            left: left.as_secs(),
            idle_minutes: times().idle.as_secs().div_ceil(60),
            account: self.sync().ok().and_then(sync::Sync::account),
            sign_in_ended,
            session_over,
            charts_updated,
            generation,
        }
    }

    /// The server ended the sign-in's 12 hours, or the session: kept in the lock's record, so a restart knows, and told
    /// to the lock. A record that can't be written changes nothing the session doesn't: with it over, nothing opens. Or
    /// an administrator revoked the sign-in: everything here is deleted.
    fn ending(&self, ending: sync::Ending, handle: &AppHandle) {
        let record = match ending {
            sync::Ending::SignIn => unlock::sign_in_ended,
            sync::Ending::Session => unlock::session_over,
            sync::Ending::Revoked => return self.revoked(handle),
        };
        if let Ok(store) = self.store() {
            let _ = store.unlock(record);
        }
        let mut lock = self.lock();
        match ending {
            sync::Ending::SignIn => lock.server_ended_sign_in(),
            _ => lock.server_ended_session(),
        }
    }

    fn take_ending(&self, handle: &AppHandle) {
        if let Some(ending) = self.sync().ok().and_then(sync::Sync::take_ending) {
            self.ending(ending, handle);
        }
    }

    /// The remote wipe (wipe.rs), only for a sign-in made with the servers that said so. The app was started with them,
    /// so they always are (starting_lock forgets a sign-in made with others before anything is sent): this checks it
    /// again where it matters, and otherwise treats the answer as the session's end, deleting nothing.
    fn revoked(&self, handle: &AppHandle) {
        // Begun already, by another answer: that wipe says what became of it.
        if self.deleted().is_some() {
            return;
        }
        let record = self
            .store()
            .ok()
            .and_then(|store| store.unlock(unlock::account).ok().flatten());
        let servers = self.sync().map(sync::Sync::servers).unwrap_or_default();
        if !wipe_allowed(record.as_ref(), servers) {
            if let Ok(sync) = self.sync() {
                let _ = sync.forget_kept();
            }
            self.lock().server_ended_session();
            return;
        }
        // From here on the app refuses everything (store()), whatever becomes of the rest.
        *self.deleted.lock().unwrap_or_else(PoisonError::into_inner) = Some(Ok(()));
        let deleted = key::key_entry().and_then(|key| {
            let mut store = self.store.as_ref().ok().map(store::lock);
            wipe::delete(store.as_deref_mut(), self.database.as_deref(), &key)
        });
        if deleted.is_ok() {
            if let Some(window) = handle.get_webview_window("main") {
                let _ = window.clear_all_browsing_data();
                let _ = window.reload();
            }
            if let Ok(sync) = self.sync() {
                let _ = sync.forget_kept();
            }
        }
        self.lock().signed_out(Moment::now());
        *self.deleted.lock().unwrap_or_else(PoisonError::into_inner) = Some(deleted);
        let _ = handle.emit("changed", ());
    }
}

/// The lock, for the page (frontend/app.js).
#[derive(Serialize, Clone, Default)]
#[serde(rename_all = "camelCase")]
struct LockView {
    /// "signedOut" (nothing to lock), "unlocked", "warning" or "locked".
    state: &'static str,
    /// What opens it, while locked.
    need: Option<Need>,
    why: Option<Why>,
    /// The warning is for the sign-in's 12-hour end.
    last: bool,
    /// Seconds until the warning's end.
    left: u64,
    idle_minutes: u64,
    /// Whose sign-in it is.
    account: Option<String>,
    /// The sign-in's 12 hours are over: a full sign-in is needed as soon as a server can complete one.
    sign_in_ended: bool,
    /// The server ended the session: the page stays as it is, under the lock.
    session_over: bool,
    /// When the offline charts were last downloaded.
    charts_updated: Option<charts::Updated>,
    /// Moves on with each lock, opening or change of what opens it (lock.rs): the page drops a view older than one it
    /// has shown, so an answer computed before a lock can't open the window.
    generation: u64,
}

/// The last sync round, and who is signed in, for the page.
#[derive(Serialize, Clone, Default)]
#[serde(rename_all = "camelCase")]
struct Status {
    /// The name of the clinician signed in, from the session when the page asks; none while signed out.
    account: Option<String>,
    /// The server that answered; none while offline.
    server: Option<String>,
    problem: Option<String>,
    /// When the round ended, in milliseconds since 1970, for the page to show in local time.
    checked_at: Option<u64>,
}

#[derive(Serialize, Default)]
#[serde(rename_all = "camelCase")]
struct View {
    phrases: Vec<Phrase>,
    conflicts: Vec<Conflict>,
    /// Changes saved here and not yet sent.
    waiting: usize,
    status: Status,
    offline_available: bool,
    clinical_waiting: usize,
    /// When the offline charts were last downloaded, which the page shows beside them.
    charts_updated: Option<charts::Updated>,
    lock: LockView,
    /// Why the kept sign-in was forgotten at start, shown with the sign-in form.
    signed_out_because: Option<&'static str>,
    /// The servers a sign-in goes to, in order, which the sign-in form names.
    servers: Vec<String>,
    /// An administrator revoked the sign-in, and everything here was deleted: the page shows only that.
    deleted: bool,
    /// What couldn't be deleted, when something couldn't: quitting and starting again finishes it.
    deleted_problem: Option<String>,
    /// This build's version.
    version: String,
    /// An update, while the window is unlocked and nothing is recording.
    update: Option<updates::Offer>,
}

#[tauri::command]
fn view(app: State<'_, App>) -> Result<View, String> {
    if let Some(deleted) = app.deleted() {
        return Ok(View {
            deleted: true,
            deleted_problem: deleted.err(),
            ..View::default()
        });
    }
    let lock = app.lock_view();
    let recording = app.lock().recording_now(Moment::now());
    let update = app.updates.view(lock.state == "locked", recording);
    let store = app.store()?;
    Ok(View {
        lock,
        signed_out_because: *app.notice.lock().unwrap_or_else(PoisonError::into_inner),
        servers: app
            .sync()
            .map(|sync| sync.servers().to_vec())
            .unwrap_or_default(),
        deleted: false,
        deleted_problem: None,
        version: app.version.clone(),
        update,
        phrases: store.phrases()?,
        conflicts: store.conflicts()?,
        waiting: store.waiting()?,
        offline_available: app.sync().is_ok_and(sync::Sync::offline_available),
        clinical_waiting: match app.sync().and_then(|sync| sync.with_clinician(None, Ok)) {
            Ok(owner) => store.clinical_waiting(owner)?,
            Err(_) => 0,
        },
        charts_updated: match app.sync().and_then(|sync| sync.with_clinician(None, Ok)) {
            Ok(owner) => store.charts_updated(owner)?,
            Err(_) => None,
        },
        status: Status {
            account: app.sync.as_ref().ok().and_then(sync::Sync::account),
            ..app.status().clone()
        },
    })
}

#[derive(Serialize)]
struct OfflineChart {
    chart: charts::Chart,
    drafts: Vec<draft_sync::DraftView>,
    recordings: Vec<recordings::RecordingView>,
}

#[tauri::command]
async fn download_panel(app: State<'_, App>, handle: AppHandle) -> Result<(), String> {
    app.unlocked()?;
    let sync = app.sync()?;
    let downloaded = sync.download_panel().await;
    app.take_ending(&handle);
    if sync.take_update_required() {
        app.updates.check_now();
    }
    let (token, panel) = downloaded?;
    let mut store = app.store()?;
    sync.with_clinician(Some(&token), |owner| {
        store.replace_panel(owner, &panel).map_err(Into::into)
    })?;
    app.wake.notify_one();
    Ok(())
}

#[tauri::command]
fn offline_panel(app: State<'_, App>) -> Result<Vec<charts::ChartSummary>, String> {
    app.unlocked()?;
    let store = app.store()?;
    app.sync()?
        .with_clinician(None, |owner| store.offline_panel(owner).map_err(Into::into))
}

#[tauri::command]
fn offline_chart(app: State<'_, App>, patient: Uuid) -> Result<OfflineChart, String> {
    app.unlocked()?;
    let store = app.store()?;
    app.sync()?.with_clinician(None, |owner| {
        let (chart, drafts) = store.offline_chart(owner, patient)?;
        let recordings = store.recordings(owner, patient)?;
        Ok(OfflineChart {
            chart,
            drafts,
            recordings,
        })
    })
}

#[tauri::command]
fn save_clinical_draft(app: State<'_, App>, patient: Uuid, body: String) -> Result<(), String> {
    let store = app.store()?;
    app.sync()?.with_clinician(None, |owner| {
        store
            .save_clinical_draft(owner, patient, &body)
            .map_err(Into::into)
    })?;
    app.wake.notify_one();
    Ok(())
}

#[tauri::command]
fn edit_clinical(
    app: State<'_, App>,
    patient: Uuid,
    id: Uuid,
    version: Option<i64>,
    body: String,
) -> Result<(), String> {
    let mut store = app.store()?;
    app.sync()?.with_clinician(None, |owner| {
        store
            .edit_clinical(owner, patient, id, version, &body)
            .map_err(Into::into)
    })?;
    app.wake.notify_one();
    Ok(())
}

#[tauri::command]
fn resolve_clinical(
    app: State<'_, App>,
    patient: Uuid,
    id: Uuid,
    version: i64,
    keep_mine: bool,
) -> Result<(), String> {
    let mut store = app.store()?;
    app.sync()?.with_clinician(None, |owner| {
        store
            .resolve_clinical(owner, patient, id, version, keep_mine)
            .map_err(Into::into)
    })?;
    app.wake.notify_one();
    Ok(())
}

#[tauri::command]
fn save_recording(
    app: State<'_, App>,
    patient: Uuid,
    media_type: String,
    audio: Vec<u8>,
) -> Result<(), String> {
    let store = app.store()?;
    app.sync()?.with_clinician(None, |owner| {
        store
            .save_recording(owner, patient, &media_type, &audio)
            .map_err(Into::into)
    })?;
    app.wake.notify_one();
    Ok(())
}

/// What became of a save, for the page.
#[derive(Serialize)]
#[serde(tag = "outcome", rename_all = "camelCase")]
enum Save {
    Saved,
    /// Nothing was saved: the problems, by field.
    Problems {
        problems: rules::Problems,
    },
    /// Nothing was saved: the phrase changed while the person was editing it. This is it now.
    ChangedSince {
        current: Phrase,
    },
}

/// Saves a new phrase here, to be sent.
#[tauri::command]
fn add(app: State<'_, App>, shortcut: String, body: String) -> Result<Save, String> {
    save(&app, None, &shortcut, &body)
}

/// Saves an edit of `version`, the phrase as the person saw it when they began.
#[tauri::command]
fn edit(
    app: State<'_, App>,
    id: Uuid,
    version: i64,
    shortcut: String,
    body: String,
) -> Result<Save, String> {
    save(&app, Some((id, version)), &shortcut, &body)
}

/// Keep mine sends the change again over their version; otherwise it's dropped.
#[tauri::command]
fn resolve(app: State<'_, App>, key: Uuid, keep_mine: bool) -> Result<(), String> {
    app.store()?.resolve(key, keep_mine)?;
    app.wake.notify_one();
    Ok(())
}

#[tauri::command]
fn sync_now(app: State<'_, App>) {
    app.wake.notify_one();
}

/// Signs in with the servers, then syncs. Err is what to show under the form.
#[tauri::command]
async fn sign_in(
    app: State<'_, App>,
    email: String,
    password: String,
    code: String,
) -> Result<(), String> {
    app.sync()?.sign_in(&email, &password, &code).await?;
    let remembered = remember_sign_in(&app, &email, password).await;
    signed_in(&app);
    *app.notice.lock().unwrap_or_else(PoisonError::into_inner) = None;
    app.wake.notify_one();
    remembered
}

/// A sign-in with both factors: the lock's 12 hours start now, and the offline copy's 30 days are the new session's.
fn signed_in(app: &App) {
    let offline_until = app.sync().ok().and_then(sync::Sync::offline_until);
    let mut lock = app.lock();
    lock.signed_in(SystemTime::now(), Moment::now());
    lock.offline_until(offline_until);
}

/// After a sign-in with both factors, what the lock needs to open without a server (ehr_desktop::unlock): the password's
/// slow hash, made off the async threads, never the password; and the servers the sign-in was made with, the only ones
/// that may answer for the lock.
async fn remember_sign_in(app: &App, email: &str, password: String) -> Result<(), String> {
    let verifier = tauri::async_runtime::spawn_blocking(move || unlock::hash(&password))
        .await
        .map_err(|error| error.to_string())?;
    let store = app.store()?;
    let sync = app.sync()?;
    sync.with_clinician(None, |owner| {
        store
            .unlock(|db| unlock::remember(db, owner, email, &verifier, unix_now(), sync.servers()))
            .map_err(Into::into)
    })
}

/// Signs out, and starts a round so the status says so. The local copy stays, to read; the lock's record goes, and
/// there's nothing left to lock. The lock offers it too, to someone else at the computer.
#[tauri::command]
async fn sign_out(app: State<'_, App>, handle: AppHandle) -> Result<(), String> {
    let signed_out = app.sync()?.sign_out().await;
    // A server that says the sign-in was revoked: everything here is deleted instead (step 20's final review).
    app.take_ending(&handle);
    if app.deleted().is_some() {
        return Ok(());
    }
    let forgotten = app
        .store()
        .and_then(|store| store.unlock(unlock::forget).map_err(Into::into));
    app.lock().signed_out(Moment::now());
    app.wake.notify_one();
    signed_out.and(forgotten)
}

#[tauri::command]
fn lock_state(app: State<'_, App>) -> LockView {
    app.lock_view()
}

/// A key, a click or tap, the wheel or typing in the window (sent at most once every 10 seconds).
#[tauri::command]
fn activity(app: State<'_, App>) {
    app.lock().activity(Moment::now());
}

/// A recording began or ended: it holds the lock off while it lasts.
#[tauri::command]
fn recording(app: State<'_, App>, active: bool) {
    app.lock().recording(active, Moment::now());
}

#[tauri::command]
fn lock_now(app: State<'_, App>) -> LockView {
    app.lock().lock_now(Moment::now());
    app.lock_view()
}

/// Opens the lock, or says why not. For the password alone, the server checks it when one answers, and only when none
/// can does this computer, against its hash; a server's no stands. For a full sign-in, the same: the password alone
/// opens it past the 12 hours only when no server can complete a sign-in; after the server ended the session, never.
#[tauri::command]
async fn unlock(
    app: State<'_, App>,
    handle: AppHandle,
    password: String,
    code: String,
) -> Result<LockView, String> {
    let Some(need) = app.lock().need() else {
        return Ok(app.lock_view());
    };
    let sync = app.sync()?;
    let record = app
        .store()?
        .unlock(unlock::account)?
        .ok_or_else(|| "Sign out, then sign in again.".to_owned())?;
    // Only the servers the sign-in was made with answer for the lock. The app forgets a sign-in kept for others as it
    // starts, before anything is sent, and its list can't change after: this repeats that check where it matters.
    if record.servers != sync.servers() {
        return Err(OTHER_SERVERS.to_owned());
    }
    let next = if need == Need::Password {
        after_unlock(sync.unlock(&password).await)
    } else {
        let email = record.email;
        let answer = sync.sign_in_answer(&email, &password, &code).await;
        if answer.is_ok() {
            remember_sign_in(&app, &email, password).await?;
            signed_in(&app);
            *app.notice.lock().unwrap_or_else(PoisonError::into_inner) = None;
            app.wake.notify_one();
            return Ok(app.lock_view());
        }
        after_sign_in(answer, need, &code)
    };
    match next {
        Next::Opened => app.store()?.unlock(unlock::right)?,
        Next::CheckHere => open_here(&app, password).await?,
        Next::Refused(problem) => return Err(problem),
        Next::Ended(ending, problem) => {
            app.ending(ending, &handle);
            return Err(problem);
        }
    }
    app.lock().unlocked(Moment::now());
    Ok(app.lock_view())
}

/// What the lock does after a server's answer, or none.
#[derive(Debug, PartialEq)]
enum Next {
    Opened,
    /// No server could check the password: this computer does.
    CheckHere,
    /// A server said no, which stands: nothing is checked here.
    Refused(String),
    /// The server ended the sign-in or the session, saying so.
    Ended(sync::Ending, String),
}

/// After the server's check of the password alone.
fn after_unlock(answer: sync::Unlock) -> Next {
    match answer {
        sync::Unlock::Unlocked => Next::Opened,
        sync::Unlock::Refused(problem) => Next::Refused(problem),
        sync::Unlock::SignInEnded => Next::Ended(
            sync::Ending::SignIn,
            "Sign-in expired. Enter the code from your authenticator app.".to_owned(),
        ),
        sync::Unlock::SessionOver => Next::Ended(sync::Ending::Session, SESSION_OVER.to_owned()),
        sync::Unlock::Revoked => Next::Ended(sync::Ending::Revoked, DELETED.to_owned()),
        sync::Unlock::Unreachable => Next::CheckHere,
    }
}

/// After a full sign-in that didn't happen. Past the 12 hours, with no server able to complete it, the password alone
/// opens the offline charts; never after the server ended the session, and never after a server said no.
fn after_sign_in(answer: Result<(), sync::SignInFailure>, need: Need, code: &str) -> Next {
    match answer {
        Ok(()) => Next::Opened,
        Err(sync::SignInFailure::Refused(_)) if code.trim().is_empty() => {
            Next::Refused("Code is required.".to_owned())
        }
        Err(sync::SignInFailure::Refused(problem)) => Next::Refused(problem),
        Err(sync::SignInFailure::Unreachable(_)) if need == Need::SignIn => Next::CheckHere,
        Err(sync::SignInFailure::Unreachable(problem)) => Next::Refused(format!(
            "{problem} Sign in again once the clinic's server can be reached."
        )),
    }
}

const SESSION_OVER: &str = "Signed out. Sign in again.";

/// The lock's password, checked on this computer against its hash, when no server could check it. Wrong ones are
/// counted (ehr_desktop::unlock): after 5 it waits 15 minutes, after 10 only a full sign-in opens it.
async fn open_here(app: &App, password: String) -> Result<(), String> {
    // Not past the offline copy's 30 days, whatever the password.
    app.sync()?.with_clinician(None, |_| Ok(()))?;
    let now = unix_now();
    let account = app
        .store()?
        .unlock(unlock::account)?
        .ok_or_else(|| SESSION_OVER.to_owned())?;
    let verifier = match account.may_check(now) {
        Allowed::Check(verifier) => verifier.to_owned(),
        Allowed::WaitUntil(until) => return Err(wait_problem(until - now)),
        Allowed::SignIn if account.verifier.is_none() => return Err(SESSION_OVER.to_owned()),
        Allowed::SignIn => {
            app.lock().require_sign_in();
            return Err(TOO_MANY.to_owned());
        }
    };
    let right = tauri::async_runtime::spawn_blocking(move || unlock::verify(&verifier, &password))
        .await
        .map_err(|error| error.to_string())?;
    let store = app.store()?;
    if right {
        store.unlock(unlock::right)?;
        return Ok(());
    }
    let counted = store.unlock(|db| unlock::wrong(db, now))?;
    drop(store);
    let allowed = counted.as_ref().map(|account| account.may_check(now));
    if matches!(allowed, Some(Allowed::SignIn)) {
        // From now on only a full sign-in opens it, wherever it's checked.
        app.lock().require_sign_in();
    }
    Err(match allowed {
        Some(Allowed::WaitUntil(until)) => wait_problem(until - now),
        Some(Allowed::SignIn) => TOO_MANY.to_owned(),
        _ => "Wrong password.".to_owned(),
    })
}

/// What every command answers once an administrator revoked the sign-in and the app deleted what it kept here.
const DELETED: &str = "Deleted. An administrator revoked this computer's sign-in. Quit SwiftChart.";

/// Whether the remote wipe's marker may delete what's kept here: only for a sign-in whose lock's record says it was made
/// with the servers the app has, which are the ones that answered (adjustment c of the step's plan review).
fn wipe_allowed(record: Option<&unlock::Account>, servers: &[String]) -> bool {
    record.is_some_and(|record| record.servers == servers)
}

/// Get the offered update ready: downloaded, its signature checked, and on macOS installed for the next start.
#[tauri::command]
async fn accept_update(app: State<'_, App>, handle: AppHandle) -> Result<(), String> {
    let _ = handle.emit("changed", ());
    let accepted = app.updates.accept().await;
    let _ = handle.emit("changed", ());
    accepted
}

/// "Not now": the offer is put away until the next start.
#[tauri::command]
fn dismiss_update(app: State<'_, App>) {
    app.updates.dismiss();
}

/// Restart now, with an update ready: never while the window is locked or a recording is being made.
#[tauri::command]
fn restart_for_update(app: State<'_, App>, handle: AppHandle) -> Result<(), String> {
    {
        let lock = app.lock();
        if lock.is_locked() {
            return Err(LOCKED.to_owned());
        }
        if lock.recording_now(Moment::now()) {
            return Err("Stop the recording first.".to_owned());
        }
    }
    app.updates.restart(&handle)
}

/// The app's commands, each refused once everything here was deleted, but for those open_after_deletion names.
fn refused_after_deletion(
    commands: impl Fn(tauri::ipc::Invoke) -> bool + Send + Sync + 'static,
) -> impl Fn(tauri::ipc::Invoke) -> bool + Send + Sync + 'static {
    move |invoke| {
        let deleted = invoke
            .message
            .webview()
            .try_state::<App>()
            .is_some_and(|app| app.deleted().is_some());
        if deleted && !open_after_deletion(invoke.message.command()) {
            invoke.resolver.reject(DELETED);
            return true;
        }
        commands(invoke)
    }
}

/// Checks for updates from the servers this build was made for (updates.rs).
async fn updates_in_background(handle: AppHandle) {
    let app = handle.state::<App>();
    let servers = app
        .sync()
        .map(|sync| sync.servers().to_vec())
        .unwrap_or_default();
    if !servers.is_empty() {
        app.updates
            .check_in_background(handle.clone(), servers)
            .await;
    }
}

/// The commands the page may still call once everything here was deleted: what it shows, and Quit. Every other is
/// refused, a command added later included (main's invoke handler).
fn open_after_deletion(command: &str) -> bool {
    matches!(command, "view" | "quit")
}

/// Ends the app, from the page that says everything here was deleted.
#[tauri::command]
fn quit(handle: AppHandle) {
    handle.exit(0);
}

const TOO_MANY: &str = "Too many wrong passwords. Sign in when the clinic's server can be reached.";

fn wait_problem(seconds: i64) -> String {
    let minutes = (seconds.max(1) + 59) / 60;
    format!(
        "Too many wrong passwords. Try again in {minutes} {}.",
        if minutes == 1 { "minute" } else { "minutes" }
    )
}

/// The lock's times. Development builds take shorter ones from EHR_DEBUG_IDLE_SECONDS and EHR_DEBUG_SIGN_IN_SECONDS,
/// for checking the lock by hand (docs/expansion/19a-hands-on-check.md); release builds never read them.
fn times() -> lock::Times {
    if cfg!(debug_assertions) {
        let seconds = |name| {
            std::env::var(name)
                .ok()
                .and_then(|value| value.parse().ok())
                .map(Duration::from_secs)
        };
        lock::Times::shortened(
            seconds("EHR_DEBUG_IDLE_SECONDS"),
            seconds("EHR_DEBUG_SIGN_IN_SECONDS"),
        )
    } else {
        lock::Times::STANDARD
    }
}

/// Locked from the start whenever a sign-in is kept, so quitting never skips the lock. A sign-in kept by an earlier
/// build, which has no record of the lock's (ehr_desktop::unlock), is forgotten here: the server ends it at its 12
/// hours anyway, and the clinician signs in again. A record with no sign-in goes too. So does a sign-in made with other
/// servers than the app has now (a build made for others, or EHR_SYNC_SERVERS in a development build), before anything
/// is sent to them: any server can answer yes to a
/// password, and only those the sign-in was made with may answer for the lock (step 19a's final review). The second
/// value says why, when that's the reason.
fn starting_lock(
    store: &Result<Mutex<Store>, String>,
    sync: &Result<sync::Sync, String>,
) -> (Lock, Option<&'static str>) {
    let now = Moment::now();
    let record = store
        .as_ref()
        .ok()
        .and_then(|store| store::lock(store).unlock(unlock::account).ok().flatten());
    let clinician = sync.as_ref().ok().and_then(sync::Sync::clinician);
    let servers = sync.as_ref().ok().map(sync::Sync::servers);
    let for_this_clinician = record
        .as_ref()
        .is_some_and(|record| clinician == Some(record.clinician_id));
    if let Some(record) = &record
        && for_this_clinician
        && servers == Some(record.servers.as_slice())
    {
        let signed_in_at =
            UNIX_EPOCH + Duration::from_secs(u64::try_from(record.signed_in_at).unwrap_or(0));
        let mut lock = Lock::new(times(), Some(signed_in_at), record.sign_in_ended, now);
        // Past the sign-in's 30 days, it opens on the lock for a full sign-in only (lock.rs), the session kept.
        lock.offline_until(sync.as_ref().ok().and_then(sync::Sync::offline_until));
        if record.wrong >= unlock::WRONG_BEFORE_SIGN_IN {
            lock.require_sign_in();
        }
        // A session the server ended, kept for a revocation to reach: only a full sign-in opens the lock.
        if sync.as_ref().is_ok_and(sync::Sync::session_ended) {
            lock.server_ended_session();
        }
        return (lock, None);
    }
    // With no database there is nothing to show, and nothing is forgotten; with no sync client, nothing is sent.
    if let (Ok(sync), Ok(store)) = (sync, store) {
        let _ = sync.forget_kept();
        if record.is_some() {
            let _ = store::lock(store).unlock(unlock::forget);
        }
    }
    let notice = (for_this_clinician && sync.is_ok()).then_some(OTHER_SERVERS);
    (Lock::new(times(), None, false, now), notice)
}

/// Every second: the lock's state, sent to the page when it changes.
async fn lock_in_background(handle: AppHandle) {
    let app = handle.state::<App>();
    let mut every = tokio::time::interval(Duration::from_secs(1));
    every.set_missed_tick_behavior(tokio::time::MissedTickBehavior::Skip);
    let mut shown = None;
    loop {
        every.tick().await;
        let view = app.lock_view();
        let showing = (view.state, view.need, view.last, view.sign_in_ended);
        if shown != Some(showing) {
            shown = Some(showing);
            let _ = handle.emit("lock", view);
        }
    }
}

/// Seconds since 1970, by this computer's clock.
fn unix_now() -> i64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map_or(0, |since| {
            i64::try_from(since.as_secs()).unwrap_or(i64::MAX)
        })
}

fn save(app: &App, edit: Option<(Uuid, i64)>, shortcut: &str, body: &str) -> Result<Save, String> {
    let store = app.store()?;
    let phrases = store.phrases()?;
    let mut problems = rules::check(shortcut, body);
    // The server checks this too, but by then the person may have moved on.
    let normalized = rules::normalize(shortcut);
    let id = edit.map(|(id, _)| id);
    if problems.is_empty()
        && phrases
            .iter()
            .any(|phrase| phrase.shortcut == normalized && Some(phrase.id) != id)
    {
        problems.insert("shortcut", "This shortcut is already in use.");
    }
    if !problems.is_empty() {
        return Ok(Save::Problems { problems });
    }

    match edit {
        None => {
            store.add(shortcut, body)?;
        }
        Some((id, version)) => match store.edit(id, version, shortcut, body) {
            Err(store::Error::ChangedSince) => {
                let current = phrases.into_iter().find(|phrase| phrase.id == id);
                return Ok(Save::ChangedSince {
                    current: current.ok_or(store::Error::NotFound)?,
                });
            }
            saved => saved?,
        },
    }
    app.wake.notify_one();
    Ok(Save::Saved)
}

async fn sync_in_background(handle: AppHandle) {
    let app = handle.state::<App>();
    let Ok(store) = &app.store else { return };
    let client = match app.sync() {
        Ok(client) => client,
        Err(problem) => {
            app.status().problem = Some(problem);
            return;
        }
    };

    loop {
        let report = client.round(store).await;
        // Before the page hears of the round: a session the server ended locks the window, rather than clear it.
        app.take_ending(&handle);
        if client.take_update_required() {
            app.updates.check_now();
        }
        if app.deleted().is_some() {
            // Wiped: nothing more is sent, or kept.
            return;
        }
        let _ = handle.emit("lock", app.lock_view());
        {
            let mut status = app.status();
            status.server = report.server;
            status.problem = report.problem;
            status.checked_at = SystemTime::now()
                .duration_since(UNIX_EPOCH)
                .ok()
                .and_then(|since| u64::try_from(since.as_millis()).ok());
        }
        // Only a closed window misses it, and the page asks for everything when it opens.
        let _ = handle.emit("changed", ());

        let every = if client.session_ended() {
            ENDED_EVERY
        } else {
            SYNC_EVERY
        };
        tokio::select! {
            () = tokio::time::sleep(every) => {}
            () = app.wake.notified() => {}
        }
    }
}

/// Opens the encrypted database at `path` without replacing it if the key is missing or invalid.
fn open_store(path: &Result<PathBuf, String>) -> Result<Store, String> {
    let path = path.as_ref().map_err(Clone::clone)?;
    let exists = path
        .try_exists()
        .map_err(|error| format!("{}: {error}", path.display()))?;
    let key = key::load(exists)?;
    Store::open(path, &key).map_err(Into::into)
}

/// In the app's data folder.
fn database_path(app: &tauri::App) -> Result<PathBuf, String> {
    let folder = app
        .path()
        .app_data_dir()
        .map_err(|error| error.to_string())?;
    std::fs::create_dir_all(&folder).map_err(|error| format!("{}: {error}", folder.display()))?;
    Ok(folder.join("ehr.db"))
}

fn main() {
    tauri::Builder::default()
        .plugin(tauri_plugin_updater::Builder::new().build())
        .setup(|app| {
            let database = database_path(app);
            let store = open_store(&database).map(Mutex::new);
            // After open_store, which chose the credential store that keeps the session.
            let version = app.package_info().version.to_string();
            let sync = key::session_entry()
                .and_then(|entry| sync::Sync::from_environment(entry, &version));
            let (lock, notice) = starting_lock(&store, &sync);
            app.manage(App {
                store,
                sync,
                status: Mutex::new(Status::default()),
                wake: Notify::new(),
                lock: Mutex::new(lock),
                notice: Mutex::new(notice),
                database: database.ok(),
                deleted: Mutex::new(None),
                version,
                updates: updates::Updates::default(),
            });
            // Recording needs the microphone, which WebKitGTK refuses unless the app allows it (capture.rs).
            #[cfg(target_os = "linux")]
            if let Some(window) = app.get_webview_window("main") {
                capture::enable(&window)?;
            }
            tauri::async_runtime::spawn(sync_in_background(app.handle().clone()));
            tauri::async_runtime::spawn(lock_in_background(app.handle().clone()));
            tauri::async_runtime::spawn(updates_in_background(app.handle().clone()));
            Ok(())
        })
        .invoke_handler(refused_after_deletion(tauri::generate_handler![
            view,
            add,
            edit,
            resolve,
            sync_now,
            sign_in,
            sign_out,
            download_panel,
            offline_panel,
            offline_chart,
            save_clinical_draft,
            edit_clinical,
            resolve_clinical,
            save_recording,
            lock_state,
            activity,
            recording,
            lock_now,
            unlock,
            quit,
            accept_update,
            dismiss_update,
            restart_for_update,
        ]))
        .run(tauri::generate_context!())
        .expect("failed to run the SwiftChart desktop app");
}

#[cfg(test)]
mod tests {
    use std::io::{Read, Write};
    use std::net::TcpListener;
    use std::path::Path;
    use std::sync::Arc;
    use std::sync::atomic::{AtomicUsize, Ordering};

    use keyring_core::api::CredentialStoreApi;
    use keyring_core::mock;

    use super::*;

    const CLINICIAN: &str = "018f0000-0000-7000-8000-0000000000c1";
    const SIGNED_IN_WITH: &str = "https://ehr.example.com:8443";

    /// A server on a free local port that answers 204 to anything, as a stand-in anyone at the computer could start,
    /// counting what reaches it.
    fn yes_to_anything() -> (String, Arc<AtomicUsize>) {
        let listener = TcpListener::bind("127.0.0.1:0").unwrap();
        let url = format!("http://{}", listener.local_addr().unwrap());
        let reached = Arc::new(AtomicUsize::new(0));
        let count = Arc::clone(&reached);
        std::thread::spawn(move || {
            for stream in listener.incoming() {
                let mut stream = stream.unwrap();
                count.fetch_add(1, Ordering::SeqCst);
                let _ = stream.read(&mut [0; 4096]);
                let _ = stream.write_all(b"HTTP/1.1 204 No Content\r\nConnection: close\r\n\r\n");
            }
        });
        (url, reached)
    }

    /// A store with the lock's record of a sign-in made with `SIGNED_IN_WITH`, and a sync client for `servers` holding
    /// that sign-in's session.
    fn kept_sign_in(
        servers: Vec<String>,
    ) -> (Result<Mutex<Store>, String>, Result<sync::Sync, String>) {
        kept_sign_in_until(servers, 4_102_444_800)
    }

    /// kept_sign_in, with the offline copy's 30 days ending at `offline_until`, in seconds since 1970.
    fn kept_sign_in_until(
        servers: Vec<String>,
        offline_until: u64,
    ) -> (Result<Mutex<Store>, String>, Result<sync::Sync, String>) {
        kept_session(servers, offline_until, false)
    }

    /// kept_sign_in_until, with the session `ended` by the server (sync.rs) or not.
    fn kept_session(
        servers: Vec<String>,
        offline_until: u64,
        ended: bool,
    ) -> (Result<Mutex<Store>, String>, Result<sync::Sync, String>) {
        let store = Store::open(Path::new(":memory:"), &[7; 32]).unwrap();
        store
            .unlock(|db| {
                unlock::remember(
                    db,
                    CLINICIAN.parse().unwrap(),
                    "clinician@example.com",
                    &unlock::hash("the password"),
                    unix_now(),
                    &[SIGNED_IN_WITH.to_owned()],
                )
            })
            .unwrap();
        let credentials = mock::Store::new().unwrap();
        let entry = credentials
            .build(crate::key::SERVICE, "server-session", None)
            .unwrap();
        entry
            .set_password(
                &serde_json::json!({ "token": "the-token", "displayName": "Dr. Example",
                    "expiresAt": "2099-01-01T00:00:00Z", "ended": ended,
                    "offlineAccess": { "clinicianId": CLINICIAN, "expiresUnixSeconds": offline_until } })
                .to_string(),
            )
            .unwrap();
        (
            Ok(Mutex::new(store)),
            sync::Sync::new(servers, entry, "0.2.0"),
        )
    }

    /// Step 19a's final review, the reviewer's case: with the window locked, someone at the computer starts the app again
    /// with EHR_SYNC_SERVERS naming a server of their own that answers yes to anything. The app forgets the kept sign-in
    /// before anything is sent, says why, and keeps no record that could open the lock; the password, any password, then
    /// goes nowhere, and the stand-in never hears from the app, the token least of all.
    #[tokio::test]
    async fn a_server_the_sign_in_wasnt_made_with_never_opens_the_lock_or_gets_the_token() {
        let (stand_in, reached) = yes_to_anything();
        let (store, sync) = kept_sign_in(vec![stand_in]);

        let (mut lock, notice) = starting_lock(&store, &sync);

        assert_eq!(lock.tick(Moment::now()), lock::State::SignedOut);
        assert_eq!(notice, Some(OTHER_SERVERS));
        let sync = sync.unwrap();
        assert_eq!(sync.account(), None, "the kept sign-in is forgotten");
        let store = store.unwrap();
        assert_eq!(store::lock(&store).unlock(unlock::account).unwrap(), None);
        assert_eq!(
            sync.unlock("certainly not the password").await,
            sync::Unlock::SessionOver
        );
        assert_eq!(reached.load(Ordering::SeqCst), 0, "nothing reached it");
    }

    /// Step 19a's final review: started again with the servers the sign-in was made with, the record of them survives the
    /// restart and the app opens on the lock, for the password, with the sign-in kept.
    #[test]
    fn with_the_servers_it_was_made_with_a_kept_sign_in_starts_locked() {
        let (store, sync) = kept_sign_in(vec![SIGNED_IN_WITH.to_owned()]);

        let (mut lock, notice) = starting_lock(&store, &sync);

        assert_eq!(
            lock.tick(Moment::now()),
            lock::State::Locked {
                need: Need::Password,
                why: Why::Started
            }
        );
        assert_eq!(notice, None);
        assert_eq!(sync.unwrap().account().as_deref(), Some("Dr. Example"));
    }

    /// TC-UC7-13 (proposed FR-69, Tahseen's 14b): a kept sign-in whose 30 days are over starts on the lock for a full
    /// sign-in only, with the session kept for the next server to answer, and nothing deleted.
    #[test]
    fn a_kept_sign_in_past_its_30_days_starts_locked_for_a_full_sign_in() {
        let yesterday = u64::try_from(unix_now()).unwrap() - 24 * 60 * 60;
        let (store, sync) = kept_sign_in_until(vec![SIGNED_IN_WITH.to_owned()], yesterday);

        let (mut lock, notice) = starting_lock(&store, &sync);

        assert_eq!(
            lock.tick(Moment::now()),
            lock::State::Locked {
                need: Need::SignedOut,
                why: Why::OfflineEnded
            }
        );
        assert_eq!(notice, None);
        assert_eq!(sync.unwrap().account().as_deref(), Some("Dr. Example"));
        assert!(
            store::lock(&store.unwrap())
                .unlock(unlock::account)
                .unwrap()
                .is_some()
        );
    }

    /// Step 20's final review: a session the server ended, its token kept for a revocation to reach, starts on the lock
    /// for a full sign-in only, and opens nothing until then.
    #[test]
    fn a_kept_session_the_server_ended_starts_locked_for_a_full_sign_in() {
        let (store, sync) = kept_session(vec![SIGNED_IN_WITH.to_owned()], 4_102_444_800, true);

        let (mut lock, notice) = starting_lock(&store, &sync);

        assert_eq!(
            lock.tick(Moment::now()),
            lock::State::Locked {
                need: Need::SignedOut,
                why: Why::Ended
            }
        );
        assert_eq!(notice, None);
        let sync = sync.unwrap();
        assert!(sync.session_ended());
        assert!(!sync.offline_available());
    }

    /// Step 20's final review: once everything here was deleted, every command is refused but the page's view and Quit,
    /// a command added later included.
    #[test]
    fn after_deletion_only_the_view_and_quit_are_open() {
        let commands = [
            "add",
            "edit",
            "resolve",
            "sync_now",
            "sign_in",
            "sign_out",
            "download_panel",
            "offline_panel",
            "offline_chart",
            "save_clinical_draft",
            "edit_clinical",
            "resolve_clinical",
            "save_recording",
            "lock_state",
            "activity",
            "recording",
            "lock_now",
            "unlock",
            "accept_update",
            "dismiss_update",
            "restart_for_update",
            "a_command_added_later",
        ];
        assert!(commands.iter().all(|command| !open_after_deletion(command)));
        assert!(open_after_deletion("view") && open_after_deletion("quit"));
    }

    /// TC-UC7-12 (proposed FR-69): the remote wipe's marker deletes what's kept here only for a sign-in made with the
    /// servers the app has, which answered; a sign-in with no lock's record, or made with others, is never wiped.
    #[test]
    fn only_the_servers_the_sign_in_was_made_with_can_wipe_it() {
        let (store, _) = kept_sign_in(vec![SIGNED_IN_WITH.to_owned()]);
        let record = store::lock(&store.unwrap())
            .unlock(unlock::account)
            .unwrap();
        let theirs = [SIGNED_IN_WITH.to_owned()];
        let others = ["https://someone-else.example.net".to_owned()];

        assert!(wipe_allowed(record.as_ref(), &theirs));
        assert!(!wipe_allowed(record.as_ref(), &others));
        assert!(!wipe_allowed(None, &theirs));
        assert_eq!(
            after_unlock(sync::Unlock::Revoked),
            Next::Ended(sync::Ending::Revoked, DELETED.to_owned())
        );
    }

    /// TC-UC7-10 (proposed FR-68), the lock's choice: the password is checked on this computer only when no server
    /// could check it; a server's no stands, the 12-hour end asks for the code too, and a session the server ended is
    /// told to the lock.
    #[test]
    fn the_password_alone_is_checked_here_only_when_no_server_could() {
        assert_eq!(after_unlock(sync::Unlock::Unlocked), Next::Opened);
        assert_eq!(after_unlock(sync::Unlock::Unreachable), Next::CheckHere);
        assert_eq!(
            after_unlock(sync::Unlock::Refused("Wrong password.".to_owned())),
            Next::Refused("Wrong password.".to_owned())
        );
        assert!(matches!(
            after_unlock(sync::Unlock::SignInEnded),
            Next::Ended(sync::Ending::SignIn, _)
        ));
        assert!(matches!(
            after_unlock(sync::Unlock::SessionOver),
            Next::Ended(sync::Ending::Session, _)
        ));
    }

    /// TC-UC7-10: past the 12 hours, a full sign-in the servers can't complete (none answers, or the cloud app answers
    /// 503 with the clinic's server cut off) lets the password alone open the offline charts; a server that answers
    /// "wrong" or "locked out" is never followed by a check here; after the server ended the session, nothing but a full
    /// sign-in opens the lock.
    #[test]
    fn past_12_hours_the_password_alone_opens_only_when_no_server_can_sign_in() {
        let unreachable = || {
            Err(sync::SignInFailure::Unreachable(
                "Couldn't connect.".to_owned(),
            ))
        };
        assert_eq!(
            after_sign_in(unreachable(), Need::SignIn, "123456"),
            Next::CheckHere
        );
        assert_eq!(
            after_sign_in(unreachable(), Need::SignIn, ""),
            Next::CheckHere
        );
        for problem in [
            "Wrong email, password or code.",
            "This account is locked after too many failed attempts. Try again in 15 minutes.",
        ] {
            let refused = after_sign_in(
                Err(sync::SignInFailure::Refused(problem.to_owned())),
                Need::SignIn,
                "123456",
            );
            assert_eq!(refused, Next::Refused(problem.to_owned()));
        }
        assert!(matches!(
            after_sign_in(
                Err(sync::SignInFailure::Refused("400".to_owned())),
                Need::SignIn,
                ""
            ),
            Next::Refused(problem) if problem == "Code is required."
        ));
        assert!(matches!(
            after_sign_in(unreachable(), Need::SignedOut, "123456"),
            Next::Refused(_)
        ));
    }
}
