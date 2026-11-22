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

    // The orders and allergies to add the dictation proposes (the expansion plan's step 15), each opened in its form or
    // dismissed here; and the one just decided, there or here, so the cursor goes on to the next one waiting.
    public IReadOnlyList<ProposedFromDictation> ChartProposals { get; private set; } = [];
    [BindProperty] public Guid ChartProposalId { get; set; }
    [BindProperty(SupportsGet = true)] public Guid? Decided { get; set; }

    // The proposal waiting after the one just decided, in the dictation's order, or the first waiting; none when nothing
    // was decided just now, or nothing waits.
    public Guid? NextProposal => Decided is not { } decided ? null
        : (ChartProposals.SkipWhile(p => p.Row.Id != decided).FirstOrDefault(p => p.Waiting) ?? ChartProposals.FirstOrDefault(p => p.Waiting))?.Row.Id;

    public object Snapshot => new { Review.State, version = Draft.Version, result = Result, proposals = Proposals };

    // A dictation in Spanish (the expansion plan's step 17): its transcript is Spanish, and each proposed field English,
    // shown a sentence at a time beside the Spanish it was translated from.
    public bool Translated => Result?.SpokenLanguage == SpokenLanguage.Spanish;

    // A field's sentences, each with the Spanish words it was translated from, when the dictation was in Spanish.
    public IEnumerable<(string English, string Spanish)> Statements(string field)
    {
        if (!Translated || Result?.Fields is not { } fields) yield break;
        var proposed = field switch
        {
            "subjective" => fields.Subjective,
            "objective" => fields.Objective,
            "assessment" => fields.Assessment,
            "plan" => fields.Plan,
            _ => null,
        };
        if (proposed is not PendingField { Statements: { } statements } pending) yield break;
        foreach (var statement in statements)
        {
            yield return (pending.Value[statement.Start..statement.End],
                string.Join(" … ", statement.Sources.Select(source => Result.Transcript.Text[source.Start..source.End])));
        }
    }

    // The fields the review shows: Subjective, Objective, Assessment and Plan (the expansion plan's step 14), or, for a
    // dictation sorted before then, its History, Assessment and Plan, as it was saved.
    public IReadOnlyList<string> FieldNames { get; private set; } = Fields;

    static readonly string[] Fields = ["subjective", "objective", "assessment", "plan"];

    static readonly string[] EarlierFields = ["history", "assessment", "plan"];

    public async Task<IActionResult> OnGetAsync(Guid patientId, Guid draftId, Guid reviewId) =>
        await LoadAsync(patientId, draftId, reviewId) ?? Page();

    public Task<IActionResult> OnPostDecideAsync(Guid patientId, Guid draftId, Guid reviewId) =>
        ChangeAsync(patientId, draftId, reviewId, () => reviews.DecideAsync(reviewId, ProposalId, Version,
            DecisionKey, Decision ?? "", EditedValue, HttpContext.RequestAborted), complete: false);

    public Task<IActionResult> OnPostCompleteAsync(Guid patientId, Guid draftId, Guid reviewId) =>
        ChangeAsync(patientId, draftId, reviewId, () => reviews.CompleteAsync(reviewId, Version, Append, HttpContext.RequestAborted), complete: true);

    public Task<IActionResult> OnPostDiscardAsync(Guid patientId, Guid draftId, Guid reviewId) =>
        ChangeAsync(patientId, draftId, reviewId, () => reviews.DiscardAsync(reviewId, HttpContext.RequestAborted), complete: true);

    // Step 15: dismissing a proposed order or allergy. Back to the review, the cursor on the next one waiting.
    public async Task<IActionResult> OnPostDismissProposalAsync(Guid patientId, Guid draftId, Guid reviewId)
    {
        if (await LoadAsync(patientId, draftId, reviewId) is { } denied) return denied;
        var result = await reviews.DismissAsync(reviewId, ChartProposalId, HttpContext.RequestAborted);
        if (result.Status == 200)
            return Redirect(Url.Page("Review", new { patientId, draftId, reviewId, decided = ChartProposalId })!);
        await LoadAsync(patientId, draftId, reviewId);
        Errors = result.Errors;
        Response.StatusCode = result.Status;
        return Page();
    }

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
        Result = review.ResultJson is null ? null : JsonSerializer.Deserialize<DictationResult>(review.ResultJson, Json);
        var proposals = await Phi.AiProposalsAsync(reviewId, HttpContext.RequestAborted);
        FieldNames = Result?.Fields?.History is not null || proposals.Any(p => p.Field == "history") ? EarlierFields : Fields;
        Proposals = proposals.OrderBy(p => FieldNames.ToList().IndexOf(p.Field)).ToList();
        ChartProposals = (await Phi.ChartProposalsAsync(reviewId, AuditParts.NoteDraft, HttpContext.RequestAborted))
            .Select(row => ProposedFromDictation.From(row, review, Result)).OfType<ProposedFromDictation>().ToList();
        Signed = await Phi.SignedDraftAsync(draftId, patientId, HttpContext.RequestAborted) is not null;
        return null;
    }
}
