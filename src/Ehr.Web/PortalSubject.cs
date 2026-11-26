using System.Security.Claims;
using Ehr.Data;
using Ehr.Data.Proxies;
using Ehr.Domain.Accounts;
using Ehr.Domain.Phi;
using Ehr.Domain.Proxies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Ehr.Web;

// Whose record a portal request is about (FR-19): the signed-in account's own patient, or, when the session is acting
// under a proxy grant, that grant's patient. Portal pages take their patient from here and nowhere else: never from
// the request (AGENTS.md), and no longer straight from the sign-in claim, which is always the account's own.
// PortalSubjectFilter fills it in before a handler runs. The database decides the same thing by itself
// (ehr.current_patient_id), so a page that got this wrong would be shown nothing.
public sealed class PortalSubject
{
    // The patient the page is about.
    public Guid PatientId { get; internal set; }

    // The signed-in account's own patient, whoever it is looking at.
    public Guid OwnPatientId { get; internal set; }

    // The grant the session is acting under and whose record it opens, or null on the account's own record.
    public ProxiedRecord? Acting { get; internal set; }

    // A proxy reads and sends messages only when the grant's scope includes them.
    public bool MayMessage => Acting is null || Acting.Scope == ProxyScope.RecordAndMessages;
}

// The cookie that says which proxy grant a portal session is acting under. It holds the grant's ID and nothing else,
// and it is only a request: the database checks on every statement that the grant is the signed-in account's and
// still in force. Gone when the browser closes, when the patient goes back to their own record, and at sign-out.
public static class ActingCookie
{
    // __Host-: sent only over HTTPS and only to this host, like the sign-in cookies.
    public const string Name = "__Host-ehr-acting-for";

    public static Guid? Read(HttpContext context) => Guid.TryParse(context.Request.Cookies[Name], out var grant) ? grant : null;

    public static void Set(HttpContext context, Guid grant) =>
        context.Response.Cookies.Append(Name, grant.ToString(), new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            IsEssential = true,
        });

    public static void Clear(HttpContext context) => context.Response.Cookies.Delete(Name, new CookieOptions { Secure = true, Path = "/" });
}

// Works out the portal's subject once for each portal page, inside the page's transaction. When the session names a
// grant that is no longer the account's or no longer in force, it forgets the grant and sends the browser to the
// account's own record, which says so, before the handler reads anything. The page that switches records, and the
// account pages, run without the check so there is always a way back.
public sealed class PortalSubjectFilter(PhiReader phi, PortalSubject subject, UserContext user) : IAsyncPageFilter
{
    // Where the layout finds the subject.
    public const string Item = "PortalSubject";

    public Task OnPageHandlerSelectionAsync(PageHandlerSelectedContext context) => Task.CompletedTask;

    public async Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
    {
        var http = context.HttpContext;
        if (string.Equals(context.RouteData.Values["area"] as string, "Portal", StringComparison.Ordinal)
            && Guid.TryParse(http.User.FindFirstValue(EhrClaims.PatientId), out var own))
        {
            subject.OwnPatientId = own;
            subject.PatientId = own;
            var page = context.RouteData.Values["page"] as string ?? "";
            var withoutCheck = page.StartsWith("/Account/", StringComparison.Ordinal) || page == "/Acting";
            if (user.ActingGrant is { } grant && !withoutCheck)
            {
                if (await phi.ProxiedRecordAsync(grant, http.RequestAborted) is not { } record)
                {
                    ActingCookie.Clear(http);
                    context.Result = new RedirectToPageResult("/Index", new { area = "Portal", ended = true });
                    return;
                }

                subject.Acting = record;
                subject.PatientId = record.PatientId;
            }

            http.Items[Item] = subject;
        }

        await next();
    }
}
