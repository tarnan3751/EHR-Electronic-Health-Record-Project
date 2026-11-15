//! Local clinical drafts. The caller uses the desktop's SQLCipher connection and
//! supplies the verified clinician's ID. The Quick text sync client does not send
//! these changes.

use rusqlite::{Connection, Result, params};
use uuid::Uuid;

const SCHEMA: &str = "
    CREATE TABLE IF NOT EXISTS clinical_drafts (
        seq INTEGER PRIMARY KEY,
        id TEXT NOT NULL UNIQUE,
        clinician_id TEXT NOT NULL,
        patient_id TEXT NOT NULL,
        body TEXT NOT NULL,
        pending_change_id TEXT NOT NULL UNIQUE
    );
    CREATE INDEX IF NOT EXISTS clinical_drafts_clinician
        ON clinical_drafts (clinician_id, seq);
";

const INSERT: &str = "INSERT INTO clinical_drafts
    (id, clinician_id, patient_id, body, pending_change_id) VALUES (?1, ?2, ?3, ?4, ?5)";
const SELECT_PENDING: &str = "SELECT id, patient_id, body, pending_change_id FROM clinical_drafts
    WHERE clinician_id = ?1 ORDER BY seq";

#[derive(Debug, PartialEq, Eq)]
pub struct PendingDraft {
    pub id: String,
    pub patient_id: String,
    pub body: String,
    pub change_id: String,
}

pub fn initialize(connection: &Connection) -> Result<()> {
    connection.execute_batch(SCHEMA)
}

/// The pending change ID is stored with the draft in the same write. Each
/// call creates a new draft; editing and server acknowledgement are separate.
pub fn save(
    connection: &Connection,
    clinician_id: Uuid,
    patient_id: Uuid,
    body: &str,
) -> Result<PendingDraft> {
    let draft = PendingDraft {
        id: Uuid::now_v7().to_string(),
        patient_id: patient_id.to_string(),
        body: body.to_owned(),
        change_id: Uuid::now_v7().to_string(),
    };
    connection.execute(
        INSERT,
        params![
            draft.id,
            clinician_id.to_string(),
            draft.patient_id,
            draft.body,
            draft.change_id,
        ],
    )?;
    Ok(draft)
}

pub fn pending(connection: &Connection, clinician_id: Uuid) -> Result<Vec<PendingDraft>> {
    let mut statement = connection.prepare(SELECT_PENDING)?;
    let rows = statement.query_map([clinician_id.to_string()], |row| {
        Ok(PendingDraft {
            id: row.get(0)?,
            patient_id: row.get(1)?,
            body: row.get(2)?,
            change_id: row.get(3)?,
        })
    })?;
    rows.collect()
}
