# UC 7 — Work offline in the desktop app

SwiftChart EHR · Sara Lelo · CS 3354 Software Engineering, Fall 2026

This file records what the expansion plan (`docs/expansion/PLAN.md`) has changed in this use case. The use case itself,
and its first six test cases (TC-UC7-01 to TC-UC7-06, in the desktop app's Rust tests and `DesktopSignInTests.cs`), are
Sara's.

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

## Test cases

- **TC-UC7-07** (`tests/Ehr.Web.Tests/TestCases/UC7/TC_UC7_07_DesktopUnlock.cs`): the right password unlocks, on the
  on-site app and through the cloud app, and a wrong one is refused, each recorded with the desktop session; an address
  in the request is ignored; with no token it's 401; cut off, the cloud app answers 503; five wrong passwords lock the
  account and its token is then refused with no `Ehr-Sign-In` header; at 12 hours the right password is refused with
  `Ehr-Sign-In: ended`, and nothing is recorded as an unlock.
