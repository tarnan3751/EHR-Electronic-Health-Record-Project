using Ehr.Data.Audit;
using Ehr.Data.Patients;
using Ehr.Domain.Accounts;
using Ehr.Domain.Notes;
using Ehr.Domain.Patients;
using Ehr.Domain.Phi;
using Ehr.Web.Accounts;
using Ehr.Web.Forwarding;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Ehr.Web.Areas.Clinical.Pages.Patients.Notes;

// Withdrawing a released visit summary, with a reason (the expansion plan's step 13a), as a vital sign is marked
// entered in error: it is never edited or removed. Withdrawn, its text is no longer shown in the portal, which keeps a
// dated placeholder in its place; it stays on the note's page, struck through, with who withdrew it, when and why, and
// a new one can be written. If the patient or a proxy had viewed it in the portal, the patient is emailed that there is
// something new, as for a new summary; and when someone else released it, the database puts an item in the releaser's
// inbox (Tahseen's decisions of 21 November 2026). Only a clinician with the patient on their panel; anyone else is
// sent back to the note. Off-site the post is forwarded to the clinic's server.
[ShownIfNotSaved(nameof(Reason))]
public class WithdrawSummaryModel(PhiReader phi, PatientAssignments assignments, BreakGlass breakGlass, VisitSummaries summaries, IOptions<SiteAddresses> addresses)
    : PatientNotePage(phi, assignments, breakGlass)
{
    readonly PatientAssignments assignments = assignments;

    public ClinicalNote Note { get; private set; } = null!;

    public VisitSummaryEntry Summary { get; private set; } = null!;

    [BindProperty]
    public string? Reason { get; set; }

    public IReadOnlyList<string> Errors { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(Guid patientId, Guid noteId, Guid summaryId) => await LoadAsync(patientId, noteId, summaryId) ?? Page();

    public async Task<IActionResult> OnPostAsync(Guid patientId, Guid noteId, Guid summaryId)
    {
        if (await LoadAsync(patientId, noteId, summaryId) is { } refused)
        {
            return refused;
        }

        var result = Summary.WithdrawnAt is not null
            ? new SummaryResult(StatusCodes.Status409Conflict, ["Already withdrawn."])
            : await summaries.WithdrawAsync(patientId, summaryId, Reason, HttpContext.RequestAborted);
        if (result.Status == StatusCodes.Status200OK)
        {
            // Viewed before it was withdrawn: the patient is told, whatever was sent that day. The link is the configured
            // portal's, never built from this request (step 6c).
            if (result.Viewed)
            {
                await summaries.NotifyPatientAsync(patientId, Links.Build(addresses.Value.PortalBaseUrl, "/portal/summaries"), HttpContext.RequestAborted);
            }

            return RedirectToPage("View", null, new { patientId, noteId, done = "withdrawn" }, "summary");
        }

        Errors = result.Errors;
        Response.StatusCode = result.Status;
        return Page();
    }

    async Task<IActionResult?> LoadAsync(Guid patientId, Guid noteId, Guid summaryId)
    {
        if (await LoadPatientAsync(patientId, AuditParts.VisitNote) is { } denied)
        {
            return denied;
        }

        if (!await assignments.IsOnPanelAsync(patientId, HttpContext.RequestAborted))
        {
            return RedirectToPage("View", new { patientId, noteId });
        }

        // The page shows the summary, not the note's text: the summary's read says when that is a sensitive one.
        if (await Phi.NoteAsync(noteId, patientId, HttpContext.RequestAborted, shown: false) is not { } note
            || await Phi.NoteSummaryAsync(note, summaryId, HttpContext.RequestAborted) is not { } summary)
        {
            return NotFound();
        }

        (Note, Summary) = (note, summary);
        return null;
    }
}
