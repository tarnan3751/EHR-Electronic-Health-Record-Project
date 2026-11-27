using Ehr.Domain.Accounts;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Ehr.Web;

// What a person sees when a page fails, instead of a blank one: the search couldn't be completed, the chart couldn't be
// loaded, or, for a form that changes something, the change may not have been saved, each with a way back (UC 3,
// UC 4). A database error is the usual cause, often before the page's handler runs,
// when its transaction can't start (DatabaseTransactionFilter), which is why this is middleware rather than part of the
// pages. A 404 under /patients/{id}, for a chart that doesn't exist or for anything in one that doesn't, says so too; any
// other page at an address with nothing there gets the site's not-found page, in the site's layout; and a form refused
// before its handler ran, a 400, says it wasn't sent. The pages are fixed text: nothing about the failure, the request or the
// patient goes on them, and the exception is logged, as any unhandled one is.
//
// Only a browser's page requests get these. The APIs keep their bare statuses, which the desktop app and the cloud app's
// audit shipper read, and so does an htmx request, which request-errors.js answers; the FHIR API answers with its own
// OperationOutcome (Api/Fhir, FhirApi.UseFhirErrors). In Development a failure still shows
// the developer exception page. Program.cs puts this after the cloud app's forwarding, so forwarded responses and the
// cloud app's own "Not saved" page pass by untouched.
public static class ErrorPages
{
    public static IApplicationBuilder UseErrorPages(this WebApplication app) =>
        app.UseWhen(IsPageRequest, pages =>
        {
            if (!app.Environment.IsDevelopment())
            {
                pages.UseExceptionHandler(new ExceptionHandlerOptions { ExceptionHandler = WriteFailureAsync });
            }

            pages.UseStatusCodePages(context =>
            {
                var request = context.HttpContext.Request;
                var opened = HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method);
                return context.HttpContext.Response.StatusCode switch
                {
                    StatusCodes.Status404NotFound when IsChart(request.Path) =>
                        WritePageAsync(context.HttpContext, "Not found", "No patient, or nothing in their chart, matches this address.", "/patients", "Back to patients"),
                    StatusCodes.Status404NotFound => ShowNotFoundAsync(context),
                    // A form refused before its handler ran, most often because its token was made for whoever was signed
                    // in when the page was opened, and someone else has signed in or out in this browser since.
                    StatusCodes.Status400BadRequest when !opened => WriteNotSentAsync(context.HttpContext),
                    _ => Task.CompletedTask,
                };
            });
        });

    static bool IsPageRequest(HttpContext context) =>
        !context.Request.Path.StartsWithSegments(AccountSchemes.ApiPath) && !context.Request.Path.StartsWithSegments(AccountSchemes.FhirPath)
        && !context.Request.Headers.ContainsKey("HX-Request");

    // A chart, or the reason it asks for: /patients/{id}, /patients/{id}/reason.
    static bool IsChart(PathString path) => path.StartsWithSegments("/patients", out var rest) && rest.Value is { Length: > 1 };

    // An address with nothing there, or a page whose handler found nothing for this account, which looks the same: the
    // site's own not-found page (Account/NotFound, in each area), in its layout. The address's own policy has run by
    // now (with nothing there, the fallback's: someone not signed in was sent to sign in instead). The rest of the
    // pipeline runs again with that page as the endpoint and the address unchanged, so the cookie read is the address's
    // site's, the person is shown signed in as they were, and the 404 stays. The page runs as a GET (a HEAD stays one),
    // so a form post that found nothing gets the same page as a link that did, and its token isn't checked again.
    static async Task ShowNotFoundAsync(StatusCodeContext status)
    {
        var context = status.HttpContext;
        var area = context.Request.Path.StartsWithSegments(AccountSchemes.PortalPath) ? "Portal" : "Clinical";
        context.SetEndpoint(context.RequestServices.GetRequiredService<EndpointDataSource>().Endpoints.Single(endpoint =>
            endpoint.Metadata.GetMetadata<PageActionDescriptor>() is { ViewEnginePath: NotFoundPage } page && page.AreaName == area));
        context.Request.RouteValues = new RouteValueDictionary { ["area"] = area, ["page"] = NotFoundPage };
        var method = context.Request.Method;
        context.Request.Method = HttpMethods.IsHead(method) ? method : HttpMethods.Get;
        try
        {
            await status.Next(context);
        }
        finally
        {
            context.Request.Method = method;
        }
    }

    const string NotFoundPage = "/Account/NotFound";

    // Where a page that failed leads back to: the portal's home, the clinician's patients, or the clinic's start.
    static (string Link, string Text) Home(PathString path) =>
        path.StartsWithSegments("/portal") ? ("/portal", "Back to home")
        : path.StartsWithSegments("/patients") ? ("/patients", "Back to patients")
        : ("/", "Back to home");

    static Task WriteNotSentAsync(HttpContext context)
    {
        var (home, homeText) = Home(context.Request.Path);
        return WritePageAsync(context, "Not sent", "This form couldn't be sent, so nothing was changed. Open its page again and try once more.", home, homeText);
    }

    static Task WriteFailureAsync(HttpContext context)
    {
        var path = context.Request.Path;
        var (home, homeText) = Home(path);
        // A change that failed may still have been saved: its transaction can commit before something after it fails,
        // such as the on-prem app reading its WAL position for the cloud app (DatabaseTransactionFilter).
        if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
        {
            return WritePageAsync(context, "Maybe not saved", "Something went wrong, and your change may not have been saved. Check before you try again.", home, homeText);
        }

        return IsChart(path)
            ? WritePageAsync(context, "Chart not loaded", "The chart couldn't be loaded. Try again in a moment.", "/patients", "Back to patients")
            : path.StartsWithSegments("/patients")
                ? WritePageAsync(context, "Search not completed", "The search couldn't be completed. Try again in a moment.", "/patients", "Back to patients")
                : WritePageAsync(context, "Page not loaded", "Something went wrong, and this page couldn't be loaded. Try again in a moment.", home, homeText);
    }

    // No inline script, like every page, so a strict CSP can be added later.
    static Task WritePageAsync(HttpContext context, string title, string message, string link, string linkText)
    {
        context.Response.ContentType = "text/html; charset=utf-8";
        return context.Response.WriteAsync(
            $$"""
            <!doctype html>
            <html lang="en">
            <head>
                <meta charset="utf-8">
                <meta name="viewport" content="width=device-width, initial-scale=1">
                <title>{{title}}</title>
                {{StandalonePage.Head}}
            </head>
            {{StandalonePage.Open}}
                    <h1>{{title}}</h1>
                    <p class="sc-notice sc-notice--danger" role="alert">{{message}}</p>
                    <p><a href="{{link}}">{{linkText}}</a></p>
            {{StandalonePage.Close}}
            </html>
            """,
            CancellationToken.None);
    }
}
