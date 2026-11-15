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
    tx.execute("DELETE FROM offline_charts WHERE clinician_id = ?1", [&owner])?;
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
