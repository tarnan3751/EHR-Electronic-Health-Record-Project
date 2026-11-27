# Step 19a: the hands-on check

Tahseen's ruling 12b of 25 November 2026: "no WebKit CI run. Write the by-hand checklist and add it to the hands-on
check I do at the end of 19a." This is that check, run once: the desktop app's lock and unlock, online and with the
network off, on the Mac (WebKit, the macOS app's web view), and what step 18's browser tests cover only in Chromium (the
session warning, a window out of sight, coming back to it), in Safari and in the macOS app. About 15 minutes of
attention over about 40 of clock, run top to bottom: Safari waits in the background while the desktop steps run. It can
run in the same session as step 19's passkey check; both need the local stack. A section for Windows runs during step
20's check on a Windows PC.

Write down, for each numbered step, whether you saw what it says, and anything that showed that shouldn't have.

## Before you start

- **The local stack, on this branch.** From `infra/compose`: `docker compose up -d --build --wait`. Its volumes were made
  before this phase's migrations and can't be migrated forward, so they may need resetting
  (`docker compose --profile "*" down --volumes`, then `up` again, then the accounts set up again): resetting is your
  call, as for step 19's check.
- **The desktop app, with the idle time shortened.** This variable works in development builds only, and only ever
  shortens. From `src/Ehr.Desktop/src-tauri`:

  ```
  EHR_DEBUG_IDLE_SECONDS=180 cargo tauri dev
  ```

  The window then warns after 2 minutes 36 seconds without a key or click, and locks at 3 minutes. Don't set
  `EHR_SYNC_SERVERS` until step 14: a sign-in is tied to the servers it was made with.
- **Sign in** to the desktop app with your email, password and code, and choose **Download my charts**.
- **Safari, at the same time:** sign in to https://ehr.example.com:8443, open a patient's **Messages**, start a new
  message, type a sentence, don't send it, and leave the tab alone.

## The desktop app on the Mac, and Safari in between

1. **The warning.** In the desktop app, open a chart, type a sentence under **New draft**, don't save, and leave the
   keyboard and mouse alone. After about 2½ minutes: a dialog "This window will lock soon" with a countdown, the focus on
   **Stay unlocked**. Press Enter: it closes, and your sentence is there.
2. **The lock.** Leave it alone again until it locks. You should see only the lock: the mark, "Locked. Inactive for 3
   minutes.", when the offline charts were last updated, **Password**, **Unlock**, and **Sign out** under your
   name. Nothing of the chart. Then:
   - press Tab several times: the focus goes only to the lock's field and buttons;
   - turn VoiceOver on (Cmd+F5), let it read, and turn it off: it reads only the lock;
   - Mission Control (F3, or Ctrl+Up): the window's picture shows the lock;
   - minimize the window (Cmd+M), look at its tile in the Dock, then restore it: no chart in the tile.
3. **Unlock.** Type a wrong password, **Unlock**: "Wrong password." Type the right one: the chart is back, with your
   unsaved sentence and the cursor in it.
4. **Safari's warning**, about 13 minutes after you left the tab. If it isn't up yet, go on with step 5 and come back
   the moment it shows. "You'll be signed out soon", the focus on **Stay signed in**. Press Enter: it closes, and your
   sentence is there. Then minimize Safari (Cmd+M) and leave it: steps 11 and 12 come back to it.
5. **A recording is never locked over.** In the desktop app choose **Record audio** and leave the app alone. It stops
   itself at 60 seconds and says "Recording saved.". No warning or lock while it records and saves; the
   lock comes 3 minutes after.
6. **The clinic's server cut off, then the network off.** Unlock. From `infra/compose`, `docker compose stop onprem-app`:
   the app now reaches only the cloud app, which can't check a password without the clinic's server. Wait for the
   warning and choose **Lock now**, then unlock with the password: it opens (this computer checked it). Then
   `docker compose stop cloud-app` too, lock again the same way, and unlock: it opens, and the status at the top says
   Offline.
7. **Wrong passwords with no server.** Lock again, and type a wrong password five times: the fifth answers "Too many
   wrong passwords. Try again in 15 minutes." Then `docker compose start onprem-app cloud-app`, and once they're up,
   unlock with the right password: the server checks it now, and it opens.
8. **Out of sight, and back.** Hide the app (Cmd+H) for longer than 3 minutes, then bring it back (Cmd+Tab): the lock,
   with no glimpse of the chart first. Unlock.
9. **Quit and start again.** Quit (Cmd+Q), and run the same `cargo tauri dev` command: it opens on the lock, whether or
   not it was locked when you quit. Unlock.
10. **Sleep.** Put the Mac to sleep (close the lid, or Apple menu > Sleep) for more than 3 minutes. Wake it and unlock
    the Mac: the app's lock is already showing, with no glimpse of the chart. Unlock.
11. **Safari, a window out of sight.** Safari has now been minimized for more than 15 minutes since step 4. Bring it
    back: the page says "Signed out." with nothing of the patient's. Choose **Sign in again**, and sign in: the message
    form is back with your sentence.
12. **Safari, coming back.** Sign out, then press Back: no signed-in page comes back from the cache.
13. **The 12-hour end, with the sign-in's time shortened.** Quit the desktop app and start it with the sign-in's 12
    hours shortened to 2½ minutes:

    ```
    EHR_DEBUG_IDLE_SECONDS=180 EHR_DEBUG_SIGN_IN_SECONDS=150 cargo tauri dev
    ```

    - Your sign-in is older than that, so it opens on "Locked. Your sign-in expired.", with **Password** and
      **Code** and a highlighted "Offline charts updated …" Sign in
      with your password and a code: it opens. (The server's own 12 hours aren't shortened: TC-UC8-07 tests those.)
    - About 2 minutes later a dialog says "Your sign-in ends in …" with **Keep working**;
      press it. At 2½ minutes the window locks again, asking for the password and the code.
    - `docker compose stop onprem-app cloud-app`, type the password only, and **Sign in**: it opens, with a banner
      "Offline. Sign in with your code as soon as the clinic's server can be reached.", the charts' time, and
      **Sign in now**.
    - `docker compose start onprem-app cloud-app`: within 10 seconds the banner begins "Sign in again.", and the window
      stays open.
    - **Sign in now**, then the password and a code: it opens, and the banner has gone.
14. **Started with other servers.** Quit, and start it once naming only the clinic's server:
    `EHR_SYNC_SERVERS=https://ehr.example.com:8443 cargo tauri dev`. It opens signed out, saying it was started with
    servers other than the ones you signed in with; nothing of the chart, and no lock to open. Quit, start it again with
    no variables, and sign in.

## Windows (run during step 20's Windows check)

Nobody can try WebView2 by hand before then: until this section is run, the lock's behaviour on Windows is unverified by
hand (CI builds and tests it on Windows). In PowerShell, from `src\Ehr.Desktop\src-tauri`:
`$env:EHR_DEBUG_IDLE_SECONDS=180; cargo tauri dev` (or the installed build, with its full 15 minutes).

W1. Let it lock: only the lock shows, and Tab goes only to the lock's field and buttons.
W2. While it's locked, press Ctrl+R (or F5): the page reloads and comes back locked (anything typed is lost by a reload,
    as before this step). Ctrl+P: nothing of the chart in the print preview. Ctrl+F: finds nothing behind the lock.
W3. While it's locked, start a second copy of the app: it opens on the lock.
W4. Alt+Tab and Task view: the window's picture shows the lock.
W5. The hash's time: CI's Windows desktop job prints "TC-UC7-11: Argon2id (64 MiB, 3 passes) made in …"; note it.
    Unlocking with the network off should feel immediate, under a second.
