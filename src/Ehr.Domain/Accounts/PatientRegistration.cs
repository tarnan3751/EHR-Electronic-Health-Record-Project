using Ehr.Data;
using Ehr.Data.Accounts;
using Ehr.Data.Audit;
using Ehr.Domain.Audit;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Ehr.Domain.Accounts;

// A patient's self-registration for the portal: the enrollment code from the clinic proves which patient they are,
// and the confirmation email proves the address is theirs. The code is redeemed as the account is created, in one
// transaction, and never works again. An unknown, expired, used or already-registered code gets one answer, and an
// address that already has an account gets the answer a new one gets, so the page gives nothing away.
public sealed class PatientRegistration(UserManager<PatientUser> users, EhrDbContext db, AuditRecorder audit, IEmailSender email)
{
    public async Task<Registration> RegisterAsync(
        string address, string password, string code, Func<Guid, string, string> confirmationLink, string signInLink, string forgotLink,
        CancellationToken cancellationToken)
    {
        var hash = EnrollmentCodes.Hash(code);
        var now = DateTimeOffset.UtcNow;
        var enrollment = await db.PatientEnrollments
            .SingleOrDefaultAsync(e => e.CodeHash == hash && e.RedeemedAt == null && e.ExpiresAt > now, cancellationToken);
        if (enrollment is null)
        {
            return Registration.InvalidCode;
        }

        // The patient's account, if they have one. A confirmed one is theirs for good; one that was never confirmed
        // (a mistyped address, say) is taken over below by whoever holds a new code from the clinic.
        var existing = await db.PatientUsers.FirstOrDefaultAsync(u => u.PatientId == enrollment.PatientId, cancellationToken);
        if (existing is { EmailConfirmed: true })
        {
            return Registration.InvalidCode;
        }

        // The password is checked before the address is looked at, so its errors say nothing about the address.
        var user = new PatientUser { UserName = address, Email = address, PatientId = enrollment.PatientId };
        var errors = new List<string>();
        foreach (var validator in users.PasswordValidators)
        {
            errors.AddRange((await validator.ValidateAsync(users, user, password)).Errors.Select(e => e.Description));
        }

        if (errors.Count > 0)
        {
            return new(false, false, errors);
        }

        // UC5's "email already registered" course (Simar Rekhi): the address belongs to another patient's account.
        // The holder of a code is a patient, not that address's owner, so the page answers exactly as it does for a
        // registration that worked, and the code stays unused. The address's owner is told by email instead.
        var owner = await users.FindByEmailAsync(address) ?? await users.FindByNameAsync(address);
        if (owner is not null && owner.Id != existing?.Id)
        {
            // The work a registration does, so this answer takes as long.
            users.PasswordHasher.HashPassword(user, password);
            if (owner.EmailConfirmed)
            {
                await email.SendAsync(owner.Email!, "Your patient portal account",
                    $"""
                    Someone tried to register a patient portal account with this address. If it was you, sign in at {signInLink} or reset your password at {forgotLink}. If it wasn't, you can ignore this message.
                    """,
                    cancellationToken);
            }
            else
            {
                await SendConfirmationAsync(owner, confirmationLink, cancellationToken);
            }

            return Registration.Registered;
        }

        if (existing is not null)
        {
            // Through UserManager, so the security stamp changes and the old address's confirmation links stop working.
            var changes = new[]
            {
                await users.SetEmailAsync(existing, address),
                await users.SetUserNameAsync(existing, address),
                await users.RemovePasswordAsync(existing),
                await users.AddPasswordAsync(existing, password),
                // Wrong guesses at the old password don't lock the new owner out.
                await users.ResetAccessFailedCountAsync(existing),
                await users.SetLockoutEndDateAsync(existing, null),
            };
            if (changes.FirstOrDefault(c => !c.Succeeded) is { } failed)
            {
                return new(false, false, [.. failed.Errors.Select(e => e.Description)]);
            }

            enrollment.RedeemedAt = now;
            audit.Record(AuditActions.RegistrationReplaced, patientId: enrollment.PatientId);
            await db.SaveChangesAsync(cancellationToken);
            await SendConfirmationAsync(existing, confirmationLink, cancellationToken);
            return Registration.Registered;
        }

        var result = await users.CreateAsync(user, password);
        if (!result.Succeeded)
        {
            return new(false, false, [.. result.Errors.Select(e => e.Description)]);
        }

        enrollment.RedeemedAt = now;
        // Nobody is signed in, so the event has no actor: the patient says whose account it is.
        audit.Record(AuditActions.PatientRegistered, patientId: enrollment.PatientId);
        await db.SaveChangesAsync(cancellationToken);

        await SendConfirmationAsync(user, confirmationLink, cancellationToken);
        return Registration.Registered;
    }

    // UC5's other way through (Simar Rekhi): the confirmation link expired, or the email got lost, and the enrollment
    // code is already used, so registering again can't help. A new link goes to an account whose address isn't
    // confirmed yet; for any other address nothing is sent. The page answers the same either way, so it doesn't tell
    // anyone which addresses have accounts.
    public async Task ResendConfirmationAsync(string address, Func<Guid, string, string> confirmationLink, CancellationToken cancellationToken)
    {
        var user = await users.FindByEmailAsync(address);
        if (user is null || user.EmailConfirmed)
        {
            return;
        }

        audit.Record(AuditActions.ConfirmationResent, patientId: user.PatientId);
        await db.SaveChangesAsync(cancellationToken);
        await SendConfirmationAsync(user, confirmationLink, cancellationToken);
    }

    async Task SendConfirmationAsync(PatientUser user, Func<Guid, string, string> confirmationLink, CancellationToken cancellationToken)
    {
        var token = await users.GenerateEmailConfirmationTokenAsync(user);
        await email.SendAsync(user.Email!, "Confirm your patient portal account",
            $"""
            Open this link to confirm your email address and finish setting up your patient portal account:

            {confirmationLink(user.Id, token)}

            If you didn't register, you can ignore this message.
            """,
            cancellationToken);
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
            audit.Record(AuditActions.EmailConfirmed, patientId: user.PatientId);
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
