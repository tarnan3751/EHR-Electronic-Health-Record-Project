//! What the app keeps in the OS credential store (the login Keychain on macOS, Credential Manager on Windows): the
//! local database's key, 32 random bytes from the OS made on first launch, and the session from the last sign-in
//! (sync.rs). Neither goes to disk beside the database. Both are kept under the app's identifier, as its data folder is
//! (tauri.conf.json): renaming it starts the app empty (the expansion plan's step 20).

use keyring_core::{Entry, Error};

use crate::api::Session;

/// The app's identifier (tauri.conf.json), which names its entries in the credential store.
pub const SERVICE: &str = "io.github.tarnan3751.swiftchart";
const USER: &str = "local-database-key";
const SESSION_USER: &str = "server-session";

/// Reads the key from the credential store. Only a new database can start with a new key.
pub fn load(database_exists: bool) -> Result<[u8; 32], String> {
    use_platform_store().map_err(|error| format!("credential store: {error}"))?;
    let entry = Entry::new(SERVICE, USER).map_err(|error| format!("credential store: {error}"))?;

    match entry.get_password() {
        Ok(hex) => {
            let mut bytes = [0; 32];
            hex::decode_to_slice(hex.trim(), &mut bytes).map_err(|_| {
                format!("the {SERVICE} entry in the credential store isn't a database key")
            })?;
            Ok(bytes)
        }
        Err(Error::NoEntry) if database_exists => {
            Err("The local database key is missing. The database was kept.".to_owned())
        }
        Err(Error::NoEntry) => {
            let mut bytes = [0; 32];
            getrandom::fill(&mut bytes)
                .map_err(|error| format!("random number source: {error}"))?;
            entry
                .set_password(&hex::encode(bytes))
                .map_err(|error| format!("credential store: {error}"))?;
            Ok(bytes)
        }
        Err(error) => Err(format!("credential store: {error}")),
    }
}

/// The entry that keeps the session, in the store load() chose.
pub fn session_entry() -> Result<Entry, String> {
    Entry::new(SERVICE, SESSION_USER).map_err(|error| format!("credential store: {error}"))
}

/// The session kept in `entry`, or none: signed out, or an entry this app can't read, which the next sign-in replaces.
pub fn read_session(entry: &Entry) -> Option<Session> {
    serde_json::from_str(&entry.get_password().ok()?).ok()
}

pub fn save_session(entry: &Entry, session: &Session) -> Result<(), String> {
    let json = serde_json::to_string(session).expect("a Session always serializes");
    entry
        .set_password(&json)
        .map_err(|error| format!("credential store: {error}"))
}

pub fn forget_session(entry: &Entry) -> Result<(), String> {
    match entry.delete_credential() {
        Ok(()) | Err(Error::NoEntry) => Ok(()),
        Err(error) => Err(format!("credential store: {error}")),
    }
}

#[cfg(target_os = "macos")]
fn use_platform_store() -> keyring_core::Result<()> {
    keyring_core::set_default_store(apple_native_keyring_store::keychain::Store::new()?);
    Ok(())
}

#[cfg(windows)]
fn use_platform_store() -> keyring_core::Result<()> {
    keyring_core::set_default_store(windows_native_keyring_store::Store::new()?);
    Ok(())
}

/// Linux is for development only: the key is kept in memory, so main.rs keeps the database in memory too.
#[cfg(not(any(target_os = "macos", windows)))]
fn use_platform_store() -> keyring_core::Result<()> {
    keyring_core::set_default_store(keyring_core::mock::Store::new()?);
    Ok(())
}

#[cfg(test)]
mod tests {
    /// The credential store's entries are named by the app's identifier, as its data folder is: change both together.
    #[test]
    fn the_entries_are_named_by_the_apps_identifier() {
        let config: serde_json::Value =
            serde_json::from_str(include_str!("../tauri.conf.json")).unwrap();
        assert_eq!(config["identifier"], super::SERVICE);
    }
}
