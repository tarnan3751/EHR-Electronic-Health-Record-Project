using Ehr.Domain.Accounts;
using Ehr.Web.Accounts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Ehr.Web.Areas.Portal.Pages.Account;

// A patient's sign-in (PatientSignIn). One step: the portal has no second factor yet. It goes on to the page it was sent
// from, without that page's query (Links.LocalPage): a link can't ask through sign-in for a file or sensitive items.
public class SignInModel(PatientSignIn signIn) : PageModel
{
    [BindProperty]
    public Credentials Input { get; set; } = new();

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public IActionResult OnGet() =>
        User.Identity?.IsAuthenticated == true ? LocalRedirect(Links.LocalPage(Url, ReturnUrl, AccountSchemes.PortalPath)) : Page();

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
                return LocalRedirect(Links.LocalPage(Url, ReturnUrl, AccountSchemes.PortalPath + "/choose"));
            case PatientSignInOutcome.EmailNotConfirmed:
                ModelState.AddModelError("", "Confirm your email address first, with the link we sent you.");
                return Page();
            case PatientSignInOutcome.LockedOut:
                ModelState.AddModelError("", "Account locked. Try again in 15 minutes.");
                return Page();
            default:
                ModelState.AddModelError("", "Wrong email or password.");
                return Page();
        }
    }
}
