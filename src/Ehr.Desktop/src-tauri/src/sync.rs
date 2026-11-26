//! The sync client. Every network call the app makes is here, in Rust; the web view makes none (AGENTS.md).
//!
//! A round tries the servers in order, on-site first, until one completes it. It pulls what changed since the
//! watermark, then sends the outbox in batches. Both are safe to repeat, on either server: a pull only moves a phrase
//! forward (store.rs), and each change carries its key, so one that reached the server before its answer was lost
//! comes back as a duplicate.
//!
//! Signing in and out are here too. The session a sign-in returns is kept in the credential store (key.rs), so it
//! outlasts the app. Every sync request carries its token: signed out, a round sends nothing, and a server that
//! refuses the token (401) ends the session here too, unless it says the sign-in's 12 hours are over ("Ehr-Sign-In:
//! ended", the expansion plan's step 19a): then the session and its offline copy are kept, and nothing more is sent
//! until a full sign-in. Either way main.rs hears of it (take_ending) and locks the window. So is the lock's check of
//! the password at the server (unlock), and the lock's full sign-in (sign_in_answer), which say when no server could
//! answer, so the lock can check the password on this computer instead.

use std::sync::{Mutex, MutexGuard, PoisonError};
use std::time::{Duration, SystemTime, UNIX_EPOCH};

use ehr_desktop::{charts::Chart, draft_sync};

use keyring_core::Entry;
use reqwest::{Client, RequestBuilder, Response, StatusCode, Url};
use serde::de::DeserializeOwned;
use uuid::Uuid;

use crate::api::{PullResponse, PushRequest, PushResponse, Refusal, Session, SignIn};
use crate::key;
use crate::store::{self, Store};

/// The on-prem app on the clinic network, then the cloud app over the internet. EHR_SYNC_SERVERS, separated by
/// commas, replaces them: HTTPS only in a release build. A sign-in is tied to the servers it was made with (main.rs):
/// started with others, the app forgets it before anything is sent.
const DEFAULT_SERVERS: &str = "https://ehr.example.com:8443,https://ehr-remote.example.com:9443";
const PATH: &str = "/api/sync/quick-texts";
const SIGN_IN: &str = "/api/desktop/sign-in";
const SIGN_OUT: &str = "/api/desktop/sign-out";
const UNLOCK: &str = "/api/desktop/unlock";
/// On a 401, says the token was refused only because the sign-in's 12 hours are over (DesktopTokenHandler.cs).
const SIGN_IN_HEADER: &str = "ehr-sign-in";
/// How long the lock waits for a server to check the password before it checks it here.
const UNLOCK_WAIT: Duration = Duration::from_secs(5);
const OFFLINE_PANEL: &str = "/api/desktop/offline-panel";
/// The most the server takes in one push (QuickTextSync.MaxOperations).
const BATCH: usize = 100;

pub struct Sync {
    http: Client,
    servers: Vec<String>,
    /// Where the session is kept between launches: the credential store's entry (key.rs), or in tests a mock store's.
    saved: Entry,
    /// The session, as saved; none while signed out.
    session: Mutex<Option<Session>>,
    /// How the server last ended the sign-in or the session, until main.rs takes it.
    ending: Mutex<Option<Ending>>,
}

/// How the server ended things, for the lock (main.rs).
#[derive(Debug, PartialEq, Clone, Copy)]
pub enum Ending {
    /// The sign-in's 12 hours are over: the session is kept, and a full sign-in is needed.
    SignIn,
    /// The session is over, and forgotten here.
    Session,
}

/// What the server said of the lock's password.
#[derive(Debug, PartialEq)]
pub enum Unlock {
    Unlocked,
    /// A wrong password, or the account is now locked out: in words for the person. Never checked here after this.
    Refused(String),
    /// The sign-in's 12 hours are over.
    SignInEnded,
    /// The session is over.
    SessionOver,
    /// No server could check it.
    Unreachable,
}

/// Why a sign-in didn't happen.
#[derive(Debug, PartialEq)]
pub enum SignInFailure {
    /// A server answered no.
    Refused(String),
    /// None answered, or the one that did couldn't complete it (a 5xx: the cloud app answers 503 while the clinic's
    /// server can't be reached).
    Unreachable(String),
}

impl From<SignInFailure> for String {
    fn from(failure: SignInFailure) -> String {
        match failure {
            SignInFailure::Refused(problem) | SignInFailure::Unreachable(problem) => problem,
        }
    }
}

/// How a round went. `server` answered, if any did; `problem` says why the round didn't finish. Changes not sent
/// stay in the outbox for the next round.
#[derive(Debug, PartialEq)]
pub struct Report {
    pub server: Option<String>,
    pub problem: Option<String>,
}

enum Failure {
    /// No answer came back: nothing listening, no such host, a certificate this computer doesn't trust, a dropped
    /// connection, or no answer in time.
    Unreachable(String),
    /// The server refused the session (401): it expired or was revoked, or the account is locked out.
    SessionEnded,
    /// The server refused the token (401) only because the sign-in's 12 hours are over.
    SignInEnded,
    /// The server answered, but not with what the round needed.
    Problem(String),
}

impl From<store::Error> for Failure {
    fn from(error: store::Error) -> Self {
        Failure::Problem(error.to_string())
    }
}

impl Sync {
    pub fn from_environment(saved: Entry) -> Result<Sync, String> {
        let servers =
            std::env::var("EHR_SYNC_SERVERS").unwrap_or_else(|_| DEFAULT_SERVERS.to_owned());
        Sync::new(parse_servers(&servers, !cfg!(debug_assertions))?, saved)
    }

    /// The servers, in the order they're tried.
    pub fn servers(&self) -> &[String] {
        &self.servers
    }

    /// HTTPS is checked against the OS's trusted certificates, as a browser would. `saved` is where the session is
    /// kept; the one in it, if any, carries on.
    pub fn new(servers: Vec<String>, saved: Entry) -> Result<Sync, String> {
        let http = Client::builder()
            .connect_timeout(Duration::from_secs(3))
            .timeout(Duration::from_secs(20))
            .build()
            .map_err(|error| format!("sync client: {}", reason(&error)))?;
        let servers = servers
            .into_iter()
            .map(|server| server.trim_end_matches('/').to_owned())
            .collect();
        let session = Mutex::new(key::read_session(&saved));
        Ok(Sync {
            http,
            servers,
            saved,
            session,
            ending: Mutex::new(None),
        })
    }

    /// The clinician the kept session is for, however old it is; none while signed out.
    pub fn clinician(&self) -> Option<Uuid> {
        self.session()
            .as_ref()
            .and_then(|session| session.offline_access.as_ref())
            .map(|access| access.clinician_id)
    }

    /// How the server last ended the sign-in or the session, once.
    pub fn take_ending(&self) -> Option<Ending> {
        self.ending
            .lock()
            .unwrap_or_else(PoisonError::into_inner)
            .take()
    }

    fn ended(&self, ending: Ending) {
        *self.ending.lock().unwrap_or_else(PoisonError::into_inner) = Some(ending);
    }

    /// Forgets the kept session here only: the server ends it at its 12 hours.
    pub fn forget_kept(&self) -> Result<(), String> {
        match self.token() {
            Some(token) => self.forget(&token),
            None => Ok(()),
        }
    }

    /// The name of the clinician signed in, for the page; none while signed out.
    pub fn account(&self) -> Option<String> {
        self.session()
            .as_ref()
            .map(|session| session.display_name.clone())
    }

    pub fn offline_available(&self) -> bool {
        self.with_clinician(None, |_| Ok(())).is_ok()
    }

    /// Hold the session lock during a local read or write so a concurrent sign-out
    /// cannot change which clinician owns it. Callers acquire the store lock first.
    pub fn with_clinician<T>(
        &self,
        token: Option<&str>,
        action: impl FnOnce(Uuid) -> Result<T, String>,
    ) -> Result<T, String> {
        let session = self.session();
        let current = session
            .as_ref()
            .filter(|current| token.is_none_or(|token| token == current.token.as_str()))
            .ok_or_else(|| "Sign in as a clinician to use offline charts.".to_owned())?;
        let access = current.offline_access.as_ref().ok_or_else(|| {
            "Sign out and sign in on the updated server to use offline charts.".to_owned()
        })?;
        let now = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .map_err(|_| "Check this computer's clock before using offline charts.".to_owned())?;
        if now.as_secs() >= access.expires_unix_seconds {
            return Err("The offline session expired. Sign in again.".to_owned());
        }
        action(access.clinician_id)
    }

    pub async fn download_panel(&self) -> Result<(String, Vec<Chart>), String> {
        let token = self
            .token()
            .ok_or_else(|| "Sign in to download charts.".to_owned())?;
        self.with_clinician(Some(&token), |_| Ok(()))?;
        let response = self
            .first_answer(|server| {
                self.http
                    .post(format!("{server}{OFFLINE_PANEL}"))
                    .bearer_auth(&token)
            })
            .await?;
        if response.status() == StatusCode::UNAUTHORIZED {
            if sign_in_ended(&response) {
                self.ended(Ending::SignIn);
                return Err(
                    "Sign in again to download charts: a sign-in lasts at most 12 hours."
                        .to_owned(),
                );
            }
            let _ = self.forget(&token);
            self.ended(Ending::Session);
            return Err("The session ended. Sign in again to download charts.".to_owned());
        }
        if !response.status().is_success() {
            return Err(format!(
                "Couldn't download charts: the server answered {}.",
                response.status()
            ));
        }
        let panel = response
            .json()
            .await
            .map_err(|_| "The server's chart response wasn't understood.".to_owned())?;
        Ok((token, panel))
    }

    /// Signs in with the first server that answers, and keeps the session. That server's answer stands: trying the
    /// next with the same code would count as a second attempt. Err says why not, for the page to show the person.
    pub async fn sign_in(&self, email: &str, password: &str, code: &str) -> Result<(), String> {
        self.sign_in_answer(email, password, code)
            .await
            .map_err(String::from)
    }

    /// sign_in, saying whether a server refused it or none could complete it.
    pub async fn sign_in_answer(
        &self,
        email: &str,
        password: &str,
        code: &str,
    ) -> Result<(), SignInFailure> {
        let device = format!("{} desktop", std::env::consts::OS);
        let sign_in = SignIn {
            email,
            password,
            code,
            device: &device,
        };
        let response = self
            .first_answer(|server| self.http.post(format!("{server}{SIGN_IN}")).json(&sign_in))
            .await
            .map_err(SignInFailure::Unreachable)?;

        let status = response.status();
        if !status.is_success() {
            let problem = match response.json::<Refusal>().await {
                Ok(refusal) => refusal.problem,
                Err(_) => format!("Couldn't sign in: the server answered {status}."),
            };
            return Err(if status.is_server_error() {
                SignInFailure::Unreachable(problem)
            } else {
                SignInFailure::Refused(problem)
            });
        }
        let session: Session = response.json().await.map_err(|error| {
            SignInFailure::Refused(format!(
                "Couldn't sign in: the server's answer wasn't understood ({}).",
                reason(&error)
            ))
        })?;

        let mut current = self.session();
        key::save_session(&self.saved, &session).map_err(SignInFailure::Refused)?;
        *current = Some(session);
        Ok(())
    }

    /// The lock's password, checked by the first server that answers, for the session's own account
    /// (/api/desktop/unlock): its answer stands, so a wrong password is never checked again here. A server that can't
    /// check it (a 5xx) is passed over, and with none that can, Unreachable says to check it on this computer.
    pub async fn unlock(&self, password: &str) -> Unlock {
        let Some(token) = self.token() else {
            return Unlock::SessionOver;
        };
        for server in &self.servers {
            let sent = self
                .http
                .post(format!("{server}{UNLOCK}"))
                .bearer_auth(&token)
                .timeout(UNLOCK_WAIT)
                .json(&serde_json::json!({ "password": password }))
                .send()
                .await;
            let Ok(response) = sent else { continue };
            let status = response.status();
            if status.is_success() {
                return Unlock::Unlocked;
            }
            if status == StatusCode::UNAUTHORIZED {
                if sign_in_ended(&response) {
                    self.ended(Ending::SignIn);
                    return Unlock::SignInEnded;
                }
                let _ = self.forget(&token);
                self.ended(Ending::Session);
                return Unlock::SessionOver;
            }
            if status.is_server_error() {
                continue;
            }
            return Unlock::Refused(match response.json::<Refusal>().await {
                Ok(refusal) => refusal.problem,
                Err(_) => format!("Couldn't unlock: the server answered {status}."),
            });
        }
        Unlock::Unreachable
    }

    /// Ends the session on the first server that answers, if any does, then forgets it here whatever the answer: a
    /// server that can't be reached, or has already ended the session, doesn't keep the person signed in.
    pub async fn sign_out(&self) -> Result<(), String> {
        let Some(token) = self.token() else {
            return Ok(());
        };
        let _ = self
            .first_answer(|server| {
                self.http
                    .post(format!("{server}{SIGN_OUT}"))
                    .bearer_auth(&token)
            })
            .await;
        self.forget(&token)
    }

    pub async fn round(&self, store: &Mutex<Store>) -> Report {
        let Some(token) = self.token() else {
            return Report {
                server: None,
                problem: Some(
                    "Signed out: nothing is sent or brought back until you sign in.".to_owned(),
                ),
            };
        };
        let mut unreachable = Vec::new();
        let mut answered = None;
        for server in &self.servers {
            match self.sync_with(server, &token, store).await {
                Ok(()) => {
                    return Report {
                        server: Some(server.clone()),
                        problem: None,
                    };
                }
                Err(Failure::Unreachable(reason)) => {
                    unreachable.push(format!("{server} ({reason})"))
                }
                Err(Failure::SessionEnded) => {
                    // Gone from memory either way. An entry the credential store couldn't delete is refused again at
                    // the next launch, and deleted then.
                    let _ = self.forget(&token);
                    self.ended(Ending::Session);
                    return Report {
                        server: Some(server.clone()),
                        problem: Some("The session ended: sign in again to sync.".to_owned()),
                    };
                }
                Err(Failure::SignInEnded) => {
                    self.ended(Ending::SignIn);
                    return Report {
                        server: Some(server.clone()),
                        problem: Some(
                            "Sign in again: a sign-in lasts at most 12 hours. Nothing is sent or brought back until \
                             you do, and what you save here waits on this computer."
                                .to_owned(),
                        ),
                    };
                }
                Err(Failure::Problem(problem)) => {
                    answered.get_or_insert((server, problem));
                }
            }
        }

        match answered {
            Some((server, problem)) => Report {
                server: Some(server.clone()),
                problem: Some(problem),
            },
            None => Report {
                server: None,
                problem: Some(couldnt_connect(&unreachable)),
            },
        }
    }

    async fn sync_with(
        &self,
        server: &str,
        token: &str,
        store: &Mutex<Store>,
    ) -> Result<(), Failure> {
        let since = store::lock(store).watermark()?;
        let url = Url::parse_with_params(&format!("{server}{PATH}"), [("since", &since)]).map_err(
            |error| Failure::Problem(format!("{server} isn't a server address: {error}")),
        )?;
        let pull: PullResponse = answer(
            "get changes",
            self.http.get(url).bearer_auth(token).send().await,
        )
        .await?;
        store::lock(store).apply_pull(&pull)?;

        loop {
            let batch = store::lock(store).pending(BATCH)?;
            if batch.is_empty() {
                break;
            }

            let push: PushResponse = answer(
                "send changes",
                self.http
                    .post(format!("{server}{PATH}"))
                    .bearer_auth(token)
                    .json(&PushRequest { operations: &batch })
                    .send()
                    .await,
            )
            .await?;

            let answered = batch
                .iter()
                .map(|operation| {
                    let result = push
                        .results
                        .iter()
                        .find(|result| result.key == operation.key);
                    result.map(|result| (operation, result))
                })
                .collect::<Option<Vec<_>>>()
                .ok_or_else(|| {
                    Failure::Problem("The server didn't answer for every change.".to_owned())
                })?;
            store::lock(store).apply_push(answered)?;
        }
        self.sync_clinical(server, token, store).await
    }

    async fn sync_clinical(
        &self,
        server: &str,
        token: &str,
        store: &Mutex<Store>,
    ) -> Result<(), Failure> {
        let Ok(owner) = self.with_clinician(Some(token), Ok) else {
            return Ok(());
        };
        let (since, patients) = store::lock(store).clinical_pull_scope(owner)?;
        if patients.is_empty() && store::lock(store).clinical_waiting(owner)? == 0 {
            return Ok(());
        }

        let pending = store::lock(store).clinical_pending(owner)?;
        for draft in pending {
            if self.with_clinician(Some(token), Ok).is_err() {
                return Ok(());
            }
            let result: draft_sync::PushResult = answer(
                "send a clinical draft",
                self.http
                    .post(format!("{server}/api/desktop/drafts/push"))
                    .bearer_auth(token)
                    .json(&draft)
                    .send()
                    .await,
            )
            .await?;
            let mut db = store::lock(store);
            self.with_clinician(Some(token), |_| {
                db.apply_clinical_answer(owner, &draft, result)
                    .map_err(Into::into)
            })
            .map_err(Failure::Problem)?;
        }

        #[derive(serde::Deserialize)]
        struct Pull {
            watermark: String,
            drafts: Vec<draft_sync::ServerDraft>,
        }
        let pull: Pull = answer(
            "get clinical draft changes",
            self.http
                .post(format!("{server}/api/desktop/drafts/pull"))
                .bearer_auth(token)
                .json(&serde_json::json!({"since": since, "patients": patients}))
                .send()
                .await,
        )
        .await?;
        {
            let mut db = store::lock(store);
            self.with_clinician(Some(token), |_| {
                db.apply_clinical_pull(owner, &since, &patients, &pull.watermark, &pull.drafts)
                    .map_err(Into::into)
            })
            .map_err(Failure::Problem)?;
        }
        self.sync_recording(server, token, owner, store).await
    }

    async fn sync_recording(
        &self,
        server: &str,
        token: &str,
        owner: Uuid,
        store: &Mutex<Store>,
    ) -> Result<(), Failure> {
        let Some(mut recording) = store::lock(store).next_recording(owner)? else {
            return Ok(());
        };
        let base = format!("{server}/api/desktop/offline-dictation");
        let patient = recording.patient.clone();
        let post = |route: &str, sequence: Option<usize>| -> Result<RequestBuilder, Failure> {
            let mut url =
                Url::parse_with_params(&format!("{base}/{route}"), [("patient", &patient)])
                    .map_err(|_| Failure::Problem("The server address is invalid.".to_owned()))?;
            if let Some(seq) = sequence {
                url.query_pairs_mut().append_pair("seq", &seq.to_string());
            }
            self.with_clinician(Some(token), |_| Ok(self.http.post(url).bearer_auth(token)))
                .map_err(Failure::Problem)
        };
        let save = |recording: &ehr_desktop::recordings::Recording| -> Result<(), Failure> {
            let db = store::lock(store);
            self.with_clinician(Some(token), |_| {
                db.checkpoint_recording(owner, recording)
                    .map_err(Into::into)
            })
            .map_err(Failure::Problem)
        };

        #[derive(serde::Deserialize)]
        #[serde(rename_all = "camelCase")]
        struct Started {
            session_id: String,
            max_chunk_bytes: usize,
        }
        #[derive(serde::Deserialize)]
        #[serde(rename_all = "camelCase")]
        struct Chunk {
            received_seq: usize,
        }
        #[derive(serde::Deserialize)]
        #[serde(rename_all = "camelCase")]
        struct Refused {
            error: String,
            expected_seq: Option<usize>,
        }
        #[derive(serde::Deserialize)]
        struct Transcript {
            text: String,
            #[serde(rename = "final")]
            finished: bool,
        }
        #[derive(serde::Deserialize)]
        struct ResultBody {
            transcript: Transcript,
            sorting: String,
        }

        if recording.session.is_none() {
            let response = post("start", None)?.send().await;
            if response
                .as_ref()
                .is_ok_and(|r| r.status() == StatusCode::FORBIDDEN)
            {
                let db = store::lock(store);
                self.with_clinician(Some(token), |_| db.reject_recording(owner, &recording,
                    "This recording's patient is no longer on your panel. The audio was kept on this device.").map_err(Into::into))
                    .map_err(Failure::Problem)?;
                return Ok(());
            }
            let session: Started = answer("start offline transcription", response).await?;
            if session.max_chunk_bytes == 0
                || !(16..=64).contains(&session.session_id.len())
                || !session
                    .session_id
                    .bytes()
                    .all(|b| b.is_ascii_alphanumeric() || b == b'-' || b == b'_')
            {
                return Err(Failure::Problem(
                    "The transcription session wasn't understood.".to_owned(),
                ));
            }
            recording.chunk_size = session.max_chunk_bytes.min(2 * 1024 * 1024);
            recording.session = Some(session.session_id);
            save(&recording)?;
        }

        let id = recording
            .session
            .clone()
            .expect("a transcription session was started");
        let total = recording.audio.len().div_ceil(recording.chunk_size);
        loop {
            let sequence = recording.next_seq;
            let uploading = sequence < total;
            let route = if uploading {
                "audio"
            } else if recording.stopped {
                "result"
            } else {
                "stop"
            };
            let request = post(&format!("{id}/{route}"), uploading.then_some(sequence))?;
            let response = if uploading {
                let start = sequence * recording.chunk_size;
                let end = (start + recording.chunk_size).min(recording.audio.len());
                request
                    .header(reqwest::header::CONTENT_TYPE, &recording.media_type)
                    .body(recording.audio[start..end].to_vec())
                    .send()
                    .await
            } else {
                request.send().await
            };
            let status = response.as_ref().ok().map(Response::status);
            if status == Some(StatusCode::NOT_FOUND) {
                // The sidecar may restart. Keep all audio and start a fresh session next round.
                recording.session = None;
                recording.next_seq = 0;
                recording.stopped = false;
                save(&recording)?;
                return Ok(());
            }
            if matches!(
                status,
                Some(
                    StatusCode::FORBIDDEN
                        | StatusCode::PAYLOAD_TOO_LARGE
                        | StatusCode::UNSUPPORTED_MEDIA_TYPE
                        | StatusCode::UNPROCESSABLE_ENTITY
                )
            ) {
                let db = store::lock(store);
                self.with_clinician(Some(token), |_| db.reject_recording(owner, &recording,
                    "The server couldn't accept this recording. The audio was kept on this device.").map_err(Into::into))
                    .map_err(Failure::Problem)?;
                return Ok(());
            }

            if uploading {
                if let Ok(refusal) = response.as_ref()
                    && refusal.status() == StatusCode::CONFLICT
                {
                    let conflict = response
                        .expect("HTTP conflict")
                        .json::<Refused>()
                        .await
                        .map_err(|_| {
                            Failure::Problem("The server couldn't resume the recording.".to_owned())
                        })?;
                    if conflict.error == "out_of_order"
                        && let Some(next) = conflict.expected_seq
                        && next <= total
                        && next != sequence
                    {
                        recording.next_seq = next;
                        save(&recording)?;
                        return Ok(());
                    }
                    if conflict.error == "stopped" {
                        recording.next_seq = total;
                        recording.stopped = true;
                        save(&recording)?;
                        return Ok(());
                    }
                    return Err(Failure::Problem(
                        "The server couldn't resume the recording.".to_owned(),
                    ));
                }
                let chunk: Chunk = answer("send offline audio", response).await?;
                if chunk.received_seq != sequence {
                    return Err(Failure::Problem(
                        "The server didn't confirm the audio chunk.".to_owned(),
                    ));
                }
                recording.next_seq += 1;
                save(&recording)?;
                continue;
            }

            let result: ResultBody = answer("transcribe offline audio", response).await?;
            recording.stopped = true;
            save(&recording)?;
            if result.sorting == "pending" {
                return Ok(());
            }
            let mut db = store::lock(store);
            let text = result.transcript.text.trim();
            self.with_clinician(Some(token), |_| {
                if !result.transcript.finished
                    || text.is_empty()
                    || text.len() > 65_536
                    || text.contains('\0')
                {
                    db.reject_recording(
                        owner,
                        &recording,
                        "No complete transcript was returned. The audio was kept on this device.",
                    )
                } else {
                    db.finish_recording(owner, &recording, text)
                }
                .map_err(Into::into)
            })
            .map_err(Failure::Problem)?;
            return Ok(());
        }
    }

    /// Sends the request `to` makes for each server in turn, until one answers. Err says why none did.
    async fn first_answer(&self, to: impl Fn(&str) -> RequestBuilder) -> Result<Response, String> {
        let mut unreachable = Vec::new();
        for server in &self.servers {
            match to(server).send().await {
                Ok(response) => return Ok(response),
                Err(error) => unreachable.push(format!("{server} ({})", reason(&error))),
            }
        }
        Err(couldnt_connect(&unreachable))
    }

    fn session(&self) -> MutexGuard<'_, Option<Session>> {
        self.session.lock().unwrap_or_else(PoisonError::into_inner)
    }

    fn token(&self) -> Option<String> {
        self.session().as_ref().map(|session| session.token.clone())
    }

    /// Forgets the session, here and in the credential store, unless a sign-in has replaced it since `token` was sent.
    fn forget(&self, token: &str) -> Result<(), String> {
        let mut current = self.session();
        if current
            .as_ref()
            .is_none_or(|session| session.token != token)
        {
            return Ok(());
        }
        *current = None;
        key::forget_session(&self.saved)
    }
}

/// The servers a comma-separated list names. `https_only`, in a release build: a plain-HTTP server, which anything on
/// this computer could stand in for, is refused, and the app can't sync until the list is put right.
fn parse_servers(list: &str, https_only: bool) -> Result<Vec<String>, String> {
    let servers: Vec<String> = list
        .split(',')
        .map(str::trim)
        .filter(|server| !server.is_empty())
        .map(str::to_owned)
        .collect();
    match servers
        .iter()
        .find(|server| https_only && !server.starts_with("https://"))
    {
        Some(server) => Err(format!(
            "EHR_SYNC_SERVERS names {server}: this app connects to HTTPS servers only."
        )),
        None => Ok(servers),
    }
}

/// Each server that couldn't be reached is listed with why: "https://ehr.example.com:8443 (Connection refused)".
fn couldnt_connect(unreachable: &[String]) -> String {
    format!("Couldn't connect to {}.", unreachable.join(" or "))
}

/// `doing` says what the request was for, in the problem if it fails: "get changes".
async fn answer<T: DeserializeOwned>(
    doing: &str,
    sent: reqwest::Result<Response>,
) -> Result<T, Failure> {
    let response = sent.map_err(|error| {
        if error.is_builder() {
            Failure::Problem(reason(&error))
        } else {
            Failure::Unreachable(reason(&error))
        }
    })?;

    let status = response.status();
    if status == StatusCode::UNAUTHORIZED {
        return Err(if sign_in_ended(&response) {
            Failure::SignInEnded
        } else {
            Failure::SessionEnded
        });
    }
    if !status.is_success() {
        return Err(Failure::Problem(format!(
            "Couldn't {doing}: the server answered {status}."
        )));
    }
    response.json().await.map_err(|error| {
        Failure::Problem(format!(
            "Couldn't {doing}: the server's answer wasn't understood ({}).",
            reason(&error)
        ))
    })
}

/// A 401 that says the token was refused only because the sign-in's 12 hours are over.
fn sign_in_ended(response: &Response) -> bool {
    response
        .headers()
        .get(SIGN_IN_HEADER)
        .is_some_and(|value| value == "ended")
}

/// The innermost cause, which names what actually went wrong: "Connection refused", an untrusted certificate.
fn reason(error: &reqwest::Error) -> String {
    let mut cause: &dyn std::error::Error = error;
    while let Some(source) = cause.source() {
        cause = source;
    }
    cause.to_string()
}

/// The desktop sync tests: also TC-UC8-06 (NFR-10). CI's desktop job (.github/workflows/ci.yml) runs them on macOS and
/// Windows, the team's two platforms.
#[cfg(test)]
mod tests {
    use std::io::{BufRead, BufReader, Read, Write};
    use std::net::{TcpListener, TcpStream};
    use std::path::Path;
    use std::sync::Arc;

    use keyring_core::api::CredentialStoreApi;
    use keyring_core::mock;
    use serde_json::{Value, json};

    use super::*;

    /// A status for `FakeServer` to close the connection without answering.
    const HANG_UP: u16 = 0;
    /// A status for `FakeServer` to answer 401 saying the sign-in's 12 hours are over, as DesktopTokenHandler does.
    const SIGN_IN_ENDED: u16 = 1401;

    /// One request as the fake server saw it: the method and path, the Authorization header, then the body.
    type Request = (String, Option<String>, String);

    /// An HTTP server on a free local port that answers each request with `respond`'s status and JSON.
    struct FakeServer {
        url: String,
        requests: Arc<Mutex<Vec<Request>>>,
    }

    impl FakeServer {
        fn start(respond: impl Fn(&Request) -> (u16, String) + Send + 'static) -> FakeServer {
            let listener = TcpListener::bind("127.0.0.1:0").unwrap();
            let url = format!("http://{}", listener.local_addr().unwrap());
            let requests = Arc::new(Mutex::new(Vec::new()));
            let seen = Arc::clone(&requests);
            std::thread::spawn(move || {
                for stream in listener.incoming() {
                    let stream = stream.unwrap();
                    let request = read_request(&stream);
                    let (status, json) = respond(&request);
                    seen.lock().unwrap().push(request);
                    if status == HANG_UP {
                        continue;
                    }
                    let (status, header) = if status == SIGN_IN_ENDED {
                        (401, "Ehr-Sign-In: ended\r\n")
                    } else {
                        (status, "")
                    };
                    write!(
                        &stream,
                        "HTTP/1.1 {status} Fake\r\n{header}Content-Type: application/json\r\nContent-Length: {}\r\n\
                         Connection: close\r\n\r\n{json}",
                        json.len()
                    )
                    .unwrap();
                }
            });
            FakeServer { url, requests }
        }

        fn requests(&self) -> Vec<Request> {
            self.requests.lock().unwrap().clone()
        }
    }

    fn read_request(stream: &TcpStream) -> Request {
        let mut reader = BufReader::new(stream);
        let mut line = String::new();
        reader.read_line(&mut line).unwrap();
        let mut length = 0;
        let mut authorization = None;
        loop {
            let mut header = String::new();
            reader.read_line(&mut header).unwrap();
            if header.trim().is_empty() {
                break;
            }
            if let Some((name, value)) = header.split_once(':') {
                if name.eq_ignore_ascii_case("content-length") {
                    length = value.trim().parse().unwrap();
                } else if name.eq_ignore_ascii_case("authorization") {
                    authorization = Some(value.trim().to_owned());
                }
            }
        }
        let mut body = vec![0; length];
        reader.read_exact(&mut body).unwrap();
        let method_and_path = line.rsplit_once(' ').unwrap().0.to_owned();
        (
            method_and_path,
            authorization,
            String::from_utf8(body).unwrap(),
        )
    }

    /// Pulls return one phrase and watermark 42; pushes apply every change.
    fn server_that_applies(request: &Request) -> (u16, String) {
        let (method, _, body) = request;
        if method.starts_with("GET") {
            return (200, json!({ "watermark": "42", "quickTexts": [phrase("00000000-0000-7000-8000-000000000001", ".pulled", 1)] }).to_string());
        }
        let push: Value = serde_json::from_str(body).unwrap();
        let results: Vec<Value> = push["operations"]
            .as_array()
            .unwrap()
            .iter()
            .map(|operation| {
                let version = operation["baseVersion"].as_i64().unwrap_or(0) + 1;
                let mut saved = phrase(
                    operation["id"].as_str().unwrap(),
                    operation["shortcut"].as_str().unwrap(),
                    version,
                );
                saved["body"] = operation["body"].clone();
                json!({ "key": operation["key"], "outcome": "applied", "quickText": saved })
            })
            .collect();
        (200, json!({ "results": results }).to_string())
    }

    fn phrase(id: &str, shortcut: &str, version: i64) -> Value {
        json!({ "id": id, "shortcut": shortcut, "body": "Text", "version": version, "updatedAt": "2026-10-07T00:00:00+00:00" })
    }

    fn memory_store() -> Mutex<Store> {
        Mutex::new(Store::open(Path::new(":memory:"), &[7; 32]).unwrap())
    }

    /// An address where nothing is listening.
    fn nothing_there() -> String {
        let listener = TcpListener::bind("127.0.0.1:0").unwrap();
        format!("http://{}", listener.local_addr().unwrap())
    }

    /// The session a sign-in returns, as the tests' servers send it.
    fn session() -> Value {
        json!({ "token": "the-token", "displayName": "Dr. Example", "expiresAt": "2026-11-26T12:34:56.789Z" })
    }

    /// A credential store in memory, so the tests never touch the real one; holding `session`, if any.
    fn credentials(session: Option<Value>) -> Arc<mock::Store> {
        let credentials = mock::Store::new().unwrap();
        if let Some(session) = session {
            saved_entry(&credentials)
                .set_password(&session.to_string())
                .unwrap();
        }
        credentials
    }

    /// The entry the client keeps its session in.
    fn saved_entry(credentials: &mock::Store) -> Entry {
        credentials
            .build("com.example.ehr", "server-session", None)
            .unwrap()
    }

    /// The session kept in `credentials`, if any.
    fn saved(credentials: &mock::Store) -> Option<Value> {
        let json = saved_entry(credentials).get_password().ok()?;
        Some(serde_json::from_str(&json).unwrap())
    }

    fn client_with(servers: &[&str], credentials: &mock::Store) -> Sync {
        Sync::new(
            servers.iter().map(|server| (*server).to_owned()).collect(),
            saved_entry(credentials),
        )
        .unwrap()
    }

    /// Signed in.
    fn client(servers: &[&str]) -> Sync {
        client_with(servers, &credentials(Some(session())))
    }

    /// TC-UC8-01 (FR-24), client side: once a server answers, a round brings back what changed and sends what waited
    /// in the outbox. The clinical draft push it makes is TC-UC8-01's server side, DesktopSignInTests.cs.
    #[tokio::test]
    async fn a_round_pulls_then_sends_the_outbox() {
        let server = FakeServer::start(server_that_applies);
        let store = memory_store();
        store::lock(&store).add(".mine", "Added here").unwrap();

        let first = client(&[&server.url]).round(&store).await;
        let second = client(&[&server.url]).round(&store).await;

        assert_eq!(
            first,
            Report {
                server: Some(server.url.clone()),
                problem: None
            }
        );
        assert_eq!(second, first);
        let paths: Vec<_> = server
            .requests()
            .into_iter()
            .map(|(path, _, _)| path)
            .collect();
        assert_eq!(
            paths,
            [
                format!("GET {PATH}?since=0"),
                format!("POST {PATH}"),
                format!("GET {PATH}?since=42")
            ]
        );

        let local = store::lock(&store);
        assert!(local.pending(10).unwrap().is_empty());
        let phrases: Vec<_> = local
            .phrases()
            .unwrap()
            .into_iter()
            .map(|phrase| (phrase.shortcut, phrase.pending))
            .collect();
        assert_eq!(
            phrases,
            [(".mine".to_owned(), false), (".pulled".to_owned(), false)]
        );
        assert_eq!(local.watermark().unwrap(), "42");
    }

    #[tokio::test]
    async fn a_server_that_cant_be_reached_is_skipped() {
        let server = FakeServer::start(server_that_applies);
        let store = memory_store();

        let report = client(&[&nothing_there(), &server.url]).round(&store).await;

        assert_eq!(
            report,
            Report {
                server: Some(server.url.clone()),
                problem: None
            }
        );
    }

    #[tokio::test]
    async fn with_no_server_changes_wait_in_the_outbox() {
        let store = memory_store();
        store::lock(&store).add(".mine", "Added offline").unwrap();
        let address = nothing_there();

        let report = client(&[&address]).round(&store).await;

        assert_eq!(report.server, None);
        assert!(
            report
                .problem
                .unwrap()
                .starts_with(&format!("Couldn't connect to {address} ("))
        );
        assert_eq!(store::lock(&store).pending(10).unwrap().len(), 1);
    }

    #[tokio::test]
    async fn a_server_that_hangs_up_counts_as_unreachable() {
        let server = FakeServer::start(|_| (HANG_UP, String::new()));
        let store = memory_store();

        let report = client(&[&server.url]).round(&store).await;

        assert_eq!(report.server, None);
        assert!(
            report
                .problem
                .unwrap()
                .starts_with(&format!("Couldn't connect to {} (", server.url))
        );
    }

    /// The cloud app answers 503 to a push when it can't reach the on-prem app.
    #[tokio::test]
    async fn changes_a_server_cant_take_wait_in_the_outbox() {
        let server = FakeServer::start(|request| {
            if request.0.starts_with("GET") {
                server_that_applies(request)
            } else {
                (503, "{}".to_owned())
            }
        });
        let store = memory_store();
        store::lock(&store).add(".mine", "Waiting").unwrap();

        let report = client(&[&server.url]).round(&store).await;

        assert_eq!(report.server.as_deref(), Some(server.url.as_str()));
        assert_eq!(
            report.problem.as_deref(),
            Some("Couldn't send changes: the server answered 503 Service Unavailable.")
        );
        let local = store::lock(&store);
        assert_eq!(local.pending(10).unwrap().len(), 1);
        assert_eq!(local.watermark().unwrap(), "42", "the pull still counts");
    }

    #[tokio::test]
    async fn the_outbox_goes_in_batches_the_server_takes() {
        let server = FakeServer::start(server_that_applies);
        let store = memory_store();
        for n in 0..150 {
            store::lock(&store).add(&format!(".p{n}"), "Text").unwrap();
        }

        client(&[&server.url]).round(&store).await;

        let sizes: Vec<_> = server
            .requests()
            .into_iter()
            .filter(|(path, _, _)| path.starts_with("POST"))
            .map(|(_, _, body)| {
                serde_json::from_str::<Value>(&body).unwrap()["operations"]
                    .as_array()
                    .unwrap()
                    .len()
            })
            .collect();
        assert_eq!(sizes, [100, 50]);
        assert!(store::lock(&store).pending(10).unwrap().is_empty());
    }

    #[tokio::test]
    async fn every_request_carries_the_session() {
        let server = FakeServer::start(server_that_applies);
        let store = memory_store();
        store::lock(&store).add(".mine", "Added here").unwrap();

        client(&[&server.url]).round(&store).await;

        let sent: Vec<_> = server
            .requests()
            .into_iter()
            .map(|(path, authorization, _)| (path, authorization))
            .collect();
        let bearer = Some("Bearer the-token".to_owned());
        assert_eq!(
            sent,
            [
                (format!("GET {PATH}?since=0"), bearer.clone()),
                (format!("POST {PATH}"), bearer)
            ]
        );
    }

    #[tokio::test]
    async fn signed_out_a_round_sends_nothing() {
        let server = FakeServer::start(server_that_applies);
        let store = memory_store();
        store::lock(&store).add(".mine", "Added here").unwrap();

        let report = client_with(&[&server.url], &credentials(None))
            .round(&store)
            .await;

        assert_eq!(
            report,
            Report {
                server: None,
                problem: Some(
                    "Signed out: nothing is sent or brought back until you sign in.".to_owned()
                )
            }
        );
        assert!(server.requests().is_empty());
        assert_eq!(store::lock(&store).pending(10).unwrap().len(), 1);
    }

    /// The server answers 401 when the session has expired or been revoked, or the account is locked out.
    #[tokio::test]
    async fn a_refused_session_is_forgotten() {
        let refuses = FakeServer::start(|_| (401, "{}".to_owned()));
        let applies = FakeServer::start(server_that_applies);
        let store = memory_store();
        store::lock(&store).add(".mine", "Waiting").unwrap();
        let credentials = credentials(Some(session()));
        let client = client_with(&[&refuses.url, &applies.url], &credentials);

        let report = client.round(&store).await;
        let next = client.round(&store).await;

        assert_eq!(
            report,
            Report {
                server: Some(refuses.url.clone()),
                problem: Some("The session ended: sign in again to sync.".to_owned())
            }
        );
        assert!(
            applies.requests().is_empty(),
            "not tried with a refused token"
        );
        assert_eq!(client.account(), None);
        assert_eq!(saved(&credentials), None);
        assert_eq!(client.take_ending(), Some(Ending::Session));
        assert_eq!(next.server, None, "signed out now");
        assert_eq!(refuses.requests().len(), 1);
        assert_eq!(
            store::lock(&store).pending(10).unwrap().len(),
            1,
            "the change stays here"
        );
    }

    /// A session with offline charts, signed in a while ago.
    fn offline_session() -> Value {
        let mut session = session();
        session["offlineAccess"] = json!({ "clinicianId": "018f0000-0000-7000-8000-0000000000c1", "expiresUnixSeconds": 4_102_444_800u64 });
        session
    }

    /// TC-UC8-08 (proposed FR-68, FR-24): a token refused because the sign-in's 12 hours are over keeps the session and
    /// the offline charts open on this computer, says a sign-in is needed, and tells the lock once; nothing is sent.
    #[tokio::test]
    async fn an_ended_sign_in_keeps_the_session_and_says_so() {
        let server = FakeServer::start(|_| (SIGN_IN_ENDED, "{}".to_owned()));
        let store = memory_store();
        store::lock(&store).add(".mine", "Waiting").unwrap();
        let credentials = credentials(Some(offline_session()));
        let client = client_with(&[&server.url], &credentials);

        let report = client.round(&store).await;

        assert_eq!(report.server.as_deref(), Some(server.url.as_str()));
        assert!(
            report
                .problem
                .unwrap()
                .starts_with("Sign in again: a sign-in lasts at most 12 hours.")
        );
        assert_eq!(client.take_ending(), Some(Ending::SignIn));
        assert_eq!(client.take_ending(), None, "once");
        assert_eq!(saved(&credentials), Some(offline_session()));
        assert!(client.offline_available());
        assert_eq!(store::lock(&store).pending(10).unwrap().len(), 1);
        let Err(problem) = client.download_panel().await else {
            panic!("a download with an ended sign-in");
        };
        assert!(problem.starts_with("Sign in again to download charts"));
        assert_eq!(client.take_ending(), Some(Ending::SignIn));
        assert!(client.offline_available());
    }

    /// TC-UC7-10 (proposed FR-68): the lock's password goes to the first server that can check it, with the session's
    /// token and nothing else; a server's no stands, and the next isn't asked; one that can't check it (a 5xx) is
    /// passed over; with none, the answer says to check it on this computer.
    #[tokio::test]
    async fn the_lock_asks_the_server_and_its_no_stands() {
        let accepts = FakeServer::start(|_| (204, String::new()));
        let refuses =
            FakeServer::start(|_| (403, json!({ "problem": "Wrong password." }).to_string()));
        let cant = FakeServer::start(|_| (503, "{}".to_owned()));
        let hangs_up = FakeServer::start(|_| (HANG_UP, String::new()));

        assert_eq!(
            client(&[&accepts.url]).unlock("a password").await,
            Unlock::Unlocked
        );
        assert_eq!(
            accepts.requests(),
            [(
                format!("POST {UNLOCK}"),
                Some("Bearer the-token".to_owned()),
                json!({ "password": "a password" }).to_string()
            )]
        );
        assert_eq!(
            client(&[&refuses.url, &accepts.url])
                .unlock("a guess")
                .await,
            Unlock::Refused("Wrong password.".to_owned())
        );
        assert_eq!(accepts.requests().len(), 1, "not asked after a no");
        assert_eq!(
            client(&[&cant.url, &accepts.url])
                .unlock("a password")
                .await,
            Unlock::Unlocked
        );
        assert_eq!(
            client(&[&nothing_there(), &hangs_up.url, &cant.url])
                .unlock("a password")
                .await,
            Unlock::Unreachable
        );
        assert_eq!(
            client_with(&[&accepts.url], &credentials(None))
                .unlock("a password")
                .await,
            Unlock::SessionOver
        );
    }

    /// TC-UC7-10: the server's word on the session reaches the lock: the 12-hour end keeps the session, any other 401
    /// forgets it.
    #[tokio::test]
    async fn the_lock_hears_how_the_server_ended_things() {
        let ended = FakeServer::start(|_| (SIGN_IN_ENDED, "{}".to_owned()));
        let over = FakeServer::start(|_| (401, "{}".to_owned()));

        let credentials_kept = credentials(Some(session()));
        let kept = client_with(&[&ended.url], &credentials_kept);
        assert_eq!(kept.unlock("a password").await, Unlock::SignInEnded);
        assert_eq!(kept.take_ending(), Some(Ending::SignIn));
        assert_eq!(saved(&credentials_kept), Some(session()));

        let credentials_gone = credentials(Some(session()));
        let gone = client_with(&[&over.url], &credentials_gone);
        assert_eq!(gone.unlock("a password").await, Unlock::SessionOver);
        assert_eq!(gone.take_ending(), Some(Ending::Session));
        assert_eq!(saved(&credentials_gone), None);
    }

    /// TC-UC7-10: the lock's full sign-in says whether a server refused it, which stands, or none could complete it.
    #[tokio::test]
    async fn a_sign_in_says_whether_it_was_refused_or_couldnt_be_made() {
        let refuses = FakeServer::start(|_| {
            (
                401,
                json!({ "problem": "Wrong email, password or code." }).to_string(),
            )
        });
        let cant = FakeServer::start(|_| {
            (
                503,
                "Not saved: the on-prem server can't be reached.".to_owned(),
            )
        });
        let client = |server: &str| client_with(&[server], &credentials(None));

        assert_eq!(
            client(&refuses.url)
                .sign_in_answer("clinician@example.com", "a guess", "123456")
                .await,
            Err(SignInFailure::Refused(
                "Wrong email, password or code.".to_owned()
            ))
        );
        assert!(matches!(
            client(&cant.url)
                .sign_in_answer("clinician@example.com", "a password", "123456")
                .await,
            Err(SignInFailure::Unreachable(_))
        ));
        assert!(matches!(
            client(&nothing_there())
                .sign_in_answer("clinician@example.com", "a password", "123456")
                .await,
            Err(SignInFailure::Unreachable(_))
        ));
    }

    /// The final review of step 19a: a server named over plain HTTP is refused in a release build, which is how
    /// from_environment reads the list there, so nothing on this computer can stand in for a server unauthenticated.
    #[test]
    fn a_release_build_takes_https_servers_only() {
        let list = "https://ehr.example.com:8443, http://127.0.0.1:9";
        assert!(
            parse_servers(list, true)
                .unwrap_err()
                .contains("http://127.0.0.1:9")
        );
        assert_eq!(
            parse_servers(
                "https://ehr.example.com:8443,,https://ehr-remote.example.com:9443",
                true
            ),
            Ok(vec![
                "https://ehr.example.com:8443".to_owned(),
                "https://ehr-remote.example.com:9443".to_owned()
            ])
        );
        assert_eq!(
            parse_servers(list, false).unwrap().len(),
            2,
            "development builds"
        );
    }

    /// The server answers 403 when the account isn't a clinician's.
    #[tokio::test]
    async fn a_forbidden_round_keeps_the_session() {
        let server = FakeServer::start(|_| (403, "{}".to_owned()));
        let store = memory_store();
        let credentials = credentials(Some(session()));
        let client = client_with(&[&server.url], &credentials);

        let report = client.round(&store).await;

        assert_eq!(
            report.problem.as_deref(),
            Some("Couldn't get changes: the server answered 403 Forbidden.")
        );
        assert_eq!(client.account().as_deref(), Some("Dr. Example"));
        assert_eq!(saved(&credentials), Some(session()));
    }

    /// A round that ends with a 401 after the person has signed out and in again leaves the new session alone.
    #[test]
    fn a_session_signed_in_since_is_kept() {
        let credentials = credentials(Some(session()));
        let client = client_with(&[], &credentials);

        client.forget("an-earlier-token").unwrap();

        assert_eq!(client.account().as_deref(), Some("Dr. Example"));
        assert_eq!(saved(&credentials), Some(session()));
    }

    #[tokio::test]
    async fn signing_in_keeps_the_session() {
        let server = FakeServer::start(|_| (200, session().to_string()));
        let credentials = credentials(None);
        let client = client_with(&[&server.url], &credentials);

        let signed_in = client
            .sign_in("clinician@example.com", "a password", "123456")
            .await;

        assert_eq!(signed_in, Ok(()));
        let requests = server.requests();
        assert_eq!(requests.len(), 1);
        let (path, _, body) = &requests[0];
        assert_eq!(path, &format!("POST {SIGN_IN}"));
        assert_eq!(
            serde_json::from_str::<Value>(body).unwrap(),
            json!({
                "email": "clinician@example.com",
                "password": "a password",
                "code": "123456",
                "device": format!("{} desktop", std::env::consts::OS),
            })
        );
        assert_eq!(client.account().as_deref(), Some("Dr. Example"));
        assert_eq!(saved(&credentials), Some(session()));
    }

    /// The first server to answer decides, so a wrong code is never tried twice.
    #[tokio::test]
    async fn a_refused_sign_in_says_why() {
        for status in [401, 403] {
            let refuses = FakeServer::start(move |_| {
                (
                    status,
                    json!({ "problem": "Wrong email, password or code." }).to_string(),
                )
            });
            let accepts = FakeServer::start(|_| (200, session().to_string()));
            let credentials = credentials(None);
            let client = client_with(&[&refuses.url, &accepts.url], &credentials);

            let signed_in = client
                .sign_in("clinician@example.com", "a password", "123456")
                .await;

            assert_eq!(
                signed_in,
                Err("Wrong email, password or code.".to_owned()),
                "{status}"
            );
            assert!(accepts.requests().is_empty(), "{status}");
            assert_eq!(client.account(), None, "{status}");
            assert_eq!(saved(&credentials), None, "{status}");
        }
    }

    #[tokio::test]
    async fn signing_in_with_no_server_says_so() {
        let address = nothing_there();
        let client = client_with(&[&address], &credentials(None));

        let signed_in = client
            .sign_in("clinician@example.com", "a password", "123456")
            .await;

        assert!(
            signed_in
                .unwrap_err()
                .starts_with(&format!("Couldn't connect to {address} ("))
        );
    }

    #[tokio::test]
    async fn signing_out_ends_the_session_there_and_here() {
        let server = FakeServer::start(|_| (204, String::new()));
        let credentials = credentials(Some(session()));
        let client = client_with(&[&server.url], &credentials);

        let signed_out = client.sign_out().await;

        assert_eq!(signed_out, Ok(()));
        assert_eq!(
            server.requests(),
            [(
                format!("POST {SIGN_OUT}"),
                Some("Bearer the-token".to_owned()),
                String::new()
            )]
        );
        assert_eq!(client.account(), None);
        assert_eq!(saved(&credentials), None);
    }

    #[tokio::test]
    async fn signing_out_works_offline() {
        let credentials = credentials(Some(session()));
        let client = client_with(&[&nothing_there()], &credentials);

        let signed_out = client.sign_out().await;

        assert_eq!(signed_out, Ok(()));
        assert_eq!(client.account(), None);
        assert_eq!(saved(&credentials), None);
    }

    /// TC-UC8-04 (FR-24, FR-23): a recording made offline waits on the device, and once a server can be reached the
    /// next round uploads it in chunks, stops the transcription session, and turns the transcript that comes back into
    /// a draft in the outbox. The fake server answers as the on-prem app's offline dictation routes do
    /// (`/api/desktop/offline-dictation`, DictationApi.cs), with a chunk size small enough that the audio goes in three
    /// chunks.
    #[tokio::test]
    async fn a_recording_made_offline_is_transcribed_into_a_draft_after_reconnecting() {
        let clinician = Uuid::now_v7();
        let patient = Uuid::now_v7();
        let store = memory_store();
        store::lock(&store)
            .replace_panel(
                clinician,
                &[Chart {
                    id: patient,
                    name: "Synthetic Patient".to_owned(),
                    mrn: "SYN1".to_owned(),
                    birth_date: "1980-01-01".to_owned(),
                    gender: "unknown".to_owned(),
                    allergies: vec![],
                    medications: vec![],
                    conditions: vec![],
                    visits: vec![],
                    sensitive_not_kept: 0,
                    allergy_status: None,
                    notes: vec![],
                    clinic_time_zone: None,
                }],
            )
            .unwrap();
        store::lock(&store)
            .save_recording(clinician, patient, "audio/webm;codecs=opus", b"abcde")
            .unwrap();
        let mut signed_in = session();
        signed_in["offlineAccess"] =
            json!({ "clinicianId": clinician, "expiresUnixSeconds": 4_102_444_800u64 });
        let credentials = credentials(Some(signed_in));

        // Offline: nothing is reached, and the recording stays on the device.
        let offline = client_with(&[&nothing_there()], &credentials)
            .round(&store)
            .await;
        assert_eq!(offline.server, None);
        assert!(
            store::lock(&store)
                .next_recording(clinician)
                .unwrap()
                .is_some()
        );
        assert_eq!(store::lock(&store).clinical_waiting(clinician).unwrap(), 1);

        // Reconnected.
        let transcript = "Synthetic transcript of the offline visit.";
        let server = FakeServer::start(move |(method, _, _)| {
            let route = method
                .split_once('?')
                .map_or(method.as_str(), |(path, _)| path);
            let seq = method
                .split("seq=")
                .nth(1)
                .map(|seq| seq.parse::<u64>().unwrap());
            match route {
                r if r.starts_with(&format!("GET {PATH}")) => (200, json!({ "watermark": "1", "quickTexts": [] }).to_string()),
                "POST /api/desktop/drafts/pull" => (200, json!({ "watermark": "1", "drafts": [] }).to_string()),
                "POST /api/desktop/offline-dictation/start" => (
                    201,
                    json!({ "sessionId": "synthetic-session-0001", "maxSeconds": 600, "maxChunkBytes": 2 }).to_string(),
                ),
                "POST /api/desktop/offline-dictation/synthetic-session-0001/audio" => (
                    200,
                    json!({ "receivedSeq": seq.unwrap(), "audioSeconds": 0.1, "transcript": { "text": "", "final": false, "segments": [] } }).to_string(),
                ),
                "POST /api/desktop/offline-dictation/synthetic-session-0001/stop" => (
                    200,
                    json!({ "transcript": { "text": transcript, "final": true, "segments": [] }, "sorting": "done", "fields": [], "notCarriedOver": [], "provenance": {} }).to_string(),
                ),
                other => panic!("unexpected request {other}"),
            }
        });
        let online = client_with(&[&server.url], &credentials)
            .round(&store)
            .await;

        assert_eq!(
            online,
            Report {
                server: Some(server.url.clone()),
                problem: None
            }
        );
        let dictation: Vec<_> = server
            .requests()
            .into_iter()
            .filter(|(method, _, _)| method.contains("offline-dictation"))
            .collect();
        let routes: Vec<_> = dictation
            .iter()
            .map(|(method, _, _)| {
                method
                    .replace(&patient.to_string(), "{patient}")
                    .replace("/api/desktop/offline-dictation", "")
            })
            .collect();
        assert_eq!(
            routes,
            [
                "POST /start?patient={patient}",
                "POST /synthetic-session-0001/audio?patient={patient}&seq=0",
                "POST /synthetic-session-0001/audio?patient={patient}&seq=1",
                "POST /synthetic-session-0001/audio?patient={patient}&seq=2",
                "POST /synthetic-session-0001/stop?patient={patient}",
            ]
        );
        assert!(
            dictation
                .iter()
                .all(|(_, authorization, _)| authorization.as_deref() == Some("Bearer the-token"))
        );
        let uploaded: String = dictation[1..4]
            .iter()
            .map(|(_, _, body)| body.as_str())
            .collect();
        assert_eq!(uploaded, "abcde");

        let local = store::lock(&store);
        assert!(
            local.next_recording(clinician).unwrap().is_none(),
            "the audio is gone once transcribed"
        );
        let draft = local.clinical_pending(clinician).unwrap().remove(0);
        assert_eq!(draft.patient_id, patient.to_string());
        assert_eq!(draft.body, transcript);
        assert_eq!(draft.base_version, None, "a new draft, waiting to be sent");
    }
}
