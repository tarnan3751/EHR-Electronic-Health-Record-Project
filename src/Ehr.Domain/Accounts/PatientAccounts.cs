using Ehr.Data;
using Ehr.Data.Accounts;
using Ehr.Data.Audit;
using Ehr.Domain.Audit;
using Microsoft.AspNetCore.Identity;

namespace Ehr.Domain.Accounts;

// A patient's own account care: a forgotten password. The request gets the same answer whether or not the address has
// a confirmed account, so the page gives nothing away, and the email carries a one-time link; the new password goes
// through Identity's rules (AddEhrAccounts) and ends the account's other sessions. Both steps are audit events.
public sealed class PatientAccounts(UserManager<PatientUser> users, EhrDbContext db, AuditRecorder audit, IEmailSender email)
{
    public async Task RequestPasswordResetAsync(string address, Func<Guid, string, string> resetLink, CancellationToken cancellationToken)
    {
        var user = await users.FindByEmailAsync(address);
        if (user is null || !user.EmailConfirmed)
        {
            return;
        }

        var token = await users.GeneratePasswordResetTokenAsync(user);
        audit.Record(AuditActions.PasswordResetRequested, patientId: user.PatientId, actorId: user.Id);
        await db.SaveChangesAsync(cancellationToken);
        await email.SendAsync(address, "Reset your patient portal password",
            $"""
            Open this link to choose a new password for your patient portal account:

            {resetLink(user.Id, token)}

            If you didn't ask for this, you can ignore this message; your password hasn't changed.
            """,
            cancellationToken);
    }

    // The new password, through the token in the link. A spent or invented link reads as an invalid token.
    public async Task<IdentityResult> ResetPasswordAsync(Guid userId, string token, string password, CancellationToken cancellationToken)
    {
        var user = await users.FindByIdAsync(userId.ToString());
        if (user is null)
        {
            return IdentityResult.Failed(users.ErrorDescriber.InvalidToken());
        }

        var result = await users.ResetPasswordAsync(user, token, password);
        if (result.Succeeded)
        {
            audit.Record(AuditActions.PasswordChanged, patientId: user.PatientId, actorId: user.Id);
            await db.SaveChangesAsync(cancellationToken);
        }

        return result;
    }
}
