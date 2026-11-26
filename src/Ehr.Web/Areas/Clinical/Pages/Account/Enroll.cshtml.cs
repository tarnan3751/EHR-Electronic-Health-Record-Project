using System.ComponentModel.DataAnnotations;
using Ehr.Domain.Accounts;
using Ehr.Web.Accounts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Ehr.Web.Areas.Clinical.Pages.Account;

// Enrolling an authenticator app, at the first sign-in of every staff account. A code from the app proves it holds
// the key; the account is then signed in, since it has just given both factors, and sees its recovery codes once.
// Reached only with a StaffPending cookie from the password step, for an account unchanged since (StaffSignIn.HeldAsync,
// step 19.1): a password reissued in between sends it back to the password. The account isn't held here, as the code
// page holds it: the enrolment's own writes are checked against the account as this request read it, so a reissue
// landing between that check and them fails them, the request with them (a 500), and nothing is saved or signed in.
public class EnrollModel(StaffSignIn signIn) : PageModel
{
    [BindProperty]
    [Required(ErrorMessage = "Enter the code from your app.")]
    public string? Code { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public AuthenticatorSetup? Setup { get; private set; }

    // The key in groups of four, for typing into an app by hand.
    public string KeyGrouped => string.Join(" ", Setup!.Key.Chunk(4).Select(group => new string(group)));

    public IReadOnlyList<string>? RecoveryCodes { get; private set; }

    // After the recovery codes: the page the sign-in started from, without its query but for a search's criteria
    // (Links.LocalPage).
    public string Continue => Links.LocalPage(Url, ReturnUrl, "/", Patients.SearchQuery.Parameters);

    public async Task<IActionResult> OnGetAsync()
    {
        if (await signIn.HeldAsync(HttpContext) is not { } user || await signIn.NextStepAsync(user) != SignInStep.EnrollAuthenticator)
        {
            return RedirectToPage("SignIn", new { ReturnUrl });
        }

        // A new key changes the account's security stamp: the pending sign-in carries the new one (step 19.1).
        Setup = await signIn.EnrollmentAsync(user, newKey: true);
        await signIn.RestampAsync(HttpContext, user);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (await signIn.HeldAsync(HttpContext) is not { } user || await signIn.NextStepAsync(user) != SignInStep.EnrollAuthenticator)
        {
            return RedirectToPage("SignIn", new { ReturnUrl });
        }

        var codes = ModelState.IsValid ? await signIn.FinishEnrollmentAsync(user, Code!, HttpContext.RequestAborted) : null;
        if (codes is null)
        {
            if (ModelState.IsValid)
            {
                ModelState.AddModelError(nameof(Code), "That code did not match. Enter the current code from your app.");
            }

            Setup = await signIn.EnrollmentAsync(user, newKey: false);
            await signIn.RestampAsync(HttpContext, user);
            return Page();
        }

        await signIn.CompleteAsync(HttpContext, user, HttpContext.RequestAborted);
        RecoveryCodes = codes;
        return Page();
    }
}
