using Ehr.Data.Accounts;
using Ehr.Domain.Phi;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Ehr.Web;

// Counts what still needs action in the signed-in staff member's inbox, for the app bar (_Layout): once for each full
// clinical page, inside the page's own transaction, where row-level security knows who is asking. One index lookup.
// Not for the portal, not for a page fragment (results as a clinician types, the code search), and not for a POST,
// which answers with a redirect or a fragment.
public sealed class InboxCountFilter(PhiReader phi) : IAsyncPageFilter
{
    // Where the layout finds the count.
    public const string Item = "InboxCount";

    public Task OnPageHandlerSelectionAsync(PageHandlerSelectedContext context) => Task.CompletedTask;

    public async Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
    {
        var http = context.HttpContext;
        var user = http.User;
        if (HttpMethods.IsGet(http.Request.Method)
            && string.Equals(context.RouteData.Values["area"] as string, "Clinical", StringComparison.Ordinal)
            && (user.IsInRole(StaffRoles.Clinician) || user.IsInRole(StaffRoles.Admin))
            && !http.Request.Query.ContainsKey("handler") && !http.Request.Headers.ContainsKey("HX-Request")
            && !http.Request.Path.StartsWithSegments("/codes"))
        {
            http.Items[Item] = await phi.InboxCountAsync(http.RequestAborted);
        }

        await next();
    }
}
