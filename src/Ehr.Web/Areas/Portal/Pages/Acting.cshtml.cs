using Ehr.Domain.Phi;
using Ehr.Web.Forwarding;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Ehr.Web.Areas.Portal.Pages;

// Switches which record a portal session is looking at (FR-19): a record the account holds a proxy grant for, or back
// to its own. It takes a grant's ID, never a patient's, and only one the PHI reader finds among the grants addressed
// to the signed-in account that are in force; the database checks the same on every statement afterwards. It saves
// nothing: the choice is a cookie. So the cloud app handles it itself (HandledOffSite), and a proxy can switch while
// the clinic's server can't be reached. Nothing is recorded here; what the proxy then opens is, under their account
// and the patient.
[HandledOffSite]
public class ActingModel(PhiReader phi, PortalSubject subject) : PageModel
{
    public IActionResult OnGet() => RedirectToPage("/Access");

    public async Task<IActionResult> OnPostAsync(Guid grantId)
    {
        if (await phi.ProxiedRecordAsync(grantId, subject.OwnPatientId, HttpContext.RequestAborted) is null)
        {
            return NotFound();
        }

        ActingCookie.Set(HttpContext, grantId);
        return RedirectToPage("/Index");
    }

    public IActionResult OnPostMine()
    {
        ActingCookie.Clear(HttpContext);
        return RedirectToPage("/Index");
    }
}
