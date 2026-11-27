using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Ehr.Data.Accounts;
using Ehr.Domain.Accounts;
using Ehr.Web.Accounts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Ehr.Web.Areas.Clinical.Pages.Account;

// The second factor of a staff sign-in: a code from the authenticator app, or a recovery code; or, for an account with
// a passkey where the clinic has them, the passkey instead (step 19). The code is always on the page, so a passkey that
// fails or is cancelled leaves the password step as it was. Reached only with a StaffPending cookie from the password
// step, so the passkey is offered, and its options given, only to someone who has given the account's password. A
// passkey is two posts: the options, as JSON for passkeys.js, then the passkey the browser used.
public class CodeModel(StaffSignIn signIn, StaffPasskeys passkeys, PasskeySettings settings) : PageModel
{
    [BindProperty]
    [Required(ErrorMessage = "Enter the code.")]
    public string? Code { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    // Whether the page offers the account's passkeys, which passkeys.js shows where the browser can use one.
    public bool OffersPasskey { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (await HeldAsync() is not { } user)
        {
            return RedirectToPage("SignIn", new { ReturnUrl });
        }

        await OfferAsync(user);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (await HeldAsync() is not { } user)
        {
            return RedirectToPage("SignIn", new { ReturnUrl });
        }

        await OfferAsync(user);
        if (!ModelState.IsValid)
        {
            return Page();
        }

        // Wrong codes lock the account as wrong passwords do, and a locked account takes no code either (step 19), each
        // checked and counted after any other for the same account. Held, the account is read again: if an
        // authenticator reset or a reissued password came first, the sign-in goes back to the password before any code
        // is checked (step 19.1), since the key this request read before the hold may be the one the reset ended.
        await signIn.HoldAccountAsync(user, HttpContext.RequestAborted);
        if (!await StillHeldAsync(user))
        {
            return RedirectToPage("SignIn", new { ReturnUrl });
        }

        if (await signIn.LockedOutAsync(user, HttpContext.RequestAborted))
        {
            ModelState.AddModelError("", Locked);
            return Page();
        }

        if (!await signIn.VerifyCodeAsync(user, Code!, HttpContext.RequestAborted))
        {
            ModelState.AddModelError("", "Wrong code.");
            return Page();
        }

        return await SignedInAsync(user, null);
    }

    // The options for the browser to use one of the account's passkeys with, and the state to send back, as JSON; or,
    // as JSON too, why not. Never kept by anything between.
    public async Task<IActionResult> OnPostOptionsAsync()
    {
        Response.Headers.CacheControl = "no-store";
        var user = await HeldAsync();
        var ceremony = user is not null && await OfferAsync(user)
            ? await passkeys.BeginSignInAsync(HttpContext, user, await StaffSignIn.PendingAsync(HttpContext))
            : PasskeyCeremony.Refused(null, "Sign-in timed out. Start again with your password.");
        return ceremony.Problem is { } problem
            ? new JsonResult(new { problem }) { StatusCode = StatusCodes.Status400BadRequest }
            : new JsonResult(new { options = JsonSerializer.Deserialize<JsonElement>(ceremony.Options!), state = ceremony.State });
    }

    // The passkey the browser used. Refused, the page says so, and the code is there as before.
    public async Task<IActionResult> OnPostPasskeyAsync(string? credential, string? state)
    {
        if (await HeldAsync() is not { } user)
        {
            return RedirectToPage("SignIn", new { ReturnUrl });
        }

        // Held before its passkeys are read (step 19.1), so the passkey is checked against its count as stored, and the
        // same answer sent twice at once signs in once; and, read again, the account goes back to the password if a reset
        // or a reissued password came first.
        await signIn.HoldAccountAsync(user, HttpContext.RequestAborted);
        if (!await StillHeldAsync(user) || !await OfferAsync(user))
        {
            return RedirectToPage("SignIn", new { ReturnUrl });
        }

        // The code's own field isn't part of this post.
        ModelState.Remove(nameof(Code));
        if (await signIn.LockedOutAsync(user, HttpContext.RequestAborted))
        {
            ModelState.AddModelError("", Locked);
            return Page();
        }

        if (await signIn.VerifyPasskeyAsync(HttpContext, user, credential, state, HttpContext.RequestAborted) is not { } used)
        {
            ModelState.AddModelError("", "Passkey not accepted.");
            return Page();
        }

        return await SignedInAsync(user, used);
    }

    const string Locked = "Account locked. Try again in 15 minutes.";

    // The account held by the password step, if its next step is this one.
    async Task<StaffUser?> HeldAsync() =>
        await signIn.HeldAsync(HttpContext) is { } user && await signIn.NextStepAsync(user) == SignInStep.EnterCode ? user : null;

    // Once the account is held and read again (step 19.1): its security stamp still the password step's, which a reset, a
    // reissued password or a disabling changes, and its next step still this one, which a reset changes too.
    async Task<bool> StillHeldAsync(StaffUser user) =>
        await signIn.UnchangedAsync(HttpContext, user) && await signIn.NextStepAsync(user) == SignInStep.EnterCode;

    // Whether the account has a passkey to offer, where the clinic has passkeys.
    async Task<bool> OfferAsync(StaffUser user) => OffersPasskey = settings.On && (await passkeys.ListAsync(user)).Count > 0;

    async Task<IActionResult> SignedInAsync(StaffUser user, string? details)
    {
        await signIn.CompleteAsync(HttpContext, user, HttpContext.RequestAborted, details);
        // On to the page the sign-in started from, without its query but for a search's criteria
        // (Links.LocalPage).
        return LocalRedirect(Links.LocalPage(Url, ReturnUrl, "/", Patients.SearchQuery.Parameters));
    }
}
