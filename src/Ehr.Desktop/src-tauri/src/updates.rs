//! The app's updates (the expansion plan's step 20, Tahseen's 15a, proposed FR-70): Tauri's updater, from Rust only, as
//! every network call is (the page has no permission for the plugin: capabilities/default.json).
//!
//! - **Where from:** the servers this build was made for (sync.rs), in order, at /api/desktop/updates on each
//!   (src/Ehr.Web/Api/Desktop/DesktopUpdates.cs): after a start and every 24 hours, whatever became of an earlier
//!   offer but one being got ready or ready, and at once when a server refuses this version as too old. A check gives
//!   up after 30 seconds, and nothing is said when no server answers. No redirect is followed, so only the servers
//!   named answer, for the release and its file.
//! - **What is taken:** only a release newer than this one, never an older, signed with the key in tauri.conf.json
//!   for the very version announced (requireSignedVersion), which the updater checks after downloading and again before
//!   installing. A signature that doesn't match installs nothing, and says so.
//! - **When:** it asks. The offer is shown only while the window is unlocked and nothing is recording, and "Not now"
//!   puts it away, or a notice that it failed, until a check finds a release to offer again. Accepted, it is downloaded and checked; on macOS the checked app then replaces
//!   this one on disk at once, and runs from the next start; on Windows its installer runs at Restart now, which a
//!   recording holds off (quitting instead offers it again at the next start). A start after an update opens on the
//!   lock (step 19a). There is no going back from inside the app: a bad release is followed by a newer one.

use std::sync::{Mutex, MutexGuard, PoisonError};
use std::time::Duration;

use serde::Serialize;
use tauri::AppHandle;
use tauri::Url;
use tauri_plugin_updater::{Update, UpdaterExt};
use tokio::sync::Notify;

/// How often it checks while the app runs.
const CHECK_EVERY: Duration = Duration::from_secs(24 * 60 * 60);
/// How long a check may take before it's given up, until the next.
const CHECK_WAIT: Duration = Duration::from_secs(30);

/// The update, as the page shows it.
#[derive(Clone, Debug, PartialEq, Serialize)]
#[serde(tag = "state", rename_all = "camelCase")]
pub enum Offer {
    Offered {
        version: String,
    },
    Downloading {
        version: String,
    },
    /// Checked, and installed on disk (macOS) or waiting for Restart now (Windows).
    Ready {
        version: String,
    },
    Failed {
        problem: String,
    },
}

#[derive(Default)]
pub struct Updates {
    offer: Mutex<Option<Offer>>,
    /// "Not now", or a failure's notice put away, until a check offers a release again.
    dismissed: Mutex<bool>,
    /// The release offered.
    update: Mutex<Option<Update>>,
    /// The checked installer, held for Restart now where it can't be installed before (Windows).
    downloaded: Mutex<Option<Vec<u8>>>,
    /// Checks at once (a server refused this version).
    now: Notify,
}

fn held<T>(mutex: &Mutex<T>) -> MutexGuard<'_, T> {
    mutex.lock().unwrap_or_else(PoisonError::into_inner)
}

/// What the page is shown: nothing while the window is locked or something is recording, nor an offer or a failure's
/// notice put away.
pub fn shown(
    offer: Option<&Offer>,
    dismissed: bool,
    locked: bool,
    recording: bool,
) -> Option<Offer> {
    match offer {
        _ if locked || recording => None,
        Some(Offer::Offered { .. } | Offer::Failed { .. }) if dismissed => None,
        offer => offer.cloned(),
    }
}

/// Whether a check that found `version` makes a new offer: not while one is being got ready or is ready, nor when it is
/// the release already offered, which stays put away if it was.
pub fn offers_anew(offer: Option<&Offer>, version: &str) -> bool {
    match offer {
        Some(Offer::Downloading { .. } | Offer::Ready { .. }) => false,
        Some(Offer::Offered { version: offered }) => offered != version,
        _ => true,
    }
}

/// Where each server answers for this platform and version: the updater fills in the parts in braces.
pub fn endpoints(servers: &[String]) -> Result<Vec<Url>, String> {
    servers
        .iter()
        .map(|server| {
            Url::parse(&format!(
                "{server}/api/desktop/updates/{{{{target}}}}-{{{{arch}}}}/{{{{current_version}}}}"
            ))
            .map_err(|error| format!("{server}: {error}"))
        })
        .collect()
}

impl Updates {
    pub fn view(&self, locked: bool, recording: bool) -> Option<Offer> {
        shown(
            held(&self.offer).as_ref(),
            *held(&self.dismissed),
            locked,
            recording,
        )
    }

    /// A server refused this version as too old: check now.
    pub fn check_now(&self) {
        self.now.notify_one();
    }

    pub fn dismiss(&self) {
        *held(&self.dismissed) = true;
    }

    fn offer(&self, offer: Offer) {
        *held(&self.offer) = Some(offer);
    }

    /// After a start, every 24 hours, and at once when asked: a newer release is offered, unless one is being got
    /// ready or is ready, or it's the one offered already.
    pub async fn check_in_background(&self, handle: AppHandle, servers: Vec<String>) {
        loop {
            let busy = matches!(
                *held(&self.offer),
                Some(Offer::Downloading { .. } | Offer::Ready { .. })
            );
            let found = if busy {
                None
            } else {
                tokio::time::timeout(CHECK_WAIT, check(&handle, &servers))
                    .await
                    .ok()
                    .and_then(Result::ok)
                    .flatten()
            };
            if let Some(update) = found
                && offers_anew(held(&self.offer).as_ref(), &update.version)
            {
                self.offer(Offer::Offered {
                    version: update.version.clone(),
                });
                *held(&self.dismissed) = false;
                *held(&self.update) = Some(update);
                let _ = tauri::Emitter::emit(&handle, "changed", ());
            }
            tokio::select! {
                () = tokio::time::sleep(CHECK_EVERY) => {}
                () = self.now.notified() => {}
            }
        }
    }

    /// Get it ready: downloaded and checked, then on macOS installed on disk for the next start, and elsewhere held
    /// for Restart now.
    pub async fn accept(&self) -> Result<(), String> {
        let update = held(&self.update)
            .clone()
            .ok_or_else(|| "There is no update to get ready.".to_owned())?;
        let version = update.version.clone();
        self.offer(Offer::Downloading {
            version: version.clone(),
        });
        let checked = update.download(|_, _| {}, || {}).await;
        let ready = checked
            .map_err(|error| error.to_string())
            .and_then(|bytes| {
                if cfg!(target_os = "macos") {
                    update.install(&bytes).map_err(|error| error.to_string())
                } else {
                    *held(&self.downloaded) = Some(bytes);
                    Ok(())
                }
            });
        match ready {
            Ok(()) => {
                self.offer(Offer::Ready { version });
                Ok(())
            }
            Err(error) => {
                let problem = format!(
                    "SwiftChart {version} couldn't be downloaded or installed, or its signature didn't match. \
                     Nothing was changed ({error})."
                );
                self.offer(Offer::Failed {
                    problem: problem.clone(),
                });
                Err(problem)
            }
        }
    }

    /// Restart now, with a release ready: on Windows its installer runs, and ends this app and starts the new one; on
    /// macOS the new one is on disk already, and the app starts again.
    pub fn restart(&self, handle: &AppHandle) -> Result<(), String> {
        if !matches!(*held(&self.offer), Some(Offer::Ready { .. })) {
            return Err("There is no update ready to install.".to_owned());
        }
        if let (Some(update), Some(bytes)) =
            (held(&self.update).clone(), held(&self.downloaded).take())
        {
            update.install(bytes).map_err(|error| error.to_string())?;
        }
        handle.restart()
    }
}

async fn check(handle: &AppHandle, servers: &[String]) -> Result<Option<Update>, String> {
    handle
        .updater_builder()
        .endpoints(endpoints(servers)?)
        .map_err(|error| error.to_string())?
        .configure_client(|client| client.redirect(reqwest::redirect::Policy::none()))
        .build()
        .map_err(|error| error.to_string())?
        .check()
        .await
        .map_err(|error| error.to_string())
}

#[cfg(test)]
mod tests {
    use super::*;

    /// TC-UC7-14 (proposed FR-70): the update is offered only while the window is unlocked and nothing is recording,
    /// "Not now" puts an offer or a failure's notice away, and an update being got ready or ready still shows.
    #[test]
    fn an_update_is_offered_only_unlocked_and_not_over_a_recording() {
        let offered = Offer::Offered {
            version: "0.2.1".to_owned(),
        };
        let ready = Offer::Ready {
            version: "0.2.1".to_owned(),
        };

        assert_eq!(
            shown(Some(&offered), false, false, false),
            Some(offered.clone())
        );
        assert_eq!(shown(Some(&offered), false, true, false), None, "locked");
        assert_eq!(shown(Some(&offered), false, false, true), None, "recording");
        assert_eq!(shown(Some(&offered), true, false, false), None, "not now");
        assert_eq!(shown(Some(&ready), true, false, false), Some(ready.clone()));
        assert_eq!(shown(Some(&ready), false, true, false), None);
        assert_eq!(shown(None, false, false, false), None);
        let failed = Offer::Failed {
            problem: "Not this time.".to_owned(),
        };
        assert_eq!(
            shown(Some(&failed), false, false, false),
            Some(failed.clone())
        );
        assert_eq!(shown(Some(&failed), true, false, false), None, "put away");
    }

    /// Step 20's final review: the daily check goes on after an offer is put away or fails, offering a release it
    /// hasn't offered, or one that failed, again; never over one being got ready or ready.
    #[test]
    fn the_daily_check_goes_on_after_an_offer_is_put_away_or_fails() {
        let offered = Offer::Offered {
            version: "0.2.1".to_owned(),
        };
        let failed = Offer::Failed {
            problem: "Not this time.".to_owned(),
        };
        let downloading = Offer::Downloading {
            version: "0.2.1".to_owned(),
        };
        let ready = Offer::Ready {
            version: "0.2.1".to_owned(),
        };

        assert!(offers_anew(None, "0.2.1"));
        assert!(
            !offers_anew(Some(&offered), "0.2.1"),
            "the same, still put away"
        );
        assert!(offers_anew(Some(&offered), "0.2.2"));
        assert!(offers_anew(Some(&failed), "0.2.1"));
        assert!(!offers_anew(Some(&downloading), "0.2.2"));
        assert!(!offers_anew(Some(&ready), "0.2.2"));
    }

    /// The updater asks each built-in server in turn, at its own address, for this platform and version.
    #[test]
    fn each_server_is_asked_at_its_own_address() {
        let asked = endpoints(&[
            "https://ehr.example.com:8443".to_owned(),
            "https://ehr-remote.example.com:9443".to_owned(),
        ])
        .unwrap();

        assert_eq!(
            asked.iter().map(Url::as_str).collect::<Vec<_>>(),
            [
                "https://ehr.example.com:8443/api/desktop/updates/%7B%7Btarget%7D%7D-%7B%7Barch%7D%7D/%7B%7Bcurrent_version%7D%7D",
                "https://ehr-remote.example.com:9443/api/desktop/updates/%7B%7Btarget%7D%7D-%7B%7Barch%7D%7D/%7B%7Bcurrent_version%7D%7D"
            ]
        );
    }
}
