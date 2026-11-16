using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace Ehr.Web;

// RateLimits: how many anonymous account POSTs (sign-in, register, forgot, resend, reset, confirm, the desktop app's
// sign-in) one address may make per window, and how many account emails one recipient may get per hour
// (CappedEmailSender). The defaults are for production; the tests raise them, since all their clients share one address.
public sealed class RateLimitOptions
{
    public const string Section = "RateLimits";

    public int PermitLimit { get; set; } = 30;

    public int WindowMinutes { get; set; } = 5;

    public int EmailsPerAddressPerHour { get; set; } = 3;
}

// Limits the anonymous account POSTs per client address, a fixed window each, and nothing else. The address is the
// socket's (Connection.RemoteIpAddress), never X-Forwarded-For, which anyone can send. The cloud app limits before it
// forwards (Program.cs), so off-site clients are counted there by their own address. Over the limit: 429 with a page
// saying so, or JSON on /api.
public static class AccountRateLimits
{
    static readonly string[] AccountPaths =
    [
        "/sign-in", "/portal/sign-in", "/portal/register", "/portal/forgot", "/portal/resend", "/portal/reset",
        "/portal/confirm", "/api/desktop/sign-in",
    ];

    public static IServiceCollection AddAccountRateLimits(this IServiceCollection services, RateLimitOptions limits) =>
        services.AddRateLimiter(options =>
        {
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context => Partition(context, limits));
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = (rejected, cancellationToken) => new ValueTask(WriteRejectedAsync(rejected.HttpContext, cancellationToken));
        });

    public static RateLimitPartition<string> Partition(HttpContext context, RateLimitOptions limits) =>
        IsAccountPost(context.Request)
            ? RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = limits.PermitLimit,
                    Window = TimeSpan.FromMinutes(limits.WindowMinutes),
                    QueueLimit = 0,
                })
            : RateLimitPartition.GetNoLimiter(string.Empty);

    static bool IsAccountPost(HttpRequest request) =>
        HttpMethods.IsPost(request.Method) && AccountPaths.Any(path => request.Path.StartsWithSegments(path));

    static Task WriteRejectedAsync(HttpContext context, CancellationToken cancellationToken)
    {
        if (context.Request.Path.StartsWithSegments("/api"))
        {
            return context.Response.WriteAsJsonAsync(new { error = "too_many_attempts" }, cancellationToken);
        }

        context.Response.ContentType = "text/html; charset=utf-8";
        return context.Response.WriteAsync(TooManyAttemptsPage, cancellationToken);
    }

    const string TooManyAttemptsPage =
        """
        <!doctype html>
        <html lang="en">
        <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>Too many attempts</title>
            <link rel="stylesheet" href="/_content/Ehr.Design/css/ehr.css">
        </head>
        <body>
            <main>
                <h1>Too many attempts</h1>
                <p role="alert">Too many attempts. Wait a few minutes and try again.</p>
            </main>
        </body>
        </html>
        """;
}
