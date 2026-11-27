using Ehr.Data.Audit;
using Ehr.Data.Patients;
using Ehr.Domain.Notes;
using Ehr.Domain.Patients;
using Ehr.Domain.Phi;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Ehr.Web.Areas.Clinical.Pages.Patients.Notes;

public class ConfirmModel(PhiReader phi, PatientAssignments assignments, BreakGlass breakGlass, ClinicalNotes notes)
    : PatientNotePage(phi, assignments, breakGlass)
{
    [BindProperty(SupportsGet = true)] public long Version { get; set; }
    [BindProperty] public bool Confirmed { get; set; }

    // Step 10: the signer marks the note sensitive as they sign it.
    [BindProperty] public bool Sensitive { get; set; }
    public ClinicalDraft Draft { get; private set; } = null!;
    public IReadOnlyList<string> Errors { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(Guid patientId, Guid draftId)
    {
        if (await LoadPatientAsync(patientId, AuditParts.NoteDraft) is { } denied)
        {
            return denied;
        }

        var draft = await Phi.ReadDraftAsync(draftId, patientId, HttpContext.RequestAborted);
        if (draft is null)
        {
            return NotFound();
        }

        if (await Phi.SignedDraftAsync(draftId, patientId, HttpContext.RequestAborted) is { } signed)
        {
            return RedirectToPage("View", new { patientId, noteId = signed.Id });
        }

        Draft = draft;
        Errors = Version == draft.Version ? await notes.SigningProblemsAsync(draft, HttpContext.RequestAborted) : NoteResult.Conflict.Errors;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(Guid patientId, Guid draftId)
    {
        Response.Headers.CacheControl = "no-store";
        var result = ModelState.IsValid
            ? await notes.SignAsync(patientId, draftId, Version, Confirmed, HttpContext.RequestAborted, Sensitive)
            : new NoteResult(422, ["Reload the page and try again."]);
        var url = result.Note is { } note ? Url.Page("View", new { patientId, noteId = note.Id, signed = true }) : null;
        if (Request.Headers.ContainsKey("HX-Request"))
        {
            return new JsonResult(new { result.Errors, url }) { StatusCode = result.Status };
        }

        if (url is not null)
        {
            return Redirect(url);
        }

        if (await OnGetAsync(patientId, draftId) is not PageResult)
        {
            return NotFound();
        }

        Errors = result.Errors;
        Response.StatusCode = result.Status;
        return Page();
    }
}
