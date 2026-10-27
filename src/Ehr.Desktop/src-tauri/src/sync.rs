//! The sync client. Every network call the app makes is here, in Rust; the web view makes none (AGENTS.md).
//!
//! A round tries the servers in order, on-site first, until one completes it. It pulls what changed since the
//! watermark, then sends the outbox in batches. Both are safe to repeat, on either server: a pull only moves a phrase
//! forward (store.rs), and each change carries its key, so one that reached the server before its answer was lost
//! comes back as a duplicate.
//!
//! Signing in and out are here too. The session a sign-in returns is kept in the credential store (key.rs), so it
//! outlasts the app.

use std::sync::{Mutex, MutexGuard, PoisonError};
use std::time::Duration;

use keyring_core::Entry;
use reqwest::{Client, RequestBuilder, Response, Url};
use serde::de::DeserializeOwned;

use crate::api::{PullResponse, PushRequest, PushResponse, Refusal, Session, SignIn};
use crate::key;
use crate::store::{self, Store};

/// The on-prem app on the clinic network, then the cloud app over the internet. EHR_SYNC_SERVERS, separated by
/// commas, replaces them.
const DEFAULT_SERVERS: &str = "https://ehr.example.com:8443,https://ehr-remote.example.com:9443";
const PATH: &str = "/api/sync/quick-texts";
const SIGN_IN: &str = "/api/desktop/sign-in";
const SIGN_OUT: &str = "/api/desktop/sign-out";
/// The most the server takes in one push (QuickTextSync.MaxOperations).
const BATCH: usize = 100;

pub struct Sync {
    http: Client,
    servers: Vec<String>,
    /// Where the session is kept between launches: the credential store's entry (key.rs), or in tests a mock store's.
    saved: Entry,
    /// The session, as saved; none while signed out.
    session: Mutex<Option<Session>>,
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
        Sync::new(
            servers
                .split(',')
                .map(str::trim)
                .filter(|server| !server.is_empty())
                .map(str::to_owned)
                .collect(),
            saved,
        )
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
        })
    }

    /// The name of the clinician signed in, for the page; none while signed out.
    pub fn account(&self) -> Option<String> {
        self.session()
            .as_ref()
            .map(|session| session.display_name.clone())
    }

    /// Signs in with the first server that answers, and keeps the session. That server's answer stands: trying the
    /// next with the same code would count as a second attempt. Err says why not, for the page to show the person.
    pub async fn sign_in(&self, email: &str, password: &str, code: &str) -> Result<(), String> {
        let device = format!("{} desktop", std::env::consts::OS);
        let sign_in = SignIn {
            email,
            password,
            code,
            device: &device,
        };
        let response = self
            .first_answer(|server| self.http.post(format!("{server}{SIGN_IN}")).json(&sign_in))
            .await?;

        let status = response.status();
        if !status.is_success() {
            return Err(match response.json::<Refusal>().await {
                Ok(refusal) => refusal.problem,
                Err(_) => format!("Couldn't sign in: the server answered {status}."),
            });
        }
        let session: Session = response.json().await.map_err(|error| {
            format!(
                "Couldn't sign in: the server's answer wasn't understood ({}).",
                reason(&error)
            )
        })?;

        let mut current = self.session();
        key::save_session(&self.saved, &session)?;
        *current = Some(session);
        Ok(())
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
        let mut unreachable = Vec::new();
        let mut answered = None;
        for server in &self.servers {
            match self.sync_with(server, store).await {
                Ok(()) => {
                    return Report {
                        server: Some(server.clone()),
                        problem: None,
                    };
                }
                Err(Failure::Unreachable(reason)) => {
                    unreachable.push(format!("{server} ({reason})"))
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

    async fn sync_with(&self, server: &str, store: &Mutex<Store>) -> Result<(), Failure> {
        let since = store::lock(store).watermark()?;
        let url = Url::parse_with_params(&format!("{server}{PATH}"), [("since", &since)]).map_err(
            |error| Failure::Problem(format!("{server} isn't a server address: {error}")),
        )?;
        let pull: PullResponse = answer("get changes", self.http.get(url).send().await).await?;
        store::lock(store).apply_pull(&pull)?;

        loop {
            let batch = store::lock(store).pending(BATCH)?;
            if batch.is_empty() {
                return Ok(());
            }

            let push: PushResponse = answer(
                "send changes",
                self.http
                    .post(format!("{server}{PATH}"))
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

/// The innermost cause, which names what actually went wrong: "Connection refused", an untrusted certificate.
fn reason(error: &reqwest::Error) -> String {
    let mut cause: &dyn std::error::Error = error;
    while let Some(source) = cause.source() {
        cause = source;
    }
    cause.to_string()
}

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
                    write!(
                        &stream,
                        "HTTP/1.1 {status} Fake\r\nContent-Type: application/json\r\nContent-Length: {}\r\n\
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
}
