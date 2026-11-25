//! Chart snapshots downloaded for one clinician, stored in the same encrypted
//! database as their drafts, and when they were downloaded, which the app shows
//! beside them: only "Download my charts" refreshes them (the expansion plan's
//! step 19a).

use std::time::{SystemTime, UNIX_EPOCH};

use rusqlite::{Connection, OptionalExtension, Result, params};
use serde::{Deserialize, Serialize};
use uuid::Uuid;

const SCHEMA: &str = "
    CREATE TABLE IF NOT EXISTS offline_charts (
        clinician_id TEXT NOT NULL,
        patient_id TEXT NOT NULL,
        name TEXT NOT NULL,
        mrn TEXT NOT NULL,
        chart TEXT NOT NULL,
        PRIMARY KEY (clinician_id, patient_id)
    );
    -- When each clinician's panel was last downloaded, in milliseconds since 1970 by this computer's clock, and the
    -- clinic's time zone its charts came with, to show that time in.
    CREATE TABLE IF NOT EXISTS offline_chart_downloads (
        clinician_id TEXT PRIMARY KEY,
        downloaded_at INTEGER NOT NULL,
        clinic_time_zone TEXT
    );
";

#[derive(Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Chart {
    pub id: Uuid,
    pub name: String,
    pub mrn: String,
    pub birth_date: String,
    pub gender: String,
    pub allergies: Vec<Allergy>,
    pub medications: Vec<Medication>,
    pub conditions: Vec<Condition>,
    pub visits: Vec<Visit>,
    /// How many sensitive diagnoses, medications and signed notes the server left out:
    /// they are never kept offline. Absent from copies made before it was sent.
    #[serde(default)]
    pub sensitive_not_kept: u32,
    /// What the allergy list is: "listed", "none-known" or "not-recorded".
    /// Absent from copies made before it was sent.
    #[serde(default)]
    pub allergy_status: Option<String>,
    /// The latest signed notes that aren't sensitive, newest first, each with
    /// its addenda in order. Absent from copies made before they were sent.
    #[serde(default)]
    pub notes: Vec<SignedNote>,
    /// The clinic's time zone, an IANA name: this copy's times are shown in
    /// it, labeled. Absent from copies made before it was sent, which are
    /// shown in UTC as before.
    #[serde(default)]
    pub clinic_time_zone: Option<String>,
}

#[derive(Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct SignedNote {
    pub signed_at: String,
    pub signer: String,
    /// The note's History as signed or, for a note signed in four sections
    /// (the expansion plan's step 14), its Subjective and Objective, each
    /// under its heading: what a build that knows only History shows.
    pub history: String,
    /// A note signed in four sections: shown in place of History. Absent
    /// from notes signed before then, and from copies made before step 14.
    #[serde(default)]
    pub subjective: Option<String>,
    #[serde(default)]
    pub objective: Option<String>,
    pub assessment: String,
    pub plan: String,
    #[serde(default)]
    pub addenda: Vec<Addendum>,
    /// How many sensitive addenda the note has that the server left out.
    #[serde(default)]
    pub sensitive_addenda_not_kept: u32,
}

#[derive(Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Addendum {
    pub signed_at: String,
    pub author: String,
    pub text: String,
    /// Written by someone other than the note's signer.
    #[serde(default)]
    pub not_note_author: bool,
}

#[derive(Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Allergy {
    pub display: String,
    pub reaction: Option<String>,
    pub criticality: Option<String>,
}

#[derive(Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Medication {
    pub display: String,
    pub dosage: Option<String>,
}

#[derive(Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Condition {
    pub display: String,
    pub onset_date: Option<String>,
}

#[derive(Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Visit {
    pub period_start: String,
    pub reason: Option<String>,
    pub systolic_mm_hg: Option<i16>,
    pub diastolic_mm_hg: Option<i16>,
    pub heart_rate_per_minute: Option<i16>,
}

/// When a clinician's offline charts were last downloaded: `at` in milliseconds since 1970, shown in the clinic's
/// time zone when the charts named one.
#[derive(Serialize, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct Updated {
    pub at: i64,
    pub clinic_time_zone: Option<String>,
}

#[derive(Serialize)]
pub struct ChartSummary {
    pub id: String,
    pub name: String,
    pub mrn: String,
}

pub fn initialize(connection: &Connection) -> Result<()> {
    connection.execute_batch(SCHEMA)
}

/// Update only this clinician's panel. Removing a chart hides it offline but
/// preserves the clinician's unsent drafts.
pub fn replace_panel(connection: &mut Connection, owner: Uuid, panel: &[Chart]) -> Result<()> {
    let tx = connection.transaction()?;
    let owner = owner.to_string();
    tx.execute(
        "DELETE FROM offline_charts WHERE clinician_id = ?1",
        [&owner],
    )?;
    for chart in panel {
        let json = serde_json::to_string(chart)
            .map_err(|error| rusqlite::Error::ToSqlConversionFailure(Box::new(error)))?;
        tx.execute(
            "INSERT INTO offline_charts (clinician_id, patient_id, name, mrn, chart)
             VALUES (?1, ?2, ?3, ?4, ?5)",
            params![owner, chart.id.to_string(), chart.name, chart.mrn, json],
        )?;
    }
    let now = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map_or(0, |since| {
            i64::try_from(since.as_millis()).unwrap_or(i64::MAX)
        });
    let zone = panel
        .iter()
        .find_map(|chart| chart.clinic_time_zone.clone());
    tx.execute(
        "INSERT INTO offline_chart_downloads (clinician_id, downloaded_at, clinic_time_zone) VALUES (?1, ?2, ?3)
         ON CONFLICT (clinician_id) DO UPDATE SET downloaded_at = excluded.downloaded_at,
             clinic_time_zone = excluded.clinic_time_zone",
        params![owner, now, zone],
    )?;
    tx.commit()
}

/// When this clinician's charts were last downloaded; none for charts downloaded before this was kept, or never.
pub fn updated(connection: &Connection, owner: Uuid) -> Result<Option<Updated>> {
    connection
        .query_row(
            "SELECT downloaded_at, clinic_time_zone FROM offline_chart_downloads WHERE clinician_id = ?1",
            [owner.to_string()],
            |row| {
                Ok(Updated {
                    at: row.get(0)?,
                    clinic_time_zone: row.get(1)?,
                })
            },
        )
        .optional()
}

pub fn panel(connection: &Connection, owner: Uuid) -> Result<Vec<ChartSummary>> {
    let mut statement = connection.prepare(
        "SELECT patient_id, name, mrn FROM offline_charts
         WHERE clinician_id = ?1 ORDER BY name, patient_id",
    )?;
    let rows = statement.query_map([owner.to_string()], |row| {
        Ok(ChartSummary {
            id: row.get(0)?,
            name: row.get(1)?,
            mrn: row.get(2)?,
        })
    })?;
    rows.collect()
}

pub fn contains(connection: &Connection, owner: Uuid, patient: Uuid) -> Result<bool> {
    connection.query_row(
        "SELECT EXISTS(SELECT 1 FROM offline_charts WHERE clinician_id = ?1 AND patient_id = ?2)",
        params![owner.to_string(), patient.to_string()],
        |row| row.get(0),
    )
}

pub fn get(connection: &Connection, owner: Uuid, patient: Uuid) -> Result<Option<Chart>> {
    connection
        .query_row(
            "SELECT chart FROM offline_charts WHERE clinician_id = ?1 AND patient_id = ?2",
            params![owner.to_string(), patient.to_string()],
            |row| {
                let json: String = row.get(0)?;
                serde_json::from_str(&json).map_err(|error| {
                    rusqlite::Error::FromSqlConversionFailure(
                        0,
                        rusqlite::types::Type::Text,
                        Box::new(error),
                    )
                })
            },
        )
        .optional()
}

#[cfg(test)]
mod tests {
    use super::*;

    /// The expansion plan's step 19a: downloading a panel records when, with the clinic's time zone its charts came
    /// with, for that clinician only, and a later download moves it on.
    #[test]
    fn a_download_records_when_it_was_made() {
        let mut db = Connection::open_in_memory().unwrap();
        initialize(&db).unwrap();
        let (owner, colleague) = (Uuid::now_v7(), Uuid::now_v7());
        let chart: Chart = serde_json::from_str(
            r#"{"id":"018f0000-0000-7000-8000-000000000001","name":"Synthetic Patient","mrn":"SYN1","birthDate":"1980-01-01",
            "gender":"unknown","allergies":[],"medications":[],"conditions":[],"visits":[],"clinicTimeZone":"America/Chicago"}"#,
        )
        .unwrap();
        assert_eq!(updated(&db, owner).unwrap(), None);

        let before = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap()
            .as_millis();
        replace_panel(&mut db, owner, &[chart]).unwrap();
        let first = updated(&db, owner).unwrap().unwrap();
        assert!(i64::try_from(before).unwrap() <= first.at);
        assert_eq!(first.clinic_time_zone.as_deref(), Some("America/Chicago"));
        assert_eq!(updated(&db, colleague).unwrap(), None);

        std::thread::sleep(std::time::Duration::from_millis(5));
        replace_panel(&mut db, owner, &[]).unwrap();
        let second = updated(&db, owner).unwrap().unwrap();
        assert!(second.at > first.at);
        assert_eq!(second.clinic_time_zone, None);
    }

    /// The expansion plan's step 10: a chart copy carries signed notes and their addenda, and a copy stored before
    /// they were sent (no `notes` at all) still reads, as a chart with none.
    #[test]
    fn a_chart_copy_reads_with_signed_notes_and_without_them() {
        let old = r#"{"id":"018f0000-0000-7000-8000-000000000001","name":"Synthetic Patient","mrn":"SYN1","birthDate":"1980-01-01",
            "gender":"unknown","allergies":[],"medications":[],"conditions":[],"visits":[]}"#;
        let chart: Chart = serde_json::from_str(old).unwrap();
        assert!(chart.notes.is_empty());

        let new = r#"{"id":"018f0000-0000-7000-8000-000000000001","name":"Synthetic Patient","mrn":"SYN1","birthDate":"1980-01-01",
            "gender":"unknown","allergies":[],"medications":[],"conditions":[],"visits":[],"sensitiveNotKept":1,
            "notes":[{"signedAt":"2026-11-19T06:00:17Z","signer":"Dr. Test","history":"H","assessment":"A","plan":"P",
                "addenda":[{"signedAt":"2026-11-19T07:00:00Z","author":"Dr. Colleague","text":"Synthetic addendum"}],
                "sensitiveAddendaNotKept":2}]}"#;
        let chart: Chart = serde_json::from_str(new).unwrap();
        let again: Chart = serde_json::from_str(&serde_json::to_string(&chart).unwrap()).unwrap();
        assert_eq!(again.notes.len(), 1);
        assert_eq!(again.notes[0].addenda[0].author, "Dr. Colleague");
        assert_eq!(again.notes[0].sensitive_addenda_not_kept, 2);
        assert_eq!(again.notes[0].subjective, None);
    }

    /// The expansion plan's step 14: a note signed in four sections carries
    /// Subjective and Objective, which survive storing; and a build from
    /// before them, which knows only History, still reads the copy, with the
    /// two sections under History.
    #[test]
    fn a_four_section_note_reads_here_and_in_a_build_that_knows_only_history() {
        let new = r#"{"id":"018f0000-0000-7000-8000-000000000001","name":"Synthetic Patient","mrn":"SYN1","birthDate":"1980-01-01",
            "gender":"unknown","allergies":[],"medications":[],"conditions":[],"visits":[],
            "notes":[{"signedAt":"2026-11-22T06:00:17Z","signer":"Dr. Test","history":"Subjective\nS\n\nObjective\nO",
                "assessment":"A","plan":"P","addenda":[],"sensitiveAddendaNotKept":0,"subjective":"S","objective":"O"}]}"#;
        let chart: Chart = serde_json::from_str(new).unwrap();
        let again: Chart = serde_json::from_str(&serde_json::to_string(&chart).unwrap()).unwrap();
        assert_eq!(again.notes[0].subjective.as_deref(), Some("S"));
        assert_eq!(again.notes[0].objective.as_deref(), Some("O"));

        // SignedNote as it stood before step 14.
        #[derive(Deserialize)]
        #[serde(rename_all = "camelCase")]
        struct EarlierNote {
            history: String,
            assessment: String,
            plan: String,
        }
        #[derive(Deserialize)]
        struct EarlierChart {
            notes: Vec<EarlierNote>,
        }
        let earlier: EarlierChart = serde_json::from_str(new).unwrap();
        assert_eq!(earlier.notes[0].history, "Subjective\nS\n\nObjective\nO");
        assert_eq!(
            (
                earlier.notes[0].assessment.as_str(),
                earlier.notes[0].plan.as_str()
            ),
            ("A", "P")
        );
    }
}
