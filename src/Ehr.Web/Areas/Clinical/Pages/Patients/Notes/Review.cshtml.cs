using System.Text.Json;
using Ehr.Data.Audit;
using Ehr.Data.Patients;
using Ehr.Domain.Notes;
using Ehr.Domain.Patients;
using Ehr.Domain.Phi;
using Ehr.Web.Api.Dictation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Ehr.Web.Areas.Clinical.Pages.Patients.Notes;

public class ReviewModel(PhiReader phi, PatientAssignments assignments, BreakGlass breakGlass, NoteReviews reviews)
    : PatientNotePage(phi, assignments, breakGlass)
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { AllowOutOfOrderMetadataProperties = true };
    public AiNoteReview Review { get; private set; } = null!;
    public ClinicalDraft Draft { get; private set; } = null!;
    public IReadOnlyList<AiFieldProposal> Proposals { get; private set; } = [];
    public DictationResult? Result { get; private set; }
    public bool Signed { get; private set; }
    public IReadOnlyList<string> Errors { get; private set; } = [];
    [BindProperty] public long Version { get; set; }
    [BindProperty] public Guid ProposalId { get; set; }
    [BindProperty] public Guid DecisionKey { get; set; }
    [BindProperty] public string? Decision { get; set; }
    [BindProperty] public string? EditedValue { get; set; }
    [BindProperty] public bool Append { get; set; }

    public object Snapshot => new { Review.State, version = Draft.Version, result = Result, proposals = Proposals };

    public async Task<IActionResult> OnGetAsync(Guid patientId, Guid draftId, Guid reviewId) =>
        await LoadAsync(patientId, draftId, reviewId) ?? Page();

    public Task<IActionResult> OnPostDecideAsync(Guid patientId, Guid draftId, Guid reviewId) =>
        ChangeAsync(patientId, draftId, reviewId, () => reviews.DecideAsync(reviewId, ProposalId, Version,
            DecisionKey, Decision ?? "", EditedValue, HttpContext.RequestAborted), complete: false);

    public Task<IActionResult> OnPostCompleteAsync(Guid patientId, Guid draftId, Guid reviewId) =>
        ChangeAsync(patientId, draftId, reviewId, () => reviews.CompleteAsync(reviewId, Version, Append, HttpContext.RequestAborted), complete: true);

    public Task<IActionResult> OnPostDiscardAsync(Guid patientId, Guid draftId, Guid reviewId) =>
        ChangeAsync(patientId, draftId, reviewId, () => reviews.DiscardAsync(reviewId, HttpContext.RequestAborted), complete: true);

    async Task<IActionResult> ChangeAsync(Guid patientId, Guid draftId, Guid reviewId, Func<Task<ReviewResult>> action, bool complete)
    {
        if (await LoadAsync(patientId, draftId, reviewId) is { } denied) return denied;
        // Route identity is checked before invoking the domain operation; extra form metadata is never authoritative.
        var result = ModelState.IsValid ? await action() : ReviewResult.Invalid("Supply a valid draft version and decision.");
        var url = complete && result.Status == 200 ? Url.Page("Edit", new { patientId, draftId }) : null;
        if (Request.Headers.ContainsKey("HX-Request"))
        {
            if (result.Status != 200) return new JsonResult(new { result.Errors }) { StatusCode = result.Status };
            await LoadAsync(patientId, draftId, reviewId);
            return new JsonResult(new { result.Errors, snapshot = Snapshot, url });
        }
        if (url is not null) return Redirect(url);
        await LoadAsync(patientId, draftId, reviewId);
        Errors = result.Errors;
        Response.StatusCode = result.Status;
        return Page();
    }

    async Task<IActionResult?> LoadAsync(Guid patientId, Guid draftId, Guid reviewId)
    {
        if (await LoadPatientAsync(patientId, AuditParts.NoteDraft) is { } denied) return denied;
        var review = await Phi.AiReviewAsync(reviewId, HttpContext.RequestAborted);
        if (review is null || review.PatientId != patientId || review.DraftId != draftId) return NotFound();
        var draft = await Phi.ReadDraftAsync(draftId, patientId, HttpContext.RequestAborted, tracking: false);
        if (draft is null) return NotFound();
        Review = review;
        Draft = draft;
        Proposals = (await Phi.AiProposalsAsync(reviewId, HttpContext.RequestAborted))
            .OrderBy(p => Array.IndexOf(new[] { "history", "assessment", "plan" }, p.Field)).ToList();
        Result = review.ResultJson is null ? null : JsonSerializer.Deserialize<DictationResult>(review.ResultJson, Json);
        Signed = await Phi.SignedDraftAsync(draftId, patientId, HttpContext.RequestAborted) is not null;
        return null;
    }
}
