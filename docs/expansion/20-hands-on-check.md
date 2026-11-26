# Step 20: the hands-on check

What the tests can't show of step 20 (desktop distribution): what macOS and Windows do with SwiftChart unsigned, the
app installed rather than run from source, its fixed servers, and the remote wipe in the real window. Two parts: the Mac
(Tahseen's) and a Windows PC (a teammate's, arranged by Tahseen a day ahead), which also runs step 19a's Windows section
(`docs/expansion/19a-hands-on-check.md`). The install notes it checks are `docs/desktop-install.md`.

Write down, for each numbered step, whether you saw what it says, the exact words of any dialog the operating system
shows, and anything that showed that shouldn't have. `docs/desktop-install.md` is corrected from what you write.

## The Mac

**Before you start, know this:** it needs your local server stack on this branch, whose volumes are yours to reset
(`docker compose --profile "*" down --volumes`, then `up` again and the accounts set up again, as for step 19a's check).
And it installs SwiftChart for real: it writes a sign-in into your login Keychain and a database into
`~/Library/Application Support/io.github.tarnan3751.swiftchart/`, both under the new identifier, apart from anything an
older build kept. Step M6 deletes them again; cleaning up after older builds is optional, and yours. About 25 minutes.

**Get the installer**, either way:

- **From CI**, if a run was started by hand to keep it (`docs/desktop-install.md`): download `SwiftChart-macOS` from the
  run's page in Safari and open the zip. Downloaded so, it's marked as from the internet, as a teammate's would be.
- **Or build it**, from `src/Ehr.Desktop/src-tauri`: `cargo tauri build -- --locked`, then mark it as downloaded, so
  macOS checks it as it would a teammate's:

  ```
  xattr -w com.apple.quarantine "0081;$(printf '%x' "$(date +%s)");Safari;" target/release/bundle/dmg/SwiftChart_0.2.0_aarch64.dmg
  ```

  (With `CARGO_TARGET_DIR` set, the `.dmg` is under that folder's `release/bundle/dmg/` instead.)

**Steps:**

1. **Install and the first launch.** Open the `.dmg`, drag SwiftChart to Applications, and open it from Applications.
   Note the dialog's words and buttons; choose **Done**. Then System Settings > Privacy & Security: note what it says
   about SwiftChart, choose **Open Anyway**, then **Open**; note whether it asks for your password. SwiftChart opens on
   "Sign in to sync". If macOS said instead that it "is damaged", stop and note it: the seal is broken.
2. **Where the password goes.** Under the form: "Signs in to: ehr.example.com:8443, ehr-remote.example.com:9443".
   Sign in as the clinician; note any Keychain prompt (choose **Always Allow**). Choose **Download my charts**, open a
   chart, and **Record audio**: macOS asks for the microphone once (allow it); it records and says "Recording saved on
   this device". If it can't record, note the message: the hardened runtime is the suspect.
3. **Its servers are its own.** Quit SwiftChart, and start it naming another server:

   ```
   EHR_SYNC_SERVERS=https://127.0.0.1:9 /Applications/SwiftChart.app/Contents/MacOS/ehr-desktop
   ```

   It opens on the lock, for your password, as it does after any start; it doesn't say it was started with other
   servers, since a release build reads no variable. Unlock, and the status says Online. Quit it (Cmd+Q).
4. **Revoked, offline.** Open SwiftChart and unlock. From `infra/compose`, `docker compose stop onprem-app cloud-app`.
   Open a chart, type a sentence under **New draft** and **Save draft**: it waits on this computer. Quit SwiftChart.
   `docker compose start onprem-app cloud-app`, and once they're up, in Safari as the administrator at
   https://ehr.example.com:8443: **Staff**, the clinician's **Desktop sign-ins**. Note the count and the paragraph;
   choose **Revoke and delete**: the staff page says their desktop sign-ins are revoked.
5. **The wipe.** Open SwiftChart. It opens on the lock; within about 10 seconds the window shows only "Deleted: an
   administrator revoked this computer's sign-in, so SwiftChart deleted everything it kept here: …", with **Quit
   SwiftChart** and the focus on it; nothing of the chart, or of your unsaved draft, anywhere. Then, in Terminal:

   ```
   ls ~/Library/Application\ Support/io.github.tarnan3751.swiftchart/
   security find-generic-password -s io.github.tarnan3751.swiftchart -a local-database-key
   security find-generic-password -s io.github.tarnan3751.swiftchart -a server-session
   ```

   No `ehr.db`, and each `security` says the item could not be found. In Safari, the audit page lists
   `desktop_revoked` by the administrator. The draft saved in step 4 never reached the server: that is the loss the
   page warned of.
6. **After.** Choose **Quit SwiftChart**, open it again: "Sign in to sync", empty. Sign in as the clinician again: it
   works (the account isn't disabled). To leave nothing behind, quit, drag SwiftChart to the Trash, and run the three
   lines in `docs/desktop-install.md` under "From a build named EHR", with `io.github.tarnan3751.swiftchart` in place
   of `com.example.ehr`. Cleaning up after older builds (`com.example.ehr`, "From a build named EHR") is optional.

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
- **The installer.** If CI keeps installers (Tahseen decides): download `SwiftChart-Windows` from the run in Edge and
  extract the zip. If not, build it on the PC (`docs/desktop-install.md`, "From source"), which needs Rust through
  rustup, Visual Studio Build Tools with "Desktop development with C++", Strawberry Perl ahead of Git's Perl on `PATH`,
  and the Tauri CLI, `cargo install tauri-cli --version 2.12.0 --locked`; then, in PowerShell, from
  `src\Ehr.Desktop\src-tauri`: `$env:CARGO_TARGET_DIR = "C:\swiftchart-target"; cargo tauri build -- --locked`. The
  installer is `C:\swiftchart-target\release\bundle\nsis\SwiftChart_0.2.0_x64-setup.exe`. A build made on the PC isn't
  marked as downloaded, so SmartScreen won't show for it: step W1 then notes only that it installed.

**Time:** about an hour once the stack runs, plus about half an hour for the first build if the installer is built
there, and more if the stack was never set up on that PC. Nothing on the PC is touched but the stack's own containers
and volumes, SwiftChart's own folders and credentials, and the hosts line; every account is the stack's, synthetic.

**Steps**, with the stack up (`docker compose up -d --build --wait` from `infra\compose`):

1. **W1. Install.** Downloaded: note what Edge says, and keep it (**…** > **Keep** > **Show more** > **Keep anyway**).
   Run the setup: note SmartScreen's words, choose **More info**, then **Run anyway**. It installs without asking for
   an administrator's password, and SwiftChart is in the Start menu.
2. **W2. The first launch.** Open SwiftChart: "Sign in to sync", and under it "Signs in to: ehr.example.com:8443,
   ehr-remote.example.com:9443". Sign in as the clinician, **Download my charts**, open a chart, **Record audio** (allow
   the microphone): "Recording saved on this device".
3. **W3 to W7.** Step 19a's Windows section, W1 to W5, with this installed build: it locks after its full 15 idle
   minutes, warned at 13.
4. **W8. Revoked, and the wipe.** As the Mac's steps 4 and 5, in Edge as the administrator: stop the two apps, save a
   draft, quit SwiftChart, start them, revoke, open SwiftChart: "Deleted: …" within about 10 seconds. Then in
   PowerShell:

   ```
   Test-Path "$env:APPDATA\io.github.tarnan3751.swiftchart\ehr.db"
   cmdkey /list:local-database-key.io.github.tarnan3751.swiftchart
   cmdkey /list:server-session.io.github.tarnan3751.swiftchart
   ```

   `False`, and each `cmdkey` lists nothing.
5. **W9. Clean up.** **Quit SwiftChart**; Settings > Apps > Installed apps > SwiftChart > **Uninstall**, deleting its
   data; remove the hosts line; and, if the teammate wants, `docker compose --profile "*" down --volumes`.
