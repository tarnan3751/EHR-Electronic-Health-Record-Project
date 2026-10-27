//! The server's JSON: the sync API's, as the server defines it in src/Ehr.Web/Api/Sync/QuickTextSyncContract.cs, and
//! the desktop sign-in's, at /api/desktop.

use std::collections::BTreeMap;

use serde::{Deserialize, Serialize};
use uuid::Uuid;

/// A phrase as the server has saved it.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct QuickText {
    pub id: Uuid,
    pub shortcut: String,
    pub body: String,
    pub version: i64,
    pub updated_at: String,
}

#[derive(Deserialize, Debug)]
#[serde(rename_all = "camelCase")]
pub struct PullResponse {
    /// A transaction ID, as a string because it's 64-bit.
    pub watermark: String,
    pub quick_texts: Vec<QuickText>,
}

#[derive(Serialize, Debug)]
pub struct PushRequest<'a> {
    pub operations: &'a [Operation],
}

/// One change to send. `key` identifies the change, so sending it again is harmless; `base_version` is the version
/// it started from, or none for a new phrase.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct Operation {
    pub key: Uuid,
    pub id: Uuid,
    pub base_version: Option<i64>,
    pub shortcut: String,
    pub body: String,
}

#[derive(Deserialize, Debug)]
pub struct PushResponse {
    pub results: Vec<PushResult>,
}

#[derive(Deserialize, Debug)]
#[serde(rename_all = "camelCase")]
pub struct PushResult {
    pub key: Uuid,
    pub outcome: Outcome,
    /// The phrase as it's saved now: the one just saved, or for a conflict, the phrase that conflicts.
    pub quick_text: Option<QuickText>,
    pub errors: Option<BTreeMap<String, Vec<String>>>,
}

#[derive(Deserialize, Debug, Clone, Copy, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub enum Outcome {
    Applied,
    /// Applied before, under the same key.
    Duplicate,
    /// The phrase changed since `base_version`, or another phrase has the shortcut.
    Conflict,
    Invalid,
}

/// A sign-in. `code` is from the clinician's authenticator app, or one of their recovery codes; `device` is a label the
/// server keeps with the session: "macos desktop".
#[derive(Serialize)]
pub struct SignIn<'a> {
    pub email: &'a str,
    pub password: &'a str,
    pub code: &'a str,
    pub device: &'a str,
}

/// What a sign-in returns, kept in the credential store (key.rs) until the session ends. Every sync request carries
/// `token`. `expires_at` is kept but not acted on: the server says when a session is over, with a 401.
#[derive(Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Session {
    pub token: String,
    pub display_name: String,
    pub expires_at: String,
}

/// Why the server refused a sign-in, in words for the person.
#[derive(Deserialize)]
pub struct Refusal {
    pub problem: String,
}
