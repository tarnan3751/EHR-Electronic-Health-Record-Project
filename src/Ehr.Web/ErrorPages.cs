using Ehr.Domain.Accounts;

namespace Ehr.Web;

// What a person sees when a page fails, instead of a blank one: the search couldn't be completed, the chart couldn't be
// loaded, or, for a form that changes something, the change may not have been saved, each with a way back (UC 3,
// UC 4). A database error is the usual cause, often before the page's handler runs,
// when its transaction can't start (DatabaseTransactionFilter), which is why this is middleware rather than part of the
// pages. A chart that doesn't exist, a 404 from ChartModel or ReasonModel, says so too. The pages are fixed text: nothing
// about the failure, the request or the patient goes on them, and the exception is logged, as any unhandled one is.
//
// Only a browser's page requests get these. The APIs keep their bare statuses, which the desktop app and the cloud app's
// audit shipper read, and so does an htmx request, which request-errors.js answers. In Development a failure still shows
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
                context.HttpContext.Response.StatusCode == StatusCodes.Status404NotFound && IsChart(context.HttpContext.Request.Path)
                    ? WritePageAsync(context.HttpContext, "Chart not loaded", "The chart couldn't be loaded: there is no patient with this ID.", "/patients", "Back to my patients")
                    : Task.CompletedTask);
        });

    static bool IsPageRequest(HttpContext context) =>
        !context.Request.Path.StartsWithSegments(AccountSchemes.ApiPath) && !context.Request.Headers.ContainsKey("HX-Request");

    // A chart, or the reason it asks for: /patients/{id}, /patients/{id}/reason.
    static bool IsChart(PathString path) => path.StartsWithSegments("/patients", out var rest) && rest.Value is { Length: > 1 };

    static Task WriteFailureAsync(HttpContext context)
    {
        var path = context.Request.Path;
        var home = path.StartsWithSegments("/portal") ? "/portal" : path.StartsWithSegments("/patients") ? "/patients" : "/";
        var homeText = home == "/patients" ? "Back to my patients" : "Back to the start";
        // A change that failed may still have been saved: its transaction can commit before something after it fails,
        // such as the on-prem app reading its WAL position for the cloud app (DatabaseTransactionFilter).
        if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
        {
            return WritePageAsync(context, "Maybe not saved", "Something went wrong, and your change may not have been saved. Check before you try again.", home, homeText);
        }

        return IsChart(path)
            ? WritePageAsync(context, "Chart not loaded", "The chart couldn't be loaded. Try again in a moment.", "/patients", "Back to my patients")
            : path.StartsWithSegments("/patients")
                ? WritePageAsync(context, "Search not completed", "The search couldn't be completed. Try again in a moment.", "/patients", "Back to my patients")
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
                <link rel="stylesheet" href="/_content/Ehr.Design/css/ehr.css">
            </head>
            <body>
                <main>
                    <h1>{{title}}</h1>
                    <p class="notice error" role="alert">{{message}}</p>
                    <p><a href="{{link}}">{{linkText}}</a></p>
                </main>
            </body>
            </html>
            """,
            CancellationToken.None);
    }
}
