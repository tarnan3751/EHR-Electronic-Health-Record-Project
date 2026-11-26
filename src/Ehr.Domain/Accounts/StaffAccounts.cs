using Ehr.Data;
using Ehr.Data.Accounts;
using Ehr.Data.Audit;
using Ehr.Domain.Audit;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Ehr.Domain.Accounts;

// Staff accounts, as an admin manages them: an account is created by invitation, without a password, and the person
// chooses their password through a one-time setup link and enrolls an authenticator at their first sign-in. No
// temporary passwords change hands. A forgotten password gets a new setup link, a lost phone or device a new
// authenticator enrollment, with the account's passkeys removed (step 19), and a person who leaves has their account
// disabled: locked out indefinitely, never deleted. Each of those changes the account's security stamp, so its cookies
// and desktop sessions end, and a sign-in begun before it, between its password and its second factor, goes back to the
// password (StaffSignIn.UnchangedAsync, step 19.1). Every action is an audit event.
public sealed class StaffAccounts(UserManager<StaffUser> users, EhrDbContext db, AuditRecorder audit, StaffPasskeys passkeys)
{
    public Task<List<StaffUser>> ListAsync(CancellationToken cancellationToken) =>
        db.StaffUsers.AsNoTracking().OrderBy(u => u.DisplayName).ThenBy(u => u.Id).ToListAsync(cancellationToken);

    public Task<StaffUser?> FindAsync(Guid id) => users.FindByIdAsync(id.ToString());

    // Creates the account and returns the token for its setup link. The link is the invitation; the admin passes it
    // on, and it works once.
    public async Task<Invitation> InviteAsync(string email, string displayName, IReadOnlyList<string> roles, CancellationToken cancellationToken)
    {
        var user = new StaffUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            DisplayName = displayName,
            Roles = [.. roles],
        };
        var result = await users.CreateAsync(user);
        if (!result.Succeeded)
        {
            return new(result, null, null);
        }

        var token = await users.GeneratePasswordResetTokenAsync(user);
        audit.Record(AuditActions.StaffInvited, tableName: "staff_users", rowId: user.Id);
        await db.SaveChangesAsync(cancellationToken);
        return new(result, user, token);
    }

    // A new setup link, for an invitation that never arrived or a forgotten password. A password the account had stops
    // working now; the person chooses the next one through the link. The account is held first, as for each action
    // below (StaffUserLock, step 19.1's final review), so a passkey added or removed at the same moment waits or is
    // waited for, and never fails the action on the account's concurrency stamp.
    public async Task<Invitation> ReissueAsync(StaffUser user, CancellationToken cancellationToken)
    {
        await StaffUserLock.AcquireAsync(db, user, cancellationToken);
        if (user.PasswordHash is not null)
        {
            var removed = await users.RemovePasswordAsync(user);
            if (!removed.Succeeded)
            {
                return new(removed, null, null);
            }
        }

        var token = await users.GeneratePasswordResetTokenAsync(user);
        audit.Record(AuditActions.PasswordReset, tableName: "staff_users", rowId: user.Id);
        await db.SaveChangesAsync(cancellationToken);
        return new(IdentityResult.Success, user, token);
    }

    // The account enrolls an authenticator app again at its next sign-in; the old app, the recovery codes and every
    // passkey stop working: a passkey is never left on an account without its authenticator, and a lost device may hold
    // either. How many passkeys were removed, each recorded with the admin who removed it. The account is held first
    // (StaffUserLock, step 19.1), so a passkey whose add finishes at the same moment is either saved before the reset
    // reads the account's passkeys, and removed and counted with them, or refused at its finish (StaffPasskeys).
    public async Task<int> ResetAuthenticatorAsync(StaffUser user, CancellationToken cancellationToken)
    {
        await StaffUserLock.AcquireAsync(db, user, cancellationToken);
        var removed = 0;
        foreach (var passkey in await passkeys.ListAsync(user))
        {
            removed += await passkeys.RemoveAsync(user, passkey.CredentialId, cancellationToken) ? 1 : 0;
        }

        await users.SetTwoFactorEnabledAsync(user, false);
        await users.ResetAuthenticatorKeyAsync(user);
        audit.Record(AuditActions.AuthenticatorReset, tableName: "staff_users", rowId: user.Id);
        await db.SaveChangesAsync(cancellationToken);
        return removed;
    }

    // Disabled: locked out with no end, which sign-in and row-level security both refuse. Nothing is deleted.
    public async Task DisableAsync(StaffUser user, CancellationToken cancellationToken)
    {
        await StaffUserLock.AcquireAsync(db, user, cancellationToken);
        await users.SetLockoutEndDateAsync(user, DateTimeOffset.MaxValue);
        await users.UpdateSecurityStampAsync(user);
        audit.Record(AuditActions.AccountDisabled, tableName: "staff_users", rowId: user.Id);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task EnableAsync(StaffUser user, CancellationToken cancellationToken)
    {
        await StaffUserLock.AcquireAsync(db, user, cancellationToken);
        await users.SetLockoutEndDateAsync(user, null);
        await users.ResetAccessFailedCountAsync(user);
        audit.Record(AuditActions.AccountEnabled, tableName: "staff_users", rowId: user.Id);
        await db.SaveChangesAsync(cancellationToken);
    }

    public static bool IsDisabled(StaffUser user) => user.LockoutEnd == DateTimeOffset.MaxValue;

    // The invited person's first password, through the token in their setup link.
    public async Task<IdentityResult> SetPasswordAsync(StaffUser user, string token, string password, CancellationToken cancellationToken)
    {
        var result = await users.ResetPasswordAsync(user, token, password);
        if (result.Succeeded)
        {
            audit.Record(AuditActions.PasswordChanged, actorId: user.Id);
            await db.SaveChangesAsync(cancellationToken);
        }

        return result;
    }
}

public sealed record Invitation(IdentityResult Result, StaffUser? User, string? Token);
