//! Recordings are durable outbox entries in SQLCipher, scoped to their owner and chart.
use rusqlite::{Connection, OptionalExtension, Result, params};
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

pub struct Recording {
    pub id: String,
    pub patient: String,
    pub media_type: String,
    pub audio: Vec<u8>,
    pub session: Option<String>,
    pub chunk_size: usize,
    pub next_seq: usize,
    pub stopped: bool,
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

pub fn waiting(db: &Connection, owner: Uuid) -> Result<usize> {
    db.query_row(
        "SELECT count(*) FROM clinical_recordings WHERE owner=?1",
        [owner.to_string()],
        |r| Ok(r.get::<_, u32>(0)? as usize),
    )
}

pub fn next(db: &Connection, owner: Uuid) -> Result<Option<Recording>> {
    db.query_row(
        "SELECT id,patient,media_type,audio,session,chunk_size,next_seq,stopped
        FROM clinical_recordings WHERE owner=?1 AND problem IS NULL ORDER BY id LIMIT 1",
        [owner.to_string()],
        |r| {
            Ok(Recording {
                id: r.get(0)?,
                patient: r.get(1)?,
                media_type: r.get(2)?,
                audio: r.get(3)?,
                session: r.get(4)?,
                chunk_size: r.get::<_, u32>(5)? as usize,
                next_seq: r.get::<_, u32>(6)? as usize,
                stopped: r.get(7)?,
            })
        },
    )
    .optional()
}

pub fn checkpoint(db: &Connection, owner: Uuid, recording: &Recording) -> Result<()> {
    db.execute(
        "UPDATE clinical_recordings SET session=?3,chunk_size=?4,next_seq=?5,stopped=?6
        WHERE id=?1 AND owner=?2",
        params![
            recording.id,
            owner.to_string(),
            recording.session,
            recording.chunk_size as u32,
            recording.next_seq as u32,
            recording.stopped
        ],
    )?;
    Ok(())
}

pub fn reject(db: &Connection, owner: Uuid, recording: &Recording, problem: &str) -> Result<()> {
    db.execute(
        "UPDATE clinical_recordings SET problem=?3 WHERE id=?1 AND owner=?2",
        params![recording.id, owner.to_string(), problem],
    )?;
    Ok(())
}

pub fn finish(db: &mut Connection, owner: Uuid, recording: &Recording, body: &str) -> Result<()> {
    let tx = db.transaction()?;
    // Removing audio and saving the transcription as a new unsigned draft is one commit.
    if tx.execute(
        "DELETE FROM clinical_recordings WHERE id=?1 AND owner=?2",
        params![recording.id, owner.to_string()],
    )? == 1
    {
        tx.execute(
            "INSERT INTO clinical_drafts(id,clinician_id,patient_id,body,pending_change_id)
            VALUES(?1,?2,?3,?4,?5)",
            params![
                recording.id,
                owner.to_string(),
                recording.patient,
                body,
                Uuid::now_v7().to_string()
            ],
        )?;
    }
    tx.commit()
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::{draft_sync, drafts};

    #[test]
    fn recording_reopens_with_its_upload_position_and_hands_off_to_the_draft_outbox() {
        let file = std::env::temp_dir().join(format!("ehr-recording-{}.db", Uuid::now_v7()));
        let cfg = |db: &Connection| {
            db.execute_batch("PRAGMA key = 'local recording test only'")
                .unwrap();
            drafts::initialize(db).unwrap();
            initialize(db).unwrap();
        };
        let owner = Uuid::now_v7();
        let patient = Uuid::now_v7();
        let expected = {
            let db = Connection::open(&file).unwrap();
            cfg(&db);
            save(&db, owner, patient, "audio/webm", &[1, 2, 3]).unwrap();
            let mut state = next(&db, owner).unwrap().unwrap();
            state.session = Some("abcdefghijklmnop".to_owned());
            state.chunk_size = 2;
            state.next_seq = 1;
            checkpoint(&db, owner, &state).unwrap();
            state
        };
        {
            let mut db = Connection::open(&file).unwrap();
            cfg(&db);
            draft_sync::initialize(&db).unwrap();
            assert!(next(&db, Uuid::now_v7()).unwrap().is_none());
            let state = next(&db, owner).unwrap().unwrap();
            assert_eq!(state.audio, [1, 2, 3]);
            assert_eq!(state.next_seq, expected.next_seq);
            finish(&mut db, owner, &state, "Transcribed draft").unwrap();
            assert!(next(&db, owner).unwrap().is_none());
            assert_eq!(
                draft_sync::pending(&db, owner).unwrap()[0].body,
                "Transcribed draft"
            );
        }
        std::fs::remove_file(file).unwrap();
    }
}
