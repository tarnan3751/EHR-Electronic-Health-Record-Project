using System.Security.Claims;
using Ehr.Data;
using Ehr.Data.Accounts;
using Ehr.Data.Audit;
using Ehr.Domain.Audit;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;

namespace Ehr.Domain.Accounts;

// A sign-in to the portal: password, confirmed email, then the Patient cookie, which carries the patient the account
// belongs to when it has one (a proxy-only account has none). Every outcome goes in the audit log.
public sealed class PatientSignIn(
    UserManager<PatientUser> users, IUserClaimsPrincipalFactory<PatientUser> principals, AuditRecorder audit, EhrDbContext db)
{
    public async Task<PatientSignInOutcome> SignInAsync(HttpContext context, string email, string password, CancellationToken cancellationToken)
    {
        var user = await users.FindByEmailAsync(email);
        if (user is null)
        {
            return PatientSignInOutcome.Failed;
        }

        if (await users.IsLockedOutAsync(user))
        {
            await RecordAsync(AuditActions.SignInFailed, user, "locked out", cancellationToken);
            return PatientSignInOutcome.LockedOut;
        }

        if (!await users.CheckPasswordAsync(user, password))
        {
            await users.AccessFailedAsync(user);
            await RecordAsync(AuditActions.SignInFailed, user, "wrong password", cancellationToken);
            return await users.IsLockedOutAsync(user) ? PatientSignInOutcome.LockedOut : PatientSignInOutcome.Failed;
        }

        if (!user.EmailConfirmed)
        {
            return PatientSignInOutcome.EmailNotConfirmed;
        }

        await users.ResetAccessFailedCountAsync(user);
        await context.SignInAsync(AccountSchemes.Patient, await principals.CreateAsync(user));
        await RecordAsync(AuditActions.SignIn, user, null, cancellationToken);
        return PatientSignInOutcome.SignedIn;
    }

    public async Task SignOutAsync(HttpContext context, Guid? userId, Guid? patientId, CancellationToken cancellationToken)
    {
        await context.SignOutAsync(AccountSchemes.Patient);
        if (userId is { } id)
        {
            audit.Record(AuditActions.SignOut, patientId: patientId, actorId: id);
            await db.SaveChangesAsync(cancellationToken);
        }

        // Signing out only tells the browser to drop the cookie; the account read from it is still this request's. The
        // rest of the request, the page that says "Signed out" included, is no longer signed in.
        context.User = new ClaimsPrincipal(new ClaimsIdentity());
    }

    async Task RecordAsync(string action, PatientUser user, string? reason, CancellationToken cancellationToken)
    {
        audit.Record(action, patientId: user.PatientId, reason: reason, actorId: user.Id);
        await db.SaveChangesAsync(cancellationToken);
    }
}

public enum PatientSignInOutcome
{
    SignedIn,

    Failed,

    LockedOut,

    EmailNotConfirmed,
}
