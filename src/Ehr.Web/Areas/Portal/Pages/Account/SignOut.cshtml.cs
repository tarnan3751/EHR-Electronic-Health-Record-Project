using System.Security.Claims;
using Ehr.Domain.Accounts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Ehr.Web.Areas.Portal.Pages.Account;

// Signing out is a POST, from the form in the page header, so a link or an image can't do it to someone.
public class SignOutModel(PatientSignIn signIn) : PageModel
{
    public IActionResult OnGet() => RedirectToPage("SignIn");

    public async Task<IActionResult> OnPostAsync()
    {
        var userId = Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : (Guid?)null;
        var patientId = Guid.TryParse(User.FindFirstValue(EhrClaims.PatientId), out var patient) ? patient : (Guid?)null;
        await signIn.SignOutAsync(HttpContext, userId, patientId, HttpContext.RequestAborted);
        return Page();
    }
}
