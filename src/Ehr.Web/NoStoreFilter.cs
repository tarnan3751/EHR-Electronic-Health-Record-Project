using Microsoft.AspNetCore.Mvc.Filters;

namespace Ehr.Web;

// Every page served to someone signed in, staff or patient, on either site, is "no-store" (the expansion plan's step 18):
// neither the browser nor anything between keeps a copy, so Back after a session ends or after signing out can't show
// the page again from a cache, PHI and all. With "no-cache" too, which is what the form token's own rule sets on a page
// with a form (every signed-in page has one, to sign out), so the form token finds nothing to override and logs nothing.
// A page that answers someone signed out sets its own where it needs one (an invitation, a sign-in step). Static files
// are the same for everyone and stay cacheable.
public sealed class NoStoreFilter : IPageFilter
{
    public void OnPageHandlerSelected(PageHandlerSelectedContext context)
    {
    }

    public void OnPageHandlerExecuting(PageHandlerExecutingContext context)
    {
        if (context.HttpContext.User.Identity?.IsAuthenticated == true)
        {
            context.HttpContext.Response.Headers.CacheControl = "no-cache, no-store";
        }
    }

    public void OnPageHandlerExecuted(PageHandlerExecutedContext context)
    {
    }
}
