using Ehr.Data.Audit;
using Ehr.Data.Patients;
using Ehr.Domain.Notes;
using Ehr.Domain.Patients;
using Ehr.Domain.Phi;
using Ehr.Web.Forwarding;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Ehr.Web.Areas.Clinical.Pages.Patients;

// Listening to a visit to draft its note (ambient listening, the expansion plan's step 16; UC 23, proposed FR-62). The
// page asks the clinician to confirm, before anything is recorded, that everyone who will speak agreed aloud; its script
// (Ehr.Design, js/ambient.js) sends that to the API (Api/Dictation/AmbientApi.cs), which records the consent, and only
// then opens the microphone, sends the visit's audio in chunks, and after Stop asks the clinician who each voice is
// before the note is drafted into the same review as a dictation (UC 13).
//
// As Dictate's: only for a patient on the clinician's panel, and anyone else is sent back to the chart before anything
// about the patient is read; the patient's name for the banner is one recorded read. A draft with a review open goes to
// that review. On the cloud site the page opens but offers nothing: a visit is recorded only at the clinic. With
// listening switched off (Ehr:Ambient:Enabled), the page answers 404.
public class ListenModel(PhiReader phi, PatientAssignments assignments, NoteWriteAuthority authority, Ambient.AmbientListening listening)
    : PageModel
{
    [BindProperty(SupportsGet = true)]
    public SearchQuery Search { get; set; } = SearchQuery.None;

    [BindProperty(SupportsGet = true)] public Guid? DraftId { get; set; }

    public long? DraftVersion { get; private set; }

    public Patient Patient { get; private set; } = null!;

    public IReadOnlyList<AllergyIntolerance> Allergies { get; private set; } = [];

    // Listening is offered on the on-site app only (Forwarding/CloudForwardingMiddleware never forwards it), and not in
    // what the cloud app forwards here, such as a page it fetches from here to show its own user's last save
    // (Forwarding/ClinicNetwork).
    public bool AtTheClinic => ClinicNetwork.AtTheClinic(Request, authority);

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        // Listening switched off (Ambient/AmbientListening): not here at all.
        if (!listening.Enabled)
        {
            return NotFound();
        }

        if (!await assignments.IsOnPanelAsync(id, HttpContext.RequestAborted))
        {
            return RedirectToPage("Chart", Search.ChartRouteValues(id));
        }

        var header = await phi.HeaderAsync(id, AuditParts.NoteDraft, HttpContext.RequestAborted);
        if (header is null)
        {
            return NotFound();
        }

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

        Patient = header.Patient;
        Allergies = header.Allergies;
        return Page();
    }
}
