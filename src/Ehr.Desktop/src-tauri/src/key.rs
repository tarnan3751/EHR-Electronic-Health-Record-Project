//! The local database's key: 32 random bytes from the OS, made on first launch and kept in the OS credential store
//! (the login Keychain on macOS, Credential Manager on Windows). It never goes to disk beside the database.

use keyring_core::{Entry, Error};

const SERVICE: &str = "com.example.ehr";
const USER: &str = "local-database-key";

pub struct Key {
    pub bytes: [u8; 32],
    /// Made just now. A database file already on disk was made with another key, and nothing can open it.
    pub new: bool,
}

/// Reads the key from the credential store, or makes and saves one.
pub fn load() -> Result<Key, String> {
    use_platform_store().map_err(|error| format!("credential store: {error}"))?;
    let entry = Entry::new(SERVICE, USER).map_err(|error| format!("credential store: {error}"))?;

    match entry.get_password() {
        Ok(hex) => {
            let mut bytes = [0; 32];
            hex::decode_to_slice(hex.trim(), &mut bytes).map_err(|_| {
                format!("the {SERVICE} entry in the credential store isn't a database key")
            })?;
            Ok(Key { bytes, new: false })
        }
        Err(Error::NoEntry) => {
            let mut bytes = [0; 32];
            getrandom::fill(&mut bytes)
                .map_err(|error| format!("random number source: {error}"))?;
            entry
                .set_password(&hex::encode(bytes))
                .map_err(|error| format!("credential store: {error}"))?;
            Ok(Key { bytes, new: true })
        }
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
