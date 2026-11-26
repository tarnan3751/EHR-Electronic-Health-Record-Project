using System.Security.Claims;
using Ehr.Data.Patients;
using Ehr.Domain.Accounts;
using Ehr.Domain.Phi;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Ehr.Web.Areas.Portal.Pages;

// The portal's results page (UC6 "View test results", FR-16; Simar Rekhi): the signed-in patient's released test
// results, newest first, each opening in place to show its value, normal range, what it means in plain language and the
// clinician's note (ResultExplanation). Which patient comes from the session, never from the request (AGENTS.md), and no
// result ID is taken either: the whole list is one page, so there is nothing to change in a URL to reach someone else's,
// and row-level security returns no one else's anyway. The read goes through the PHI reader, which records it. It works
// off-site too, read from the cloud app's replica, and says read-only there while the clinic's server is down (UC2).
public class ResultsModel(PortalSubject subject, PhiReader phi) : PageModel
{
    public IReadOnlyList<Observation> Results { get; private set; } = [];

    public bool Available { get; private set; }

    // Whether these are someone else's results, read as a proxy (FR-19): the page then doesn't call them "yours".
    public bool Acting => subject.Acting is not null;

    // Whether the session may write to the care team: a proxy's grant may not include messages (FR-19).
    public bool MayMessage => subject.MayMessage;

    public async Task OnGetAsync()
    {
        // PHI: not kept by the browser, so Back after signing out on a shared computer doesn't show it again.
        Response.Headers.CacheControl = "no-store";
        var patientId = subject.PatientId;
        var results = await phi.ResultsAsync(patientId, HttpContext.RequestAborted);
        Available = results is not null;
        Results = results?.Results ?? [];
    }
}
