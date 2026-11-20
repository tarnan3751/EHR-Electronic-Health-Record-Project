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
// Under the list (the expansion plan's step 9): a trend for each test, and each vital sign, with more than one value,
// from the same read.
public class ResultsModel(PortalSubject subject, PhiReader phi) : PageModel
{
    public IReadOnlyList<Observation> Results { get; private set; } = [];

    // Step 9: a trend for each test, and each vital sign, that has more than one value.
    public IReadOnlyList<Ehr.Domain.Results.Trend> ResultTrends { get; private set; } = [];

    public IReadOnlyList<Ehr.Domain.Results.Trend> VitalTrends { get; private set; } = [];

    public bool Available { get; private set; }

    public async Task OnGetAsync()
    {
        // PHI: not kept by the browser, so Back after signing out on a shared computer doesn't show it again.
        Response.Headers.CacheControl = "no-store";
        var patientId = subject.PatientId;
        var results = await phi.ResultsAsync(patientId, HttpContext.RequestAborted);
        Available = results is not null;
        Results = results?.Results ?? [];
        ResultTrends = Ehr.Domain.Results.Trend.From(Results);
        // A blood pressure's two numbers for each whole reading read (step 9c); the charts are as they were.
        VitalTrends = Ehr.Domain.Results.Trend.From(Ehr.Domain.Results.BloodPressure.Whole(results?.Vitals ?? []), VitalSign.All);
    }
}
