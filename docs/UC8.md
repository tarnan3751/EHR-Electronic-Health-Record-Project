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
- **Every other refusal** (a session signed out here, a changed password, an account locked out or disabled, or the
  session's 30 days) carries no such header: that session is over.
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
  happens when the 30 days run out offline are step 20's (below, and `docs/UC7.md`).

## A revoked sign-in's token says so (the plan's step 20)

An administrator can revoke a clinician's desktop sign-ins (`docs/UC11.md`). From then on every desktop and sync
request with one of their tokens, on either site, is refused with a 401 carrying `Ehr-Sign-In: revoked`: the remote
wipe's marker, on which the app deletes what it keeps on that computer (`docs/UC7.md`). The marker comes first,
whatever else is true of the session: its 12 hours or its 30 days past, its password changed, its account disabled or
locked out. So a computer that comes back after any time away is still wiped. Only a session an administrator revoked
gets it (`revoked_by`); a sign-out's token, or any other session that is over, gets the plain 401 it always did, and the
app keeps what it holds.

## Updates, and the oldest build that may sync (the plan's step 20)

Each site hands the desktop app its updates, from a folder named in its settings (`Ehr:Desktop:UpdatesFolder`;
`src/Ehr.Web/Api/Desktop/DesktopUpdates.cs`): `GET /api/desktop/updates/{platform}/{version}`, with nobody signed in,
so a copy signed out or wiped can update too, answers with the release's entry for that platform, its file's address
on the site that was asked, or 204 when there is none; `GET /api/desktop/updates/files/{name}` gives the file. The app
installs a release only if it's newer than itself and signed with the key built into it for the version announced
(`docs/UC7.md`); the server signs nothing.

A site can also refuse to sync with a build older than `Ehr:Desktop:MinimumVersion` (Tahseen's 15a: "the server can
name a minimum version and refuse sync below it"). The app says its version in `Ehr-Desktop-Version` on every request;
one older, or a build from before step 20, which says nothing, gets 426 from the sync API and the desktop's data
(the chart download, the drafts, the offline recordings), with the minimum in `Ehr-Desktop-Minimum` and "Update
needed: the clinic's server syncs only with SwiftChart … or later. What you save waits on this computer, and the
offline charts can still be read." Sign-in, the lock's check of the password, sign-out and the updates are never
refused for the version. 426, not a 401, which every build takes as its token refused, nor a 403, which a build from
before step 20 takes as a waiting recording refused for good.

## Test cases

- **TC-UC8-07** (`tests/Ehr.Web.Tests/TestCases/UC8/TC_UC8_07_TwelveHourDesktopSignIn.cs`): a desktop token syncs at 11
  hours 59 minutes on both sites; at 12 hours a sync on either site and a forwarded chart download are each refused
  with `Ehr-Sign-In: ended`; a new full sign-in syncs; a signed-out session's refusal, and a changed password's, don't
  carry the header.
- **TC-UC8-09** (`tests/Ehr.Web.Tests/TestCases/UC8/TC_UC8_09_RevokedDesktopToken.cs`): two revoked tokens are refused
  with `Ehr-Sign-In: revoked` on a sync and an unlock on the on-site app and a sync and a forwarded chart download on
  the cloud app, and still are after the password changes and 31 days pass; a signed-out token of the same account
  never carries the header.
- **TC-UC8-10** (`tests/Ehr.Web.Tests/TestCases/UC8/TC_UC8_10_MinimumDesktopVersion.cs`): with a minimum of 0.3.0, no
  version, 0.2.0 and 0.2.9 are refused a sync on both sites and a chart download with 426, the minimum and the words;
  0.3.0, 0.3 and 1.0.0 sync; sign-in, a wrong password at the lock and sign-out are answered as ever; with no minimum
  nothing is refused.
- **TC-UC8-11** (`tests/Ehr.Web.Tests/TestCases/UC8/TC_UC8_11_DesktopUpdates.cs`): each site, asked with no cookie or
  token, hands out the release's macOS entry alone with its file at its own address, and the file; nothing for a
  platform with none or with no folder; and no file outside the folder, nor `latest.json` or one not there.
- **TC-UC8-08** (`src/Ehr.Desktop/src-tauri/src/sync.rs`, `an_ended_sign_in_keeps_the_session_and_says_so`): a round
  answered with `Ehr-Sign-In: ended` keeps the session and the offline charts open, says a sign-in is needed, tells the
  lock once, and sends nothing; a chart download answered so says the same.
