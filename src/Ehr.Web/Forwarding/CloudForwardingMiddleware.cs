using System.Text.Encodings.Web;
using Ehr.Data;
using Ehr.Web.Api.Audit;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Yarp.ReverseProxy.Forwarder;

namespace Ehr.Web.Forwarding;

// The cloud app's write path. The replica can't take writes, so every request that could change data goes to the
// on-prem app, which saves and renders the response; it comes back in one WAN round trip. Signing out, which saves
// nothing, is handled here instead (HandledOffSiteAttribute). The on-prem app reports the save's WAL position, which
// LastSaveCookie keeps. Until the replica has replayed that far, the same browser's page requests go to the on-prem
// app too, so people see their own saves, unless the on-prem app can't save just then (OnPremStatus) and the
// replica's copy is all there is. Other reads, PHI included, come from the replica; the PHI reader records them in the
// cloud app's audit queue (CloudAudit). A forward that can't reach the on-prem app marks OnPremStatus unreachable;
// only CloudAuditShipper marks it reachable again.
public sealed class CloudForwardingMiddleware(
    RequestDelegate next, IHttpForwarder forwarder, OnPremApp onPrem, LastSaveCookie lastSave, OnPremStatus status)
{
    static readonly ForwarderRequestConfig RequestConfig = new() { ActivityTimeout = TimeSpan.FromSeconds(30) };

    readonly HttpTransformer transformer = new OnPremTransformer(lastSave);

    public async Task InvokeAsync(HttpContext context, EhrDbContext db)
    {
        var method = context.Request.Method;
        var endpoint = context.GetEndpoint();
        var isPage = endpoint?.Metadata.GetMetadata<PageActionDescriptor>() is not null;
        if (!HttpMethods.IsGet(method) && !HttpMethods.IsHead(method))
        {
            if (endpoint?.Metadata.GetMetadata<HandledOffSiteAttribute>() is not null)
            {
                await next(context);
                return;
            }

            if (await ForwardAsync(context) != ForwarderError.None && !context.Response.HasStarted)
            {
                context.Response.Clear();
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                if (isPage && !context.Request.Headers.ContainsKey("HX-Request"))
                {
                    // A plain form post replaces the whole page, so it gets a page that says why, with a way back to
                    // the form. htmx shows its own message (request-errors.js), and the APIs read the status.
                    context.Response.ContentType = "text/html; charset=utf-8";
                    await context.Response.WriteAsync(NotSavedPage(context.Request.Path), CancellationToken.None);
                }
                else
                {
                    await context.Response.WriteAsync("Not saved: the on-prem server can't be reached.", CancellationToken.None);
                }
            }

            return;
        }

        // Only pages read data. Static files are the same on both servers.
        if (!isPage || !lastSave.TryRead(context, out var position))
        {
            await next(context);
            return;
        }

        if (await WalPosition.HasReachedAsync(db, position, context.RequestAborted))
        {
            lastSave.Remove(context);
            await next(context);
            return;
        }

        // While the on-prem app can't save, its answer would be an error page (its database down) or a wait for the
        // link to time out: the replica's copy, with the read-only notice, is better. The cookie stays, so these reads
        // go to on-prem again once it's back, until the replica has the save.
        if (!status.IsReachable)
        {
            await next(context);
            return;
        }

        // If the on-prem app can't be reached, the replica's slightly older copy is better than an error.
        if (await ForwardAsync(context) != ForwarderError.None && !context.Response.HasStarted)
        {
            context.Response.Clear();
            await next(context);
        }
    }

    async ValueTask<ForwarderError> ForwardAsync(HttpContext context)
    {
        var error = await forwarder.SendAsync(context, onPrem.Url, onPrem.Client, RequestConfig, transformer);
        // Only no answer at all says something here. An answer doesn't prove the on-prem app can save: with its
        // database down it still answers whatever doesn't need it (a refused key, a sign-in redirect, a bad form
        // token), and a server error may be one page's bug. The shipper's check every couple of seconds decides.
        if (error is ForwarderError.Request or ForwarderError.RequestTimedOut)
        {
            status.Report(reachable: false);
        }

        return error;
    }

    // Back goes back in the browser's history (back-link.js), where the form may still have what was typed, though a
    // browser may reload it instead; without JavaScript the link opens the form's page again. No inline script, so a
    // strict CSP can be added later.
    static string NotSavedPage(PathString formPage) =>
        $$"""
        <!doctype html>
        <html lang="en">
        <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>Not saved</title>
            <link rel="stylesheet" href="/_content/Ehr.Design/css/ehr.css">
            <script src="/_content/Ehr.Design/js/back-link.js" defer></script>
        </head>
        <body>
            <main>
                <h1>Not saved</h1>
                <p class="notice read-only" role="alert">Nothing was saved, because the clinic's server can't be reached. Go back and try again once it's back; what you entered may still be there.</p>
                <p><a href="{{HtmlEncoder.Default.Encode(formPage.ToString())}}" data-back>Back</a></p>
            </main>
        </body>
        </html>
        """;

    // Keeps the cloud audit key off what is forwarded, and moves the on-prem app's WAL position from the response into
    // the browser's LastSaveCookie.
    sealed class OnPremTransformer(LastSaveCookie lastSave) : HttpTransformer
    {
        public override async ValueTask TransformRequestAsync(
            HttpContext httpContext, HttpRequestMessage proxyRequest, string destinationPrefix, CancellationToken cancellationToken)
        {
            await base.TransformRequestAsync(httpContext, proxyRequest, destinationPrefix, cancellationToken);

            // Only the shipper sends the key, straight to the on-prem app. From the internet the on-prem app is reached
            // only through here, so this closes that path: a leaked key can't be used from the internet, only from the
            // cloud box or the clinic's network.
            proxyRequest.Headers.Remove(CloudAuditApi.KeyHeader);

            // Whatever a client sent under this name goes; the cloud app's own proof replaces it, so the on-prem app
            // doesn't limit forwarded requests as one client (AccountRateLimits).
            proxyRequest.Headers.Remove(AccountRateLimits.ForwardedHeader);
            if (httpContext.RequestServices.GetRequiredService<IConfiguration>()["Ehr:CloudAudit:Key"] is { Length: > 0 } sharedKey)
            {
                proxyRequest.Headers.TryAddWithoutValidation(AccountRateLimits.ForwardedHeader, AccountRateLimits.ForwardedProof(sharedKey));
            }
        }

        public override async ValueTask<bool> TransformResponseAsync(
            HttpContext httpContext, HttpResponseMessage? proxyResponse, CancellationToken cancellationToken)
        {
            var proceed = await base.TransformResponseAsync(httpContext, proxyResponse, cancellationToken);

            if (httpContext.Response.Headers.Remove(DatabaseTransactionFilter.WalPositionHeader, out var position)
                && WalPosition.IsValid(position.ToString()))
            {
                lastSave.Set(httpContext, position.ToString());
            }

            return proceed;
        }
    }
}
