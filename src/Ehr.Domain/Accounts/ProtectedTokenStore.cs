using Ehr.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;

namespace Ehr.Domain.Accounts;

// Identity's user store, with the secrets it keeps for itself encrypted before they are saved. Those are the
// authenticator key, the secret behind every sign-in code, and the unused recovery codes; Identity keeps both in the
// account's token table (ehr.staff_user_tokens, ehr.patient_user_tokens) under its own login provider, and on its own
// it stores them as plain text, so a copy of the table, the replica or a backup would be enough to make codes for any
// account. They are encrypted with Data Protection, whose keys are themselves encrypted with the certificate (Program),
// and both apps share those keys, so the cloud app checks codes as the on-prem app does.
//
// A value saved before this change is plain text and has no prefix. It is still read as it is, so nobody is locked out,
// and it is encrypted the next time it is saved: a new authenticator enrollment, new recovery codes, or a redeemed code.
//
// It is also the store of an account's passkeys (the expansion plan's step 19), TUserPasskey, which are saved as they
// are: a passkey holds no secret. Only staff accounts have a passkey table (StaffUserPasskey); the patient store is
// given Identity's own type, which the database doesn't map, so any passkey call on a patient account throws.
public sealed class ProtectedTokenStore<TUser, TUserClaim, TUserLogin, TUserToken, TUserPasskey>(
    EhrDbContext context, IDataProtectionProvider dataProtection, IdentityErrorDescriber? describer = null)
    : UserOnlyStore<TUser, EhrDbContext, Guid, TUserClaim, TUserLogin, TUserToken, TUserPasskey>(context, describer)
    where TUser : IdentityUser<Guid>
    where TUserClaim : IdentityUserClaim<Guid>, new()
    where TUserLogin : IdentityUserLogin<Guid>, new()
    where TUserToken : IdentityUserToken<Guid>, new()
    where TUserPasskey : IdentityUserPasskey<Guid>, new()
{
    // The login provider UserStoreBase files its own tokens under (private there).
    const string IdentityProvider = "[AspNetUserStore]";

    // Marks an encrypted value, and leaves room for another scheme later.
    const string Prefix = "dp1:";

    readonly IDataProtector protector = dataProtection.CreateProtector("Ehr.Accounts.UserTokens", typeof(TUser).Name);

    public override Task SetTokenAsync(TUser user, string loginProvider, string name, string? value, CancellationToken cancellationToken) =>
        base.SetTokenAsync(user, loginProvider, name,
            loginProvider == IdentityProvider && value is not null ? Prefix + protector.Protect(value) : value,
            cancellationToken);

    public override async Task<string?> GetTokenAsync(TUser user, string loginProvider, string name, CancellationToken cancellationToken)
    {
        var stored = await base.GetTokenAsync(user, loginProvider, name, cancellationToken);
        return loginProvider == IdentityProvider && stored is not null && stored.StartsWith(Prefix, StringComparison.Ordinal)
            ? protector.Unprotect(stored[Prefix.Length..])
            : stored;
    }
}
