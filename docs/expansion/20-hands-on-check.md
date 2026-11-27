# Step 20: the hands-on check

What the tests can't show of step 20 (desktop distribution): what macOS and Windows do with SwiftChart unsigned, the
app installed rather than run from source, its fixed servers, an update end to end with the real key, and the remote
wipe in the real window. Two parts: the Mac (Tahseen's) and a Windows PC (a teammate's, arranged by Tahseen a day
ahead), which also runs step 19a's Windows section (`docs/expansion/19a-hands-on-check.md`). The install notes it checks
are `docs/desktop-install.md`.

Write down, for each step, whether you saw what it says, the exact words of any dialog the operating system shows, and
anything that showed that shouldn't have. `docs/desktop-install.md` is corrected from what you write.

## The Mac

**Before you start, know this:** it needs your local server stack on this branch, whose volumes are yours to reset
(`docker compose --profile "*" down --volumes`, then `up` again and the accounts set up again, as for step 19a's check).
And it installs SwiftChart for real: it writes a sign-in into your login Keychain and a database into
`~/Library/Application Support/io.github.tarnan3751.swiftchart/`, both under the new identifier, apart from anything an
older build kept. Step M8 deletes them again; cleaning up after older builds is optional, and yours. It uses the
updater's private key in `~/.swiftchart-secrets/` once, to sign the update in M4; nothing prints it. About 40 minutes.

**Start the stack with the update server** (`infra/compose/README.md`, "Desktop updates"), from `infra/compose`:

```
mkdir -p desktop-updates
docker compose -f compose.yaml -f compose.desktop-updates.yaml up -d --build --wait
```

**Get the installer**, version 0.2.0, either way:

- **From CI**, if a run was started by hand to keep it (`docs/desktop-install.md`): download `SwiftChart-macOS` from the
  run's page in Safari and open the zip. Downloaded so, it's marked as from the internet, as a teammate's would be.
- **Or build it**, from `src/Ehr.Desktop/src-tauri`: `cargo tauri build -- --locked`, then mark it as downloaded, so
  macOS checks it as it would a teammate's:

  ```
  xattr -w com.apple.quarantine "0081;$(printf '%x' "$(date +%s)");Safari;" target/release/bundle/dmg/SwiftChart_0.2.0_aarch64.dmg
  ```

  (With `CARGO_TARGET_DIR` set, the `.dmg` is under that folder's `release/bundle/dmg/` instead.)

**M1. Install and the first launch.** Open the `.dmg`, drag SwiftChart to Applications, and open it from Applications.
Note the dialog's words and buttons; choose **Done**. Then System Settings > Privacy & Security: note what it says about
SwiftChart, choose **Open Anyway**, then **Open**; note whether it asks for your password. SwiftChart opens on "Sign in
to sync", with "SwiftChart 0.2.0." in the status line. If macOS said instead that it "is damaged", stop and note it: the
seal is broken.

**M2. Where the password goes.** Under the form: "Signs in to: ehr.example.com:8443, ehr-remote.example.com:9443". Sign
in as the clinician; note any Keychain prompt (choose **Always Allow**). Choose **Download my charts**, open a chart, and
**Record audio**: macOS asks for the microphone once (allow it); it records and says "Recording saved."
If it can't record, note the message: the hardened runtime is the suspect.

**M3. Its servers are its own.** Quit SwiftChart, and start it naming another server:

```
EHR_SYNC_SERVERS=https://127.0.0.1:9 /Applications/SwiftChart.app/Contents/MacOS/ehr-desktop
```

It opens on the lock, for your password, as after any start; it doesn't say it was started with other servers, since a
release build reads no variable. Unlock: the status says Online. Quit it (Cmd+Q).

**M4. An update.** Build 0.2.1, signed with the updater's key, from `src/Ehr.Desktop/src-tauri`:

```
TAURI_SIGNING_PRIVATE_KEY="$HOME/.swiftchart-secrets/swiftchart-updater.key" TAURI_SIGNING_PRIVATE_KEY_PASSWORD="" \
  cargo tauri build --config '{"version":"0.2.1","bundle":{"createUpdaterArtifacts":true}}' -- --locked
```

Copy `target/release/bundle/macos/SwiftChart.app.tar.gz` into `infra/compose/desktop-updates/`, and write
`infra/compose/desktop-updates/latest.json` as `infra/compose/README.md` shows, with `"version": "0.2.1"` and only the
`darwin-aarch64` entry, its signature the contents of `SwiftChart.app.tar.gz.sig`. Then:

- Open SwiftChart (the 0.2.0 in Applications) and unlock. Within a few seconds: "Update available. SwiftChart 0.2.1.",
  with **Download update** and **Not now**. Choose **Not now**: it goes. Quit and open it again, and unlock: it's back.
- Lock it (**Lock now** from the idle warning, or wait): the offer isn't over the lock. Unlock.
- **Download update**: "Downloading. …", then "Ready to install. SwiftChart 0.2.1. …" with **Restart now**.
  **Restart now**: SwiftChart opens again on the lock; unlock: "SwiftChart 0.2.1." in the status line, your charts
  and drafts as they were. Note any Gatekeeper, Keychain or microphone prompt after the update.

**M5. What it won't take.** Edit `latest.json`'s `version` to `0.2.2`, leaving the 0.2.1 file and its signature: a
release that says it's newer than what was signed. Quit, open and unlock SwiftChart: "Update available. SwiftChart
0.2.2."; **Download update**: "Not installed. SwiftChart 0.2.2 couldn't be downloaded, verified or installed (…)", and the status line
still says 0.2.1. Then, from `infra/compose`, restart the two apps with a minimum above it:

```
EHR_DESKTOP_MINIMUM_VERSION=0.2.2 docker compose -f compose.yaml -f compose.desktop-updates.yaml up -d --wait
```

Within 10 seconds the sync problem reads "Update needed: the clinic's server syncs only with SwiftChart 0.2.2 or later.
…"; open a chart: it opens; lock and unlock: it works. Delete `latest.json` and the file, and run the same command without
the variable.

**M6. Disabled, then revoked.** From `infra/compose`, `docker compose stop onprem-app cloud-app`. In SwiftChart open a
chart, type a sentence under **New draft** and **Save draft**: it waits on this computer. Quit SwiftChart (Cmd+Q), so
nothing is sent when the servers come back. `docker compose start onprem-app cloud-app`, and once they're up, in
Safari as the administrator at https://ehr.example.com:8443: **Staff**, **Disable** the clinician; the page says that
what SwiftChart keeps on their computers stays there until their desktop sign-ins are revoked, and to revoke them now.
Open SwiftChart: it opens on the lock, and within a few seconds the lock reads "Signed out. The server ended this
sign-in. …", asking for the password and the code; nothing of the chart. Don't sign out (that would drop what the
revocation needs to reach this computer); quit it. In Safari, the clinician's **Desktop sign-ins**: note the count and
both paragraphs; choose **Revoke and delete**: the staff page says their desktop sign-ins are revoked. (This is the
order the final review found skipped the wipe; revoking first works the same.)

**M7. The wipe.** Open SwiftChart. It opens on the lock; within a few seconds the window shows only "Deleted. An
administrator revoked this computer's sign-in, so SwiftChart deleted the offline charts and everything else it kept
here. …", with **Quit
SwiftChart** and the focus on it; nothing of the chart, or of your draft, anywhere. Then, in Terminal:

```
ls ~/Library/Application\ Support/io.github.tarnan3751.swiftchart/
security find-generic-password -s io.github.tarnan3751.swiftchart -a local-database-key
security find-generic-password -s io.github.tarnan3751.swiftchart -a server-session
```

No `ehr.db`, and each `security` says the item could not be found. In Safari, the audit page lists `desktop_revoked` by
the administrator. The draft saved in M6 never reached the server: that is the loss the page warned of. Also look,
since the final review couldn't: `ls ~/Library/Saved\ Application\ State/ | grep -i swiftchart` (macOS may keep a
picture of the window there: note whether anything is, and whether it shows a chart).

**M8. After.** Choose **Quit SwiftChart**, and open it again: "Sign in", empty. In Safari, **Enable** the
clinician, then sign in as them in SwiftChart: it works. To leave nothing behind, quit, drag SwiftChart to the Trash,
run the three lines in `docs/desktop-install.md` under "From a build named EHR", with `io.github.tarnan3751.swiftchart`
in place of `com.example.ehr`, and remove `~/Library/WebKit/io.github.tarnan3751.swiftchart` and
`~/Library/Caches/io.github.tarnan3751.swiftchart`. Cleaning up after older builds (`com.example.ehr`) is optional.

## Windows (a teammate's PC)

**What the PC needs**, all of it before the day:

- The repository at the step's head, and Docker Desktop with the WSL2 backend.
- The local server stack's one-time setup from `infra/compose/README.md`: mkcert's root trusted on the PC
  (`mkcert -install`) and the stack's certificate made with it; the hosts line
  `127.0.0.1 ehr.example.com ehr-remote.example.com portal.example.com`, added as administrator and removed after;
  `infra/compose/.env` made from `.env.example`. Then the accounts, as the README there says.
- **Smart App Control not On:** Windows Security > App & browser control > Smart App Control says **Off** or
  **Evaluation**. On, Windows blocks SwiftChart with no way past it for one app; turning it off is for the whole PC, and
  the teammate's decision, not ours.
- **Two installers, 0.2.0 and 0.2.1.** Tahseen starts two CI runs by hand at the step's head, the second with
  `-f desktop_version=0.2.1`, each with `upload_installers` (his decision of 2026-11-26: an upload is publishing, so
  only a run started by hand does it, kept 7 days; they are unsigned test builds for a local stack), and the teammate
  downloads `SwiftChart-Windows` from each in Edge, so the PC needs no Rust. The other way is to build both on
  the PC (`docs/desktop-install.md`, "From source"), which needs Rust through rustup, Visual Studio Build Tools with
  "Desktop development with C++", Strawberry Perl ahead of Git's Perl on `PATH`, and the Tauri CLI,
  `cargo install tauri-cli --version 2.12.0 --locked`; then, in PowerShell, from `src\Ehr.Desktop\src-tauri`:

  ```
  $env:CARGO_TARGET_DIR = "C:\swiftchart-target"
  cargo tauri build -- --locked
  Copy-Item C:\swiftchart-target\release\bundle\nsis\SwiftChart_0.2.0_x64-setup.exe $HOME\Desktop
  cargo tauri build --config '{"version":"0.2.1"}' -- --locked
  ```

  (In PowerShell 7, as `docs/development.md`'s setup has it; Windows PowerShell 5 would drop the JSON's quotes.)

  A build made on the PC isn't marked as downloaded, so SmartScreen won't show for it: W1 then notes only that it
  installed.
- **0.2.1's signature, made on Tahseen's Mac** the day before: `SwiftChart_0.2.1_x64-setup.exe` copied to the Mac,
  signed there (`docs/desktop-install.md`, "Making a release", step 2), and `SwiftChart_0.2.1_x64-setup.exe.sig` brought
  back. The key never leaves the Mac; the signature isn't secret.

**Time:** about an hour once the stack runs, plus about half an hour for the first build if the installers are built
there, and more if the stack was never set up on that PC. Nothing on the PC is touched but the stack's own containers
and volumes, SwiftChart's own folders and credentials, and the hosts line; every account is the stack's, synthetic.

**Start the stack with the update server**, from `infra\compose`: `mkdir desktop-updates`, then
`docker compose -f compose.yaml -f compose.desktop-updates.yaml up -d --build --wait`.

**W1. Install 0.2.0.** Downloaded: note what Edge says, and keep it (**…** > **Keep** > **Show more** > **Keep
anyway**). Run the setup: note SmartScreen's words, choose **More info**, then **Run anyway**. It installs without asking
for an administrator's password, and SwiftChart is in the Start menu.

**W2. The first launch.** Open SwiftChart: "Sign in", "SwiftChart 0.2.0." in the status line, and under the form
"Signs in to: ehr.example.com:8443, ehr-remote.example.com:9443". Sign in as the clinician, **Download my charts**, open
a chart, **Record audio** (allow the microphone): "Recording saved."

**W3. Step 19a's Windows section,** its W1 to W5, with this installed build: it locks after its full 15 idle minutes,
warned at 13.

**W4. An update.** Copy `SwiftChart_0.2.1_x64-setup.exe` and its `.sig` into `infra\compose\desktop-updates\`, and write
`latest.json` there as `infra/compose/README.md` shows, with `"version": "0.2.1"` and only the `windows-x86_64` entry,
its signature the contents of the `.sig` file. Quit SwiftChart, open it, unlock: "Update available. SwiftChart 0.2.1.".
**Download update**, then **Restart now**: the installer's progress shows, SwiftChart starts again on the lock, and
after unlocking says "SwiftChart 0.2.1.", with the charts as they were. Note whether SmartScreen or anything else asked.

**W5. Disabled, revoked, and the wipe.** As the Mac's M6 and M7, in Edge as the administrator: stop the two apps, save
a draft, quit SwiftChart, start the apps, disable the clinician, open SwiftChart (it locks: "Signed out. …"), quit it
without signing out, revoke, open SwiftChart again: "Deleted. …" within a few seconds. Then in PowerShell:

```
Test-Path "$env:APPDATA\io.github.tarnan3751.swiftchart\ehr.db"
cmdkey /list:local-database-key.io.github.tarnan3751.swiftchart
cmdkey /list:server-session.io.github.tarnan3751.swiftchart
```

`False`, and each `cmdkey` lists nothing.

**W6. Clean up.** In Edge, **Enable** the clinician; sign in as them in SwiftChart once more, so it keeps a sign-in
again. **Quit SwiftChart**; Settings > Apps > Installed apps > SwiftChart > **Uninstall**, deleting its data. Then look,
since the final review couldn't: does the uninstaller leave the Credential Manager entries
(`cmdkey /list:local-database-key.io.github.tarnan3751.swiftchart`)? If it does, note it, and remove each with
`cmdkey /delete:local-database-key.io.github.tarnan3751.swiftchart` and
`cmdkey /delete:server-session.io.github.tarnan3751.swiftchart`, noting whether `cmdkey /delete` works on them (the
install notes rely on it for an old build's entries). Remove the hosts line; and, if the teammate wants,
`docker compose --profile "*" down --volumes`.
