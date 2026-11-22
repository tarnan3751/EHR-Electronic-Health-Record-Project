using Ehr.Data;
using Ehr.Data.Audit;
using Ehr.Data.Patients;
using Ehr.Domain.Patients;
using Ehr.Domain.Phi;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Ehr.Web.Areas.Clinical.Pages.Patients.Notes;

// A signed note, read-only, and its addenda (the expansion plan's step 10): each a signed text attached to the note,
// in the order they were signed, with its author and time. A sensitive note's view is recorded as a view of sensitive
// items; sensitive addenda to another note are left out until asked for. Signing an addendum is the Addendum page.
// Under them, the visit summaries written from the note for the patient (step 13a): the one standing in the portal,
// and those withdrawn, struck through. Writing one is the Summary page, withdrawing one WithdrawSummary.
public class ViewModel(PhiReader phi, PatientAssignments assignments, BreakGlass breakGlass, EhrDbContext db)
    : PatientNotePage(phi, assignments, breakGlass)
{
    readonly PatientAssignments assignments = assignments;

    public string SignerName { get; private set; } = "";

    public IReadOnlyList<AiNoteReview> CompletedReviews { get; private set; } = [];

    public ClinicalNote Note { get; private set; } = null!;

    public NoteAddenda Addenda { get; private set; } = null!;

    // The note's visit summaries (step 13a): the standing one first, then those withdrawn, latest first; sensitive ones
    // of a note that isn't sensitive only when asked for, as for addenda.
    public NoteSummaries Summaries { get; private set; } = null!;

    // Whether the note or one of its addenda is sensitive: a summary of it is then sensitive with it (step 13a).
    public bool SourcesSensitive { get; private set; }

    public bool OnPanel { get; private set; }

    public IReadOnlyDictionary<Guid, string> Markers { get; private set; } = new Dictionary<Guid, string>();

    // "Marked sensitive by Dr. X" or "Unmarked as sensitive", for the line under a note or addendum.
    public string Change(bool sensitive, Guid? markedBy) =>
        !sensitive ? "Unmarked as sensitive" : markedBy is { } id && Markers.TryGetValue(id, out var name) ? $"Marked sensitive by {name}" : "Marked sensitive";

    // "Show sensitive addenda", or summaries.
    [BindProperty(SupportsGet = true)]
    public bool Sensitive { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Done { get; set; }

    public async Task<IActionResult> OnGetAsync(Guid patientId, Guid noteId)
    {
        if (await LoadPatientAsync(patientId, AuditParts.VisitNote) is { } denied)
        {
            return denied;
        }

        if (await Phi.NoteWithAddendaAsync(noteId, patientId, Sensitive, HttpContext.RequestAborted) is not var (note, addenda, summaries))
        {
            return NotFound();
        }

        (Note, Addenda) = (note, addenda);
        OnPanel = await assignments.IsOnPanelAsync(patientId, HttpContext.RequestAborted);
        Summaries = summaries;
        SourcesSensitive = note.Sensitive || addenda.SensitiveHidden > 0 || addenda.Shown.Any(a => a.Sensitive);
        CompletedReviews = (await Phi.AiReviewsAsync(note.DraftId, AuditParts.VisitNote, HttpContext.RequestAborted))
            .Where(r => r.State == AiReviewStates.Completed).ToList();
        SignerName = await db.StaffUsers.Where(u => u.Id == note.SignerId).Select(u => u.DisplayName)
            .SingleAsync(HttpContext.RequestAborted);
        // Who marked what is sensitive after signing (step 10b), by name: so someone who can't unmark it knows whom to ask.
        var markers = addenda.Shown.Where(a => a.SensitiveReason is not null).Select(a => a.SensitiveMarkedBy)
            .Concat(summaries.Shown.Where(s => s.SensitiveReason is not null).Select(s => s.SensitiveMarkedBy))
            .Append(note.SensitiveReason is null ? null : note.SensitiveMarkedBy).OfType<Guid>().Distinct().ToList();
        Markers = markers.Count == 0
            ? []
            : await db.StaffUsers.Where(u => markers.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, HttpContext.RequestAborted);
        return Page();
    }
}
