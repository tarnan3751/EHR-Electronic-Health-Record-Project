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

// Writing a visit summary for the patient from a signed note (the expansion plan's step 13a). The clinician types it,
// in three parts, with the note under the form for reference; nothing is copied in. Released, it is shown in the
// portal to the patient and to whoever they share their record with, and the patient is told by email that there is
// something new, with no health details, at most once a day. Only a clinician with the patient on their panel, and only
// while the note has no standing summary; anyone else is sent back to the note. The database sets who released it and
// when, makes it sensitive when the note or an addendum is (or when the clinician ticks "Sensitive"), and refuses
// internal codes and identifiers. Off-site the post is forwarded to the clinic's server,
// and while that can't be reached the note's page doesn't offer this one.
[ShownIfNotSaved(nameof(TalkedAbout), nameof(Found), nameof(NextSteps))]
public class SummaryModel(PhiReader phi, PatientAssignments assignments, BreakGlass breakGlass, VisitSummaries summaries, IOptions<SiteAddresses> addresses)
    : PatientNotePage(phi, assignments, breakGlass)
{
    readonly PatientAssignments assignments = assignments;

    public ClinicalNote Note { get; private set; } = null!;

    // The summary of this note that stands in the portal already, when there is one: then there is no form.
    public VisitSummaryEntry? Standing { get; private set; }

    // Whether the note or one of its addenda is sensitive: then the summary is too, and the form says so rather than
    // offer the tick (Tahseen's decision of 21 November 2026).
    public bool SourcesSensitive { get; private set; }

    // The clinician marks it sensitive as they release it.
    [BindProperty]
    public bool Sensitive { get; set; }

    public IReadOnlyList<string> Errors { get; private set; } = [];

    // The part each error is about, if any (SummaryResult.ErrorParts).
    public IReadOnlyList<int?> ErrorParts { get; private set; } = [];

    [BindProperty]
    public string? TalkedAbout { get; set; }

    [BindProperty]
    public string? Found { get; set; }

    [BindProperty]
    public string? NextSteps { get; set; }

    public async Task<IActionResult> OnGetAsync(Guid patientId, Guid noteId) => await LoadAsync(patientId, noteId, shown: true) ?? Page();

    public async Task<IActionResult> OnPostAsync(Guid patientId, Guid noteId)
    {
        // Releasing shows nobody the note: no view of a sensitive note is recorded unless the page comes back.
        if (await LoadAsync(patientId, noteId, shown: false) is { } refused)
        {
            return refused;
        }

        var result = Standing is not null
            ? new SummaryResult(StatusCodes.Status409Conflict, ["A summary of this note has already been released. Withdraw it first to write another."])
            : await summaries.ReleaseAsync(patientId, noteId, TalkedAbout, Found, NextSteps, Sensitive, HttpContext.RequestAborted);
        if (result.Status == StatusCodes.Status200OK)
        {
            // At most one notice a day: none when another summary was released for the patient earlier in the clinic's
            // day. The link is the configured portal's, never built from this request (step 6c).
            await summaries.NotifyReleasedAsync(patientId, Links.Build(addresses.Value.PortalBaseUrl, "/portal/summaries"), HttpContext.RequestAborted);

            // A sensitive one of an ordinary note is shown to the person who has just released it.
            return RedirectToPage("View", null, new { patientId, noteId, done = "summary", sensitive = Sensitive && !SourcesSensitive ? true : (bool?)null }, "summary");
        }

        // The page comes back with the note under the form: that is a view of it.
        if (await Phi.NoteAsync(noteId, patientId, HttpContext.RequestAborted) is null)
        {
            return NotFound();
        }

        Errors = result.Errors;
        ErrorParts = result.ErrorParts;
        Response.StatusCode = result.Status;
        return Page();
    }

    async Task<IActionResult?> LoadAsync(Guid patientId, Guid noteId, bool shown)
    {
        if (await LoadPatientAsync(patientId) is { } denied)
        {
            return denied;
        }

        if (!await assignments.IsOnPanelAsync(patientId, HttpContext.RequestAborted))
        {
            return RedirectToPage("View", new { patientId, noteId });
        }

        if (await Phi.NoteAsync(noteId, patientId, HttpContext.RequestAborted, shown) is not { } note)
        {
            return NotFound();
        }

        Note = note;
        Standing = (await Phi.NoteSummariesAsync(note, HttpContext.RequestAborted)).FirstOrDefault(summary => summary.WithdrawnAt is null);
        SourcesSensitive = await Phi.NoteSourcesSensitiveAsync(note, HttpContext.RequestAborted);
        return null;
    }
}
