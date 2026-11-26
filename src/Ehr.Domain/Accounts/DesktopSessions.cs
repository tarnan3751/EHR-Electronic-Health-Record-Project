using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ehr.Data;
using Ehr.Data.Accounts;
using Ehr.Data.Audit;
using Ehr.Domain.Audit;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Ehr.Domain.Accounts;

// The desktop app's sign-in (DesktopSession). Both factors at once, the same checks as the web sign-in (StaffSignIn),
// then a token the app keeps and sends with every sync request; only its hash is stored. The account must have
// finished its web setup first, since the authenticator is enrolled there, and must be a clinician: the desktop app
// is for clinical work. A session ends when it expires, when the app signs out, or when the account's security stamp
// changes, as at a password change. Every sign-in, unlock, refusal and sign-out goes in the audit log.
//
// An administrator can revoke every desktop sign-in of an account at once (the expansion plan's step 20: a computer
// holds only its latest token, and each 12-hour sign-in makes a new one, so sessions can't stand for computers). A
// revoked session's token is answered with the remote wipe's marker whatever else is true of it, expired or its
// account disabled included, so a computer that comes back after any time away deletes what the app keeps there
// (DesktopTokenHandler); nothing else is ever answered so, a sign-out least of all.
//
// The token works for 12 hours after the sign-in that made it (SessionTimes.Longest), however busy, as a web sign-in
// does (the expansion plan's step 19a): after that every request is refused, saying the sign-in has ended
// (DesktopTokenHandler), and the app asks for both factors again. Lifetime is how long the app may open its offline
// copy without a sign-in (the sign-in's OfflineAccess). The clock is the Desktop scheme's (its options' TimeProvider),
// which tests move on.
public sealed class DesktopSessions(
    UserManager<StaffUser> users, StaffSignIn signIn, EhrDbContext db, AuditRecorder audit,
    IOptionsMonitor<AuthenticationSchemeOptions> schemes)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);

    DateTimeOffset Now => (schemes.Get(AccountSchemes.Desktop).TimeProvider ?? TimeProvider.System).GetUtcNow();

    public async Task<DesktopSignIn> SignInAsync(string email, string password, string code, string device, CancellationToken cancellationToken)
    {
        var check = await signIn.CheckPasswordAsync(email, password, cancellationToken);
        if (check.Outcome != PasswordOutcome.Ok)
        {
            return new(check.Outcome == PasswordOutcome.LockedOut ? DesktopSignInOutcome.LockedOut : DesktopSignInOutcome.Refused);
        }

        // The password check held the account and read it again once the password was checked (StaffSignIn, step 19.2),
        // as the web's code page holds it: sign-ins racing each other or an administrator's action on the account are
        // checked one after another, never failing on a stale account with a 500; one whose account changed since its
        // password was checked (a reset, a reissued password, a disabling) was refused, nothing counted; and one whose
        // account was locked out meanwhile was refused as locked, before its code is checked here.
        var user = check.User!;
        if (user.MustChangePassword || !user.TwoFactorEnabled || await users.GetAuthenticatorKeyAsync(user) is null)
        {
            return new(DesktopSignInOutcome.NotSetUp);
        }

        if (!user.Roles.Contains(StaffRoles.Clinician))
        {
            return new(DesktopSignInOutcome.NotAClinician);
        }

        if (!await signIn.VerifyCodeAsync(user, code, cancellationToken))
        {
            return new(await users.IsLockedOutAsync(user) ? DesktopSignInOutcome.LockedOut : DesktopSignInOutcome.Refused);
        }

        var token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        var now = Now;
        var session = new DesktopSession
        {
            Id = Guid.CreateVersion7(),
            StaffUserId = user.Id,
            TokenHash = Hash(token),
            SecurityStamp = user.SecurityStamp!,
            Device = device,
            CreatedAt = now,
            ExpiresAt = now + Lifetime,
        };
        db.DesktopSessions.Add(session);
        audit.Record(AuditActions.SignIn, actorId: user.Id, details: Details(session));
        await db.SaveChangesAsync(cancellationToken);
        return new(DesktopSignInOutcome.SignedIn, token, session.ExpiresAt, user.DisplayName, user.Id);
    }

    // The session behind a token and its account, or null when the session is over: no such token, expired, signed
    // out, the account locked out or disabled, or its security stamp changed. A session an administrator revoked comes
    // back with Revoked, before any of those, and a session good but for its sign-in being 12 hours old with
    // SignInEnded: each is good for nothing but saying so. Reads only, so the cloud app can do it.
    public async Task<DesktopSignedIn?> AuthenticateAsync(string token, CancellationToken cancellationToken)
    {
        var hash = Hash(token);
        var now = Now;
        var session = await db.DesktopSessions.AsNoTracking().SingleOrDefaultAsync(s => s.TokenHash == hash, cancellationToken);
        if (session is null || (session.RevokedBy is null && (session.RevokedAt is not null || session.ExpiresAt <= now)))
        {
            return null;
        }

        var user = await users.FindByIdAsync(session.StaffUserId.ToString());
        if (user is not null && session.RevokedBy is not null)
        {
            return new(session, user) { Revoked = true };
        }

        if (user is null || user.SecurityStamp != session.SecurityStamp || await users.IsLockedOutAsync(user))
        {
            return null;
        }

        return new(session, user) { SignInEnded = session.CreatedAt + SessionTimes.Longest <= now };
    }

    // The password the app's lock asks for (step 19a), checked for the account whose session it is, as the sign-in's
    // password step checks it (StaffSignIn): a wrong one counts towards the account's lockout. The session was
    // authenticated just before, so it was good and its account not locked out then. As at the password step (step
    // 19.2), the password is checked with nothing held, then the account held and read again before anything is counted
    // or recorded: wrong passwords sent at once are each counted, the fifth locks the account, and the rest are refused
    // as locked; a right one whose account changed in between is refused, counting nothing and recording nothing.
    // Each unlock and each other refusal is recorded, with the session's device.
    public async Task<DesktopUnlockOutcome> UnlockAsync(Guid sessionId, string password, CancellationToken cancellationToken)
    {
        var session = await db.DesktopSessions.AsNoTracking().SingleAsync(s => s.Id == sessionId, cancellationToken);
        var user = (await users.FindByIdAsync(session.StaffUserId.ToString()))!;
        var right = await users.CheckPasswordAsync(user, password);
        var (stamp, hash) = (user.SecurityStamp, user.PasswordHash);
        await signIn.HoldAccountAsync(user, cancellationToken);
        if (await users.IsLockedOutAsync(user))
        {
            audit.Record(AuditActions.SignInFailed, reason: "locked out", actorId: user.Id, details: Details(session));
            await db.SaveChangesAsync(cancellationToken);
            return DesktopUnlockOutcome.LockedOut;
        }

        if (!right)
        {
            await users.AccessFailedAsync(user);
            audit.Record(AuditActions.SignInFailed, reason: "wrong password", actorId: user.Id, details: Details(session));
            await db.SaveChangesAsync(cancellationToken);
            return await users.IsLockedOutAsync(user) ? DesktopUnlockOutcome.LockedOut : DesktopUnlockOutcome.Refused;
        }

        if (user.SecurityStamp != stamp || user.PasswordHash != hash)
        {
            return DesktopUnlockOutcome.Refused;
        }

        await users.ResetAccessFailedCountAsync(user);
        audit.Record(AuditActions.Unlock, actorId: user.Id, details: Details(session));
        await db.SaveChangesAsync(cancellationToken);
        return DesktopUnlockOutcome.Unlocked;
    }

    // Ends the session, whoever holds the token. Nothing happens for a session that is over already.
    public async Task SignOutAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var session = await db.DesktopSessions.AsNoTracking()
            .SingleOrDefaultAsync(s => s.Id == sessionId && s.RevokedAt == null, cancellationToken);
        if (session is null)
        {
            return;
        }

        var now = Now;
        await db.DesktopSessions.Where(s => s.Id == session.Id)
            .ExecuteUpdateAsync(set => set.SetProperty(s => s.RevokedAt, now), cancellationToken);
        audit.Record(AuditActions.SignOut, actorId: session.StaffUserId, details: Details(session));
        await db.SaveChangesAsync(cancellationToken);
    }

    // The account's desktop sign-ins that haven't ended by sign-out or revocation, expired ones included: a computer
    // that hasn't reached a server since still holds its token. Reads only, for the administrator's confirmation.
    public Task<int> UnendedCountAsync(Guid staffUserId, CancellationToken cancellationToken) =>
        db.DesktopSessions.CountAsync(s => s.StaffUserId == staffUserId && s.RevokedAt == null, cancellationToken);

    // Revokes every one of them, as the signed-in administrator, at once: from now on each token is refused, saying
    // so, and the app holding it deletes what it keeps on that computer the next time it presents it. Recorded once,
    // with the account and how many; it may be none. The administrator's own account is allowed: it may be their
    // laptop that is lost.
    public async Task<int> RevokeAllAsync(Guid staffUserId, Guid administratorId, CancellationToken cancellationToken)
    {
        var now = Now;
        var revoked = await db.DesktopSessions.Where(s => s.StaffUserId == staffUserId && s.RevokedAt == null)
            .ExecuteUpdateAsync(set => set.SetProperty(s => s.RevokedAt, now).SetProperty(s => s.RevokedBy, administratorId), cancellationToken);
        audit.Record(AuditActions.DesktopRevoked, tableName: "staff_users", rowId: staffUserId, actorId: administratorId,
            details: JsonSerializer.Serialize(new { client = "desktop", sessions = revoked }));
        await db.SaveChangesAsync(cancellationToken);
        return revoked;
    }

    static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(token)));

    static string Details(DesktopSession session) =>
        JsonSerializer.Serialize(new { client = "desktop", device = session.Device, session = session.Id });
}

public enum DesktopSignInOutcome
{
    SignedIn,

    // A wrong email, password or code: one answer for all three.
    Refused,

    LockedOut,

    // No authenticator yet, or a password still to change: the web sign-in comes first.
    NotSetUp,

    NotAClinician,
}

public enum DesktopUnlockOutcome
{
    Unlocked,

    // A wrong password.
    Refused,

    // A wrong password, and the account is now locked out: its sessions are refused until the lockout ends.
    LockedOut,
}

// The outcome and, when signed in, the token the app keeps, the session's 30-day end, and whose it is.
public sealed record DesktopSignIn(DesktopSignInOutcome Outcome, string? Token = null, DateTimeOffset? ExpiresAt = null, string? DisplayName = null, Guid? ClinicianId = null);

public sealed record DesktopSignedIn(DesktopSession Session, StaffUser User)
{
    // The sign-in that made the session is SessionTimes.Longest old or more: the token is refused, saying so.
    public bool SignInEnded { get; init; }

    // An administrator revoked the session: the token is refused, saying so, and the app deletes what it keeps.
    public bool Revoked { get; init; }
}
