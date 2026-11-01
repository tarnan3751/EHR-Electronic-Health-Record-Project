using System.Security.Claims;
using Ehr.Data.Accounts;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Ehr.Domain.Accounts;

// Identity's claims for a patient account, plus the patient it belongs to, so portal pages know their subject from
// the cookie alone.
public sealed class PatientClaimsPrincipalFactory(UserManager<PatientUser> users, IOptions<IdentityOptions> options)
    : UserClaimsPrincipalFactory<PatientUser>(users, options)
{
    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(PatientUser user)
    {
        var identity = await base.GenerateClaimsAsync(user);
        identity.AddClaim(new Claim(EhrClaims.PatientId, user.PatientId.ToString()));
        return identity;
    }
}
