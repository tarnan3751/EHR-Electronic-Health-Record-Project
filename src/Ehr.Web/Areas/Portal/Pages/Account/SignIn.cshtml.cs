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
                // A new session starts on its own record, whatever the last one on this browser was looking at (FR-19).
                ActingCookie.Clear(HttpContext);
                // To the page that asks whose record, which goes straight on to the account's own when nothing is
                // shared with it (step 6b; UC 6's alternative course).
                return LocalRedirect(Links.Local(Url, ReturnUrl, AccountSchemes.PortalPath + "/choose"));
            case PatientSignInOutcome.EmailNotConfirmed:
                ModelState.AddModelError("", "Confirm your email address first: open the link we sent you when you registered. If it expired, ask for a new one below.");
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
