using Ehr.Data;
using Ehr.Data.Accounts;
using Ehr.Data.Audit;
using Ehr.Domain.Audit;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Ehr.Domain.Accounts;

// Staff accounts, as an admin manages them: an account is created by invitation, without a password, and the person
// chooses their password through a one-time setup link and enrolls an authenticator at their first sign-in. No
// temporary passwords change hands. Accounts are disabled by lockout, never deleted.
public sealed class StaffAccounts(UserManager<StaffUser> users, EhrDbContext db, AuditRecorder audit)
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
