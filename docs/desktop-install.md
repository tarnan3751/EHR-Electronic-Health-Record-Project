# Installing the desktop app

The desktop app is SwiftChart, identifier `io.github.tarnan3751.swiftchart` (`src/Ehr.Desktop/src-tauri/tauri.conf.json`).
The identifier names the app's data folder and its entries in the Keychain or Credential Manager, so it never changes
once a copy is installed: a copy under another identifier is another app, which starts empty.

## What you get, and what it isn't

SwiftChart is distributed unsigned (the expansion plan's standing decision for step 20; nothing is spent, NFR-16):

- **No Apple code signing or notarization.** The macOS app is *sealed ad hoc* (`codesign` with the identity `-`, set
  as `bundle.macOS.signingIdentity`), which costs nothing and needs no account. Without the seal its signature would be
  invalid, and macOS would call a downloaded copy damaged. The seal is not Apple's: it says nothing about who made the
  app, and macOS still blocks the first launch until you allow it once, below. The seal turns on the hardened runtime,
  which keeps other code out of the app; its one entitlement is the microphone (`Entitlements.plist`).
- **No Windows code-signing certificate.** Windows warns before the first install, below.
- **What a build talks to is fixed when it is built:** the servers in `EHR_BUILD_SERVERS`, or by default the local
  server stack's, `https://ehr.example.com:8443` and `https://ehr-remote.example.com:9443`. The sign-in form names
  them. A build for other servers is a build of its own (see the README's **Running the apps**). Its updates come from
  the same servers.
- **Updates are signed, by their own key:** the updater's key pair, made on the owner's Mac. The public half is built
  into every build (`plugins.updater.pubkey` in `tauri.conf.json`); the private half never leaves the owner's Mac, and
  never reaches CI.

What follows was checked against Apple's and Microsoft's documentation as of November 2026, not yet on a teammate's
computer; the hands-on check (`docs/expansion/20-hands-on-check.md`) records what each one actually shows, and this
page is corrected from it.

## Where the installers come from

Nothing is published: there is no GitHub release. Two ways:

- **From CI, when it keeps them.** Every CI run builds the `.dmg` (macOS, Apple Silicon) and the NSIS setup (Windows,
  x64), and keeps neither. A run started by hand with **upload_installers** keeps both as the run's artifacts for 3
  days: `gh workflow run ci.yml --ref <branch> -f upload_installers=true`, then download `SwiftChart-macOS` or
  `SwiftChart-Windows` from the run's page. Add `-f desktop_version=0.2.1` to build them as another version, to test an
  update to it. The repository is public, so anyone signed in to GitHub can download them
  meanwhile: whether that's allowed is the owner's call.
- **From source**, on the computer it's for, from the repository at the commit you want:
  - **What must be installed:** what the README's **Setup** lists for the desktop app: Rust through rustup (the pinned
    version installs itself), and the Tauri CLI 2.x as it says; CI uses 2.12.0, the version these steps were checked
    with (`cargo install tauri-cli --version 2.12.0 --locked`). On Windows also Visual Studio Build Tools with "Desktop development
    with C++", and Strawberry Perl ahead of Git's Perl on `PATH` (the bundled OpenSSL's build refuses Git's).
  - **macOS**, in Terminal:

    ```
    cd src/Ehr.Desktop/src-tauri
    cargo tauri build -- --locked
    ```

    The installer is `target/release/bundle/dmg/SwiftChart_<version>_aarch64.dmg`.
  - **Windows**, in PowerShell, with a short build folder, since Windows refuses paths over 260 characters and
    OpenSSL's build nests deep:

    ```
    cd src\Ehr.Desktop\src-tauri
    $env:CARGO_TARGET_DIR = "C:\swiftchart-target"
    cargo tauri build -- --locked
    ```

    The installer is `C:\swiftchart-target\release\bundle\nsis\SwiftChart_<version>_x64-setup.exe`.
  - **How long:** the first build compiles SQLCipher and OpenSSL: several minutes on an Apple Silicon Mac, and about
    half an hour on a Windows PC, as in CI; later builds take a minute or two.

## Installing on a Mac

1. Open the `.dmg` and drag **SwiftChart** to **Applications**.
2. Open SwiftChart. macOS refuses, saying it can't check the app for malicious software (Apple could not verify
   "SwiftChart" is free of malware). Choose **Done**, not **Move to Trash**.
3. Open **System Settings > Privacy & Security**, scroll to **Security**, and next to the line about SwiftChart being
   blocked choose **Open Anyway**; confirm with **Open**. macOS may ask for your password.

Control-click (right-click) then **Open** no longer gets past this since macOS 15: only **Open Anyway** does. It's
asked once for the installed copy. If macOS says instead that SwiftChart "is damaged and can't be opened", the copy
isn't the one that was built: get the installer again.

Then, as with every build of SwiftChart (the README, **Running the apps**):

- The Keychain asks whether SwiftChart may use its items: choose **Always Allow**. An ad-hoc seal is different for every
  build, so each new build asks again.
- The first recording asks for the microphone; a new build may ask again.

## Installing on Windows

1. **Smart App Control must not be On.** Look in **Windows Security > App & browser control > Smart App Control**. On,
   it blocks unsigned apps, SwiftChart included, with no way to allow one app; turning it off is a setting for the
   whole PC, and the PC's owner's decision. **Evaluation** and **Off** let SwiftChart run.
2. The browser may warn that the download isn't commonly downloaded. In Edge, open the download's **…** menu, choose
   **Keep**, then **Show more** and **Keep anyway**.
3. Run `SwiftChart_<version>_x64-setup.exe`. SmartScreen says "Windows protected your PC": choose **More info**, then
   **Run anyway**.
4. The setup installs SwiftChart for you alone, with no administrator's password, and adds it to the Start menu.

To remove it: **Settings > Apps > Installed apps > SwiftChart > Uninstall**, which offers to delete its data too.

## Updates

SwiftChart asks before it updates, and never over the lock or a recording (`docs/UC7.md`). **Get it ready** downloads
the update and checks its signature; on a Mac it then installs at once and runs from the next start, and on Windows it
installs at **Restart now**, which shows the installer's progress and starts SwiftChart again. Either way what SwiftChart
keeps stays, since the identifier is the same: the next start opens on the lock, with the sign-in kept, and drafts and
recordings not yet sent are still there to send. On a Mac, as with any new build, the Keychain may ask again (**Always
Allow**), and the microphone may be asked for again.

An installer run over an installed copy, the same way as the first time, does the same: that is also how a copy goes
back to an earlier release if one goes wrong, since the updater itself never takes an older one.

## Making a release (the owner)

On the Mac that holds the updater's private key (`~/.swiftchart-secrets/swiftchart-updater.key`, the expansion plan's
decision log, step 20). Nothing here prints the key.

1. **The macOS update**, built and signed in one go, as version `<version>` (newer than every installed copy), from
   `src/Ehr.Desktop/src-tauri`:

   ```
   TAURI_SIGNING_PRIVATE_KEY="$HOME/.swiftchart-secrets/swiftchart-updater.key" TAURI_SIGNING_PRIVATE_KEY_PASSWORD="" \
     cargo tauri build --config '{"version":"<version>","bundle":{"createUpdaterArtifacts":true}}' -- --locked
   ```

   It makes `target/release/bundle/macos/SwiftChart.app.tar.gz` and its signature, `SwiftChart.app.tar.gz.sig`, bound
   to that version, beside the `.dmg` for new installs.
2. **The Windows update** is its installer, built on Windows as that version (by CI, `-f desktop_version=<version>`, or
   on a PC with `cargo tauri build --config '{"version":"<version>"}' -- --locked` in PowerShell 7), then signed on the
   Mac:

   ```
   TAURI_SIGNING_PRIVATE_KEY_PASSWORD="" cargo tauri signer sign -f "$HOME/.swiftchart-secrets/swiftchart-updater.key" \
     --app-version <version> SwiftChart_<version>_x64-setup.exe
   ```

   which writes `SwiftChart_<version>_x64-setup.exe.sig` beside it. Only the signature leaves the Mac.
3. **On each server**, put the files and `latest.json` in the folder `Ehr:Desktop:UpdatesFolder` names: in the local
   stack, `infra/compose/desktop-updates/`, served with `compose.desktop-updates.yaml` (`infra/compose/README.md`, "Desktop
   updates", has `latest.json`'s form, each signature being the contents of its `.sig` file). Each copy checks within a
   day, or at its next start.
4. **To make every older copy update before it syncs again**, set `Ehr:Desktop:MinimumVersion` to the new version on
   both servers (`EHR_DESKTOP_MINIMUM_VERSION` in the local stack). Builds named EHR can't sync then (above).

If the key is lost, no installed copy can be updated again: each is reinstalled by hand from a build with a new key, and
keeps what it holds. If it leaks, see the decision log's entry for what to do.

## Where SwiftChart keeps things

- **macOS:** the encrypted database in `~/Library/Application Support/io.github.tarnan3751.swiftchart/`; its key and the
  kept sign-in in the login Keychain, as items named `io.github.tarnan3751.swiftchart` (accounts `local-database-key`
  and `server-session`); the web view's own data in `~/Library/WebKit/io.github.tarnan3751.swiftchart/` and
  `~/Library/Caches/io.github.tarnan3751.swiftchart/` (nothing of a patient's: the page keeps nothing there).
- **Windows:** the encrypted database in `%APPDATA%\io.github.tarnan3751.swiftchart\`; its key and the kept sign-in in
  Credential Manager, as Windows credentials named `local-database-key.io.github.tarnan3751.swiftchart` and
  `server-session.io.github.tarnan3751.swiftchart`; the web view's own data in
  `%LOCALAPPDATA%\io.github.tarnan3751.swiftchart\`.

An administrator who revokes a clinician's desktop sign-ins has the app delete all of it the next time it reaches a
server, its key included; drafts and recordings it hadn't sent are lost (`docs/UC7.md`). Signing out keeps it.

## From a build named EHR

Builds before the expansion plan's step 20 were named EHR, with the identifier `com.example.ehr`. A SwiftChart build
doesn't see what they kept: it starts empty, signs in again, and downloads the offline charts again. Drafts and
recordings an old copy hasn't sent stay in its own encrypted database, which only that old build can open. Nothing moves
them across, so send them first, and before any minimum version is set on the servers: a build named EHR says no
version, and a server with a minimum refuses to sync with it, so it couldn't send them then.

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
