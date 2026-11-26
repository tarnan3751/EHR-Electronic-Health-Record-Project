# Installing the desktop app

The desktop app is SwiftChart, identifier `io.github.tarnan3751.swiftchart` (`src/Ehr.Desktop/src-tauri/tauri.conf.json`).
The identifier names the app's data folder and its entries in the Keychain or Credential Manager, so it never changes
once a copy is installed: a copy under another identifier is another app, which starts empty.

## From a build named EHR

Builds before the expansion plan's step 20 were named EHR, with the identifier `com.example.ehr`. A SwiftChart build
doesn't see what they kept: it starts empty, signs in again, and downloads the offline charts again. Drafts and
recordings an old copy hasn't sent stay in its own encrypted database, which only that old build can open. Nothing moves
them across, so send them first:

1. Open the old build, sign in, and wait until it says nothing is waiting to be sent: no changes, drafts or recordings.
2. Sign out, and quit it.
3. Remove it and what it kept. Anything it hadn't sent is lost from here on.
   - macOS, in Terminal:

     ```
     rm -rf ~/Library/Application\ Support/com.example.ehr
     security delete-generic-password -s com.example.ehr -a local-database-key
     security delete-generic-password -s com.example.ehr -a server-session
     ```

     and move the old `EHR.app`, if there is one, to the Trash. A build run with `cargo tauri dev` has no app to remove.
   - Windows, in PowerShell:

     ```
     Remove-Item -Recurse "$env:APPDATA\com.example.ehr"
     cmdkey /delete:local-database-key.com.example.ehr
     cmdkey /delete:server-session.com.example.ehr
     ```

     and uninstall EHR in Settings > Apps, if it was installed.

`security` and `cmdkey` say so when an entry isn't there; that's fine.
