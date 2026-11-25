# UC 7 — Work offline in the desktop app

SwiftChart EHR · Sara Lelo · CS 3354 Software Engineering, Fall 2026

This file records what the expansion plan (`docs/expansion/PLAN.md`) has changed in this use case. The use case itself,
and its first six test cases (TC-UC7-01 to TC-UC7-06, in the desktop app's Rust tests and `DesktopSignInTests.cs`), are
Sara's.

## The window locks when it's left (the plan's step 19a)

The desktop app's window locks as a web sign-in idles out, with the same warning, but it is a lock, not a sign-out:
everything on screen stays as it was underneath, and sync carries on (Tahseen's rulings of 24 November 2026 on step 18,
and 10a, 11b and 12b of 25 November 2026; proposed FR-68). `src/Ehr.Desktop/src-tauri/src/lock.rs` decides when;
`main.rs` refuses the offline charts while it's up; `frontend/app.js` covers the window.

**When it locks**

- **15 minutes after the last key, click or tap, wheel turn or typing in the window**, warned 2 minutes before, as the
  web's `SessionTimes.Idle` and `Warning`. Mouse movement doesn't count, nor does sync. The page tells the Rust side at
  most once every 10 seconds, so the lock may come up to 10 seconds early. Time asleep counts: the timer reads the wall
  clock as well as the monotonic one, which stands still while a Mac sleeps, so a laptop opened after 15 minutes is
  locked already.
- **At the sign-in's 12-hour end**, warned once 2 minutes before ("Keep working" closes that warning for good), or as
  soon as a server says the 12 hours are over (`docs/UC8.md`). Once only: see "Past the 12 hours" below.
- **When the server ends the session** (signed out on another computer, a changed password, an account locked or
  disabled). Before this step the page was cleared then; now it's kept under the lock for the same clinician.
- **At every launch with a sign-in kept**, so quitting never skips the lock. A sign-in kept by an earlier build, which has
  no record of the lock's, is forgotten at the first launch of this one: sign in again.
- **Never over a recording.** A recording being made, and its save, hold every lock and warning off, for 5 minutes at
  most (the desktop records 60 seconds at most), and count as activity.

**The warning** is the web's session warning (the `sc-dialog` component), in words for a lock: "This window will lock
soon", the countdown, that what was typed stays and sync carries on, **Stay unlocked** (the focus; Escape does the same)
and **Lock now**.

**What the lock covers.** The app bar and the page are made inert and hidden: nothing in them can be focused, clicked,
selected, found, printed or read by assistive technology, and they're kept exactly as they were, typed text, scroll and
focus included, which come back when it opens. The lock panel fills the window: why it locked, what opens it, when the
offline charts were last updated, whose sign-in it is, and **Sign out** for someone else, which says first that it closes
the offline charts and loses anything typed and not saved. The window's title never names a patient, and the app sends
no notifications. While it's up the app refuses to read the offline charts for the page (a reload or a second copy of
the app gets nothing either); saves already under way finish; sync, uploads included, carries on.

**What opens it**

| When | What it takes | Checked by |
|---|---|---|
| Within the sign-in's 12 hours | The password | One of the servers the sign-in was made with, when it answers within 5 seconds (`POST /api/desktop/unlock`, below), counting a wrong one towards the account's lockout; otherwise this computer, against its hash |
| Past the 12 hours | A full sign-in: the password and the authenticator code, for the same account | The servers the sign-in was made with; while none can complete one (none answers, or the one that does can't, as the cloud app with the clinic's server cut off), the password alone opens the offline charts |
| After the server ended the session | A full sign-in | The server only |

A server's no stands: once one has said "wrong password" or "locked out", nothing is checked on this computer.

**Only the servers the sign-in was made with** answer for the lock (step 19a's final review). Any server can answer yes
to a password, so the app keeps, with the lock's record, the servers each sign-in was made with (the built-in ones, or
`EHR_SYNC_SERVERS`), and asks only those. Started with any others, it forgets the kept sign-in and its record before
anything is sent, the token included, and the sign-in form says: "Signed out: this app was started with servers other
than the ones you signed in with, and only those can open its lock. Sign in again; nothing on this computer was
deleted." So the hash on this computer isn't used then either: the lock is gone, and only a sign-in opens the offline
charts. A release build takes HTTPS servers only, so nothing on the computer can stand in for one unauthenticated.

**Past the 12 hours with no server** (decided under the plan's first principle: an outage is when the offline chart is
needed, and the password still has to be known). The password alone opens the offline charts, to read and to save
drafts and recordings on this computer, which wait in its outbox. A banner says so, with when the charts were last
updated, and **Sign in now**: "Offline: a sign-in with your code is needed as soon as the clinic's server can be
reached", or "Sign in again:" once a server answers. The window isn't locked again each time a server says the 12 hours
are over (the cloud app does, every 10 seconds, while the clinic's server is cut off, and the sign-in itself needs the
clinic's server); its next lock asks for the full sign-in again. The offline charts still close 30 days after the
sign-in, as before.

**The hash on this computer** (`src-tauri/src/unlock.rs`). At each sign-in with both factors the app keeps an Argon2id
hash of the password, never the password: 64 MiB, 3 passes, one lane, a new random salt, about a tenth of a second on an
Apple M5 and under a second on CI's runners (TC-UC7-11). It's kept in the encrypted store with the offline charts, with
the clinician's email (for the full sign-in), when the sign-in was made, and the wrong passwords typed at the lock in a
row: after 5 it waits 15 minutes, after 10 only a full sign-in opens it, wherever it's checked; nothing is deleted. It goes at sign-out, and is
cleared when the server ends the session. It adds nothing against someone who can already read the store, who can read
the charts in it directly; it's a target for guessing the password away from the app, at Argon2id's cost per guess. The
lock is against someone at an unattended computer, not encryption.

**Known limits**

- An unlock made on this computer, with no server, isn't in the audit log.
- The app doesn't lock at the moment the computer sleeps or its screen locks; the computer's own lock covers it then, and
  the time asleep counts towards the 15 minutes.
- A minimized window's tile in the Dock, or a window coming back from out of sight, may show its last picture for a moment
  before the lock draws. Not yet seen by a person: the hands-on check (`docs/expansion/19a-hands-on-check.md`) looks.
- On Windows, WebView2's own keys stay on: Ctrl+R reloads the page, losing anything typed, as before this step, and comes
  back locked; Ctrl+P and Ctrl+F find nothing while it's locked. Windows behaviour is unverified by hand until step 20's
  check on a Windows PC.
- After the lock's last control, Tab can reach the window itself before coming back round; nothing of the page.

## The server's check of the lock's password (the plan's step 19a)

`POST /api/desktop/unlock`, with the desktop session's token and `{"password": …}`, checks the password the app's lock
asks for (Tahseen's ruling 11b of 25 November 2026, proposed FR-68; `src/Ehr.Web/Api/Desktop/DesktopApi.cs`,
`DesktopSessions.UnlockAsync`).

- **Whose password.** The token's account's. The request names no account, so it can't be used to learn which addresses
  have one.
- **The answers.** 204 when it's right. 403 with the reason when it's wrong ("Wrong password.") or the account is now
  locked out ("This account is locked after too many failed attempts. Try again in 15 minutes."): the token was good, so
  this isn't a 401, which always means the token was refused. Once the sign-in is 12 hours old the token is refused with
  `Ehr-Sign-In: ended` (`docs/UC8.md`), and the lock asks for a full sign-in instead.
- **As sign-in counts it.** A wrong password counts towards the account's lockout (5, then 15 minutes, on every site and
  the desktop app alike), and a right one clears the count, as the sign-in's password step does. The path is
  rate-limited per address with the sign-ins (`AccountRateLimits.cs`).
- **Recorded.** Each unlock is an `unlock` event, each wrong password a `sign_in_failed` one ("wrong password"), both with
  the desktop session and its device in their details, as the desktop sign-in's are.
- **Off-site.** The cloud app forwards it to the on-site app, which records it. While the clinic's server can't be
  reached, the cloud app answers 503.

## When the offline charts were last updated (the plan's step 19a)

The offline charts are refreshed only by **Download my charts**; nothing in the background refreshes them. Since step
19a stops every sync 12 hours after a sign-in until the next one (`docs/UC8.md`), a computer left running can hold a
copy older than it would have, so the offline charts now say how old they are: "Offline charts last updated 2026-11-25
14:05 CST.", on the clinic's clock as the charts' own times are (step 10d), under the section's description. The time is
recorded with each download, by this computer's clock, in the encrypted store (`charts.rs`); charts downloaded by an
earlier build say that when isn't recorded, until they are downloaded again.

## Test cases

- **TC-UC7-07** (`tests/Ehr.Web.Tests/TestCases/UC7/TC_UC7_07_DesktopUnlock.cs`): the right password unlocks, on the
  on-site app and through the cloud app, and a wrong one is refused, each recorded with the desktop session; an address
  in the request is ignored; with no token it's 401; cut off, the cloud app answers 503; five wrong passwords lock the
  account and its token is then refused with no `Ehr-Sign-In` header; at 12 hours the right password is refused with
  `Ehr-Sign-In: ended`, and nothing is recorded as an unlock.
- **TC-UC7-08** (`src-tauri/src/unlock.rs`): the hash is salted Argon2id with its parameters, never the password, and
  tells the right password from a wrong one; 5 wrong ones wait 15 minutes and 10 take a full sign-in, a right one starts
  the count again, and nothing is deleted; the count and the sign-in's servers survive reopening the store, the server's
  end of the session leaves only the email and the servers, and signing out leaves nothing; a record from before the
  servers were kept names none.
- **TC-UC7-09** (`src-tauri/src/lock.rs`): a kept sign-in starts locked; the warning at 13 idle minutes and the lock at
  15, activity starting them again; a recording holds both off and counts as activity, and a hold the page never ended
  lapses after 5 minutes; time asleep counts, and a clock set back gains nothing; the 12-hour warning shows once and the
  12-hour lock waits for a recording; opened past the 12 hours, the window isn't locked again by the server's word, and
  its next idle lock asks for a full sign-in; the server's word before this computer's clock counts; a session the server
  ends locks for a full sign-in; too many wrong passwords here turn a lock for the password into one for a full sign-in.
- **TC-UC7-10** (`src-tauri/src/sync.rs`, `main.rs`): the password goes only to `/api/desktop/unlock`, with the token; a
  server's no stands and the next isn't asked; a 5xx is passed over; with no server it's checked here; the 12-hour answer
  keeps the session and any other 401 forgets it; past the 12 hours the password alone opens only when no server can
  complete a sign-in, never after "wrong" or "locked out", and never after the server ended the session. From step 19a's
  final review: started with a server the sign-in wasn't made with, one that answers yes to anything, the app forgets the
  kept sign-in and says why, and any password then opens nothing and the stand-in hears nothing, the token least of all
  (`a_server_the_sign_in_wasnt_made_with_never_opens_the_lock_or_gets_the_token`); started with the servers it was made
  with, it starts locked for the password with the sign-in kept; a release build refuses a plain-HTTP server.
- **TC-UC7-11** (`src-tauri/src/unlock.rs`): the hash takes under a second, and CI's macOS and Windows runners print how
  long.
- **The download's time** (`charts.rs`, `a_download_records_when_it_was_made`): a download records when it was made and
  the clinic's time zone, for that clinician only, and the next one moves it on.
