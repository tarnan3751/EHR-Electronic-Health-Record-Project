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
    public async Task<string> IssueAsync(Guid grant, string proxyEmail, CancellationToken cancellationToken)
    {
        var code = Convert.ToHexString(RandomNumberGenerator.GetBytes(20));
        db.Add(new ProxyInvitation
        {
            Id = Guid.CreateVersion7(),
            GrantId = grant,
            Email = proxyEmail.Trim().ToLowerInvariant(),
            CodeHash = EnrollmentCodes.Hash(code),
            ExpiresAt = DateTimeOffset.UtcNow + Lifetime,
        });
        await db.SaveChangesAsync(cancellationToken);
        return code;
    }

    // Whether a code is one that could still be accepted: for the page to show its form or say why not.
    public async Task<bool> IsOpenAsync(string? code, CancellationToken cancellationToken) => await FindAsync(code, cancellationToken) is not null;

    // Creates the account the invitation is for: its address is the invitation's, already confirmed, since the link
    // was sent there. The invitation never works again.
    public async Task<Acceptance> AcceptAsync(string? code, string name, string password, CancellationToken cancellationToken)
    {
        if (await FindAsync(code, cancellationToken) is not { } invitation)
        {
            return Acceptance.Invalid;
        }

        if (await users.FindByEmailAsync(invitation.Email) is not null || await users.FindByNameAsync(invitation.Email) is not null)
        {
            return Acceptance.HasAccount;
        }

        var user = new PatientUser { UserName = invitation.Email, Email = invitation.Email, EmailConfirmed = true, DisplayName = name.Trim() };
        var created = await users.CreateAsync(user, password);
        if (!created.Succeeded)
        {
            return new Acceptance(AcceptanceOutcome.Refused, [.. created.Errors.Select(e => e.Description)]);
        }

        invitation.RedeemedAt = DateTimeOffset.UtcNow;
        // No patient: the account has no record of its own. The actor is the account itself.
        audit.Record(AuditActions.ProxyAccountCreated, actorId: user.Id);
        await db.SaveChangesAsync(cancellationToken);
        return Acceptance.Accepted;
    }

    async Task<ProxyInvitation?> FindAsync(string? code, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        var hash = EnrollmentCodes.Hash(code);
        var now = DateTimeOffset.UtcNow;
        return await db.ProxyInvitations.SingleOrDefaultAsync(i => i.CodeHash == hash && i.RedeemedAt == null && i.ExpiresAt > now, cancellationToken);
    }
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
