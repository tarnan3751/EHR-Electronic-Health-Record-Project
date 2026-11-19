using System.Security.Claims;
using Ehr.Data.Patients;
using Ehr.Domain.Notes;
using Ehr.Domain.Patients;
using Ehr.Domain.Phi;
using Ehr.Web.Forwarding;
using Microsoft.AspNetCore.Mvc;

namespace Ehr.Web.Areas.Clinical.Pages.Patients.Notes;

// Marking a signed note, or one of its addenda, sensitive after signing, or unmarking it, with a reason (the expansion
// plan's step 10b). The text never changes. A clinician with the patient on their panel marks; only the person who
// made it sensitive, or the note's signer, unmarks: the page says so instead of offering a form to anyone else, and the
// database refuses them whatever is posted. Off-site the post is forwarded to the clinic's server.
[ShownIfNotSaved(nameof(Reason))]
public class SensitivityModel(PhiReader phi, PatientAssignments assignments, BreakGlass breakGlass, ClinicalNotes notes)
    : PatientNotePage(phi, assignments, breakGlass)
{
    readonly PatientAssignments assignments = assignments;

    // The addendum, when it is one and not the note itself.
    [BindProperty(SupportsGet = true)]
    public Guid? Addendum { get; set; }

    [BindProperty]
    public string? Reason { get; set; }

    // What the form was offering when it was shown: to mark (true) or to unmark (false). The post does that, and not
    // "the other of whatever it is now": a form sent twice, or after someone else changed it, would otherwise undo it.
    [BindProperty]
    public bool? Mark { get; set; }

    public ClinicalNote Note { get; private set; } = null!;

    public AddendumEntry? Entry { get; private set; }

    // Whether it is sensitive now: the page offers the other.
    public bool Sensitive { get; private set; }

    // Whether the signed-in clinician may unmark it: they made it sensitive, or they signed the note.
    public bool MayUnmark { get; private set; }

    // An addendum can't be unmarked while its note is sensitive.
    public bool HeldByNote => Entry is not null && Note.Sensitive;

    public bool IsSigner { get; private set; }

    public IReadOnlyList<string> Errors { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(Guid patientId, Guid noteId) => await LoadAsync(patientId, noteId) ?? Page();

    public async Task<IActionResult> OnPostAsync(Guid patientId, Guid noteId)
    {
        if (await LoadAsync(patientId, noteId) is { } refused)
        {
            return refused;
        }

        if (Mark is not { } mark)
        {
            return BadRequest();
        }

        var result = mark == Sensitive
            ? new SensitivityResult(StatusCodes.Status409Conflict, [mark ? "It is already marked sensitive. Nothing was changed." : "It is already unmarked. Nothing was changed."])
            : await notes.SetSensitivityAsync(patientId, noteId, Addendum, mark, Reason, HttpContext.RequestAborted);
        if (result.Status == StatusCodes.Status200OK)
        {
            // What was just marked is shown to the person who marked it, as a recorded view.
            return RedirectToPage("View", null, new { patientId, noteId, done = mark ? "marked" : "unmarked", sensitive = mark && Addendum is not null ? true : (bool?)null },
                Addendum is null ? null : "addenda");
        }

        Errors = result.Errors;
        Response.StatusCode = result.Status;
        return Page();
    }

    async Task<IActionResult?> LoadAsync(Guid patientId, Guid noteId)
    {
        if (await LoadPatientAsync(patientId) is { } denied)
        {
            return denied;
        }

        if (!await assignments.IsOnPanelAsync(patientId, HttpContext.RequestAborted))
        {
            return RedirectToPage("View", new { patientId, noteId });
        }

        // The page shows nothing of the note's text: no view of a sensitive one is recorded here.
        if (await Phi.NoteAsync(noteId, patientId, HttpContext.RequestAborted, shown: false) is not { } note)
        {
            return NotFound();
        }

        Note = note;
        var me = Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : Guid.Empty;
        if (Addendum is { } addendum)
        {
            if (await Phi.AddendumAsync(note, addendum, HttpContext.RequestAborted) is not { } entry)
            {
                return NotFound();
            }

            Entry = entry;
            (Sensitive, MayUnmark) = (entry.Sensitive, me == (entry.SensitiveMarkedBy ?? entry.AuthorId) || me == note.SignerId);
        }
        else
        {
            (Sensitive, MayUnmark) = (note.Sensitive, me == note.SensitiveMarkedBy || me == note.SignerId);
            IsSigner = me == note.SignerId;
        }

        return null;
    }
}
