# Installing the desktop app

The desktop app is SwiftChart, identifier `io.github.tarnan3751.swiftchart` (`src/Ehr.Desktop/src-tauri/tauri.conf.json`).
The identifier names the app's data folder and its entries in the Keychain, Credential Manager or Secret Service, so it
never changes once a copy is installed: a copy under another identifier is another app, which starts empty.

## What you get, and what it isn't

SwiftChart is distributed unsigned (the expansion plan's standing decision for step 20; nothing is spent, NFR-16):

- **No Apple code signing or notarization.** The macOS app is *sealed ad hoc* (`codesign` with the identity `-`, set
  as `bundle.macOS.signingIdentity`), which costs nothing and needs no account. Without the seal its signature would be
  invalid, and macOS would call a downloaded copy damaged. The seal is not Apple's: it says nothing about who made the
  app, and macOS still blocks the first launch until you allow it once, below. The seal turns on the hardened runtime,
  which keeps other code out of the app; its one entitlement is the microphone (`Entitlements.plist`).
- **No Windows code-signing certificate.** Windows warns before the first install, below.
- **Nothing signs the Linux packages.** The `.deb` comes from no package repository, so `apt` never updates it, and the
  AppImage carries no signature.
- **What a build talks to is fixed when it is built:** the servers in `EHR_BUILD_SERVERS`, or by default the local
  server stack's, `https://ehr.example.com:8443` and `https://ehr-remote.example.com:9443`. The sign-in form names
  them. A build for other servers is a build of its own (see [Running the apps](development.md#running-the-apps)).
  Its updates come from the same servers.
- **Updates are signed, by their own key:** the updater's key pair, made on the owner's Mac. The public half is built
  into every build (`plugins.updater.pubkey` in `tauri.conf.json`); the private half never leaves the owner's Mac, and
  never reaches CI.

What follows was checked against Apple's and Microsoft's documentation as of November 2026, not yet on a teammate's
computer; the hands-on check (`docs/expansion/20-hands-on-check.md`) records what each one actually shows, and this
page is corrected from it. The Linux build is built and its tests run in CI, but nobody has opened it in a Linux
desktop session yet: what "Installing on Linux" says of the keyring and the microphone is unverified.

## Where the installers come from

Three ways:

- **From a release**, on the repository's Releases page on GitHub, the first being `v1.0.0`: the `.dmg` (macOS, Apple
  Silicon), the NSIS setup (Windows, x64), the `.deb` and the AppImage (Linux, x86-64), and `SHA256SUMS.txt` with each
  one's SHA-256. `.github/workflows/release.yml` builds them from the tagged commit, on main, as CI builds them below:
  **unsigned test builds for the local stack**, talking only to its servers (`ehr.example.com:8443` and
  `ehr-remote.example.com:9443`), with synthetic data. A release carries no update: nothing in it is signed with the
  updater's key, which never reaches CI ("Making a release", below, is how an update is made). The owner releases by
  pushing a tag `v<version>` on a commit on main whose `Cargo.toml` has that version and whose
  `docs/release-notes/v<version>.md` holds the notes; release.yml refuses anything else.
- **From CI, when it keeps them.** Every CI run builds the `.dmg` (macOS, Apple Silicon), the NSIS setup (Windows,
  x64), and the `.deb` and AppImage (Linux, x86-64), and keeps none of them. A run started by hand with
  **upload_installers** keeps them as the run's artifacts for 7 days: `gh workflow run ci.yml --ref <branch> -f
  upload_installers=true`, then download `SwiftChart-macOS`, `SwiftChart-Windows` or `SwiftChart-Linux` from the run's
  page. Add `-f desktop_version=1.0.1` to build them as another version, to test an
  update to it. The repository is public, so anyone signed in to GitHub can download them meanwhile: an upload is
  publishing (the owner's decision of 2026-11-26), which is why no run does it but one started by hand, and why they
  are kept 7 days and no longer. **What such a run keeps are unsigned test builds for a local stack:** not signed for
  macOS or Windows, talking only to the local stack's servers (`ehr.example.com:8443` and
  `ehr-remote.example.com:9443`), with synthetic data. They are not a release, and no update is signed in CI. A run
  started by hand with **release_dry_run** also rehearses the release from its branch: it builds everything a release
  would, keeps the installers as `SwiftChart-<version>-macOS`, `-Windows` and `-Linux` and the checksums and notes as
  `SwiftChart-<version>-release-notes`, for 7 days, and pushes, tags and releases nothing.
- **From source**, on the computer it's for, from the repository at the commit you want:
  - **What must be installed:** what [Setup](development.md#setup) lists for the desktop app: Rust through rustup (the pinned
    version installs itself), and the Tauri CLI 2.x as it says; CI uses 2.12.0, the version these steps were checked
    with (`cargo install tauri-cli --version 2.12.0 --locked`). On Windows also Visual Studio Build Tools with "Desktop development
    with C++", and Strawberry Perl ahead of Git's Perl on `PATH` (the bundled OpenSSL's build refuses Git's). On
    Debian or Ubuntu, the packages CI installs: `sudo apt install build-essential pkg-config file xdg-utils
    libwebkit2gtk-4.1-dev libdbus-1-dev librsvg2-dev`.
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
  - **Linux**, in a terminal, as on macOS. The packages are `target/release/bundle/deb/SwiftChart_<version>_amd64.deb`
    and `target/release/bundle/appimage/SwiftChart_<version>_amd64.AppImage` (`arm64` and `aarch64` in their names on
    an Arm computer). The AppImage's bundler downloads its tools from GitHub on the first build.
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

Then, as with every build of SwiftChart ([Running the apps](development.md#running-the-apps)):

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

## Installing on Linux

The Linux build is for x86-64 computers, built on Ubuntu 24.04: a `.deb` for Debian and Ubuntu, and an AppImage for
other distributions. A distribution older than Ubuntu 24.04 may lack the system libraries it was built against. It
needs a desktop session with:

- **WebKitGTK 4.1 and GTK 3**, which draw the window. The `.deb` depends on them, so `apt` installs them with it; the
  AppImage carries its own copies.
- **A Secret Service provider**, running and unlocked: GNOME Keyring (on GNOME, Ubuntu's default, and most other
  desktops) or KDE Wallet. SwiftChart keeps its database key and the kept sign-in there, in the default collection
  (usually the login keyring). With none running, SwiftChart can't open its database and says so ("credential store:
  …"); it writes the key nowhere else.

Then:

- **The `.deb`:** `sudo apt install ./SwiftChart_<version>_amd64.deb`, then open SwiftChart from the applications menu.
  To remove it: `sudo apt remove swift-chart`, which leaves what it keeps (below).
- **The AppImage:** `chmod +x SwiftChart_<version>_amd64.AppImage`, then run it. AppImages need FUSE 2, which Ubuntu
  22.04 and later don't install: `sudo apt install libfuse2t64` on Ubuntu 24.04 (`libfuse2` on 22.04), or run it with
  `--appimage-extract-and-run`. Nothing installs it: it's the one file.

**Unverified on Linux** (nobody has run it in a desktop session yet; the hands-on check is still owed):

- The keyring: GNOME Keyring may ask to unlock the login keyring, or to make one with a password if the session has
  none, the first time SwiftChart stores its key.
- **Recording**, a dictation or a visit: WebKitGTK gives a page the microphone only when the app turns its media
  capture on and grants the request. SwiftChart does both for its own page and for the microphone alone, and refuses
  every other request (`capture.rs`), so there is no prompt. Whether a recording is then made has not been tried: it
  also needs the GStreamer plugins WebKitGTK records with and a working PipeWire or PulseAudio session.
- Updates: no server offers one to a Linux copy (`latest.json` has no Linux entry, and "Making a release" signs none),
  so a newer version is installed by hand over the old one, the same way as the first time.

## Updates

SwiftChart asks before it updates, and never over the lock or a recording (`docs/UC7.md`). **Download update** downloads
the update and checks its signature; on a Mac it then installs at once and runs from the next start, and on Windows it
installs at **Restart now**, which shows the installer's progress and starts SwiftChart again. Either way what SwiftChart
keeps stays, since the identifier is the same: the next start opens on the lock, with the sign-in kept, and drafts and
recordings not yet sent are still there to send. On a Mac, as with any new build, the Keychain may ask again (**Always
Allow**), and the microphone may be asked for again.

An installer run over an installed copy, the same way as the first time, does the same: that is also how a copy goes
back to an earlier release if one goes wrong, since the updater itself never takes an older one.

## Making a release (the owner)

This is the update installed copies take, signed on the owner's Mac. A GitHub release (above) is for new installs, and
signs nothing.

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
- **Linux:** the encrypted database in `~/.local/share/io.github.tarnan3751.swiftchart/`, beside the web view's own
  data; its key and the kept sign-in in the Secret Service's default collection, as items with the attributes
  `service` `io.github.tarnan3751.swiftchart` and `username` `local-database-key` or `server-session` (labelled
  `keyring:local-database-key@io.github.tarnan3751.swiftchart` and so on, as Seahorse, GNOME's Passwords and Keys,
  shows them).

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
