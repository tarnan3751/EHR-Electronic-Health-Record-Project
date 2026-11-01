using System.Security.Claims;
using System.Text.Encodings.Web;
using Ehr.Data.Accounts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ehr.Domain.Accounts;

// The Desktop scheme: the session token the desktop app sends as "Authorization: Bearer <token>" (DesktopSessions). A
// good token gives the request the staff account's principal, the same claims the Staff cookie carries, plus the
// session it came from, so sign-out knows which one to end. Anything else fails, and a failed or missing token gets
// the base handler's answer: 401, with no redirect, since the app is not a browser.
public sealed class DesktopTokenHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
    DesktopSessions sessions, IUserClaimsPrincipalFactory<StaffUser> principals)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SessionClaim = "ehr:desktop_session";

    const string Bearer = "Bearer ";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith(Bearer, StringComparison.Ordinal))
        {
            return AuthenticateResult.NoResult();
        }

        var signedIn = await sessions.AuthenticateAsync(header[Bearer.Length..].Trim(), Context.RequestAborted);
        if (signedIn is null)
        {
            return AuthenticateResult.Fail("The session is over.");
        }

        var principal = await principals.CreateAsync(signedIn.User);
        ((ClaimsIdentity)principal.Identity!).AddClaim(new Claim(SessionClaim, signedIn.Session.Id.ToString()));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
    }
}
