using Ehr.Domain.Accounts;
using Ehr.Web.Accounts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Ehr.Web.Areas.Clinical.Pages.Account;

// The new password an account owes before it can sign in: the bootstrap admin's, whose first one came from
// configuration. Reached only with a StaffPending cookie from the password step.
public class PasswordModel(StaffSignIn signIn) : PageModel
{
    [BindProperty]
    public NewPassword Input { get; set; } = new();

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public async Task<IActionResult> OnGetAsync() =>
        await signIn.HeldAsync(HttpContext) is { MustChangePassword: true } ? Page() : RedirectToPage("SignIn", new { ReturnUrl });

    public async Task<IActionResult> OnPostAsync()
    {
        if (await signIn.HeldAsync(HttpContext) is not { MustChangePassword: true } user)
        {
            return RedirectToPage("SignIn", new { ReturnUrl });
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        var result = await signIn.ChangePasswordAsync(user, Input.Password!, HttpContext.RequestAborted);
        if (!result.Succeeded)
        {
            foreach (var error in result.Errors)
            {
                ModelState.AddModelError("", error.Description);
            }

            return Page();
        }

        return RedirectToPage(await signIn.NextStepAsync(user) == SignInStep.EnterCode ? "Code" : "Enroll", new { ReturnUrl });
    }
}
