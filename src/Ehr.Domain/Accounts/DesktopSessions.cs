using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ehr.Data;
using Ehr.Data.Accounts;
using Ehr.Data.Audit;
using Ehr.Domain.Audit;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Ehr.Domain.Accounts;

// The desktop app's sign-in (DesktopSession). Both factors at once, the same checks as the web sign-in (StaffSignIn),
// then a token the app keeps and sends with every sync request; only its hash is stored. The account must have
// finished its web setup first, since the authenticator is enrolled there, and must be a clinician: the desktop app
// is for clinical work. A session ends when it expires, when the app signs out, or when the account's security stamp
// changes, as at a password change. Every sign-in, refusal and sign-out goes in the audit log.
public sealed class DesktopSessions(UserManager<StaffUser> users, StaffSignIn signIn, EhrDbContext db, AuditRecorder audit)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);

    public async Task<DesktopSignIn> SignInAsync(string email, string password, string code, string device, CancellationToken cancellationToken)
    {
        var check = await signIn.CheckPasswordAsync(email, password, cancellationToken);
        if (check.Outcome != PasswordOutcome.Ok)
        {
            return new(check.Outcome == PasswordOutcome.LockedOut ? DesktopSignInOutcome.LockedOut : DesktopSignInOutcome.Refused);
        }

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
        var now = DateTimeOffset.UtcNow;
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
        return new(DesktopSignInOutcome.SignedIn, token, session.ExpiresAt, user.DisplayName);
    }

    // The session behind a token and its account, or null when the session is over: no such token, expired, signed
    // out, the account locked out or disabled, or its security stamp changed. Reads only, so the cloud app can do it.
    public async Task<DesktopSignedIn?> AuthenticateAsync(string token, CancellationToken cancellationToken)
    {
        var hash = Hash(token);
        var now = DateTimeOffset.UtcNow;
        var session = await db.DesktopSessions.AsNoTracking()
            .SingleOrDefaultAsync(s => s.TokenHash == hash && s.RevokedAt == null && s.ExpiresAt > now, cancellationToken);
        if (session is null)
        {
            return null;
        }

        var user = await users.FindByIdAsync(session.StaffUserId.ToString());
        if (user is null || user.SecurityStamp != session.SecurityStamp || await users.IsLockedOutAsync(user))
        {
            return null;
        }

        return new(session, user);
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

        var now = DateTimeOffset.UtcNow;
        await db.DesktopSessions.Where(s => s.Id == session.Id)
            .ExecuteUpdateAsync(set => set.SetProperty(s => s.RevokedAt, now), cancellationToken);
        audit.Record(AuditActions.SignOut, actorId: session.StaffUserId, details: Details(session));
        await db.SaveChangesAsync(cancellationToken);
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

// The outcome and, when signed in, the token the app keeps, until when it works, and whose it is.
public sealed record DesktopSignIn(DesktopSignInOutcome Outcome, string? Token = null, DateTimeOffset? ExpiresAt = null, string? DisplayName = null);

public sealed record DesktopSignedIn(DesktopSession Session, StaffUser User);
