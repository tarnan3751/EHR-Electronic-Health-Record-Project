//! The local database, encrypted with SQLCipher under a key from the OS credential store (key.rs). It holds the
//! phrases as the server last sent them, the outbox of changes not yet sent, the changes the server refused (for the
//! person to choose), and the sync watermark. What the app shows is the server's copy with the outbox on top.

use std::fmt;
use std::path::Path;
use std::sync::{Mutex, MutexGuard, PoisonError};

use rusqlite::{Connection, OptionalExtension, params};
use serde::Serialize;
use uuid::Uuid;

use crate::api::{Operation, Outcome, PullResponse, PushResult, QuickText};
use crate::rules;
use ehr_desktop::{charts, draft_sync, drafts, recordings};

const SCHEMA: &str = "
    CREATE TABLE IF NOT EXISTS quick_texts (
        id TEXT PRIMARY KEY,
        shortcut TEXT NOT NULL,
        body TEXT NOT NULL,
        version INTEGER NOT NULL,
        updated_at TEXT NOT NULL
    );
    -- Sent in seq order. An operation is never changed once queued: it may already have reached the server.
    CREATE TABLE IF NOT EXISTS outbox (
        seq INTEGER PRIMARY KEY AUTOINCREMENT,
        key TEXT NOT NULL UNIQUE,
        id TEXT NOT NULL,
        base_version INTEGER,
        shortcut TEXT NOT NULL,
        body TEXT NOT NULL
    );
    -- theirs is the server's phrase as JSON; message explains a change the server found invalid.
    CREATE TABLE IF NOT EXISTS conflicts (
        key TEXT PRIMARY KEY,
        id TEXT NOT NULL,
        shortcut TEXT NOT NULL,
        body TEXT NOT NULL,
        theirs TEXT,
        message TEXT
    );
    CREATE TABLE IF NOT EXISTS sync_state (
        name TEXT PRIMARY KEY,
        value TEXT NOT NULL
    );
";

pub struct Store {
    connection: Connection,
}

/// A phrase as the app shows it. `pending` means it has changes the server hasn't confirmed yet. `version` is the one
/// it will have once they're applied: an edit starts from it.
#[derive(Serialize, Clone, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct Phrase {
    pub id: Uuid,
    pub shortcut: String,
    pub body: String,
    pub version: i64,
    pub pending: bool,
}

/// A change the server refused. When `theirs` is the same phrase, saved by someone else since, the person can keep
/// either version. Otherwise (another phrase has the shortcut, or the change was invalid) they can only discard it.
#[derive(Serialize, Clone, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct Conflict {
    pub key: Uuid,
    pub id: Uuid,
    pub shortcut: String,
    pub body: String,
    pub theirs: Option<QuickText>,
    pub message: Option<String>,
    pub can_keep_mine: bool,
}

#[derive(Debug)]
pub enum Error {
    Database(rusqlite::Error),
    NotFound,
    /// The phrase changed while the person was editing it.
    ChangedSince,
    /// The request doesn't make sense in the current state, with the reason.
    Refused(&'static str),
}

impl From<rusqlite::Error> for Error {
    fn from(error: rusqlite::Error) -> Self {
        Error::Database(error)
    }
}

impl fmt::Display for Error {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Error::Database(error) => write!(f, "local database: {error}"),
            Error::NotFound => f.write_str("That phrase isn't here any more."),
            Error::ChangedSince => f.write_str("This phrase changed while you were editing it."),
            Error::Refused(reason) => f.write_str(reason),
        }
    }
}

/// For the app's commands, whose errors go to the page as text.
impl From<Error> for String {
    fn from(error: Error) -> Self {
        error.to_string()
    }
}

pub type Result<T> = std::result::Result<T, Error>;

/// The commands and the sync loop share one Store. A panic elsewhere while it was locked can't leave it half-done:
/// each change is one statement or one transaction, and an unfinished transaction rolls back.
pub fn lock(store: &Mutex<Store>) -> MutexGuard<'_, Store> {
    store.lock().unwrap_or_else(PoisonError::into_inner)
}

impl Store {
    /// Opens (or creates) the database at `path`, or in memory for ":memory:". A wrong key fails here, with
    /// SQLCipher's "file is not a database".
    pub fn open(path: &Path, key: &[u8; 32]) -> Result<Store> {
        let connection = Connection::open(path)?;
        // A raw key, so SQLCipher skips its password-based key derivation. hex::encode only emits [0-9a-f].
        connection.execute_batch(&format!("PRAGMA key = \"x'{}'\";", hex::encode(key)))?;
        connection.query_row("SELECT count(*) FROM sqlite_master", [], |_| Ok(()))?;
        connection.execute_batch(SCHEMA)?;
        drafts::initialize(&connection)?;
        draft_sync::initialize(&connection)?;
        recordings::initialize(&connection)?;
        charts::initialize(&connection)?;
        Ok(Store { connection })
    }

    pub fn replace_panel(&mut self, owner: Uuid, panel: &[charts::Chart]) -> Result<()> {
        draft_sync::reset_watermark(&self.connection, owner)?;
        charts::replace_panel(&mut self.connection, owner, panel)?;
        Ok(())
    }

    pub fn offline_panel(&self, owner: Uuid) -> Result<Vec<charts::ChartSummary>> {
        Ok(charts::panel(&self.connection, owner)?)
    }

    pub fn offline_chart(
        &self,
        owner: Uuid,
        patient: Uuid,
    ) -> Result<(charts::Chart, Vec<draft_sync::DraftView>)> {
        let chart = charts::get(&self.connection, owner, patient)?
            .ok_or(Error::Refused("This chart is only available online."))?;
        let notes = draft_sync::list(&self.connection, owner, patient)?;
        Ok((chart, notes))
    }

    pub fn save_clinical_draft(&self, owner: Uuid, patient: Uuid, body: &str) -> Result<()> {
        if body.trim().is_empty() || body.len() > 65_536 || body.contains('\0') {
            return Err(Error::Refused("Enter a draft of up to 64 KiB."));
        }
        if !charts::contains(&self.connection, owner, patient)? {
            return Err(Error::Refused("This chart is only available online."));
        }
        drafts::save(&self.connection, owner, patient, body)?;
        Ok(())
    }

    pub fn clinical_waiting(&self, owner: Uuid) -> Result<usize> {
        Ok(draft_sync::waiting(&self.connection, owner)?
            + recordings::waiting(&self.connection, owner)?)
    }

    pub fn clinical_pull_scope(&self, owner: Uuid) -> Result<(String, Vec<String>)> {
        let patients = self
            .offline_panel(owner)?
            .into_iter()
            .map(|chart| chart.id)
            .collect();
        Ok((draft_sync::watermark(&self.connection, owner)?, patients))
    }

    pub fn apply_clinical_pull(
        &mut self,
        owner: Uuid,
        since: &str,
        patients: &[String],
        next: &str,
        changes: &[draft_sync::ServerDraft],
    ) -> Result<()> {
        if self.clinical_pull_scope(owner)?.1 == patients {
            draft_sync::pull(&mut self.connection, owner, since, next, changes)?;
        }
        Ok(())
    }

    pub fn clinical_pending(&self, owner: Uuid) -> Result<Vec<draft_sync::Change>> {
        Ok(draft_sync::pending(&self.connection, owner)?)
    }

    pub fn apply_clinical_answer(
        &mut self,
        owner: Uuid,
        change: &draft_sync::Change,
        answer: draft_sync::PushResult,
    ) -> Result<()> {
        Ok(draft_sync::answer(
            &mut self.connection,
            owner,
            change,
            answer,
        )?)
    }

    pub fn edit_clinical(
        &mut self,
        owner: Uuid,
        patient: Uuid,
        id: Uuid,
        version: Option<i64>,
        body: &str,
    ) -> Result<()> {
        if body.trim().is_empty() || body.len() > 65_536 || body.contains('\0') {
            return Err(Error::Refused("Enter a draft of up to 64 KiB."));
        }
        if !charts::contains(&self.connection, owner, patient)?
            || !draft_sync::edit(&mut self.connection, owner, patient, id, version, body)?
        {
            return Err(Error::ChangedSince);
        }
        Ok(())
    }

    pub fn resolve_clinical(
        &mut self,
        owner: Uuid,
        patient: Uuid,
        id: Uuid,
        version: i64,
        keep_mine: bool,
    ) -> Result<()> {
        if !charts::contains(&self.connection, owner, patient)?
            || !draft_sync::resolve(&mut self.connection, owner, patient, id, version, keep_mine)?
        {
            return Err(Error::ChangedSince);
        }
        Ok(())
    }

    pub fn save_recording(
        &self,
        owner: Uuid,
        patient: Uuid,
        media_type: &str,
        audio: &[u8],
    ) -> Result<()> {
        if !media_type.starts_with("audio/")
            || media_type.len() > 128
            || reqwest::header::HeaderValue::from_str(media_type).is_err()
            || audio.is_empty()
            || audio.len() > recordings::MAX_BYTES
        {
            return Err(Error::Refused(
                "The recording is empty, too large, or can't be played.",
            ));
        }
        if !charts::contains(&self.connection, owner, patient)? {
            return Err(Error::Refused("This chart is only available online."));
        }
        Ok(recordings::save(
            &self.connection,
            owner,
            patient,
            media_type,
            audio,
        )?)
    }

    pub fn recordings(&self, owner: Uuid, patient: Uuid) -> Result<Vec<recordings::RecordingView>> {
        Ok(recordings::list(&self.connection, owner, patient)?)
    }

    pub fn next_recording(&self, owner: Uuid) -> Result<Option<recordings::Recording>> {
        Ok(recordings::next(&self.connection, owner)?)
    }

    pub fn checkpoint_recording(
        &self,
        owner: Uuid,
        recording: &recordings::Recording,
    ) -> Result<()> {
        Ok(recordings::checkpoint(&self.connection, owner, recording)?)
    }

    pub fn reject_recording(
        &self,
        owner: Uuid,
        recording: &recordings::Recording,
        problem: &str,
    ) -> Result<()> {
        Ok(recordings::reject(
            &self.connection,
            owner,
            recording,
            problem,
        )?)
    }

    pub fn finish_recording(
        &mut self,
        owner: Uuid,
        recording: &recordings::Recording,
        text: &str,
    ) -> Result<()> {
        Ok(recordings::finish(
            &mut self.connection,
            owner,
            recording,
            text,
        )?)
    }

    pub fn phrases(&self) -> Result<Vec<Phrase>> {
        let mut phrases = self
            .connection
            .prepare("SELECT id, shortcut, body, version FROM quick_texts")?
            .query_map([], |row| {
                Ok(Phrase {
                    id: uuid(row.get(0)?),
                    shortcut: row.get(1)?,
                    body: row.get(2)?,
                    version: row.get(3)?,
                    pending: false,
                })
            })?
            .collect::<rusqlite::Result<Vec<_>>>()?;

        for operation in self.outbox()? {
            match phrases.iter_mut().find(|phrase| phrase.id == operation.id) {
                Some(phrase) => {
                    phrase.shortcut = operation.shortcut;
                    phrase.body = operation.body;
                    phrase.version += 1;
                    phrase.pending = true;
                }
                None => phrases.push(Phrase {
                    id: operation.id,
                    shortcut: operation.shortcut,
                    body: operation.body,
                    version: 1,
                    pending: true,
                }),
            }
        }

        phrases.sort_by(|a, b| a.shortcut.cmp(&b.shortcut));
        Ok(phrases)
    }

    /// Queues a new phrase. Its ID is made here, so it stays the same once the server has it.
    pub fn add(&self, shortcut: &str, body: &str) -> Result<Uuid> {
        let id = Uuid::now_v7();
        self.queue(id, None, shortcut, body)?;
        Ok(id)
    }

    /// Queues an edit of `version`, the phrase as the person saw it. If it has changed since (a pull brought someone
    /// else's version, or the server refused an earlier change), nothing is queued: the person chooses first.
    pub fn edit(&self, id: Uuid, version: i64, shortcut: &str, body: &str) -> Result<()> {
        match self.expected_version(id)? {
            0 => Err(Error::NotFound),
            expected if expected != version => Err(Error::ChangedSince),
            _ => self.queue(id, Some(version), shortcut, body),
        }
    }

    /// The changes to send next, oldest first: one per phrase. A later change to a phrase waits until the server has
    /// answered for the one before, which it was made on top of.
    pub fn pending(&self, limit: usize) -> Result<Vec<Operation>> {
        let limit = i64::try_from(limit).unwrap_or(i64::MAX);
        operations(
            &self.connection,
            "SELECT key, id, base_version, shortcut, body FROM outbox
             WHERE seq IN (SELECT min(seq) FROM outbox GROUP BY id) ORDER BY seq LIMIT ?1",
            [limit],
        )
    }

    /// How many changes are saved here and not yet sent.
    pub fn waiting(&self) -> Result<usize> {
        let count: i64 = self
            .connection
            .query_row("SELECT count(*) FROM outbox", [], |row| row.get(0))?;
        Ok(usize::try_from(count).unwrap_or_default())
    }

    /// Records what the server did with each sent change, and takes them out of the outbox, in one transaction.
    pub fn apply_push<'a>(
        &mut self,
        answered: impl IntoIterator<Item = (&'a Operation, &'a PushResult)>,
    ) -> Result<()> {
        let transaction = self.connection.transaction()?;
        for (operation, result) in answered {
            record_push(&transaction, operation, result)?;
        }
        transaction.commit()?;
        Ok(())
    }

    /// Saves a pull's phrases and its watermark together, so a crash can't record one without the other.
    pub fn apply_pull(&mut self, pull: &PullResponse) -> Result<()> {
        let transaction = self.connection.transaction()?;
        for quick_text in &pull.quick_texts {
            upsert(&transaction, quick_text)?;
        }
        transaction.execute(
            "INSERT INTO sync_state (name, value) VALUES ('watermark', ?1)
             ON CONFLICT (name) DO UPDATE SET value = excluded.value",
            [&pull.watermark],
        )?;
        transaction.commit()?;
        Ok(())
    }

    /// Where the next pull starts: "0", for everything, until the first pull.
    pub fn watermark(&self) -> Result<String> {
        Ok(self
            .connection
            .query_row(
                "SELECT value FROM sync_state WHERE name = 'watermark'",
                [],
                |row| row.get(0),
            )
            .optional()?
            .unwrap_or_else(|| "0".to_owned()))
    }

    pub fn conflicts(&self) -> Result<Vec<Conflict>> {
        let conflicts = self
            .connection
            .prepare(
                "SELECT key, id, shortcut, body, theirs, message FROM conflicts ORDER BY rowid",
            )?
            .query_map([], |row| {
                let id = uuid(row.get(1)?);
                let theirs: Option<QuickText> = row
                    .get::<_, Option<String>>(4)?
                    .and_then(|json| serde_json::from_str(&json).ok());
                Ok(Conflict {
                    key: uuid(row.get(0)?),
                    id,
                    shortcut: row.get(2)?,
                    body: row.get(3)?,
                    can_keep_mine: theirs.as_ref().is_some_and(|theirs| theirs.id == id),
                    theirs,
                    message: row.get(5)?,
                })
            })?
            .collect::<rusqlite::Result<_>>()?;
        Ok(conflicts)
    }

    /// Keep mine sends this change again, starting from their version. Otherwise (keep theirs, or discard) the
    /// change is dropped, and the server's version, saved when the conflict came back, stays.
    pub fn resolve(&mut self, key: Uuid, keep_mine: bool) -> Result<()> {
        let conflict = self
            .conflicts()?
            .into_iter()
            .find(|conflict| conflict.key == key)
            .ok_or(Error::NotFound)?;
        if keep_mine && !conflict.can_keep_mine {
            return Err(Error::Refused(
                "This change can't be kept: change the shortcut and add it again.",
            ));
        }

        let transaction = self.connection.transaction()?;
        if let Some(theirs) = conflict.theirs.as_ref().filter(|_| keep_mine) {
            insert_operation(
                &transaction,
                &Operation {
                    key: Uuid::now_v7(),
                    id: conflict.id,
                    base_version: Some(theirs.version),
                    shortcut: conflict.shortcut.clone(),
                    body: conflict.body.clone(),
                },
            )?;
        }
        transaction.execute("DELETE FROM conflicts WHERE key = ?1", [key.to_string()])?;
        transaction.commit()?;
        Ok(())
    }

    fn queue(&self, id: Uuid, base_version: Option<i64>, shortcut: &str, body: &str) -> Result<()> {
        insert_operation(
            &self.connection,
            &Operation {
                key: Uuid::now_v7(),
                id,
                base_version,
                shortcut: rules::normalize(shortcut),
                body: body.to_owned(),
            },
        )?;
        Ok(())
    }

    /// Every unsent change, oldest first.
    fn outbox(&self) -> Result<Vec<Operation>> {
        operations(
            &self.connection,
            "SELECT key, id, base_version, shortcut, body FROM outbox ORDER BY seq",
            [],
        )
    }

    /// The server's version, plus one for each queued change; 0 for a phrase that doesn't exist here.
    fn expected_version(&self, id: Uuid) -> Result<i64> {
        Ok(self.connection.query_row(
            "SELECT coalesce((SELECT version FROM quick_texts WHERE id = ?1), 0)
                  + (SELECT count(*) FROM outbox WHERE id = ?1)",
            [id.to_string()],
            |row| row.get(0),
        )?)
    }
}

/// When the server refused a change, later changes to the same phrase (made on top of it, and not sent: see
/// `Store::pending`) go into the conflict too, so the person chooses between their latest version and the server's.
fn record_push(connection: &Connection, operation: &Operation, result: &PushResult) -> Result<()> {
    let message = match result.outcome {
        Outcome::Applied | Outcome::Duplicate => {
            if let Some(saved) = &result.quick_text {
                upsert(connection, saved)?;
            }
            connection.execute(
                "DELETE FROM outbox WHERE key = ?1",
                [operation.key.to_string()],
            )?;
            return Ok(());
        }
        Outcome::Conflict => {
            // Their phrase is what the server has now, whichever phrase it is, so the list shows it straight away.
            if let Some(theirs) = &result.quick_text {
                upsert(connection, theirs)?;
            }
            None
        }
        Outcome::Invalid => Some(
            result
                .errors
                .iter()
                .flatten()
                .flat_map(|(_, messages)| messages.iter().map(String::as_str))
                .collect::<Vec<_>>()
                .join(" "),
        ),
    };

    let latest = operations(
        connection,
        "SELECT key, id, base_version, shortcut, body FROM outbox WHERE id = ?1 ORDER BY seq DESC LIMIT 1",
        [operation.id.to_string()],
    )?;
    let mine = Operation {
        key: operation.key,
        ..latest
            .into_iter()
            .next()
            .unwrap_or_else(|| operation.clone())
    };
    record_conflict(
        connection,
        &mine,
        result.quick_text.as_ref(),
        message.as_deref(),
    )?;
    connection.execute(
        "DELETE FROM outbox WHERE id = ?1",
        [operation.id.to_string()],
    )?;
    Ok(())
}

/// Never replaces a newer version with an older one: a pull from a replica that's behind can repeat a version this
/// app already has from a push.
fn upsert(connection: &Connection, quick_text: &QuickText) -> rusqlite::Result<()> {
    connection.execute(
        "INSERT INTO quick_texts (id, shortcut, body, version, updated_at) VALUES (?1, ?2, ?3, ?4, ?5)
         ON CONFLICT (id) DO UPDATE SET
             shortcut = excluded.shortcut, body = excluded.body,
             version = excluded.version, updated_at = excluded.updated_at
         WHERE excluded.version >= quick_texts.version",
        params![
            quick_text.id.to_string(),
            quick_text.shortcut,
            quick_text.body,
            quick_text.version,
            quick_text.updated_at
        ],
    )?;
    Ok(())
}

fn operations(
    connection: &Connection,
    sql: &str,
    parameters: impl rusqlite::Params,
) -> Result<Vec<Operation>> {
    let operations = connection
        .prepare(sql)?
        .query_map(parameters, |row| {
            Ok(Operation {
                key: uuid(row.get(0)?),
                id: uuid(row.get(1)?),
                base_version: row.get(2)?,
                shortcut: row.get(3)?,
                body: row.get(4)?,
            })
        })?
        .collect::<rusqlite::Result<_>>()?;
    Ok(operations)
}

fn insert_operation(connection: &Connection, operation: &Operation) -> rusqlite::Result<()> {
    connection.execute(
        "INSERT INTO outbox (key, id, base_version, shortcut, body) VALUES (?1, ?2, ?3, ?4, ?5)",
        params![
            operation.key.to_string(),
            operation.id.to_string(),
            operation.base_version,
            operation.shortcut,
            operation.body
        ],
    )?;
    Ok(())
}

fn record_conflict(
    connection: &Connection,
    operation: &Operation,
    theirs: Option<&QuickText>,
    message: Option<&str>,
) -> rusqlite::Result<()> {
    let theirs =
        theirs.map(|theirs| serde_json::to_string(theirs).expect("a QuickText always serializes"));
    connection.execute(
        "INSERT OR REPLACE INTO conflicts (key, id, shortcut, body, theirs, message) VALUES (?1, ?2, ?3, ?4, ?5, ?6)",
        params![
            operation.key.to_string(),
            operation.id.to_string(),
            operation.shortcut,
            operation.body,
            theirs,
            message
        ],
    )?;
    Ok(())
}

/// IDs are stored as text; everything here wrote them, so they always parse.
fn uuid(text: String) -> Uuid {
    Uuid::parse_str(&text).expect("the local database holds only valid UUIDs")
}

#[cfg(test)]
mod tests {
    use std::collections::BTreeMap;
    use std::path::PathBuf;

    use super::*;

    const KEY: [u8; 32] = [7; 32];

    /// A database file in the temp folder, deleted at the end of the test.
    struct TempDatabase(PathBuf);

    impl TempDatabase {
        fn new() -> Self {
            TempDatabase(
                std::env::temp_dir().join(format!("ehr-desktop-test-{}.db", Uuid::now_v7())),
            )
        }

        fn open(&self) -> Store {
            Store::open(&self.0, &KEY).expect("the test database opens")
        }
    }

    impl Drop for TempDatabase {
        fn drop(&mut self) {
            let _ = std::fs::remove_file(&self.0);
        }
    }

    fn saved(id: Uuid, shortcut: &str, body: &str, version: i64) -> QuickText {
        QuickText {
            id,
            shortcut: shortcut.to_owned(),
            body: body.to_owned(),
            version,
            updated_at: "2026-10-07T00:00:00+00:00".to_owned(),
        }
    }

    fn pull(store: &mut Store, watermark: &str, quick_texts: Vec<QuickText>) {
        store
            .apply_pull(&PullResponse {
                watermark: watermark.to_owned(),
                quick_texts,
            })
            .unwrap();
    }

    fn result(
        operation: &Operation,
        outcome: Outcome,
        quick_text: Option<QuickText>,
    ) -> PushResult {
        PushResult {
            key: operation.key,
            outcome,
            quick_text,
            errors: None,
        }
    }

    fn only_pending(store: &Store) -> Operation {
        let mut pending = store.pending(10).unwrap();
        assert_eq!(pending.len(), 1, "one change waiting");
        pending.remove(0)
    }

    /// TC-UC7-06 (NFR-06): a wrong key can neither read nor change the store; the right one recovers the draft.
    #[test]
    fn the_file_is_encrypted_and_opens_only_with_its_key() {
        let database = TempDatabase::new();
        database.open().add(".nad", "No acute distress.").unwrap();

        let header = std::fs::read(&database.0).unwrap();
        assert!(
            !header.starts_with(b"SQLite format 3"),
            "the file should not be plain SQLite"
        );
        assert!(Store::open(&database.0, &[8; 32]).is_err());
        assert_eq!(database.open().phrases().unwrap()[0].shortcut, ".nad");
    }

    #[test]
    fn an_unreadable_database_keeps_pending_drafts() {
        use ehr_desktop::drafts;

        let database = TempDatabase::new();
        let clinician = Uuid::now_v7();
        let patient = Uuid::now_v7();
        let store = database.open();
        let saved = drafts::save(&store.connection, clinician, patient, "Test note").unwrap();
        drop(store);

        let before = std::fs::read(&database.0).unwrap();
        assert!(Store::open(&database.0, &[8; 32]).is_err());
        assert_eq!(std::fs::read(&database.0).unwrap(), before);

        let store = database.open();
        assert_eq!(
            drafts::pending(&store.connection, clinician, None).unwrap(),
            vec![saved],
        );
    }

    #[test]
    fn reopening_the_database_restores_only_this_clinicians_pending_drafts() {
        use ehr_desktop::drafts;

        let database = TempDatabase::new();
        let clinician = Uuid::now_v7();
        let other_clinician = Uuid::now_v7();
        let patient = Uuid::now_v7();
        let store = database.open();
        let first = drafts::save(&store.connection, clinician, patient, "First note").unwrap();
        let other =
            drafts::save(&store.connection, other_clinician, patient, "Other note").unwrap();
        let second = drafts::save(&store.connection, clinician, patient, "Second note").unwrap();
        drop(store);

        let store = database.open();
        assert_eq!(
            drafts::pending(&store.connection, clinician, None).unwrap(),
            vec![first, second],
        );
        assert_eq!(
            drafts::pending(&store.connection, other_clinician, None).unwrap(),
            vec![other],
        );
        assert_eq!(store.waiting().unwrap(), 0, "Quick text has its own queue");
    }

    /// TC-UC7-01 (FR-21, FR-22): a downloaded chart and the draft written on it reopen without a connection.
    /// Also TC-UC7-02 (FR-21): only the clinician's downloaded charts are available offline; a chart that wasn't
    /// downloaded, or another clinician's, can't be opened or drafted on.
    #[test]
    fn an_assigned_chart_and_its_draft_reopen_without_a_connection() {
        let database = TempDatabase::new();
        let clinician = Uuid::now_v7();
        let colleague = Uuid::now_v7();
        let patient = Uuid::now_v7();
        let another = Uuid::now_v7();
        let mut store = database.open();
        let chart = charts::Chart {
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
        };
        store.replace_panel(clinician, &[chart]).unwrap();
        assert!(
            store
                .save_clinical_draft(clinician, another, "Wrong chart")
                .is_err()
        );
        assert!(
            store
                .save_clinical_draft(colleague, patient, "Wrong owner")
                .is_err()
        );
        store
            .save_clinical_draft(clinician, patient, "Offline draft")
            .unwrap();
        drop(store);

        let mut store = database.open();
        assert!(store.offline_panel(colleague).unwrap().is_empty());
        assert!(store.offline_chart(colleague, patient).is_err());
        let (chart, drafts) = store.offline_chart(clinician, patient).unwrap();
        assert_eq!(chart.id, patient);
        assert_eq!(drafts.len(), 1);
        assert_eq!(drafts[0].body, "Offline draft");

        store.replace_panel(clinician, &[]).unwrap();
        assert!(store.offline_chart(clinician, patient).is_err());
        assert_eq!(drafts[0].state, "pending");
        assert_eq!(
            drafts::pending(&store.connection, clinician, None).unwrap()[0].id,
            drafts[0].id
        );
    }

    /// TC-UC7-03 (FR-22): an existing draft is edited offline. The edit is stored encrypted, waits in the outbox
    /// as a new change based on the version the clinician edited, and is refused for another clinician, for a chart
    /// that isn't downloaded, and for a version replaced since.
    #[test]
    fn an_existing_draft_is_edited_offline_and_waits_encrypted_in_the_outbox() {
        let database = TempDatabase::new();
        let clinician = Uuid::now_v7();
        let colleague = Uuid::now_v7();
        let patient = Uuid::now_v7();
        let another = Uuid::now_v7();
        let chart = |id| charts::Chart {
            id,
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
        };

        // The draft exists: written on the chart and saved by the server as version 1, before going offline.
        let mut store = database.open();
        store.replace_panel(clinician, &[chart(patient)]).unwrap();
        store
            .save_clinical_draft(clinician, patient, "Synthetic first draft")
            .unwrap();
        let first = store.clinical_pending(clinician).unwrap().remove(0);
        let id = Uuid::parse_str(&first.id).unwrap();
        store
            .apply_clinical_answer(
                clinician,
                &first,
                draft_sync::PushResult::Applied {
                    current: draft_sync::ServerDraft {
                        id: first.id.clone(),
                        patient_id: patient.to_string(),
                        body: "Synthetic first draft".to_owned(),
                        version: 1,
                    },
                },
            )
            .unwrap();
        assert!(store.clinical_pending(clinician).unwrap().is_empty());
        drop(store);

        // Offline: the store reopens, and the clinician edits the draft.
        let mut store = database.open();
        assert!(matches!(
            store.edit_clinical(colleague, patient, id, Some(1), "Wrong owner"),
            Err(Error::ChangedSince)
        ));
        assert!(matches!(
            store.edit_clinical(clinician, another, id, Some(1), "Wrong chart"),
            Err(Error::ChangedSince)
        ));
        assert!(matches!(
            store.edit_clinical(clinician, patient, id, Some(0), "Stale version"),
            Err(Error::ChangedSince)
        ));
        store
            .edit_clinical(clinician, patient, id, Some(1), "Synthetic edited offline")
            .unwrap();

        let edit = store.clinical_pending(clinician).unwrap().remove(0);
        assert_eq!(edit.id, first.id);
        assert_ne!(edit.key, first.key, "the edit is a new change");
        assert_eq!(edit.body, "Synthetic edited offline");
        assert_eq!(edit.base_version, Some(1));
        let (_, drafts) = store.offline_chart(clinician, patient).unwrap();
        assert_eq!(drafts.len(), 1);
        assert_eq!(drafts[0].body, "Synthetic edited offline");
        assert_eq!(drafts[0].state, "pending");
        assert!(store.clinical_pending(colleague).unwrap().is_empty());

        // Once the chart is no longer downloaded, the draft can't be edited offline.
        store.replace_panel(clinician, &[]).unwrap();
        assert!(matches!(
            store.edit_clinical(clinician, patient, id, Some(1), "Not on the panel"),
            Err(Error::ChangedSince)
        ));
        drop(store);

        // At rest: the file isn't plain SQLite, the edit's words aren't in it, and a wrong key can't open it.
        let file = std::fs::read(&database.0).unwrap();
        assert!(!file.starts_with(b"SQLite format 3"));
        let words = b"Synthetic edited offline";
        assert!(!file.windows(words.len()).any(|bytes| bytes == words));
        assert!(Store::open(&database.0, &[8; 32]).is_err());
        assert_eq!(
            database.open().clinical_pending(clinician).unwrap()[0].body,
            "Synthetic edited offline"
        );
    }

    #[test]
    fn a_new_phrase_shows_as_waiting_until_the_server_saves_it() {
        let database = TempDatabase::new();
        let mut store = database.open();

        let id = store.add(".NAD", "No acute distress.").unwrap();
        assert_eq!(
            store.phrases().unwrap(),
            [Phrase {
                id,
                shortcut: ".nad".into(),
                body: "No acute distress.".into(),
                version: 1,
                pending: true
            }]
        );

        let operation = only_pending(&store);
        assert_eq!((operation.id, operation.base_version), (id, None));
        store
            .apply_push([(
                &operation,
                &result(
                    &operation,
                    Outcome::Applied,
                    Some(saved(id, ".nad", "No acute distress.", 1)),
                ),
            )])
            .unwrap();

        assert!(store.pending(10).unwrap().is_empty());
        assert!(!store.phrases().unwrap()[0].pending);
    }

    #[test]
    fn a_duplicate_counts_as_saved() {
        let database = TempDatabase::new();
        let mut store = database.open();
        let id = store.add(".nad", "Text").unwrap();
        let operation = only_pending(&store);

        store
            .apply_push([(
                &operation,
                &result(
                    &operation,
                    Outcome::Duplicate,
                    Some(saved(id, ".nad", "Text", 1)),
                ),
            )])
            .unwrap();

        assert!(store.pending(10).unwrap().is_empty());
        assert!(store.conflicts().unwrap().is_empty());
    }

    #[test]
    fn each_edit_starts_from_the_version_it_will_follow() {
        let database = TempDatabase::new();
        let mut store = database.open();
        let saved_id = Uuid::now_v7();
        pull(&mut store, "10", vec![saved(saved_id, ".old", "Saved", 3)]);
        let new_id = store.add(".new", "Not sent yet").unwrap();

        store.edit(saved_id, 3, ".old", "First edit").unwrap();
        store.edit(saved_id, 4, ".old", "Second edit").unwrap();
        store
            .edit(new_id, 1, ".new", "Edited before sending")
            .unwrap();

        let bases: Vec<_> = store
            .outbox()
            .unwrap()
            .iter()
            .map(|operation| (operation.id, operation.base_version))
            .collect();
        assert_eq!(
            bases,
            [
                (new_id, None),
                (saved_id, Some(3)),
                (saved_id, Some(4)),
                (new_id, Some(1))
            ]
        );
        assert_eq!(store.waiting().unwrap(), 4);
        let versions: Vec<_> = store
            .phrases()
            .unwrap()
            .iter()
            .map(|phrase| (phrase.id, phrase.version))
            .collect();
        assert_eq!(versions, [(new_id, 2), (saved_id, 5)]);
        assert!(matches!(
            store.edit(Uuid::now_v7(), 1, ".x", "x"),
            Err(Error::NotFound)
        ));
    }

    #[test]
    fn an_edit_of_a_version_since_replaced_is_refused() {
        let database = TempDatabase::new();
        let mut store = database.open();
        let id = Uuid::now_v7();
        pull(
            &mut store,
            "10",
            vec![saved(id, ".nad", "Seen when editing began", 3)],
        );
        pull(
            &mut store,
            "11",
            vec![saved(id, ".nad", "Saved by someone else meanwhile", 4)],
        );

        assert!(matches!(
            store.edit(id, 3, ".nad", "Mine"),
            Err(Error::ChangedSince)
        ));
        assert_eq!(store.waiting().unwrap(), 0);
    }

    #[test]
    fn a_later_change_waits_for_the_one_before_and_joins_its_conflict() {
        let database = TempDatabase::new();
        let mut store = database.open();
        let id = Uuid::now_v7();
        pull(&mut store, "10", vec![saved(id, ".nad", "Original", 3)]);
        store.edit(id, 3, ".nad", "First").unwrap();
        store.edit(id, 4, ".nad", "Second").unwrap();

        let first = only_pending(&store);
        assert_eq!(
            (first.base_version, first.body.as_str()),
            (Some(3), "First")
        );
        store
            .apply_push([(
                &first,
                &result(
                    &first,
                    Outcome::Conflict,
                    Some(saved(id, ".nad", "Theirs", 4)),
                ),
            )])
            .unwrap();

        // The second edit, from version 4, would otherwise go over their version 4 without anyone choosing.
        assert_eq!(store.waiting().unwrap(), 0);
        let conflict = store.conflicts().unwrap().remove(0);
        assert_eq!(
            (conflict.key, conflict.body.as_str(), conflict.can_keep_mine),
            (first.key, "Second", true)
        );
        store.resolve(conflict.key, true).unwrap();
        let again = only_pending(&store);
        assert_eq!(
            (again.base_version, again.body.as_str()),
            (Some(4), "Second")
        );
    }

    #[test]
    fn a_later_change_is_sent_once_the_one_before_is_saved() {
        let database = TempDatabase::new();
        let mut store = database.open();
        let id = store.add(".nad", "First").unwrap();
        store.edit(id, 1, ".nad", "Second").unwrap();

        let first = only_pending(&store);
        store
            .apply_push([(
                &first,
                &result(
                    &first,
                    Outcome::Applied,
                    Some(saved(id, ".nad", "First", 1)),
                ),
            )])
            .unwrap();

        let second = only_pending(&store);
        assert_eq!(
            (second.base_version, second.body.as_str()),
            (Some(1), "Second")
        );
        assert_eq!(store.phrases().unwrap()[0].body, "Second");
    }

    #[test]
    fn a_pull_never_brings_back_an_older_version() {
        let database = TempDatabase::new();
        let mut store = database.open();
        let id = Uuid::now_v7();

        pull(&mut store, "10", vec![saved(id, ".nad", "Version 5", 5)]);
        pull(&mut store, "9", vec![saved(id, ".nad", "Version 4", 4)]);

        assert_eq!(store.phrases().unwrap()[0].body, "Version 5");
    }

    #[test]
    fn a_change_made_since_can_be_kept_or_dropped() {
        let database = TempDatabase::new();
        let mut store = database.open();
        let id = Uuid::now_v7();
        pull(&mut store, "10", vec![saved(id, ".nad", "Original", 1)]);

        for keep_mine in [true, false] {
            let version = store.phrases().unwrap()[0].version;
            store.edit(id, version, ".nad", "Mine").unwrap();
            let operation = only_pending(&store);
            let theirs = saved(id, ".nad", "Theirs", operation.base_version.unwrap() + 1);
            store
                .apply_push([(
                    &operation,
                    &result(&operation, Outcome::Conflict, Some(theirs.clone())),
                )])
                .unwrap();

            let conflict = store.conflicts().unwrap().remove(0);
            assert!(conflict.can_keep_mine);
            assert_eq!(
                (conflict.body.as_str(), conflict.theirs.as_ref()),
                ("Mine", Some(&theirs))
            );
            assert_eq!(
                store.phrases().unwrap()[0].body,
                "Theirs",
                "the list shows what the server has"
            );

            store.resolve(conflict.key, keep_mine).unwrap();
            assert!(store.conflicts().unwrap().is_empty());
            if keep_mine {
                let again = only_pending(&store);
                assert_eq!(
                    (again.base_version, again.body.as_str()),
                    (Some(theirs.version), "Mine")
                );
                assert_ne!(
                    again.key, operation.key,
                    "a new change, not the refused one again"
                );
                store
                    .apply_push([(
                        &again,
                        &result(
                            &again,
                            Outcome::Applied,
                            Some(saved(id, ".nad", "Mine", theirs.version + 1)),
                        ),
                    )])
                    .unwrap();
            } else {
                assert!(store.pending(10).unwrap().is_empty());
                assert_eq!(store.phrases().unwrap()[0].body, "Theirs");
            }
        }
    }

    #[test]
    fn a_new_phrase_whose_shortcut_is_taken_can_only_be_discarded() {
        let database = TempDatabase::new();
        let mut store = database.open();
        store.add(".nad", "Mine").unwrap();
        let operation = only_pending(&store);
        let holder = saved(Uuid::now_v7(), ".nad", "Already here", 1);

        store
            .apply_push([(
                &operation,
                &result(&operation, Outcome::Conflict, Some(holder.clone())),
            )])
            .unwrap();
        let conflict = store.conflicts().unwrap().remove(0);

        assert!(!conflict.can_keep_mine);
        assert!(matches!(
            store.resolve(conflict.key, true),
            Err(Error::Refused(_))
        ));
        store.resolve(conflict.key, false).unwrap();
        let phrases = store.phrases().unwrap();
        assert_eq!(
            phrases.iter().map(|phrase| phrase.id).collect::<Vec<_>>(),
            [holder.id]
        );
    }

    #[test]
    fn an_invalid_change_comes_back_with_the_reason() {
        let database = TempDatabase::new();
        let mut store = database.open();
        store.add(".nad", "Text").unwrap();
        let operation = only_pending(&store);
        let errors = BTreeMap::from([(
            "shortcut".to_owned(),
            vec!["Start with a period.".to_owned()],
        )]);

        store
            .apply_push([(
                &operation,
                &PushResult {
                    key: operation.key,
                    outcome: Outcome::Invalid,
                    quick_text: None,
                    errors: Some(errors),
                },
            )])
            .unwrap();

        let conflict = store.conflicts().unwrap().remove(0);
        assert_eq!(conflict.message.as_deref(), Some("Start with a period."));
        assert!(!conflict.can_keep_mine);
    }

    #[test]
    fn the_watermark_starts_at_zero_and_is_kept() {
        let database = TempDatabase::new();
        let mut store = database.open();
        assert_eq!(store.watermark().unwrap(), "0");

        pull(&mut store, "12345", vec![]);
        drop(store);

        assert_eq!(database.open().watermark().unwrap(), "12345");
    }

    /// TC-UC7-05 (NFR-02, with NFR-04): opening a cached chart offline takes under 5 ms. This times what the
    /// offline_chart command does when a clinician picks a chart from their offline panel: the chart, its drafts and
    /// its recordings, read from the encrypted database and decoded. Drawing the page in the web view isn't included.
    /// The desktop app keeps only the clinician's own panel, never the clinic's 50,000 patients, so the database holds
    /// a large panel: 500 charts, each with a full history, and drafts for many of them. Timed only in a release
    /// build, which is what clinicians run: `cargo test --release` (CI runs it on macOS and Windows).
    #[test]
    #[cfg_attr(
        debug_assertions,
        ignore = "timed in a release build: cargo test --release"
    )]
    fn opening_a_cached_chart_takes_under_5_ms() {
        use std::time::{Duration, Instant};

        use ehr_desktop::drafts;

        const PANEL: usize = 500;
        const OPENS: usize = 30;
        let limit = Duration::from_millis(5);

        let database = TempDatabase::new();
        let mut store = database.open();
        let clinician = Uuid::now_v7();
        let panel: Vec<charts::Chart> = (0..PANEL).map(synthetic_chart).collect();
        store.replace_panel(clinician, &panel).unwrap();
        for chart in panel.iter().step_by(5) {
            drafts::save(&store.connection, clinician, chart.id, "Test note").unwrap();
        }
        let patient = panel[PANEL / 2].id;

        // One warm-up open, as the test case says, then the timed ones.
        let open = |store: &Store| {
            let (chart, notes) = store.offline_chart(clinician, patient).unwrap();
            let recordings = store.recordings(clinician, patient).unwrap();
            (chart.id, notes.len(), recordings.len())
        };
        assert_eq!(open(&store), (patient, 1, 0));
        let mut times = Vec::with_capacity(OPENS);
        for _ in 0..OPENS {
            let started = Instant::now();
            std::hint::black_box(open(&store));
            times.push(started.elapsed());
        }

        let slowest = *times.iter().max().unwrap();
        println!(
            "TC-UC7-05: {OPENS} opens of one chart in a panel of {PANEL}; slowest {slowest:?}; all: {times:?}"
        );
        assert!(
            slowest < limit,
            "the slowest open took {slowest:?}, over {limit:?}"
        );
    }

    /// A synthetic chart with a full history: allergies, medications, conditions and visits.
    fn synthetic_chart(number: usize) -> charts::Chart {
        charts::Chart {
            id: Uuid::now_v7(),
            name: format!("Synthetic{number}, Patient{number}"),
            mrn: format!("SYN{number:07}"),
            birth_date: "1980-01-01".to_owned(),
            gender: "unknown".to_owned(),
            allergies: (0..10)
                .map(|i| charts::Allergy {
                    display: format!("Allergen {i}"),
                    reaction: Some("Hives".to_owned()),
                    criticality: Some("low".to_owned()),
                })
                .collect(),
            medications: (0..20)
                .map(|i| charts::Medication {
                    display: format!("Medication {i} 10 mg tablet"),
                    dosage: Some("1 tablet by mouth daily".to_owned()),
                })
                .collect(),
            conditions: (0..20)
                .map(|i| charts::Condition {
                    display: format!("Condition {i}"),
                    onset_date: Some("2020-01-01".to_owned()),
                })
                .collect(),
            visits: (0..20)
                .map(|i| charts::Visit {
                    period_start: format!("2026-01-{:02}T09:00:00Z", i + 1),
                    reason: Some("Follow-up visit".to_owned()),
                    systolic_mm_hg: Some(120),
                    diastolic_mm_hg: Some(80),
                    heart_rate_per_minute: Some(70),
                })
                .collect(),
            sensitive_not_kept: 0,
            allergy_status: None,
            notes: vec![],
        }
    }
}
