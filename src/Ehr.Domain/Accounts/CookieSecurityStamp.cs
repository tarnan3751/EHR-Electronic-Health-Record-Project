using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Ehr.Domain.Accounts;

// Checks a sign-in cookie against the account every few minutes: an account that was locked out (how an account is
// disabled), or whose security stamp changed (a password change, a new authenticator), loses its sessions within
// that interval rather than when the cookie expires. Row-level security already denies a locked-out account's rows
// (db/policies/30-rls.sql); this makes the page say so.
public static class CookieSecurityStamp
{
    static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    public static async Task ValidateAsync<TUser>(CookieValidatePrincipalContext context) where TUser : IdentityUser<Guid>
    {
        if (context.Properties.IssuedUtc is { } issued && DateTimeOffset.UtcNow - issued < Interval)
        {
            return;
        }

        var services = context.HttpContext.RequestServices;
        var users = services.GetRequiredService<UserManager<TUser>>();
        var id = context.Principal?.FindFirstValue(users.Options.ClaimsIdentity.UserIdClaimType);
        var stamp = context.Principal?.FindFirstValue(users.Options.ClaimsIdentity.SecurityStampClaimType);
        var user = id is null ? null : await users.FindByIdAsync(id);
        if (user is null || await users.IsLockedOutAsync(user) || stamp != await users.GetSecurityStampAsync(user))
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(context.Scheme.Name);
            return;
        }

        // Fresh claims (a changed role or display name), and a fresh issue time so the next check is an interval away.
        context.ReplacePrincipal(await services.GetRequiredService<IUserClaimsPrincipalFactory<TUser>>().CreateAsync(user));
        context.ShouldRenew = true;
    }
}
