//! Versioned drafts use the same encrypted database as the chart cache.
use rusqlite::{Connection, OptionalExtension, Result, params};
use serde::{Deserialize, Serialize};
use uuid::Uuid;

const SCHEMA: &str = "
CREATE TABLE IF NOT EXISTS clinical_draft_sync (
    id TEXT PRIMARY KEY REFERENCES clinical_drafts(id),
    version INTEGER,
    state TEXT NOT NULL,
    theirs TEXT,
    their_version INTEGER,
    problem TEXT
);
CREATE TABLE IF NOT EXISTS clinical_draft_watermarks (
    owner TEXT PRIMARY KEY,
    watermark TEXT NOT NULL
);";

pub fn initialize(db: &Connection) -> Result<()> {
    db.execute_batch(SCHEMA)
}

#[derive(Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ServerDraft {
    pub id: String,
    pub patient_id: String,
    pub body: String,
    pub version: i64,
    /// The draft was signed as a sensitive note, which is never kept on this
    /// device: the server sends no text, and any copy here is emptied.
    #[serde(default)]
    pub withheld: bool,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Change {
    pub key: String,
    pub id: String,
    pub patient_id: String,
    pub body: String,
    pub base_version: Option<i64>,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct DraftView {
    pub id: String,
    pub body: String,
    pub state: String,
    pub version: Option<i64>,
    pub theirs: Option<String>,
    pub their_version: Option<i64>,
    pub problem: Option<String>,
}

pub fn list(db: &Connection, owner: Uuid, patient: Uuid) -> Result<Vec<DraftView>> {
    db.prepare("SELECT d.id,d.body,coalesce(s.state,'pending'),s.version,s.theirs,s.their_version,s.problem
        FROM clinical_drafts d LEFT JOIN clinical_draft_sync s ON s.id=d.id
        WHERE d.clinician_id=?1 AND d.patient_id=?2 ORDER BY d.seq")?
        .query_map(params![owner.to_string(), patient.to_string()], |r| {
            Ok(DraftView { id:r.get(0)?, body:r.get(1)?, state:r.get(2)?, version:r.get(3)?,
                theirs:r.get(4)?, their_version:r.get(5)?, problem:r.get(6)? })
        })?.collect()
}

pub fn waiting(db: &Connection, owner: Uuid) -> Result<usize> {
    db.query_row(
        "SELECT count(*) FROM clinical_drafts d LEFT JOIN clinical_draft_sync s ON s.id=d.id
        WHERE d.clinician_id=?1 AND coalesce(s.state,'pending') NOT IN ('synced','withheld')",
        [owner.to_string()],
        |r| Ok(r.get::<_, u32>(0)? as usize),
    )
}

pub fn pending(db: &Connection, owner: Uuid) -> Result<Vec<Change>> {
    db.prepare(
        "SELECT d.pending_change_id,d.id,d.patient_id,d.body,s.version
        FROM clinical_drafts d LEFT JOIN clinical_draft_sync s ON s.id=d.id
        WHERE d.clinician_id=?1 AND coalesce(s.state,'pending')='pending' ORDER BY d.seq LIMIT 10",
    )?
    .query_map([owner.to_string()], |r| {
        Ok(Change {
            key: r.get(0)?,
            id: r.get(1)?,
            patient_id: r.get(2)?,
            body: r.get(3)?,
            base_version: r.get(4)?,
        })
    })?
    .collect()
}

pub fn watermark(db: &Connection, owner: Uuid) -> Result<String> {
    Ok(db
        .query_row(
            "SELECT watermark FROM clinical_draft_watermarks WHERE owner=?1",
            [owner.to_string()],
            |r| r.get(0),
        )
        .optional()?
        .unwrap_or_else(|| "0".to_owned()))
}

pub fn reset_watermark(db: &Connection, owner: Uuid) -> Result<()> {
    db.execute(
        "DELETE FROM clinical_draft_watermarks WHERE owner=?1",
        [owner.to_string()],
    )?;
    Ok(())
}

pub fn pull(
    db: &mut Connection,
    owner: Uuid,
    since: &str,
    next: &str,
    changed: &[ServerDraft],
) -> Result<()> {
    let tx = db.transaction()?;
    // A chart download may have changed the scope of this pull while it was in flight.
    if watermark(&tx, owner)? != since {
        return Ok(());
    }
    for draft in changed {
        let owner = owner.to_string();
        if draft.withheld {
            // Signed as a sensitive note. Whatever state the copy here is in, its text goes: the note is final, so
            // nothing local could still be saved to it. A draft this device never had is not created.
            let emptied = tx.execute(
                "UPDATE clinical_drafts SET body='' WHERE id=?1 AND clinician_id=?2 AND patient_id=?3",
                params![draft.id, owner, draft.patient_id],
            )?;
            if emptied == 1 {
                tx.execute(
                    "INSERT INTO clinical_draft_sync(id,version,state) VALUES(?1,?2,'withheld')
                    ON CONFLICT(id) DO UPDATE SET version=excluded.version,state='withheld',theirs=NULL,their_version=NULL,problem=NULL",
                    params![draft.id, draft.version],
                )?;
            }
            continue;
        }
        tx.execute("INSERT OR IGNORE INTO clinical_drafts(id,clinician_id,patient_id,body,pending_change_id)
            VALUES(?1,?2,?3,?4,?5)", params![draft.id, owner, draft.patient_id, draft.body, Uuid::now_v7().to_string()])?;
        if tx.changes() == 1 {
            tx.execute(
                "INSERT INTO clinical_draft_sync(id,version,state) VALUES(?1,?2,'synced')",
                params![draft.id, draft.version],
            )?;
        } else {
            let updated = tx.execute("UPDATE clinical_drafts SET body=?4 WHERE id=?1 AND clinician_id=?2 AND patient_id=?3
                AND EXISTS (SELECT 1 FROM clinical_draft_sync s WHERE s.id=?1 AND s.state='synced' AND s.version<?5)",
                params![draft.id,owner,draft.patient_id,draft.body,draft.version])?;
            if updated == 1 {
                tx.execute(
                    "UPDATE clinical_draft_sync SET version=?2 WHERE id=?1",
                    params![draft.id, draft.version],
                )?;
            } else {
                // Keep the conflict preview current even when a third device changes the draft again.
                tx.execute("UPDATE clinical_draft_sync SET theirs=?4,their_version=?5 WHERE id=?1
                    AND state='conflict' AND their_version<?5 AND EXISTS (
                        SELECT 1 FROM clinical_drafts WHERE id=?1 AND clinician_id=?2 AND patient_id=?3)",
                    params![draft.id, owner, draft.patient_id, draft.body, draft.version])?;
            }
        }
    }
    tx.execute(
        "INSERT INTO clinical_draft_watermarks(owner,watermark) VALUES(?1,?2)
        ON CONFLICT(owner) DO UPDATE SET watermark=excluded.watermark",
        params![owner.to_string(), next],
    )?;
    tx.commit()
}

#[derive(Deserialize)]
#[serde(tag = "outcome", rename_all = "camelCase")]
pub enum PushResult {
    Applied { current: ServerDraft },
    Duplicate { current: ServerDraft },
    Conflict { current: ServerDraft },
    Invalid { problem: String },
}

pub fn answer(db: &mut Connection, owner: Uuid, change: &Change, answer: PushResult) -> Result<()> {
    let tx = db.transaction()?;
    let exists: bool = tx.query_row(
        "SELECT EXISTS(SELECT 1 FROM clinical_drafts WHERE id=?1
        AND clinician_id=?2 AND pending_change_id=?3)",
        params![change.id, owner.to_string(), change.key],
        |r| r.get(0),
    )?;
    if !exists {
        return Ok(());
    }
    match answer {
        PushResult::Applied { current } | PushResult::Duplicate { current } => {
            verify(change, &current)?;
            tx.execute(
                "UPDATE clinical_drafts SET body=?2 WHERE id=?1",
                params![change.id, current.body],
            )?;
            meta(&tx, &change.id, Some(current.version), "synced", None, None)?;
        }
        PushResult::Conflict { current } => {
            verify(change, &current)?;
            meta(
                &tx,
                &change.id,
                change.base_version,
                "conflict",
                Some(&current),
                None,
            )?;
        }
        PushResult::Invalid { problem } => {
            meta(
                &tx,
                &change.id,
                change.base_version,
                "invalid",
                None,
                Some(&problem),
            )?;
        }
    }
    tx.commit()
}

fn verify(change: &Change, current: &ServerDraft) -> Result<()> {
    if change.id == current.id && change.patient_id == current.patient_id && current.version > 0 {
        Ok(())
    } else {
        Err(rusqlite::Error::InvalidQuery)
    }
}

fn meta(
    db: &Connection,
    id: &str,
    version: Option<i64>,
    state: &str,
    theirs: Option<&ServerDraft>,
    problem: Option<&str>,
) -> Result<()> {
    db.execute("INSERT INTO clinical_draft_sync(id,version,state,theirs,their_version,problem)
        VALUES(?1,?2,?3,?4,?5,?6) ON CONFLICT(id) DO UPDATE SET version=excluded.version,
        state=excluded.state,theirs=excluded.theirs,their_version=excluded.their_version,problem=excluded.problem",
        params![id,version,state,theirs.map(|d| &d.body),theirs.map(|d| d.version),problem])?;
    Ok(())
}

pub fn edit(
    db: &mut Connection,
    owner: Uuid,
    patient: Uuid,
    id: Uuid,
    version: Option<i64>,
    body: &str,
) -> Result<bool> {
    let tx = db.transaction()?;
    let id = id.to_string();
    let updated = tx.execute("UPDATE clinical_drafts SET body=?4,pending_change_id=?5
        WHERE id=?1 AND clinician_id=?2 AND patient_id=?3 AND EXISTS
            (SELECT 1 FROM clinical_draft_sync s WHERE s.id=?1 AND s.state IN ('synced','invalid') AND s.version IS ?6)",
        params![id,owner.to_string(),patient.to_string(),body,Uuid::now_v7().to_string(),version])?;
    if updated == 1 {
        meta(&tx, &id, version, "pending", None, None)?;
    }
    tx.commit()?;
    Ok(updated == 1)
}

pub fn resolve(
    db: &mut Connection,
    owner: Uuid,
    patient: Uuid,
    id: Uuid,
    version: i64,
    keep_mine: bool,
) -> Result<bool> {
    let tx = db.transaction()?;
    let id = id.to_string();
    let updated = tx.execute("UPDATE clinical_drafts SET
        body=CASE WHEN ?4 THEN body ELSE (SELECT theirs FROM clinical_draft_sync WHERE id=?1) END,
        pending_change_id=?5 WHERE id=?1 AND clinician_id=?2 AND patient_id=?3 AND EXISTS
            (SELECT 1 FROM clinical_draft_sync s WHERE s.id=?1 AND s.state='conflict' AND s.their_version=?6)",
        params![id,owner.to_string(),patient.to_string(),keep_mine,Uuid::now_v7().to_string(),version])?;
    if updated == 1 {
        meta(
            &tx,
            &id,
            Some(version),
            if keep_mine { "pending" } else { "synced" },
            None,
            None,
        )?;
    }
    tx.commit()?;
    Ok(updated == 1)
}

/// The desktop sync tests: also TC-UC8-06 (NFR-10). CI's desktop job (.github/workflows/ci.yml) runs them on macOS and
/// Windows, the team's two platforms.
#[cfg(test)]
mod tests {
    use super::*;
    use crate::drafts;

    /// TC-UC8-02 (FR-24): a draft the server changed since keeps both versions, and only the clinician's explicit
    /// choice, to keep theirs or resend their own on the server's version, changes it.
    #[test]
    fn conflict_keeps_both_versions_and_only_an_explicit_choice_changes_the_draft() {
        let mut db = Connection::open_in_memory().unwrap();
        drafts::initialize(&db).unwrap();
        initialize(&db).unwrap();
        let owner = Uuid::now_v7();
        let patient = Uuid::now_v7();
        let note = drafts::save(&db, owner, patient, "Mine").unwrap();
        let id = Uuid::parse_str(&note.id).unwrap();
        let operation = pending(&db, owner).unwrap().remove(0);
        let server = || ServerDraft {
            id: note.id.clone(),
            patient_id: patient.to_string(),
            body: "Theirs".to_owned(),
            version: 2,
            withheld: false,
        };
        answer(
            &mut db,
            owner,
            &operation,
            PushResult::Conflict { current: server() },
        )
        .unwrap();
        let displayed = list(&db, owner, patient).unwrap().remove(0);
        assert_eq!(displayed.body, "Mine");
        assert_eq!(displayed.theirs.as_deref(), Some("Theirs"));
        assert_ne!(waiting(&db, owner).unwrap(), 0);
        assert!(pending(&db, owner).unwrap().is_empty());
        assert!(!resolve(&mut db, Uuid::now_v7(), patient, id, 2, true).unwrap());
        assert!(!resolve(&mut db, owner, patient, id, 1, true).unwrap());
        assert!(resolve(&mut db, owner, patient, id, 2, true).unwrap());
        let retry = pending(&db, owner).unwrap().remove(0);
        assert_ne!(retry.key, operation.key);
        assert_eq!(retry.body, "Mine");
        assert_eq!(retry.base_version, Some(2));

        answer(
            &mut db,
            owner,
            &retry,
            PushResult::Conflict {
                current: ServerDraft {
                    version: 3,
                    ..server()
                },
            },
        )
        .unwrap();
        assert!(resolve(&mut db, owner, patient, id, 3, false).unwrap());
        assert_eq!(list(&db, owner, patient).unwrap()[0].body, "Theirs");
        assert_eq!(waiting(&db, owner).unwrap(), 0);
    }

    /// The expansion plan's step 10: a draft signed as a sensitive note is never kept on this device. The server
    /// sends it with no text and marked withheld; the copy here is emptied whatever state it was in (synced, or
    /// holding text not yet sent), stops counting as waiting, and offers nothing to send. A withheld draft this
    /// device never had is not created.
    #[test]
    fn a_draft_signed_as_a_sensitive_note_is_emptied_here_and_not_created() {
        let mut db = Connection::open_in_memory().unwrap();
        drafts::initialize(&db).unwrap();
        initialize(&db).unwrap();
        let owner = Uuid::now_v7();
        let patient = Uuid::now_v7();
        let withheld = |id: &str, version| ServerDraft {
            id: id.to_owned(),
            patient_id: patient.to_string(),
            body: String::new(),
            version,
            withheld: true,
        };

        // One that was synced from the server, and one typed here and still waiting to be sent.
        let synced = Uuid::now_v7().to_string();
        let server = ServerDraft {
            id: synced.clone(),
            patient_id: patient.to_string(),
            body: "Synthetic sensitive text, synced".to_owned(),
            version: 1,
            withheld: false,
        };
        pull(&mut db, owner, "0", "1", &[server]).unwrap();
        let typed =
            drafts::save(&db, owner, patient, "Synthetic sensitive text, typed here").unwrap();
        assert_eq!(waiting(&db, owner).unwrap(), 1);

        let never_here = Uuid::now_v7().to_string();
        pull(
            &mut db,
            owner,
            "1",
            "2",
            &[
                withheld(&synced, 2),
                withheld(&typed.id, 2),
                withheld(&never_here, 2),
            ],
        )
        .unwrap();

        let shown = list(&db, owner, patient).unwrap();
        assert_eq!(shown.len(), 2);
        assert!(
            shown
                .iter()
                .all(|draft| draft.body.is_empty() && draft.state == "withheld")
        );
        assert_eq!(waiting(&db, owner).unwrap(), 0);
        assert!(pending(&db, owner).unwrap().is_empty());
        let kept: i64 = db
            .query_row(
                "SELECT count(*) FROM clinical_drafts WHERE body LIKE '%sensitive%'",
                [],
                |r| r.get(0),
            )
            .unwrap();
        assert_eq!(kept, 0);
    }
}
