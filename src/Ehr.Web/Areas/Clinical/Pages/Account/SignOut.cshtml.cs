using System.Security.Claims;
using Ehr.Domain.Accounts;
using Ehr.Web.Forwarding;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Ehr.Web.Areas.Clinical.Pages.Account;

// Signing out is a POST, from the form in the page header, so a link or an image can't do it to someone. It only
// deletes the cookie and records the event, so the cloud app does it itself, even while the on-prem app is down
// (HandledOffSite); its event goes to the cloud app's audit queue.
[HandledOffSite]
public class SignOutModel(StaffSignIn signIn) : PageModel
{
    public IActionResult OnGet() => RedirectToPage("SignIn");

    public async Task<IActionResult> OnPostAsync()
    {
        var userId = Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : (Guid?)null;
        await signIn.SignOutAsync(HttpContext, userId, HttpContext.RequestAborted);
        return Page();
    }
}
