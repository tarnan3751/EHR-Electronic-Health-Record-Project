using System.ComponentModel.DataAnnotations;
using Ehr.Domain.Notes;
using Ehr.Domain.Patients;
using Ehr.Domain.Phi;
using Microsoft.AspNetCore.Mvc;

namespace Ehr.Web.Areas.Clinical.Pages.Patients.Notes;

public class EditModel(PhiReader phi, PatientAssignments assignments, BreakGlass breakGlass, ClinicalNotes notes)
    : PatientNotePage(phi, assignments, breakGlass)
{
    // Only content and an optimistic version are accepted. Identity, timestamps, review status and final state
    // are exclusively server-owned, including when a caller sends extra form fields.
    [BindProperty, DisplayFormat(ConvertEmptyStringToNull = false)] public string History { get; set; } = "";
    [BindProperty, DisplayFormat(ConvertEmptyStringToNull = false)] public string Assessment { get; set; } = "";
    [BindProperty, DisplayFormat(ConvertEmptyStringToNull = false)] public string Plan { get; set; } = "";
    [BindProperty] public long Version { get; set; }
    public string SaveStatus { get; private set; } = "Saved";
    public IReadOnlyList<string> Errors { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(Guid patientId, Guid draftId)
    {
        if (await LoadPatientAsync(patientId) is { } denied)
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

        var content = NoteContent.From(draft);
        (History, Assessment, Plan, Version) = (content.History, content.Assessment, content.Plan, draft.Version);
        return Page();
    }

    public Task<IActionResult> OnPostSaveAsync(Guid patientId, Guid draftId) => SaveAsync(patientId, draftId, review: false);
    public Task<IActionResult> OnPostReviewAsync(Guid patientId, Guid draftId) => SaveAsync(patientId, draftId, review: true);

    async Task<IActionResult> SaveAsync(Guid patientId, Guid draftId, bool review)
    {
        Response.Headers.CacheControl = "no-store";
        // Empty fields are valid in a draft. Razor's default string binder converts them to null.
        var content = new NoteContent(History ?? "", Assessment ?? "", Plan ?? "");
        var result = ModelState.IsValid
            ? await notes.SaveAsync(patientId, draftId, Version, content, HttpContext.RequestAborted)
            : new NoteResult(422, ["Send a valid saved draft version."]);
        var errors = result.Errors;
        if (review && result.Draft is { } saved)
        {
            errors = ClinicalNotes.SigningProblems(saved);
        }

        var status = errors.Count > 0 && result.Status == 200 ? 422 : result.Status;
        var url = review && status == 200 ? Url.Page("Confirm", new { patientId, draftId, version = result.Draft!.Version }) : null;
        if (Request.Headers.ContainsKey("HX-Request"))
        {
            return new JsonResult(new { version = result.Draft?.Version, errors, url }) { StatusCode = status };
        }

        if (url is not null)
        {
            return Redirect(url);
        }

        if (await LoadPatientAsync(patientId) is { } denied)
        {
            return denied;
        }

        Version = result.Draft?.Version ?? Version;
        // Tag helpers prefer posted ModelState over updated properties.
        ModelState.Remove(nameof(Version));
        Errors = errors;
        SaveStatus = result.Draft is null ? "Save Failed" : "Saved";
        Response.StatusCode = status;
        return Page();
    }
}
