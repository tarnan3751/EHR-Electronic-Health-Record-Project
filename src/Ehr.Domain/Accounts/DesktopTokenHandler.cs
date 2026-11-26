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
// the base handler's answer: 401, with no redirect, since the app is not a browser. A token refused only because its
// sign-in is 12 hours old (the expansion plan's step 19a) says so in SignInHeader, so the app asks for a full sign-in and
// keeps its offline copy; every other refusal means the session is over, and the app forgets it.
public sealed class DesktopTokenHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
    DesktopSessions sessions, IUserClaimsPrincipalFactory<StaffUser> principals)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SessionClaim = "ehr:desktop_session";

    // On the 401 for a session whose sign-in has ended (SignInEnded); nothing else sends it.
    public const string SignInHeader = "Ehr-Sign-In";

    public const string SignInEnded = "ended";

    const string Bearer = "Bearer ";

    // Set while authenticating a session whose sign-in has ended, for the challenge to say so.
    const string EndedItem = "ehr:desktop-sign-in-ended";

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

        if (signedIn.SignInEnded)
        {
            Context.Items[EndedItem] = true;
            return AuthenticateResult.Fail("The sign-in is 12 hours old.");
        }

        var principal = await principals.CreateAsync(signedIn.User);
        ((ClaimsIdentity)principal.Identity!).AddClaim(new Claim(SessionClaim, signedIn.Session.Id.ToString()));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        if (Context.Items.ContainsKey(EndedItem))
        {
            Response.Headers[SignInHeader] = SignInEnded;
        }

        return base.HandleChallengeAsync(properties);
    }
}
