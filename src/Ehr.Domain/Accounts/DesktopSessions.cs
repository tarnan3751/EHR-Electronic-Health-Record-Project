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
// changes, as at a password change. Every sign-in, refusal and sign-out goes in the audit log.
//
// The token works for 12 hours after the sign-in that made it (SessionTimes.Longest), however busy, as a web sign-in
// does (the expansion plan's step 19a): after that every request is refused, saying the sign-in has ended
// (DesktopTokenHandler), and the app asks for both factors again. Lifetime is how long the app may open its offline
// copy without a sign-in (the sign-in's OfflineAccess); revoking a session is unchanged. The clock is the Desktop
// scheme's (its options' TimeProvider), which tests move on.
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
    // out, the account locked out or disabled, or its security stamp changed. A session good but for its sign-in being
    // 12 hours old comes back with SignInEnded, and is good for nothing but saying so. Reads only, so the cloud app can
    // do it.
    public async Task<DesktopSignedIn?> AuthenticateAsync(string token, CancellationToken cancellationToken)
    {
        var hash = Hash(token);
        var now = Now;
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

        return new(session, user) { SignInEnded = session.CreatedAt + SessionTimes.Longest <= now };
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
public sealed record DesktopSignIn(DesktopSignInOutcome Outcome, string? Token = null, DateTimeOffset? ExpiresAt = null, string? DisplayName = null, Guid? ClinicianId = null);

public sealed record DesktopSignedIn(DesktopSession Session, StaffUser User)
{
    // The sign-in that made the session is SessionTimes.Longest old or more: the token is refused, saying so.
    public bool SignInEnded { get; init; }
}
