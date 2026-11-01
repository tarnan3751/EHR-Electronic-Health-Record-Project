using System.ComponentModel.DataAnnotations;
using Ehr.Domain.Accounts;
using Ehr.Web.Accounts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Ehr.Web.Areas.Clinical.Pages.Account;

// Enrolling an authenticator app, at the first sign-in of every staff account. A code from the app proves it holds
// the key; the account is then signed in, since it has just given both factors, and sees its recovery codes once.
// Reached only with a StaffPending cookie from the password step.
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

    public string Continue => Links.Local(Url, ReturnUrl, "/");

    public async Task<IActionResult> OnGetAsync()
    {
        if (await signIn.HeldAsync(HttpContext) is not { } user || await signIn.NextStepAsync(user) != SignInStep.EnrollAuthenticator)
        {
            return RedirectToPage("SignIn", new { ReturnUrl });
        }

        Setup = await signIn.EnrollmentAsync(user, newKey: true);
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
            return Page();
        }

        await signIn.CompleteAsync(HttpContext, user, HttpContext.RequestAborted);
        RecoveryCodes = codes;
        return Page();
    }
}
