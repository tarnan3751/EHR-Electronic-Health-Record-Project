//! Recordings are durable outbox entries in SQLCipher, scoped to their owner and chart.
use rusqlite::{Connection, Result, params};
use serde::Serialize;
use uuid::Uuid;

pub const MAX_BYTES: usize = 8 * 1024 * 1024;

pub fn initialize(db: &Connection) -> Result<()> {
    db.execute_batch(
        "CREATE TABLE IF NOT EXISTS clinical_recordings (
        id TEXT PRIMARY KEY, owner TEXT NOT NULL, patient TEXT NOT NULL,
        media_type TEXT NOT NULL, audio BLOB NOT NULL, session TEXT,
        chunk_size INTEGER NOT NULL DEFAULT 0, next_seq INTEGER NOT NULL DEFAULT 0,
        stopped INTEGER NOT NULL DEFAULT 0, problem TEXT
    );",
    )
}

#[derive(Serialize)]
pub struct RecordingView {
    pub problem: Option<String>,
}

pub fn save(
    db: &Connection,
    owner: Uuid,
    patient: Uuid,
    media_type: &str,
    audio: &[u8],
) -> Result<()> {
    db.execute(
        "INSERT INTO clinical_recordings(id,owner,patient,media_type,audio) VALUES(?1,?2,?3,?4,?5)",
        params![
            Uuid::now_v7().to_string(),
            owner.to_string(),
            patient.to_string(),
            media_type,
            audio
        ],
    )?;
    Ok(())
}

pub fn list(db: &Connection, owner: Uuid, patient: Uuid) -> Result<Vec<RecordingView>> {
    db.prepare("SELECT problem FROM clinical_recordings WHERE owner=?1 AND patient=?2 ORDER BY id")?
        .query_map(params![owner.to_string(), patient.to_string()], |r| {
            Ok(RecordingView { problem: r.get(0)? })
        })?
        .collect()
}
