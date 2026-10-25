using System.Security.Claims;
using Ehr.Data.Accounts;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Ehr.Domain.Accounts;

// Identity's claims for a staff account, plus the display name and one role claim per role. Roles are a column on
// the account (StaffRoles), and this is where they become the claims RequireRole reads.
public sealed class StaffClaimsPrincipalFactory(UserManager<StaffUser> users, IOptions<IdentityOptions> options)
    : UserClaimsPrincipalFactory<StaffUser>(users, options)
{
    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(StaffUser user)
    {
        var identity = await base.GenerateClaimsAsync(user);
        identity.AddClaim(new Claim(EhrClaims.DisplayName, user.DisplayName));
        foreach (var role in user.Roles)
        {
            identity.AddClaim(new Claim(Options.ClaimsIdentity.RoleClaimType, role));
        }

        return identity;
    }
}
