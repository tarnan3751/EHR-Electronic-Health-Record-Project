//! The sync client. Every network call the app makes is here, in Rust; the web view makes none (AGENTS.md).
//!
//! A round tries the servers in order, on-site first, until one completes it. It pulls what changed since the
//! watermark, then sends the outbox in batches. Both are safe to repeat, on either server: a pull only moves a phrase
//! forward (store.rs), and each change carries its key, so one that reached the server before its answer was lost
//! comes back as a duplicate.

use std::sync::Mutex;
use std::time::Duration;

use reqwest::{Client, Response, Url};
use serde::de::DeserializeOwned;

use crate::api::{PullResponse, PushRequest, PushResponse};
use crate::store::{self, Store};

/// The on-prem app on the clinic network, then the cloud app over the internet. EHR_SYNC_SERVERS, separated by
/// commas, replaces them.
const DEFAULT_SERVERS: &str = "https://ehr.example.com:8443,https://ehr-remote.example.com:9443";
const PATH: &str = "/api/sync/quick-texts";
/// The most the server takes in one push (QuickTextSync.MaxOperations).
const BATCH: usize = 100;

pub struct Sync {
    http: Client,
    servers: Vec<String>,
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
    pub fn from_environment() -> Result<Sync, String> {
        let servers =
            std::env::var("EHR_SYNC_SERVERS").unwrap_or_else(|_| DEFAULT_SERVERS.to_owned());
        Sync::new(
            servers
                .split(',')
                .map(str::trim)
                .filter(|server| !server.is_empty())
                .map(str::to_owned)
                .collect(),
        )
    }

    /// HTTPS is checked against the OS's trusted certificates, as a browser would.
    pub fn new(servers: Vec<String>) -> Result<Sync, String> {
        let http = Client::builder()
            .connect_timeout(Duration::from_secs(3))
            .timeout(Duration::from_secs(20))
            .build()
            .map_err(|error| format!("sync client: {}", reason(&error)))?;
        let servers = servers
            .into_iter()
            .map(|server| server.trim_end_matches('/').to_owned())
            .collect();
        Ok(Sync { http, servers })
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
                problem: Some(format!("Couldn't connect to {}.", unreachable.join(" or "))),
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

    use serde_json::{Value, json};

    use super::*;

    /// A status for `FakeServer` to close the connection without answering.
    const HANG_UP: u16 = 0;

    /// One request as the fake server saw it: the method and path, then the body.
    type Request = (String, String);

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
        loop {
            let mut header = String::new();
            reader.read_line(&mut header).unwrap();
            if header.trim().is_empty() {
                break;
            }
            if let Some((name, value)) = header.split_once(':')
                && name.eq_ignore_ascii_case("content-length")
            {
                length = value.trim().parse().unwrap();
            }
        }
        let mut body = vec![0; length];
        reader.read_exact(&mut body).unwrap();
        let method_and_path = line.rsplit_once(' ').unwrap().0.to_owned();
        (method_and_path, String::from_utf8(body).unwrap())
    }

    /// Pulls return one phrase and watermark 42; pushes apply every change.
    fn server_that_applies(request: &Request) -> (u16, String) {
        let (method, body) = request;
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

    fn client(servers: &[&str]) -> Sync {
        Sync::new(servers.iter().map(|server| (*server).to_owned()).collect()).unwrap()
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
            .map(|(path, _)| path)
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
            .filter(|(path, _)| path.starts_with("POST"))
            .map(|(_, body)| {
                serde_json::from_str::<Value>(&body).unwrap()["operations"]
                    .as_array()
                    .unwrap()
                    .len()
            })
            .collect();
        assert_eq!(sizes, [100, 50]);
        assert!(store::lock(&store).pending(10).unwrap().is_empty());
    }
}
