//! Chart snapshots downloaded for one clinician, stored in the same encrypted
//! database as their drafts.

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
}

#[derive(Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct SignedNote {
    pub signed_at: String,
    pub signer: String,
    pub history: String,
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
    tx.commit()
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
    }
}
