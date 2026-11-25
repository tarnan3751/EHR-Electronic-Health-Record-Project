# UC 8 — Sync the desktop app

SwiftChart EHR · Sara Lelo · CS 3354 Software Engineering, Fall 2026

This file records what the expansion plan (`docs/expansion/PLAN.md`) has changed in this use case. The use case itself,
and its first six test cases (TC-UC8-01 to TC-UC8-06, in the desktop app's Rust tests and `DesktopSignInTests.cs`), are
Sara's.

## A desktop sign-in lasts 12 hours (the plan's step 19a)

The desktop app's sign-in takes the password and the authenticator code, and gives the app a token that every sync
request carries. Until step 19a the token worked for 30 days. Now it works for 12 hours after that sign-in, however
busy the app is, as a web sign-in does (Tahseen's ruling 11b of 25 November 2026, proposed FR-68;
`src/Ehr.Domain/Accounts/DesktopSessions.cs`). The 12 hours are counted on the server, from when the session was made
(`ehr.desktop_sessions.created_at`), so nothing on the computer can move them.

- **After the 12 hours** every request with the token is refused, on the clinic's site and on the cloud app alike, with a
  401 that carries `Ehr-Sign-In: ended` (`DesktopTokenHandler.cs`). Nothing is sent or brought back: Quick text, the
  drafts' pull and push, recordings' uploads and **Download my charts** all wait for a sign-in with both factors.
- **Every other refusal** (signed out on another computer, a changed password, an account locked out or disabled) carries
  no such header: that session is over.
- **In the app** (`src/Ehr.Desktop/src-tauri/src/sync.rs`): the 12-hour answer keeps the session and the offline copy,
  says "Sign in again: a sign-in lasts at most 12 hours. Nothing is sent or brought back until you do, and what you save
  here waits on this computer.", and locks the window once for a full sign-in (`docs/UC7.md`). Any other 401 forgets the
  session as before, and now locks the window too, keeping the page for the same clinician, rather than clearing it.
- **What it costs.** The offline charts are refreshed only by **Download my charts**, and the drafts' pull brings back
  drafts changed elsewhere; after the 12 hours neither works until a sign-in with both factors, so a copy on a computer
  left running grows older than it would have. Wherever the app shows the offline charts, on the lock, and in the banner
  after the 12 hours, it says when they were last updated (`docs/UC7.md`).
- **Unchanged:** the 30 days, which are now how long the app may open its offline copy without a sign-in (the sign-in's
  `offlineAccess`); signing out; and an administrator ending a session by disabling the account. Remote wipe and what
  happens when the 30 days run out offline are step 20's.

## Test cases

- **TC-UC8-07** (`tests/Ehr.Web.Tests/TestCases/UC8/TC_UC8_07_TwelveHourDesktopSignIn.cs`): a desktop token syncs at 11
  hours 59 minutes on both sites; at 12 hours a sync on either site and a forwarded chart download are each refused
  with `Ehr-Sign-In: ended`; a new full sign-in syncs; a signed-out session's refusal, and a changed password's, don't
  carry the header.
- **TC-UC8-08** (`src/Ehr.Desktop/src-tauri/src/sync.rs`, `an_ended_sign_in_keeps_the_session_and_says_so`): a round
  answered with `Ehr-Sign-In: ended` keeps the session and the offline charts open, says a sign-in is needed, tells the
  lock once, and sends nothing; a chart download answered so says the same.
