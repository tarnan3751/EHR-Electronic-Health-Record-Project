using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Ehr.Domain.Accounts;

// Checks a sign-in cookie against the account every few minutes: an account that was locked out (how an account is
// disabled), or whose security stamp changed (a password change, a new authenticator), loses its sessions within
// that interval rather than when the cookie expires. Row-level security already denies a locked-out account's rows
// (db/policies/30-rls.sql); this makes the page say so. The last check's time is kept in the ticket (SessionTimes), since
// the cookie's renewal once a minute moves its issue time. It also ends a sign-in 12 hours after it began, on every
// request, whatever the activity (SessionTimes); a ticket written before that was kept counts from its first request
// since, takes the idle time from then on, and has its account checked at once. A check (SessionTimes.CheckHeader) is
// never renewed here either.
public static class CookieSecurityStamp
{
    static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    public static async Task ValidateAsync<TUser>(CookieValidatePrincipalContext context) where TUser : IdentityUser<Guid>
    {
        var now = SessionTimes.Now(context.Options);
        var check = SessionTimes.IsCheck(context.Request);
        if (SessionTimes.SignedIn(context.Properties) is { } signedIn)
        {
            if (now - signedIn >= SessionTimes.Longest)
            {
                context.RejectPrincipal();
                await context.HttpContext.SignOutAsync(context.Scheme.Name);
                return;
            }
        }
        else if (!check)
        {
            // A ticket written before step 18: a renewal keeps a ticket's own life, 30 minutes for staff and 20 for the
            // portal then, so it is cut to the idle time first.
            SessionTimes.MarkSignedIn(context.Properties, now);
            if (context.Properties.IssuedUtc is { } issued)
            {
                context.Properties.ExpiresUtc = issued + SessionTimes.Idle;
            }

            context.ShouldRenew = true;
            SessionTimes.Renewed(context.HttpContext);
        }

        // The last check; a ticket that holds none, written before that was kept, is checked now.
        if (SessionTimes.Checked(context.Properties) is { } last && now - last < Interval)
        {
            return;
        }

        var services = context.HttpContext.RequestServices;
        var user = await CurrentAccountAsync<TUser>(services, context.Principal);
        if (user is null)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(context.Scheme.Name);
            return;
        }

        // Fresh claims (a changed role or display name), and, but for a check, which renews nothing, the time of this
        // check kept in the renewed ticket, so the next is an interval away.
        context.ReplacePrincipal(await services.GetRequiredService<IUserClaimsPrincipalFactory<TUser>>().CreateAsync(user));
        SessionTimes.MarkChecked(context.Properties, now);
        if (!check)
        {
            context.ShouldRenew = true;
            SessionTimes.Renewed(context.HttpContext);
        }
    }

    // The account a sign-in is for, if the sign-in still holds: the account exists, isn't locked out, and has the
    // security stamp the sign-in carries. Otherwise null.
    public static async Task<TUser?> CurrentAccountAsync<TUser>(IServiceProvider services, ClaimsPrincipal? principal)
        where TUser : IdentityUser<Guid>
    {
        var users = services.GetRequiredService<UserManager<TUser>>();
        var id = principal?.FindFirstValue(users.Options.ClaimsIdentity.UserIdClaimType);
        var stamp = principal?.FindFirstValue(users.Options.ClaimsIdentity.SecurityStampClaimType);
        var user = id is null ? null : await users.FindByIdAsync(id);
        return user is null || await users.IsLockedOutAsync(user) || stamp != await users.GetSecurityStampAsync(user) ? null : user;
    }
}
