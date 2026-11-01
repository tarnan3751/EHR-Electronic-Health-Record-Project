using Ehr.Domain.Accounts;
using Ehr.Web.Accounts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Ehr.Web.Areas.Clinical.Pages.Account;

// The first step of a staff sign-in: the password. The account then goes on to whatever it still owes, and to its
// second factor (StaffSignIn.NextStepAsync), carried between the steps by the StaffPending cookie.
public class SignInModel(StaffSignIn signIn) : PageModel
{
    [BindProperty]
    public Credentials Input { get; set; } = new();

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    [TempData]
    public string? Message { get; set; }

    public IActionResult OnGet() =>
        User.Identity?.IsAuthenticated == true ? LocalRedirect(Links.Local(Url, ReturnUrl, "/")) : Page();

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var check = await signIn.CheckPasswordAsync(Input.Email!, Input.Password!, HttpContext.RequestAborted);
        switch (check.Outcome)
        {
            case PasswordOutcome.LockedOut:
                ModelState.AddModelError("", "This account is locked after too many failed attempts. Try again in 15 minutes.");
                return Page();
            case PasswordOutcome.Failed:
                ModelState.AddModelError("", "Wrong email or password.");
                return Page();
        }

        await signIn.HoldAsync(HttpContext, check.User!);
        var next = await signIn.NextStepAsync(check.User!) switch
        {
            SignInStep.ChangePassword => "Password",
            SignInStep.EnrollAuthenticator => "Enroll",
            _ => "Code",
        };
        return RedirectToPage(next, new { ReturnUrl });
    }
}
