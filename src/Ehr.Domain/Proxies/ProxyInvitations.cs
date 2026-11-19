using System.Security.Cryptography;
using Ehr.Data;
using Ehr.Data.Accounts;
using Ehr.Data.Audit;
using Ehr.Data.Proxies;
using Ehr.Domain.Accounts;
using Ehr.Domain.Audit;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Ehr.Domain.Proxies;

// Invitations to create a proxy-only portal account (the expansion plan's step 6b). Whenever access to a record is
// given, the address it names is sent a link that works once and expires. Whoever opens it, which proves the address
// is theirs, chooses a name and a password and gets a portal account with no record of its own. The grant is already
// addressed to that email, so the account sees that record and nothing else. An address that already has a portal
// account doesn't need the link: it signs in.
public sealed class ProxyInvitations(EhrDbContext db, UserManager<PatientUser> users, AuditRecorder audit)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);

    // Records an invitation for the grant's address and returns the code for its link. Only the code's hash is kept.
    public async Task<string> IssueAsync(Guid grant, CancellationToken cancellationToken)
    {
        var code = Convert.ToHexString(RandomNumberGenerator.GetBytes(20));
        // Through the database's function: it takes the address from the grant, which must be one the signed-in user
        // gave, and sets the seven days. The app roles can't write or read the table (db/policies/45-codes.sql).
        await CodeStore.IssueInvitationAsync(db, Guid.CreateVersion7(), grant, EnrollmentCodes.Hash(code), cancellationToken);
        return code;
    }

    // Whether a code is one that could still be accepted: for the page to show its form or say why not.
    public async Task<bool> IsOpenAsync(string? code, CancellationToken cancellationToken) => await FindAsync(code, cancellationToken) is not null;

    // Creates the account the invitation is for: its address is the invitation's, already confirmed, since the link
    // was sent there. The invitation never works again.
    public async Task<Acceptance> AcceptAsync(string? code, string name, string password, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return Acceptance.Invalid;
        }

        // The invitation is claimed first, in one statement: of two posts of the same code, the second waits on the
        // row and then finds it used. Everything after it is undone together if the account isn't created.
        const string savepoint = "invitation";
        var transaction = db.Database.CurrentTransaction!;
        await transaction.CreateSavepointAsync(savepoint, cancellationToken);
        if (await CodeStore.RedeemInvitationAsync(db, EnrollmentCodes.Hash(code), cancellationToken) is not { } invitation)
        {
            return Acceptance.Invalid;
        }

        var address = invitation.Email;
        var existing = await users.FindByEmailAsync(address) ?? await users.FindByNameAsync(address);
        if (existing is { EmailConfirmed: true })
        {
            // Theirs already: they sign in. The invitation stays unused.
            await transaction.RollbackToSavepointAsync(savepoint, cancellationToken);
            return Acceptance.HasAccount;
        }

        if (existing is not null)
        {
            // An account that names this address but never confirmed it: someone registered with an address that
            // wasn't theirs, by mistake or to wait for it to be confirmed for them. Whoever holds this invitation
            // has shown the mailbox is theirs, so the unconfirmed account gives the address up. Its patient can
            // register again with a new enrollment code from the clinic. Through UserManager, so its security stamp
            // changes and confirmation links sent to this address stop working.
            var parked = $"unconfirmed-{existing.Id:N}@unconfirmed.invalid";
            var released = new[] { await users.SetEmailAsync(existing, parked), await users.SetUserNameAsync(existing, parked) };
            if (released.Any(result => !result.Succeeded))
            {
                await transaction.RollbackToSavepointAsync(savepoint, cancellationToken);
                db.Entry(existing).State = EntityState.Detached;
                return Acceptance.Invalid;
            }

            audit.Record(AuditActions.RegistrationReplaced, patientId: existing.PatientId, actorId: existing.Id);
        }

        var user = new PatientUser { UserName = address, Email = address, EmailConfirmed = true, DisplayName = name.Trim() };
        var created = await users.CreateAsync(user, password);
        if (!created.Succeeded)
        {
            await transaction.RollbackToSavepointAsync(savepoint, cancellationToken);
            if (existing is not null)
            {
                db.Entry(existing).State = EntityState.Detached;
            }

            return new Acceptance(AcceptanceOutcome.Refused, [.. created.Errors.Select(e => e.Description)]);
        }

        // No patient: the account has no record of its own. The actor is the account itself.
        audit.Record(AuditActions.ProxyAccountCreated, actorId: user.Id);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.ReleaseSavepointAsync(savepoint, cancellationToken);
        return Acceptance.Accepted;
    }

    async Task<(Guid Id, string Email)?> FindAsync(string? code, CancellationToken cancellationToken) =>
        string.IsNullOrWhiteSpace(code) ? null : await CodeStore.FindInvitationAsync(db, EnrollmentCodes.Hash(code), cancellationToken);
}

public enum AcceptanceOutcome
{
    Accepted,

    // Unknown, used or expired.
    Invalid,

    // The address already has a portal account: sign in instead.
    HasAccount,

    // The password didn't meet the rules; Errors says how.
    Refused,
}

public sealed record Acceptance(AcceptanceOutcome Outcome, IReadOnlyList<string> Errors)
{
    public static readonly Acceptance Accepted = new(AcceptanceOutcome.Accepted, []);

    public static readonly Acceptance Invalid = new(AcceptanceOutcome.Invalid, []);

    public static readonly Acceptance HasAccount = new(AcceptanceOutcome.HasAccount, []);
}
