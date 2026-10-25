using Ehr.Data;
using Ehr.Data.Accounts;
using Ehr.Data.Audit;
using Ehr.Domain.Audit;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Ehr.Domain.Accounts;

// A patient's self-registration for the portal: the enrollment code from the clinic proves which patient they are,
// and the confirmation email proves the address is theirs. The code is redeemed as the account is created, in one
// transaction, and never works again. An unknown, expired, used or already-registered code gets one answer, so the
// page gives nothing away.
public sealed class PatientRegistration(UserManager<PatientUser> users, EhrDbContext db, AuditRecorder audit, IEmailSender email)
{
    public async Task<Registration> RegisterAsync(
        string address, string password, string code, Func<Guid, string, string> confirmationLink, CancellationToken cancellationToken)
    {
        var hash = EnrollmentCodes.Hash(code);
        var now = DateTimeOffset.UtcNow;
        var enrollment = await db.PatientEnrollments
            .SingleOrDefaultAsync(e => e.CodeHash == hash && e.RedeemedAt == null && e.ExpiresAt > now, cancellationToken);
        if (enrollment is null || await db.PatientUsers.AnyAsync(u => u.PatientId == enrollment.PatientId, cancellationToken))
        {
            return Registration.InvalidCode;
        }

        var user = new PatientUser { UserName = address, Email = address, PatientId = enrollment.PatientId };
        var result = await users.CreateAsync(user, password);
        if (!result.Succeeded)
        {
            return new(false, false, [.. result.Errors.Select(e => e.Description)]);
        }

        enrollment.RedeemedAt = now;
        audit.Record(AuditActions.PatientRegistered, patientId: enrollment.PatientId, actorId: user.Id);
        await db.SaveChangesAsync(cancellationToken);

        var token = await users.GenerateEmailConfirmationTokenAsync(user);
        await email.SendAsync(address, "Confirm your patient portal account",
            $"""
            Open this link to confirm your email address and finish setting up your patient portal account:

            {confirmationLink(user.Id, token)}

            If you didn't register, you can ignore this message.
            """,
            cancellationToken);
        return Registration.Registered;
    }

    public async Task<bool> ConfirmAsync(Guid userId, string token, CancellationToken cancellationToken)
    {
        var user = await users.FindByIdAsync(userId.ToString());
        if (user is null)
        {
            return false;
        }

        var result = await users.ConfirmEmailAsync(user, token);
        if (result.Succeeded)
        {
            audit.Record(AuditActions.EmailConfirmed, patientId: user.PatientId, actorId: user.Id);
            await db.SaveChangesAsync(cancellationToken);
        }

        return result.Succeeded;
    }
}

public sealed record Registration(bool Succeeded, bool CodeInvalid, IReadOnlyList<string> Errors)
{
    public static readonly Registration Registered = new(true, false, []);

    public static readonly Registration InvalidCode = new(false, true, []);
}
