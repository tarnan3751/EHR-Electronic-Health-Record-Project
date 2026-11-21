using Ehr.Domain.Phi;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Ehr.Web.Areas.Portal.Pages;

// The portal's visit summaries (the expansion plan's step 13a): what the care team wrote for the patient after a visit,
// from a signed note, newest first, and a placeholder where one was withdrawn. Never the note itself. Which patient
// comes from the session, never from the request (AGENTS.md), and row-level security returns nothing else anyway; no
// summary ID is taken either: the whole list is one page. Sensitive summaries are left out until asked for, the asking
// recorded, as everywhere: for the patient, and for a proxy the patient allowed sensitive items. For a proxy without
// them, sensitive summaries don't exist: no count, no notice, and asking changes nothing. The read goes through the
// PHI reader, which records it and each summary shown. Off-site it comes from the cloud copy.
public class SummariesModel(PortalSubject subject, PhiReader phi) : PageModel
{
    public const int Limit = 50;

    public PatientVisitSummaries? Summaries { get; private set; }

    // "Show sensitive summaries": a link, so the cloud app answers it itself, as a read.
    [BindProperty(SupportsGet = true)]
    public bool Sensitive { get; set; }

    // The patient always; a proxy only when the patient allowed them sensitive items (FR-19).
    public bool MaySeeSensitive => subject.Acting is null || subject.Acting.IncludeSensitive;

    // Whose record this is, when it isn't the signed-in account's own.
    public bool Own => subject.Acting is null;

    public async Task OnGetAsync()
    {
        // PHI: not kept by the browser, so Back after signing out on a shared computer doesn't show it again.
        Response.Headers.CacheControl = "no-store";
        Summaries = await phi.VisitSummariesAsync(subject.PatientId, Sensitive && MaySeeSensitive, Limit, HttpContext.RequestAborted);
    }
}
