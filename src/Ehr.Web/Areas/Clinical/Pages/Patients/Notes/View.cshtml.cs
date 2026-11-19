using Ehr.Data;
using Ehr.Data.Patients;
using Ehr.Domain.Patients;
using Ehr.Domain.Phi;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Ehr.Web.Areas.Clinical.Pages.Patients.Notes;

// A signed note, read-only, and its addenda (the expansion plan's step 10): each a signed text attached to the note,
// in the order they were signed, with its author and time. A sensitive note's view is recorded as a view of sensitive
// items; sensitive addenda to another note are left out until asked for. Signing an addendum is the Addendum page.
public class ViewModel(PhiReader phi, PatientAssignments assignments, BreakGlass breakGlass, EhrDbContext db)
    : PatientNotePage(phi, assignments, breakGlass)
{
    readonly PatientAssignments assignments = assignments;

    public string SignerName { get; private set; } = "";

    public IReadOnlyList<AiNoteReview> CompletedReviews { get; private set; } = [];

    public ClinicalNote Note { get; private set; } = null!;

    public NoteAddenda Addenda { get; private set; } = null!;

    public bool OnPanel { get; private set; }

    // "Show sensitive addenda".
    [BindProperty(SupportsGet = true)]
    public bool Sensitive { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Done { get; set; }

    public async Task<IActionResult> OnGetAsync(Guid patientId, Guid noteId)
    {
        if (await LoadPatientAsync(patientId) is { } denied)
        {
            return denied;
        }

        if (await Phi.NoteAsync(noteId, patientId, HttpContext.RequestAborted) is not { } note)
        {
            return NotFound();
        }

        Note = note;
        Addenda = await Phi.AddendaAsync(note, Sensitive, HttpContext.RequestAborted);
        OnPanel = await assignments.IsOnPanelAsync(patientId, HttpContext.RequestAborted);
        CompletedReviews = (await Phi.AiReviewsAsync(note.DraftId, HttpContext.RequestAborted))
            .Where(r => r.State == AiReviewStates.Completed).ToList();
        SignerName = await db.StaffUsers.Where(u => u.Id == note.SignerId).Select(u => u.DisplayName)
            .SingleAsync(HttpContext.RequestAborted);
        return Page();
    }
}
