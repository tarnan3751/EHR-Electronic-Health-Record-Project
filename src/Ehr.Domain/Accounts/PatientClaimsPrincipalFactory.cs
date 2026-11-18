using System.Security.Claims;
using Ehr.Data.Accounts;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Ehr.Domain.Accounts;

// Identity's claims for a portal account, plus the patient it belongs to when it has one, so portal pages know the
// account's own record from the cookie alone.
public sealed class PatientClaimsPrincipalFactory(UserManager<PatientUser> users, IOptions<IdentityOptions> options)
    : UserClaimsPrincipalFactory<PatientUser>(users, options)
{
    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(PatientUser user)
    {
        var identity = await base.GenerateClaimsAsync(user);
        identity.AddClaim(new Claim(EhrClaims.Portal, "account"));
        // Only an account with a record of its own: a proxy-only account has none (step 6b).
        if (user.PatientId is { } patient)
        {
            identity.AddClaim(new Claim(EhrClaims.PatientId, patient.ToString()));
        }

        if (!string.IsNullOrWhiteSpace(user.DisplayName))
        {
            identity.AddClaim(new Claim(EhrClaims.DisplayName, user.DisplayName));
        }

        return identity;
    }
}
