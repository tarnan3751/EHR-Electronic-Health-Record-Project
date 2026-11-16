//! The EHR desktop app: a window over an encrypted local database (store.rs) that syncs with the servers in the
//! background (sync.rs) while a clinician is signed in. The page (frontend/) calls the commands below and redraws on
//! the "changed" event.

// Release builds on Windows open no console window alongside the app.
#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

mod api;
mod key;
mod rules;
mod store;
mod sync;

use std::path::PathBuf;
use std::sync::{Mutex, MutexGuard, PoisonError};
use std::time::{Duration, SystemTime, UNIX_EPOCH};

use ehr_desktop::{charts, drafts, recordings};
use serde::Serialize;
use tauri::{AppHandle, Emitter, Manager, State};
use tokio::sync::Notify;
use uuid::Uuid;

use store::{Conflict, Phrase, Store};

/// How often the app syncs when nothing prompts it sooner.
const SYNC_EVERY: Duration = Duration::from_secs(10);

struct App {
    /// Or why the local database couldn't be opened.
    store: Result<Mutex<Store>, String>,
    /// Or why the sync client couldn't be made.
    sync: Result<sync::Sync, String>,
    status: Mutex<Status>,
    /// Starts a sync round now: after a change, or when the person asks.
    wake: Notify,
}

impl App {
    fn store(&self) -> Result<MutexGuard<'_, Store>, String> {
        self.store
            .as_ref()
            .map(store::lock)
            .map_err(|problem| format!("The local database couldn't be opened: {problem}"))
    }

    fn sync(&self) -> Result<&sync::Sync, String> {
        self.sync.as_ref().map_err(Clone::clone)
    }

    fn status(&self) -> MutexGuard<'_, Status> {
        self.status.lock().unwrap_or_else(PoisonError::into_inner)
    }
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

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct View {
    phrases: Vec<Phrase>,
    conflicts: Vec<Conflict>,
    /// Changes saved here and not yet sent.
    waiting: usize,
    status: Status,
    offline_available: bool,
}

#[tauri::command]
fn view(app: State<'_, App>) -> Result<View, String> {
    let store = app.store()?;
    Ok(View {
        phrases: store.phrases()?,
        conflicts: store.conflicts()?,
        waiting: store.waiting()?,
        offline_available: app.sync().is_ok_and(sync::Sync::offline_available),
        status: Status {
            account: app.sync.as_ref().ok().and_then(sync::Sync::account),
            ..app.status().clone()
        },
    })
}

#[derive(Serialize)]
struct OfflineChart {
    chart: charts::Chart,
    drafts: Vec<drafts::PendingDraft>,
    recordings: Vec<recordings::RecordingView>,
}

#[tauri::command]
async fn download_panel(app: State<'_, App>) -> Result<(), String> {
    let sync = app.sync()?;
    let (token, panel) = sync.download_panel().await?;
    let mut store = app.store()?;
    sync.with_clinician(Some(&token), |owner| {
        store.replace_panel(owner, &panel).map_err(Into::into)
    })
}

#[tauri::command]
fn offline_panel(app: State<'_, App>) -> Result<Vec<charts::ChartSummary>, String> {
    let store = app.store()?;
    app.sync()?
        .with_clinician(None, |owner| store.offline_panel(owner).map_err(Into::into))
}

#[tauri::command]
fn offline_chart(app: State<'_, App>, patient: Uuid) -> Result<OfflineChart, String> {
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
    })
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
    })
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
    app.wake.notify_one();
    Ok(())
}

/// Signs out, and starts a round so the status says so. The local copy stays, to read.
#[tauri::command]
async fn sign_out(app: State<'_, App>) -> Result<(), String> {
    let signed_out = app.sync()?.sign_out().await;
    app.wake.notify_one();
    signed_out
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

        tokio::select! {
            () = tokio::time::sleep(SYNC_EVERY) => {}
            () = app.wake.notified() => {}
        }
    }
}

/// Opens the encrypted database without replacing it if the key is missing or invalid.
fn open_store(app: &tauri::App) -> Result<Store, String> {
    let path = database_path(app)?;
    let exists = path != std::path::Path::new(":memory:")
        && path
            .try_exists()
            .map_err(|error| format!("{}: {error}", path.display()))?;
    let key = key::load(exists)?;
    Store::open(&path, &key).map_err(Into::into)
}

/// In the app's data folder on macOS and Windows. Linux is for development only, and keeps its key in memory
/// (key.rs), so it keeps the database in memory too.
fn database_path(app: &tauri::App) -> Result<PathBuf, String> {
    if cfg!(any(target_os = "macos", windows)) {
        let folder = app
            .path()
            .app_data_dir()
            .map_err(|error| error.to_string())?;
        std::fs::create_dir_all(&folder)
            .map_err(|error| format!("{}: {error}", folder.display()))?;
        Ok(folder.join("ehr.db"))
    } else {
        Ok(PathBuf::from(":memory:"))
    }
}

fn main() {
    tauri::Builder::default()
        .setup(|app| {
            let store = open_store(app).map(Mutex::new);
            app.manage(App {
                store,
                // After open_store, which chose the credential store that keeps the session.
                sync: key::session_entry().and_then(sync::Sync::from_environment),
                status: Mutex::new(Status::default()),
                wake: Notify::new(),
            });
            tauri::async_runtime::spawn(sync_in_background(app.handle().clone()));
            Ok(())
        })
        .invoke_handler(tauri::generate_handler![
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
            save_recording,
        ])
        .run(tauri::generate_context!())
        .expect("failed to run the EHR desktop app");
}
