using Ehr.Domain.Accounts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Ehr.Web.Areas.Portal.Pages;

// How long the signed-in portal session has left (the expansion plan's step 18; SessionAnswer), for session.js on every
// portal page: a keep-alive, or with SessionTimes.CheckHeader a question that renews nothing. Under the Portal area's
// Patient policy, so an ended session is sent to sign in. It runs without PortalSubjectFilter's redirects (a
// proxy-only account choosing a record is still signed in), reads nothing about a patient, and names the grant the
// session's cookie names, unchecked: it only tells the page whether it is looking at the same record as before.
public class SessionModel : PageModel
{
    public Task<IActionResult> OnGetAsync() => SessionAnswer.ForAsync(HttpContext, AccountSchemes.Patient, ActingCookie.Read(HttpContext));
}
