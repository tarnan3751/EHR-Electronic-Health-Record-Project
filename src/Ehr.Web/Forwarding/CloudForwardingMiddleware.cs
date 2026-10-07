using Ehr.Data;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Yarp.ReverseProxy.Forwarder;

namespace Ehr.Web.Forwarding;

// The cloud app's write path. The replica can't take writes, so every request that could change data goes to the
// on-prem app, which saves and renders the response; it comes back in one WAN round trip. The on-prem app reports
// the save's WAL position, which LastSaveCookie keeps. Until the replica has replayed that far, the same browser's
// page requests go to the on-prem app too, so people always see their own saves.
public sealed class CloudForwardingMiddleware(
    RequestDelegate next, IHttpForwarder forwarder, OnPremApp onPrem, LastSaveCookie lastSave)
{
    static readonly ForwarderRequestConfig RequestConfig = new() { ActivityTimeout = TimeSpan.FromSeconds(30) };

    readonly HttpTransformer transformer = new KeepWalPosition(lastSave);

    public async Task InvokeAsync(HttpContext context, EhrDbContext db)
    {
        var method = context.Request.Method;
        if (!HttpMethods.IsGet(method) && !HttpMethods.IsHead(method))
        {
            if (await ForwardAsync(context) != ForwarderError.None && !context.Response.HasStarted)
            {
                context.Response.Clear();
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await context.Response.WriteAsync("Not saved: the on-prem server can't be reached.", CancellationToken.None);
            }

            return;
        }

        // Only pages read data. Static files are the same on both servers.
        var isPage = context.GetEndpoint()?.Metadata.GetMetadata<PageActionDescriptor>() is not null;
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

        // If the on-prem app can't be reached, the replica's slightly older copy is better than an error.
        if (await ForwardAsync(context) != ForwarderError.None && !context.Response.HasStarted)
        {
            context.Response.Clear();
            await next(context);
        }
    }

    ValueTask<ForwarderError> ForwardAsync(HttpContext context) =>
        forwarder.SendAsync(context, onPrem.Url, onPrem.Client, RequestConfig, transformer);

    // Moves the on-prem app's WAL position from the response into the browser's LastSaveCookie.
    sealed class KeepWalPosition(LastSaveCookie lastSave) : HttpTransformer
    {
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
