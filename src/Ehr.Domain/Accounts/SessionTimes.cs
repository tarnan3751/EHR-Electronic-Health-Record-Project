using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Ehr.Domain.Accounts;

// How long a web sign-in lasts, for the Staff and Patient cookies (the expansion plan's step 18; UC 11, proposed FR-63):
// it ends 15 minutes after the last request made with it, and 12 hours after the sign-in whatever happens meanwhile.
// The cookie is renewed whenever a minute has passed since it was issued (ASP.NET Core's own rule waits for half its
// life, which made an idle session end anywhere between 15 and 30 minutes), so the end is 15 minutes after the last
// request, to within a minute; and CookieSecurityStamp ends a sign-in at 12 hours. A request carrying CheckHeader is the
// page's own question, how long is left (session.js), and is never a renewal, so asking doesn't move what it asks
// about; the account is still checked on it. The pages learn the answer from the session pages
// (Areas/Clinical/Pages/Session, Areas/Portal/Pages/Session) and from the layout; the server decides every end.
public static class SessionTimes
{
    public static readonly TimeSpan Idle = TimeSpan.FromMinutes(15);

    // How long before the end the page warns.
    public static readonly TimeSpan Warning = TimeSpan.FromMinutes(2);

    // The most a sign-in lasts, however active.
    public static readonly TimeSpan Longest = TimeSpan.FromHours(12);

    static readonly TimeSpan RenewAfter = TimeSpan.FromMinutes(1);

    public const string CheckHeader = "Ehr-Session-Check";

    // The sign-in's time, kept in the ticket's properties, which every renewal carries over.
    const string SignedInItem = "ehr:signed-in";

    // Set on a request whose ticket is renewed, so its new end is known without reading the cookie written.
    const string RenewedItem = "ehr:session-renewed";

    // When CookieSecurityStamp last found the account as the sign-in has it, kept in the ticket's properties: the issue
    // time can't say, now that a renewal once a minute moves it.
    const string CheckedItem = "ehr:account-checked";

    public static bool IsCheck(HttpRequest request) => request.Headers.ContainsKey(CheckHeader);

    // The Staff and Patient cookies' events.
    public static Task SigningInAsync(CookieSigningInContext context)
    {
        var now = Now(context.Options);
        MarkSignedIn(context.Properties, now);
        MarkChecked(context.Properties, now);
        return Task.CompletedTask;
    }

    public static Task CheckSlidingExpirationAsync(CookieSlidingExpirationContext context)
    {
        context.ShouldRenew = !IsCheck(context.Request) && context.ElapsedTime >= RenewAfter;
        if (context.ShouldRenew)
        {
            Renewed(context.HttpContext);
        }

        return Task.CompletedTask;
    }

    // When the sign-in began, or null for a ticket written before this was kept.
    public static DateTimeOffset? SignedIn(AuthenticationProperties properties) =>
        properties.Items.TryGetValue(SignedInItem, out var value)
        && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at) ? at : null;

    public static void MarkSignedIn(AuthenticationProperties properties, DateTimeOffset at) =>
        properties.Items[SignedInItem] = at.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    // When the account was last checked, or null for a ticket written before this was kept.
    public static DateTimeOffset? Checked(AuthenticationProperties properties) =>
        properties.Items.TryGetValue(CheckedItem, out var value)
        && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at) ? at : null;

    public static void MarkChecked(AuthenticationProperties properties, DateTimeOffset at) =>
        properties.Items[CheckedItem] = at.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    // This request renews the ticket, so its end is now Idle from now.
    public static void Renewed(HttpContext context) => context.Items[RenewedItem] = true;

    public static DateTimeOffset Now(CookieAuthenticationOptions options) => (options.TimeProvider ?? TimeProvider.System).GetUtcNow();

    // What the request's own sign-in has left, by the scheme named: the idle end or the 12-hour one, whichever comes
    // first, and whether it is the 12-hour one, which nothing moves. Null when the request has no sign-in of that scheme.
    public static async Task<SessionLeft?> LeftAsync(HttpContext context, string scheme)
    {
        var result = await context.AuthenticateAsync(scheme);
        if (!result.Succeeded || result.Properties?.ExpiresUtc is not { } expires)
        {
            return null;
        }

        var now = Now(context.RequestServices.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(scheme));
        var idleEnd = context.Items.ContainsKey(RenewedItem) ? now + Idle : expires;
        var longestEnd = (SignedIn(result.Properties) ?? now) + Longest;
        var end = idleEnd < longestEnd ? idleEnd : longestEnd;
        return new(Seconds(end - now), Seconds(longestEnd - now), longestEnd <= idleEnd,
            Guid.Parse(result.Principal!.FindFirstValue(ClaimTypes.NameIdentifier)!));
    }

    static int Seconds(TimeSpan span) => Math.Max(0, (int)Math.Floor(span.TotalSeconds));
}

// Seconds until the sign-in ends, and until its 12-hour end; whether the end is the 12-hour one; and whose it is.
public sealed record SessionLeft(int Left, int UntilLongest, bool Final, Guid Account);
