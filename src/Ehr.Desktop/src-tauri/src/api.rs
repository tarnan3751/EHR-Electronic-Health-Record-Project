//! The sync API's JSON, as the server defines it in src/Ehr.Web/Api/Sync/QuickTextSyncContract.cs.

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
