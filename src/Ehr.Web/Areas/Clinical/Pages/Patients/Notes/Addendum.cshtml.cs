using Ehr.Data.Audit;
using Ehr.Data.Patients;
using Ehr.Domain.Notes;
using Ehr.Domain.Patients;
using Ehr.Domain.Phi;
using Ehr.Web.Forwarding;
using Microsoft.AspNetCore.Mvc;

namespace Ehr.Web.Areas.Clinical.Pages.Patients.Notes;

// Signing an addendum to a signed note (the expansion plan's step 10). The note stays as it was: the addendum is a new
// signed text shown after it. Only a clinician with the patient on their panel; anyone else is sent back to the note.
// The database sets who signed and when, and makes an addendum to a sensitive note sensitive. Off-site the post is
// forwarded to the clinic's server, and while that can't be reached the note page doesn't offer this one.
[ShownIfNotSaved(nameof(Text))]
public class AddendumModel(PhiReader phi, PatientAssignments assignments, BreakGlass breakGlass, ClinicalNotes notes)
    : PatientNotePage(phi, assignments, breakGlass)
{
    readonly PatientAssignments assignments = assignments;

    public ClinicalNote Note { get; private set; } = null!;

    public IReadOnlyList<string> Errors { get; private set; } = [];

    [BindProperty]
    public string? Text { get; set; }

    // The author marks it sensitive. An addendum to a sensitive note is sensitive whatever this says.
    [BindProperty]
    public bool Sensitive { get; set; }

    public Task<IActionResult> OnGetAsync(Guid patientId, Guid noteId) => LoadAsync(patientId, noteId, shown: true);

    public async Task<IActionResult> OnPostAsync(Guid patientId, Guid noteId)
    {
        // Signing shows nobody the note: no view of a sensitive note is recorded unless the page comes back.
        if (await LoadAsync(patientId, noteId, shown: false) is not Microsoft.AspNetCore.Mvc.RazorPages.PageResult)
        {
            return RedirectToPage("View", new { patientId, noteId });
        }

        var result = await notes.AddAddendumAsync(patientId, noteId, Text, Sensitive, HttpContext.RequestAborted);
        if (result.Status == StatusCodes.Status200OK)
        {
            // A sensitive addendum to an ordinary note is shown to the person who has just signed it.
            return RedirectToPage("View", null, new { patientId, noteId, done = "addendum", sensitive = Sensitive && !Note.Sensitive ? true : (bool?)null }, "addenda");
        }

        // The page comes back with the note under the form: that is a view of it.
        if (await Phi.NoteAsync(noteId, patientId, HttpContext.RequestAborted) is not { } shownNote)
        {
            return NotFound();
        }

        Note = shownNote;
        Errors = result.Errors;
        Response.StatusCode = result.Status;
        return Page();
    }

    async Task<IActionResult> LoadAsync(Guid patientId, Guid noteId, bool shown)
    {
        if (await LoadPatientAsync(patientId, AuditParts.VisitNote) is { } denied)
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
        return Page();
    }
}
