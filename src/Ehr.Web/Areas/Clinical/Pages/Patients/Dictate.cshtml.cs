using Ehr.Data.Audit;
using Ehr.Data.Patients;
using Ehr.Domain.Patients;
using Ehr.Domain.Phi;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Ehr.Web.Areas.Clinical.Pages.Patients;

// Dictating a note for a patient (UC 9 Transcribe dictated audio, UC 10 Structure and validate note fields; Samir
// Mukshe). The page is the screen for the clinician dictation API (Api/Dictation): its script (Ehr.Design,
// js/dictation.js) records the microphone, sends the audio in chunks, shows the transcript as the clinician speaks,
// and after Stop shows the Subjective, Objective, Assessment and Plan the model proposed and validation let through,
// each awaiting review. Nothing is saved here (FR-13): reviewing the fields is UC 13, and saving and signing the note is UC 14.
//
// The same rule as the chart's "New note": only for a patient on the clinician's panel. Anyone else, a clinician under
// emergency access included, is sent back to the chart before anything about the patient is read. The patient's name
// for the banner is one recorded read through the PHI reader. Off-site with the clinic's server down the page still
// opens, but offers no dictation, since the dictation service runs beside the on-prem app (UC 2).
public class DictateModel(PhiReader phi, PatientAssignments assignments) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public SearchQuery Search { get; set; } = SearchQuery.None;

    [BindProperty(SupportsGet = true)] public Guid? DraftId { get; set; }
    public long? DraftVersion { get; private set; }

    public Patient Patient { get; private set; } = null!;

    // The patient's current allergies, for the banner: part of the same recorded read as the patient.
    public IReadOnlyList<AllergyIntolerance> Allergies { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        if (!await assignments.IsOnPanelAsync(id, HttpContext.RequestAborted))
        {
            return RedirectToPage("Chart", Search.ChartRouteValues(id));
        }

        var header = await phi.HeaderAsync(id, AuditParts.NoteDraft, HttpContext.RequestAborted);
        if (header is null)
        {
            return NotFound();
        }

        var patient = header.Patient;
        Allergies = header.Allergies;

        if (DraftId is { } idOfDraft)
        {
            var draft = await phi.ReadDraftAsync(idOfDraft, id, HttpContext.RequestAborted, tracking: false);
            if (draft is null) return NotFound();
            if (await phi.SignedDraftAsync(idOfDraft, id, HttpContext.RequestAborted) is { } signed)
                return RedirectToPage("Notes/View", new { patientId = id, noteId = signed.Id });
            var review = (await phi.AiReviewsAsync(idOfDraft, AuditParts.NoteDraft, HttpContext.RequestAborted))
                .FirstOrDefault(r => r.State is AiReviewStates.Pending or AiReviewStates.Recording);
            if (review is not null)
                return RedirectToPage("Notes/Review", new { patientId = id, draftId = idOfDraft, reviewId = review.Id });
            DraftVersion = draft.Version;
        }
        Patient = patient;
        return Page();
    }
}
