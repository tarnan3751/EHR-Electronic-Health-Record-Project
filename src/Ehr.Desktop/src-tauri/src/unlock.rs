//! What opens the window's lock (the expansion plan's step 19a, Tahseen's 10a) when no server can check the password: a
//! slow hash of it, Argon2id, kept in the encrypted store beside the offline charts, never the password itself. Written
//! at each sign-in with both factors, cleared when the server ends the session, deleted at sign-out. Kept with it: the
//! clinician's email, so the lock's full sign-in is for the same account; when that sign-in was made, by this computer's
//! clock, for its 12 hours; whether the server has said those 12 hours are over; and the wrong passwords typed here in a
//! row: after 5 the lock waits 15 minutes, after 10 it takes a full sign-in. Nothing is ever deleted for a wrong one.
//!
//! The hash adds nothing against someone who can already read this store: they can read the charts in it directly. What
//! it adds is a target for guessing the password away from the app, at Argon2id's cost per guess. The lock is against
//! someone at an unattended computer; it isn't encryption.

use argon2::{Algorithm, Argon2, Params, PasswordHasher, PasswordVerifier, Version};
use rusqlite::{Connection, OptionalExtension, Result, params};
use uuid::Uuid;

/// Argon2id with 64 MiB, 3 passes and one lane: about a tenth of a second on an Apple M5, and under a second on the
/// slower computers the team has (TC-UC7-11 times it on CI's macOS and Windows runners). A hash keeps its own
/// parameters, so raising them only changes the hashes written after.
const MEMORY_KIB: u32 = 64 * 1024;
const PASSES: u32 = 3;
const LANES: u32 = 1;

/// Wrong passwords in a row, checked here, before the lock waits.
pub const WRONG_BEFORE_WAIT: u32 = 5;
/// How long it waits, as the server's lockout does (AccountServiceCollectionExtensions).
pub const WAIT_SECONDS: i64 = 15 * 60;
/// Wrong passwords in a row, checked here, before only a full sign-in opens the lock.
pub const WRONG_BEFORE_SIGN_IN: u32 = 10;

/// One row at most: the clinician signed in on this computer. Times are seconds since 1970 by this computer's clock.
const SCHEMA: &str = "
    CREATE TABLE IF NOT EXISTS desktop_unlock (
        clinician_id TEXT PRIMARY KEY,
        email TEXT NOT NULL,
        verifier TEXT,
        signed_in_at INTEGER NOT NULL,
        sign_in_ended INTEGER NOT NULL DEFAULT 0,
        wrong INTEGER NOT NULL DEFAULT 0,
        wait_until INTEGER
    );
";

/// The lock's record of the clinician signed in here.
#[derive(Debug, PartialEq)]
pub struct Account {
    pub clinician_id: Uuid,
    pub email: String,
    /// None once the server has ended the session: only a full sign-in opens the lock then.
    pub verifier: Option<String>,
    pub signed_in_at: i64,
    /// The server has said the sign-in's 12 hours are over (sync.rs).
    pub sign_in_ended: bool,
    pub wrong: u32,
    pub wait_until: Option<i64>,
}

/// Whether a password typed now may be checked against the hash.
#[derive(Debug, PartialEq)]
pub enum Allowed<'a> {
    Check(&'a str),
    /// Too many wrong ones: not before then.
    WaitUntil(i64),
    /// Only a full sign-in opens the lock: too many wrong ones, or the session is over.
    SignIn,
}

impl Account {
    pub fn may_check(&self, now: i64) -> Allowed<'_> {
        match &self.verifier {
            Some(_) if self.wrong >= WRONG_BEFORE_SIGN_IN => Allowed::SignIn,
            Some(_) if self.wait_until.is_some_and(|until| until > now) => {
                Allowed::WaitUntil(self.wait_until.unwrap_or(now))
            }
            Some(verifier) => Allowed::Check(verifier),
            None => Allowed::SignIn,
        }
    }
}

pub fn initialize(db: &Connection) -> Result<()> {
    db.execute_batch(SCHEMA)
}

/// The password's hash, with a new random salt, as a PHC string ("$argon2id$v=19$m=65536,t=3,p=1$…").
pub fn hash(password: &str) -> String {
    let mut salt = [0; 16];
    getrandom::fill(&mut salt).expect("the OS's random number source works");
    let params = Params::new(MEMORY_KIB, PASSES, LANES, None).expect("valid Argon2 parameters");
    Argon2::new(Algorithm::Argon2id, Version::V0x13, params)
        .hash_password_with_salt(password.as_bytes(), &salt)
        .expect("Argon2id hashes any password")
        .to_string()
}

/// Whether `password` is the one `verifier` was made from, with the verifier's own parameters.
pub fn verify(verifier: &str, password: &str) -> bool {
    Argon2::default()
        .verify_password(password.as_bytes(), verifier)
        .is_ok()
}

/// After a sign-in with both factors: this clinician's record, and nobody else's, with no wrong passwords counted.
pub fn remember(
    db: &Connection,
    clinician_id: Uuid,
    email: &str,
    verifier: &str,
    signed_in_at: i64,
) -> Result<()> {
    db.execute_batch("DELETE FROM desktop_unlock")?;
    db.execute(
        "INSERT INTO desktop_unlock (clinician_id, email, verifier, signed_in_at) VALUES (?1, ?2, ?3, ?4)",
        params![clinician_id.to_string(), email, verifier, signed_in_at],
    )?;
    Ok(())
}

pub fn account(db: &Connection) -> Result<Option<Account>> {
    db.query_row(
        "SELECT clinician_id, email, verifier, signed_in_at, sign_in_ended, wrong, wait_until FROM desktop_unlock",
        [],
        |row| {
            let id: String = row.get(0)?;
            Ok(Account {
                clinician_id: id.parse().map_err(|error| {
                    rusqlite::Error::FromSqlConversionFailure(
                        0,
                        rusqlite::types::Type::Text,
                        Box::new(error),
                    )
                })?,
                email: row.get(1)?,
                verifier: row.get(2)?,
                signed_in_at: row.get(3)?,
                sign_in_ended: row.get(4)?,
                wrong: row.get(5)?,
                wait_until: row.get(6)?,
            })
        },
    )
    .optional()
}

/// A wrong password typed here: counted, and at the 5th the wait begins. The record as it is now.
pub fn wrong(db: &Connection, now: i64) -> Result<Option<Account>> {
    db.execute(
        "UPDATE desktop_unlock SET wrong = wrong + 1,
            wait_until = CASE WHEN wrong + 1 = ?1 THEN ?2 ELSE wait_until END",
        params![WRONG_BEFORE_WAIT, now + WAIT_SECONDS],
    )?;
    account(db)
}

/// The right password, here or at the server: the count starts again.
pub fn right(db: &Connection) -> Result<()> {
    db.execute_batch("UPDATE desktop_unlock SET wrong = 0, wait_until = NULL")
}

/// The server has said the sign-in's 12 hours are over: kept, so the app asks for a full sign-in after a restart too.
pub fn sign_in_ended(db: &Connection) -> Result<()> {
    db.execute_batch("UPDATE desktop_unlock SET sign_in_ended = 1")
}

/// The server has ended the session: the hash goes, and only a full sign-in, for the same email, opens the lock.
pub fn session_over(db: &Connection) -> Result<()> {
    db.execute_batch("UPDATE desktop_unlock SET verifier = NULL")
}

/// Signed out: nothing of the lock's is kept.
pub fn forget(db: &Connection) -> Result<()> {
    db.execute_batch("DELETE FROM desktop_unlock")
}

#[cfg(test)]
mod tests {
    use std::time::Instant;

    use super::*;

    const PASSWORD: &str = "a synthetic password";
    const NOW: i64 = 1_790_000_000;

    fn signed_in() -> (Connection, Uuid) {
        let db = Connection::open_in_memory().unwrap();
        initialize(&db).unwrap();
        let clinician = Uuid::now_v7();
        remember(
            &db,
            clinician,
            "clinician@example.com",
            &hash(PASSWORD),
            NOW,
        )
        .unwrap();
        (db, clinician)
    }

    fn may_check(db: &Connection, now: i64) -> Allowed<'static> {
        match account(db).unwrap().unwrap().may_check(now) {
            Allowed::Check(_) => Allowed::Check(""),
            Allowed::WaitUntil(until) => Allowed::WaitUntil(until),
            Allowed::SignIn => Allowed::SignIn,
        }
    }

    /// TC-UC7-08 (proposed FR-68, NFR-06): what the lock keeps is a salted Argon2id hash with its parameters, never the
    /// password, and it tells the right password from a wrong one.
    #[test]
    fn the_verifier_is_a_slow_salted_hash_never_the_password() {
        let first = hash(PASSWORD);
        let second = hash(PASSWORD);

        assert!(
            first.starts_with("$argon2id$v=19$m=65536,t=3,p=1$"),
            "{first}"
        );
        assert!(!first.contains(PASSWORD));
        assert_ne!(first, second, "a new salt each time");
        assert!(verify(&first, PASSWORD));
        assert!(verify(&second, PASSWORD));
        assert!(!verify(&first, "a synthetic passworD"));
        assert!(!verify("not a hash", PASSWORD));
    }

    /// TC-UC7-08: 5 wrong passwords in a row and the lock waits 15 minutes; 5 more and only a full sign-in opens it.
    /// A right one starts the count again, and nothing is deleted.
    #[test]
    fn five_wrong_passwords_wait_15_minutes_and_ten_take_a_full_sign_in() {
        let (db, clinician) = signed_in();
        for _ in 0..4 {
            wrong(&db, NOW).unwrap();
        }
        assert_eq!(may_check(&db, NOW), Allowed::Check(""));
        right(&db).unwrap();
        assert_eq!(account(&db).unwrap().unwrap().wrong, 0);

        for _ in 0..5 {
            wrong(&db, NOW).unwrap();
        }
        assert_eq!(may_check(&db, NOW), Allowed::WaitUntil(NOW + WAIT_SECONDS));
        assert_eq!(
            may_check(&db, NOW + WAIT_SECONDS - 1),
            Allowed::WaitUntil(NOW + WAIT_SECONDS)
        );
        let later = NOW + WAIT_SECONDS;
        assert_eq!(may_check(&db, later), Allowed::Check(""));

        for _ in 0..4 {
            wrong(&db, later).unwrap();
        }
        assert_eq!(may_check(&db, later), Allowed::Check(""));
        let kept = wrong(&db, later).unwrap().unwrap();
        assert_eq!(kept.wrong, WRONG_BEFORE_SIGN_IN);
        assert_eq!(kept.may_check(later + 365 * 24 * 3600), Allowed::SignIn);
        assert_eq!(kept.clinician_id, clinician);
        assert!(kept.verifier.is_some(), "nothing is deleted");

        remember(
            &db,
            clinician,
            "clinician@example.com",
            &hash(PASSWORD),
            later,
        )
        .unwrap();
        assert_eq!(may_check(&db, later), Allowed::Check(""));
    }

    /// TC-UC7-08: the count and the wait are kept in the database, so quitting the app doesn't reset them; the server's
    /// end of the session leaves only the email; signing out leaves nothing.
    #[test]
    fn the_record_survives_reopening_and_goes_at_sign_out() {
        let path =
            std::env::temp_dir().join(format!("ehr-unlock-test-{}.db", Uuid::now_v7().simple()));
        {
            let db = Connection::open(&path).unwrap();
            initialize(&db).unwrap();
            remember(
                &db,
                Uuid::now_v7(),
                "clinician@example.com",
                &hash(PASSWORD),
                NOW,
            )
            .unwrap();
            for _ in 0..5 {
                wrong(&db, NOW).unwrap();
            }
            sign_in_ended(&db).unwrap();
        }

        let db = Connection::open(&path).unwrap();
        initialize(&db).unwrap();
        let reopened = account(&db).unwrap().unwrap();
        assert_eq!(
            (reopened.wrong, reopened.wait_until),
            (5, Some(NOW + WAIT_SECONDS))
        );
        assert!(reopened.sign_in_ended);

        session_over(&db).unwrap();
        let over = account(&db).unwrap().unwrap();
        assert_eq!(over.verifier, None);
        assert_eq!(over.email, "clinician@example.com");
        assert_eq!(over.may_check(NOW + WAIT_SECONDS), Allowed::SignIn);

        forget(&db).unwrap();
        assert_eq!(account(&db).unwrap(), None);
        drop(db);
        std::fs::remove_file(&path).unwrap();
    }

    /// TC-UC7-11 (proposed FR-68, NFR-02): making the hash, which each unlock away from the server repeats, takes under a
    /// second. CI runs this on its macOS (Apple Silicon) and Windows (x64) runners and shows the time.
    #[test]
    fn hashing_the_password_takes_under_a_second() {
        let started = Instant::now();
        let verifier = hash(PASSWORD);
        let made = started.elapsed();
        let started = Instant::now();
        assert!(verify(&verifier, PASSWORD));
        let checked = started.elapsed();

        println!("TC-UC7-11: Argon2id (64 MiB, 3 passes) made in {made:?}, checked in {checked:?}");
        assert!(made.as_secs_f64() < 1.0, "{made:?}");
        assert!(checked.as_secs_f64() < 1.0, "{checked:?}");
    }
}
