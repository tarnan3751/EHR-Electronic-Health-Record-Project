using System.Security.Claims;
using Ehr.Data;

namespace Ehr.Web;

// Hands the signed-in account to the database. After authentication has read the request's cookie, the account's ID
// goes into the request's UserContext, which UserContextInterceptor (Ehr.Data) sets as app.user_id at the start of
// every transaction, where row-level security reads it. With nobody signed in the context stays empty, and every PHI
// table returns no rows.
public sealed class UserContextMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context, UserContext user)
    {
        if (context.User.Identity?.IsAuthenticated == true
            && Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id))
        {
            user.UserId = id;

            // A portal session may be acting under a proxy grant (FR-19): the database is told which, and decides
            // whether it counts (ehr.current_patient_id). Staff sessions never act under one.
            if (Ehr.Domain.Accounts.EhrClaims.IsPortalAccount(context.User))
            {
                user.ActingGrant = ActingCookie.Read(context);
            }
        }

        return next(context);
    }
}
