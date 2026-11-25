using Ehr.Domain.Accounts;
using Microsoft.AspNetCore.Mvc;

namespace Ehr.Web;

// What the session pages (Areas/Clinical/Pages/Session, Areas/Portal/Pages/Session) answer session.js, as JSON, never
// cached: how many seconds the sign-in has left, whether its end is the 12-hour one that nothing moves, whose sign-in it
// is, and, in the portal, the proxy grant the session names (ActingCookie), so a page kept while signed out goes back
// only to the same account looking at the same record. Nothing about a patient.
public sealed record SessionAnswer(int Left, bool Final, Guid Account, Guid? Acting)
{
    public static async Task<IActionResult> ForAsync(HttpContext context, string scheme, Guid? acting)
    {
        context.Response.Headers.CacheControl = "no-store";
        // The area's policy has let only a signed-in account of this scheme through.
        var left = (await SessionTimes.LeftAsync(context, scheme))!;
        return new JsonResult(new SessionAnswer(left.Left, left.Final, left.Account, acting));
    }
}
