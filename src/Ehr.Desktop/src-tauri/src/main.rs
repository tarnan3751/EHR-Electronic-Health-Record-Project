//! The EHR desktop app: a window over an encrypted local database (store.rs) that syncs with the servers in the
//! background (sync.rs). The page (frontend/) calls the commands below and redraws on the "changed" event.

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

    fn status(&self) -> MutexGuard<'_, Status> {
        self.status.lock().unwrap_or_else(PoisonError::into_inner)
    }
}

/// The last sync round, for the page.
#[derive(Serialize, Clone, Default)]
#[serde(rename_all = "camelCase")]
struct Status {
    /// The server that answered; none while offline.
    server: Option<String>,
    problem: Option<String>,
    /// When the round ended, in milliseconds since 1970, for the page to show in local time.
    checked_at: Option<u64>,
    /// Shown until the app closes, when the local database had to be started again.
    notice: Option<String>,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct View {
    phrases: Vec<Phrase>,
    conflicts: Vec<Conflict>,
    /// Changes saved here and not yet sent.
    waiting: usize,
    status: Status,
}

#[tauri::command]
fn view(app: State<'_, App>) -> Result<View, String> {
    let store = app.store()?;
    Ok(View {
        phrases: store.phrases()?,
        conflicts: store.conflicts()?,
        waiting: store.waiting()?,
        status: app.status().clone(),
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
    let client = match sync::Sync::from_environment() {
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

/// Opens the local database, with a notice when it had to start it again.
fn open_store(app: &tauri::App) -> Result<(Store, Option<String>), String> {
    let key = key::load()?;
    let (store, started_again) = Store::open_or_start_again(&database_path(app)?, &key.bytes)?;
    let notice = started_again.then(|| {
        let why = if key.new {
            "the key to this computer's copy was missing"
        } else {
            "this computer's copy was damaged"
        };
        format!(
            "The app started its copy of the phrases again, because {why}. Changes that hadn't been sent were lost."
        )
    });
    Ok((store, notice))
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
            let (store, notice) = match open_store(app) {
                Ok((store, notice)) => (Ok(Mutex::new(store)), notice),
                Err(problem) => (Err(problem), None),
            };
            app.manage(App {
                store,
                status: Mutex::new(Status {
                    notice,
                    ..Status::default()
                }),
                wake: Notify::new(),
            });
            tauri::async_runtime::spawn(sync_in_background(app.handle().clone()));
            Ok(())
        })
        .invoke_handler(tauri::generate_handler![view, add, edit, resolve, sync_now])
        .run(tauri::generate_context!())
        .expect("failed to run the EHR desktop app");
}
