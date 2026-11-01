using Ehr.Domain.Accounts;
using Ehr.Web.Accounts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Ehr.Web.Areas.Portal.Pages.Account;

// A patient's sign-in (PatientSignIn). One step: the portal has no second factor yet.
public class SignInModel(PatientSignIn signIn) : PageModel
{
    [BindProperty]
    public Credentials Input { get; set; } = new();

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public IActionResult OnGet() =>
        User.Identity?.IsAuthenticated == true ? LocalRedirect(Links.Local(Url, ReturnUrl, AccountSchemes.PortalPath)) : Page();

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        switch (await signIn.SignInAsync(HttpContext, Input.Email!, Input.Password!, HttpContext.RequestAborted))
        {
            case PatientSignInOutcome.SignedIn:
                return LocalRedirect(Links.Local(Url, ReturnUrl, AccountSchemes.PortalPath));
            case PatientSignInOutcome.EmailNotConfirmed:
                ModelState.AddModelError("", "Confirm your email address first: open the link we sent you when you registered.");
                return Page();
            case PatientSignInOutcome.LockedOut:
                ModelState.AddModelError("", "This account is locked after too many failed attempts. Try again in 15 minutes.");
                return Page();
            default:
                ModelState.AddModelError("", "Wrong email or password.");
                return Page();
        }
    }
}
