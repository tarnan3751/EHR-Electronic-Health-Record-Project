//! The remote wipe (the expansion plan's step 20, proposed FR-69). When a server the sign-in was made with refuses its
//! token saying an administrator revoked it ("Ehr-Sign-In: revoked", sync.rs; nothing else leads here), main.rs deletes
//! everything the app keeps on this computer, in this order:
//!
//! 1. the local database, closed first, since Windows can't delete an open file, and SQLite's files beside it: the
//!    offline charts, drafts and recordings, phrases and the outbox, and the lock's record with its hash of the
//!    password (here);
//! 2. its key, in the Keychain or Credential Manager (here);
//! 3. the web view's own data, and the page, which is reloaded, so nothing it showed or held stays in memory (main.rs);
//! 4. the kept sign-in, last (main.rs).
//!
//! Until the last step the kept sign-in is there to present again, so a start after a crash at any point is told again
//! and finishes: before step 1 is done it opens the database as usual, starts locked and is told at its first round;
//! after it, it finds no database, makes an empty one, and with no lock's record forgets the sign-in itself
//! (main.rs, starting_lock). Nothing is readable at any point: the database goes while its key still exists, and the
//! key after it. Drafts and recordings not yet sent are lost, by design, as the administrator was told.

use std::io::ErrorKind;
use std::path::Path;

use keyring_core::Entry;

use crate::key;
use crate::store::Store;

/// Steps 1 and 2: `store` is closed, the files at `database` and beside it are deleted, then `key`. Stops at the first
/// thing it can't delete and says what, so the kept sign-in stays for the next start. Safe to repeat: what's gone
/// already is skipped.
pub fn delete(
    store: Option<&mut Store>,
    database: Option<&Path>,
    key: &Entry,
) -> Result<(), String> {
    if let Some(store) = store {
        store
            .close()
            .map_err(|error| format!("closing the local database: {error}"))?;
    }
    if let Some(database) = database {
        for suffix in ["", "-journal", "-wal", "-shm"] {
            let mut name = database.as_os_str().to_owned();
            name.push(suffix);
            match std::fs::remove_file(&name) {
                Ok(()) => {}
                Err(error) if error.kind() == ErrorKind::NotFound => {}
                Err(error) => return Err(format!("{}: {error}", Path::new(&name).display())),
            }
        }
    }
    key::delete(key)
}

#[cfg(test)]
mod tests {
    use keyring_core::api::CredentialStoreApi;
    use keyring_core::{Error, mock};
    use uuid::Uuid;

    use super::*;

    /// TC-UC7-12 (proposed FR-69): the wipe deletes the database's file and SQLite's beside it, after closing it, and
    /// then its key; a second run, as after a crash, finds nothing left and is fine.
    #[test]
    fn the_wipe_deletes_the_database_and_then_its_key() {
        let folder = std::env::temp_dir().join(format!("ehr-wipe-test-{}", Uuid::now_v7()));
        std::fs::create_dir_all(&folder).unwrap();
        let database = folder.join("ehr.db");
        let mut store = Store::open(&database, &[7; 32]).unwrap();
        store.add(".kept", "Kept on this computer").unwrap();
        std::fs::write(folder.join("ehr.db-journal"), b"left by a crash").unwrap();
        let credentials = mock::Store::new().unwrap();
        let key = credentials
            .build(key::SERVICE, "local-database-key", None)
            .unwrap();
        key.set_password(&"07".repeat(32)).unwrap();

        delete(Some(&mut store), Some(&database), &key).unwrap();

        assert!(!database.exists());
        assert!(!folder.join("ehr.db-journal").exists());
        assert!(matches!(key.get_password(), Err(Error::NoEntry)));
        assert!(store.phrases().is_err(), "nothing is read after");
        delete(None, Some(&database), &key).unwrap();
        std::fs::remove_dir(&folder).unwrap();
    }
}
