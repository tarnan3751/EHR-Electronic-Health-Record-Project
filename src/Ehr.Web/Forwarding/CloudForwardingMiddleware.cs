using System.Text.Encodings.Web;
using Ehr.Data;
using Ehr.Web.Api.Audit;
using Ehr.Web.Api.Dictation;
using Ehr.Web.Api.Lab;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Yarp.ReverseProxy.Forwarder;

namespace Ehr.Web.Forwarding;

// The cloud app's write path. The replica can't take writes, so every request that could change data goes to the
// on-prem app, which saves and renders the response; it comes back in one WAN round trip. Signing out, which saves
// nothing, is handled here instead (HandledOffSiteAttribute), and the lab's door (Api/Lab) is never forwarded: it is the
// clinic's network's alone. Nor is listening to a visit (Api/Dictation, the expansion plan's step 16): a visit's
// conversation is recorded only at the clinic, and never crosses the WAN. The on-prem app reports the save's WAL position, which
// LastSaveCookie keeps. Until the replica has replayed that far, the same browser's page requests go to the on-prem
// app too, so people see their own saves, unless the on-prem app can't save just then (OnPremStatus) and the
// replica's copy is all there is. Other reads, PHI included, come from the replica; the PHI reader records them in the
// cloud app's audit queue (CloudAudit). A forward that can't reach the on-prem app marks OnPremStatus unreachable;
// only CloudAuditShipper marks it reachable again. A plain form post that can't be forwarded gets a "Not saved" page,
// which shows again what was typed in the fields its page names (ShownIfNotSavedAttribute).
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
            // The lab's door is the clinic's network's alone (step 12): nothing under it is forwarded, so nobody on the
            // internet can reach it, or use up its rate limit, through here. With no door on this app, such a request is
            // answered here as any address with nothing there is.
            // Nor is listening to a visit (step 16), which this app doesn't map either.
            if (endpoint?.Metadata.GetMetadata<HandledOffSiteAttribute>() is not null || context.Request.Path.StartsWithSegments(LabApi.Path)
                || context.Request.Path.StartsWithSegments(DictationApi.AmbientPath))
            {
                await next(context);
                return;
            }

            var plainFormPost = isPage && !context.Request.Headers.ContainsKey("HX-Request");
            // Kept, so the "Not saved" page can show it again. Only for the browser's own form: a page elsewhere could
            // otherwise post text for this site to show (Sec-Fetch-Site, which current browsers send).
            var shown = plainFormPost && context.Request.Headers["Sec-Fetch-Site"] == "same-origin"
                ? endpoint!.Metadata.GetMetadata<ShownIfNotSavedAttribute>()
                : null;
            // In memory only, and only a body of a known size that fits: nothing typed is ever written to the cloud
            // server's disk, and nobody can fill that disk by posting. A larger form isn't kept, and isn't shown again.
            if (shown is not null && context.Request.ContentLength is > 0 and <= MaximumKeptBody)
            {
                context.Request.EnableBuffering(bufferThreshold: MaximumKeptBody, bufferLimit: MaximumKeptBody);
            }
            else
            {
                shown = null;
            }

            if (await ForwardAsync(context) != ForwarderError.None && !context.Response.HasStarted)
            {
                context.Response.Clear();
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                if (plainFormPost)
                {
                    // A plain form post replaces the whole page, so it gets a page that says why, with what was typed
                    // and a way back to the form. htmx shows its own message (request-errors.js), and the APIs read the
                    // status.
                    context.Response.ContentType = "text/html; charset=utf-8";
                    context.Response.Headers.CacheControl = "no-store";
                    var entered = shown is null ? [] : await EnteredAsync(context.Request, shown);
                    await context.Response.WriteAsync(NotSavedPage(context.Request.Path, entered), CancellationToken.None);
                }
                else
                {
                    await context.Response.WriteAsync("Not saved. The clinic's server can't be reached.", CancellationToken.None);
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

    // The largest form body kept in memory to show again: several times a note at its longest (three sections of 16,000
    // characters, form-encoded).
    const int MaximumKeptBody = 256 * 1024;

    // What was typed in the fields the page names, those not left empty, from the body kept for this: the forward may
    // have read some of it.
    static async Task<List<(string Field, string Value)>> EnteredAsync(HttpRequest request, ShownIfNotSavedAttribute shown)
    {
        if (!request.HasFormContentType)
        {
            return [];
        }

        try
        {
            request.Body.Position = 0;
            var form = await request.ReadFormAsync(CancellationToken.None);
            return [.. shown.Fields.Select(field => (field, form[field].ToString())).Where(entry => entry.Item2.Length > 0)];
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or BadHttpRequestException)
        {
            // The browser gave up partway through sending the form, or sent one that can't be read: the page without it.
            return [];
        }
    }

    // Back goes back in the browser's history (back-link.js), where the form may still have what was typed, though a
    // browser may reload it instead; without JavaScript the link opens the form's page again. Either way, what was typed
    // in the fields the page names is shown here, to copy (UC 2). No inline script, so a strict CSP can be added later.
    static string NotSavedPage(PathString formPage, List<(string Field, string Value)> entered)
    {
        var next = entered.Count > 0
            ? "Copy what you entered below, then try again later."
            : "Go back and try again later.";
        // A newline first in each box, which the browser drops, so one that starts the text is kept.
        var fields = string.Concat(entered.Select(entry =>
            $"<label>{HtmlEncoder.Default.Encode(entry.Field)} <textarea readonly rows=\"4\">\n"
            + $"{HtmlEncoder.Default.Encode(entry.Value)}</textarea></label>\n        "));
        return $$"""
            <!doctype html>
            <html lang="en">
            <head>
                <meta charset="utf-8">
                <meta name="viewport" content="width=device-width, initial-scale=1">
                <title>Not saved</title>
                {{StandalonePage.Head}}
                <script src="/_content/Ehr.Design/js/back-link.js" defer></script>
            </head>
            {{StandalonePage.Open}}
                    <h1>Not saved</h1>
                    <p class="sc-notice sc-notice--warning" role="alert"><span class="sc-notice__state">Not saved.</span> The clinic's server can't be reached. {{next}}</p>
                    <div class="sc-form sc-kept">
                    {{fields}}<p><a href="{{HtmlEncoder.Default.Encode(formPage.ToString())}}" data-back>Back</a></p>
                    </div>
            {{StandalonePage.Close}}
            </html>
            """;
    }

    // Keeps the cloud audit key and a lab's key off what is forwarded, and moves the on-prem app's WAL position from the
    // response into the browser's LastSaveCookie.
    sealed class OnPremTransformer(LastSaveCookie lastSave) : HttpTransformer
    {
        public override async ValueTask TransformRequestAsync(
            HttpContext httpContext, HttpRequestMessage proxyRequest, string destinationPrefix, CancellationToken cancellationToken)
        {
            await base.TransformRequestAsync(httpContext, proxyRequest, destinationPrefix, cancellationToken);

            // Only the shipper sends the key, straight to the on-prem app. From the internet the on-prem app is reached
            // only through here, so this closes that path: a leaked key can't be used from the internet, only from the
            // cloud box or the clinic's network. The same for a lab's key (step 12), sent with any other request: the
            // lab's own door isn't forwarded at all (above).
            proxyRequest.Headers.Remove(CloudAuditApi.KeyHeader);
            proxyRequest.Headers.Remove(LabApi.KeyHeader);

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
