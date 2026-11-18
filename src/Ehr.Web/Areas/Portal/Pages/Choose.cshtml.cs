using Ehr.Domain.Phi;
using Ehr.Domain.Proxies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Ehr.Web.Areas.Portal.Pages;

// Where the portal goes after sign-in (step 6b; UC 6's alternative course, "a proxy chooses which patient's records
// to view"): an account that has records shared with it chooses whose to look at, its own first when it has one. An
// account with nothing shared goes straight to its own record. It shows names only, of the records the database
// says are shared with this account (ehr.proxied_records), and choosing posts a grant's ID to the Acting page: never
// a patient's. Read from the cloud copy off-site; it saves nothing.
public class ChooseModel(PhiReader phi, PortalSubject subject) : PageModel
{
    // Set by the redirect when the access the session was using has ended (PortalSubjectFilter).
    [BindProperty(SupportsGet = true)]
    public bool Ended { get; set; }

    public bool HasOwnRecord => subject.OwnPatientId is not null;

    public IReadOnlyList<ProxiedRecord> Records { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync()
    {
        Response.Headers.CacheControl = "no-store";
        // Arriving here starts from the account itself, whatever the browser was looking at before.
        ActingCookie.Clear(HttpContext);
        Records = await phi.ProxiedRecordsAsync(HttpContext.RequestAborted);
        return Records.Count == 0 && HasOwnRecord ? RedirectToPage("/Index") : Page();
    }
}
