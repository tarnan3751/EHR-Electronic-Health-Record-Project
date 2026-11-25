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
- **Unchanged:** the 30 days, which are now how long the app may open its offline copy without a sign-in (the sign-in's
  `offlineAccess`); signing out; and an administrator ending a session by disabling the account. Remote wipe and what
  happens when the 30 days run out offline are step 20's.

## Test cases

- **TC-UC8-07** (`tests/Ehr.Web.Tests/TestCases/UC8/TC_UC8_07_TwelveHourDesktopSignIn.cs`): a desktop token syncs at 11
  hours 59 minutes on both sites; at 12 hours a sync on either site and a forwarded chart download are each refused
  with `Ehr-Sign-In: ended`; a new full sign-in syncs; a signed-out session's refusal, and a changed password's, don't
  carry the header.
