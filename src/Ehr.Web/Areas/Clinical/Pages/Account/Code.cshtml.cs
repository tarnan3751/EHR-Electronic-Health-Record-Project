using System.ComponentModel.DataAnnotations;
using Ehr.Domain.Accounts;
using Ehr.Web.Accounts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Ehr.Web.Areas.Clinical.Pages.Account;

// The second factor of a staff sign-in: a code from the authenticator app, or a recovery code. Reached only with a
// StaffPending cookie from the password step.
public class CodeModel(StaffSignIn signIn) : PageModel
{
    [BindProperty]
    [Required(ErrorMessage = "Enter the code.")]
    public string? Code { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public async Task<IActionResult> OnGetAsync() =>
        await signIn.HeldAsync(HttpContext) is { } user && await signIn.NextStepAsync(user) == SignInStep.EnterCode
            ? Page()
            : RedirectToPage("SignIn", new { ReturnUrl });

    public async Task<IActionResult> OnPostAsync()
    {
        if (await signIn.HeldAsync(HttpContext) is not { } user || await signIn.NextStepAsync(user) != SignInStep.EnterCode)
        {
            return RedirectToPage("SignIn", new { ReturnUrl });
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        if (!await signIn.VerifyCodeAsync(user, Code!, HttpContext.RequestAborted))
        {
            ModelState.AddModelError("", "That code did not match. Try the current code from your app, or a recovery code.");
            return Page();
        }

        await signIn.CompleteAsync(HttpContext, user, HttpContext.RequestAborted);
        return LocalRedirect(Links.Local(Url, ReturnUrl, "/"));
    }
}
