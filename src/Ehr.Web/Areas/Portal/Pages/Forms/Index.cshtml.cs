using Ehr.Domain.Phi;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Ehr.Web.Areas.Portal.Pages.Forms;

// The portal's pre-visit forms (the expansion plan's step 13e): the forms the care team asked the patient to fill in
// before a visit, those to fill in now or later, and those sent, with the answers given but never a score. Every form is
// a sensitive item: opening this page is asking to see them, recorded as such. A proxy the patient didn't allow
// sensitive items is given none, by row-level security as well as here, so for them the page is the page of a record
// with no forms: no count, no hint. Which patient comes from the session, never from the request (AGENTS.md). Off-site it
// is read from the cloud copy.
public class IndexModel(PortalSubject subject, PhiReader phi) : PageModel
{
    public PortalForms? Forms { get; private set; }

    // Whose record this is, when it isn't the signed-in account's own.
    public string? ActingFor => subject.Acting?.PatientName;

    // The patient always; a proxy only when the patient allowed them sensitive items (FR-19).
    public bool MaySeeSensitive => subject.Acting is null || subject.Acting.IncludeSensitive;

    public async Task OnGetAsync()
    {
        // PHI: not kept by the browser, so Back after signing out on a shared computer doesn't show it again.
        Response.Headers.CacheControl = "no-store";
        Forms = await phi.PortalFormsAsync(subject.PatientId, MaySeeSensitive, HttpContext.RequestAborted);
    }
}
