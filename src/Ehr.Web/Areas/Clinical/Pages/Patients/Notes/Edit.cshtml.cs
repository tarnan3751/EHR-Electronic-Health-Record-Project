using System.ComponentModel.DataAnnotations;
using Ehr.Data.Audit;
using Ehr.Data.Patients;
using Ehr.Domain.Notes;
using Ehr.Domain.Patients;
using Ehr.Domain.Phi;
using Ehr.Web.Forwarding;
using Microsoft.AspNetCore.Mvc;

namespace Ehr.Web.Areas.Clinical.Pages.Patients.Notes;

// Off-site with the clinic's server unreachable, a save without JavaScript shows the four sections again on the "Not
// saved" page (UC 2); with it, note-editor.js keeps them on screen.
[ShownIfNotSaved(nameof(Subjective), nameof(Objective), nameof(Assessment), nameof(Plan))]
public class EditModel(PhiReader phi, PatientAssignments assignments, BreakGlass breakGlass, ClinicalNotes notes, Ehr.Domain.Time.ClinicTime clinic)
    : PatientNotePage(phi, assignments, breakGlass)
{
    // Only content, the visit type and an optimistic version are accepted. Identity, timestamps, review status and
    // final state are exclusively server-owned, including when a caller sends extra form fields.
    [BindProperty, DisplayFormat(ConvertEmptyStringToNull = false)] public string Subjective { get; set; } = "";
    [BindProperty, DisplayFormat(ConvertEmptyStringToNull = false)] public string Objective { get; set; } = "";
    [BindProperty, DisplayFormat(ConvertEmptyStringToNull = false)] public string Assessment { get; set; } = "";
    [BindProperty, DisplayFormat(ConvertEmptyStringToNull = false)] public string Plan { get; set; } = "";
    [BindProperty] public long Version { get; set; }

    // The visit type chosen (step 14), a reference template's code, or none. Its prompts are guidance, never note text.
    [BindProperty] public string? NoteTemplateCode { get; set; }

    public IReadOnlyList<Ehr.Data.Reference.NoteTemplate> Templates { get; private set; } = [];
    public Guid? ActiveReviewId { get; private set; }
    public IReadOnlyList<AiNoteReview> CompletedReviews { get; private set; } = [];

    // Where the text in the sections came from (step 14): an earlier draft's History, or the desktop app's plain text,
    // is put in Subjective for its author to sort, and the page says so. The draft itself changes only when saved.
    public NoteOrigin Origin { get; private set; } = NoteOrigin.Sections;

    // The draft as it was last saved, for the page shown while the clinic's server can't be reached.
    public IReadOnlyList<NoteSection> Saved { get; private set; } = [];

    public string SaveStatus { get; private set; } = "Saved";
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

        var reviews = await Phi.AiReviewsAsync(draftId, AuditParts.NoteDraft, HttpContext.RequestAborted);
        ActiveReviewId = reviews.FirstOrDefault(r => r.State is AiReviewStates.Pending or AiReviewStates.Recording)?.Id;
        CompletedReviews = reviews.Where(r => r.State == AiReviewStates.Completed).ToList();
        var content = NoteContent.From(draft);
        (Subjective, Objective, Assessment, Plan, Version) = (content.Subjective, content.Objective, content.Assessment, content.Plan, draft.Version);
        (Origin, Saved) = (NoteContent.OriginOf(draft), NoteSections.AsSaved(draft));
        (NoteTemplateCode, Templates) = (draft.NoteTemplateCode, await notes.TemplatesAsync(HttpContext.RequestAborted));
        return Page();
    }

    // The vital signs the clinician may add to Objective (step 14), when they ask: JSON, for note-editor.js to show. Read
    // as the vital signs page reads them, with its part; only for a patient on the clinician's panel, as writing a note
    // is. Nothing about the draft changes here: the text goes into Objective only if the clinician adds it.
    public async Task<IActionResult> OnGetVitalsAsync(Guid patientId, Guid draftId)
    {
        Response.Headers.CacheControl = "no-store";
        if (!await Phi.IsAssignedAsync(patientId, HttpContext.RequestAborted))
        {
            return NotFound();
        }

        var offer = ObjectiveVitals.Of(await Phi.ObjectiveVitalsAsync(patientId, clinic, HttpContext.RequestAborted), clinic);
        return new JsonResult(new { text = offer.Text, day = offer.Day, today = offer.Today });
    }

    public Task<IActionResult> OnPostSaveAsync(Guid patientId, Guid draftId) => SaveAsync(patientId, draftId, review: false);
    public Task<IActionResult> OnPostReviewAsync(Guid patientId, Guid draftId) => SaveAsync(patientId, draftId, review: true);

    async Task<IActionResult> SaveAsync(Guid patientId, Guid draftId, bool review)
    {
        Response.Headers.CacheControl = "no-store";
        // Empty fields are valid in a draft. Razor's default string binder converts them to null.
        var content = new NoteContent(Subjective ?? "", Objective ?? "", Assessment ?? "", Plan ?? "");
        var result = ModelState.IsValid
            ? await notes.SaveAsync(patientId, draftId, Version, content, HttpContext.RequestAborted, NoteTemplateCode)
            : new NoteResult(422, ["Send a valid saved draft version."]);
        var errors = result.Errors;
        if (review && result.Draft is { } saved)
        {
            errors = await notes.SigningProblemsAsync(saved, HttpContext.RequestAborted);
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

        if (await LoadPatientAsync(patientId, AuditParts.NoteDraft) is { } denied)
        {
            return denied;
        }

        Version = result.Draft?.Version ?? Version;
        Templates = await notes.TemplatesAsync(HttpContext.RequestAborted);
        // Tag helpers prefer posted ModelState over updated properties.
        ModelState.Remove(nameof(Version));
        Errors = errors;
        SaveStatus = result.Draft is null ? "Save failed" : "Saved";
        Response.StatusCode = status;
        return Page();
    }
}
