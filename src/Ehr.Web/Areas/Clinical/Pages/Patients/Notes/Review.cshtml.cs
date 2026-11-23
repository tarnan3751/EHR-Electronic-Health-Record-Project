using System.Text.Json;
using System.Text.Json.Serialization;
using Ehr.Data.Audit;
using Ehr.Data.Patients;
using Ehr.Domain.Notes;
using Ehr.Domain.Patients;
using Ehr.Domain.Phi;
using Ehr.Web.Api.Dictation;
using Ehr.Web.Forwarding;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Ehr.Web.Areas.Clinical.Pages.Patients.Notes;

public class ReviewModel(PhiReader phi, PatientAssignments assignments, BreakGlass breakGlass, NoteReviews reviews,
    NoteWriteAuthority authority)
    : PatientNotePage(phi, assignments, breakGlass)
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { AllowOutOfOrderMetadataProperties = true };

    // A recorded visit's stored result as the page reads it: its segments, where each passage was, skipped unread, since
    // the transcript's own say the same (or the transcript is gone, and with it what they would point at).
    static readonly JsonSerializerOptions Visit = new(Json) { Converters = { new SegmentsUnread() } };
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

    // A recorded visit's result has no transcript (null): the stored result keeps no word of it, so the page's script leaves
    // what the page shows.
    public object Snapshot => new { Review.State, version = Draft.Version, result = Result, proposals = Proposals };

    // A recorded visit's transcript as this page read it, at the clinic: its JSON, as it was stored and checked, which the
    // page writes in as it is, beside the snapshot, never read into objects and written out again, every "<" escaped so
    // nothing in it can end its script element; and its text, which the page shows without its script. None otherwise.
    public string? ConversationJson { get; private set; }

    public string? ConversationText { get; private set; }

    // A recorded visit's review (the expansion plan's step 16). Its transcript is kept apart, only while it is open and for
    // 7 days at most, and read only at the clinic: the review shows it from there, or says where it went. While it is kept,
    // the proposals are decided at the clinic only, against what was said; once it is gone, none is accepted as offered.
    public bool Ambient => Review.Mode == AiReviewModes.Ambient;

    public bool TranscriptKept { get; private set; }

    // Served at the clinic and not forwarded by the cloud app, which fails closed (Forwarding/ClinicNetwork).
    public bool AtTheClinic => ClinicNetwork.AtTheClinic(Request, authority);

    // Kept, and here off-site: the decisions wait for the clinic.
    public bool DecideAtTheClinic => Ambient && TranscriptKept && !AtTheClinic;

    // Gone from a review still open: rejected, or edited into the clinician's words, never accepted as offered.
    public bool TranscriptGone => Ambient && !TranscriptKept && Review.State == AiReviewStates.Pending;

    // The fields the review shows: Subjective, Objective, Assessment and Plan (the expansion plan's step 14), or, for a
    // dictation sorted before then, its History, Assessment and Plan, as it was saved.
    public IReadOnlyList<string> FieldNames { get; private set; } = Fields;

    static readonly string[] Fields = ["subjective", "objective", "assessment", "plan"];

    static readonly string[] EarlierFields = ["history", "assessment", "plan"];

    public async Task<IActionResult> OnGetAsync(Guid patientId, Guid draftId, Guid reviewId) =>
        await LoadAsync(patientId, draftId, reviewId) ?? Page();

    public Task<IActionResult> OnPostDecideAsync(Guid patientId, Guid draftId, Guid reviewId) =>
        ChangeAsync(patientId, draftId, reviewId, () => DecideAtTheClinic
            ? Task.FromResult(ReviewResult.Invalid(FinishAtTheClinic))
            : reviews.DecideAsync(reviewId, ProposalId, Version, DecisionKey, Decision ?? "", EditedValue, HttpContext.RequestAborted), complete: false);

    // Step 16: a patient who withdraws their consent after the visit. At the clinic, as every decision on a kept transcript.
    public Task<IActionResult> OnPostRejectAllAsync(Guid patientId, Guid draftId, Guid reviewId) =>
        ChangeAsync(patientId, draftId, reviewId, () => DecideAtTheClinic
            ? Task.FromResult(ReviewResult.Invalid(FinishAtTheClinic))
            : reviews.RejectAllAndFinishAsync(reviewId, Version, HttpContext.RequestAborted), complete: true);

    public const string FinishAtTheClinic = "Finish this review at the clinic: the conversation is read only on the clinic's network.";

    // Finishing a visit's review deletes its transcript: at the clinic too, while it is kept.
    public Task<IActionResult> OnPostCompleteAsync(Guid patientId, Guid draftId, Guid reviewId) =>
        ChangeAsync(patientId, draftId, reviewId, () => DecideAtTheClinic
            ? Task.FromResult(ReviewResult.Invalid(FinishAtTheClinic))
            : reviews.CompleteAsync(reviewId, Version, Append, HttpContext.RequestAborted), complete: true);

    public Task<IActionResult> OnPostDiscardAsync(Guid patientId, Guid draftId, Guid reviewId) =>
        ChangeAsync(patientId, draftId, reviewId, () => reviews.DiscardAsync(reviewId, HttpContext.RequestAborted), complete: true);

    // Step 15: dismissing a proposed order or allergy. Back to the review, the cursor on the next one waiting.
    public async Task<IActionResult> OnPostDismissProposalAsync(Guid patientId, Guid draftId, Guid reviewId)
    {
        if (await LoadAsync(patientId, draftId, reviewId, read: false) is { } denied) return denied;
        var result = await reviews.DismissAsync(reviewId, ChartProposalId, HttpContext.RequestAborted);
        if (result.Status == 200)
            return Redirect(Url.Page("Review", new { patientId, draftId, reviewId, decided = ChartProposalId })!);
        await LoadAsync(patientId, draftId, reviewId);
        Errors = result.Errors;
        Response.StatusCode = result.Status;
        return Page();
    }

    // A decision, as the page's script posts it, reads where the review stands before and after, never a visit's transcript:
    // it answers with the review's state, the draft's version, the proposals and the stored result, with no transcript
    // (the page keeps the one it shows). Only a page rendered again, without the script, reads the transcript, once.
    async Task<IActionResult> ChangeAsync(Guid patientId, Guid draftId, Guid reviewId, Func<Task<ReviewResult>> action, bool complete)
    {
        if (await LoadAsync(patientId, draftId, reviewId, read: false) is { } denied) return denied;
        // Route identity is checked before invoking the domain operation; extra form metadata is never authoritative.
        var result = ModelState.IsValid ? await action() : ReviewResult.Invalid("Supply a valid draft version and decision.");
        var url = complete && result.Status == 200 ? Url.Page("Edit", new { patientId, draftId }) : null;
        if (Request.Headers.ContainsKey("HX-Request"))
        {
            if (result.Status != 200) return new JsonResult(new { result.Errors }) { StatusCode = result.Status };
            await LoadAsync(patientId, draftId, reviewId, read: false);
            return new JsonResult(new { result.Errors, snapshot = Snapshot, url });
        }
        if (url is not null) return Redirect(url);
        await LoadAsync(patientId, draftId, reviewId);
        Errors = result.Errors;
        Response.StatusCode = result.Status;
        return Page();
    }

    // `read`: whether this request shows a visit's transcript, which is then read once, a recorded read, at the clinic.
    // Every read the page makes is recorded together, in one statement.
    Task<IActionResult?> LoadAsync(Guid patientId, Guid draftId, Guid reviewId, bool read = true) =>
        Phi.TogetherAsync(() => ReadAsync(patientId, draftId, reviewId, read), HttpContext.RequestAborted);

    async Task<IActionResult?> ReadAsync(Guid patientId, Guid draftId, Guid reviewId, bool read)
    {
        if (await LoadPatientAsync(patientId, AuditParts.NoteDraft) is { } denied) return denied;
        var review = await Phi.AiReviewAsync(reviewId, HttpContext.RequestAborted);
        if (review is null || review.PatientId != patientId || review.DraftId != draftId) return NotFound();
        var draft = await Phi.ReadDraftAsync(draftId, patientId, HttpContext.RequestAborted, tracking: false);
        if (draft is null) return NotFound();
        Review = review;
        Draft = draft;
        Result = review.ResultJson is null ? null
            : JsonSerializer.Deserialize<DictationResult>(review.ResultJson, review.Mode == AiReviewModes.Ambient ? Visit : Json);
        if (Ambient && Result is not null)
        {
            // At the clinic, for a page that shows it, the transcript itself, a recorded read, its JSON read once; otherwise,
            // and for what the cloud app forwards here, only whether it is still kept, so it never leaves the clinic's
            // network. The stored result's own transcript has no words: this one replaces it, or none does.
            var transcript = AtTheClinic && read ? await Phi.AmbientTranscriptAsync(reviewId, HttpContext.RequestAborted) : null;
            TranscriptKept = transcript is not null
                || !(AtTheClinic && read) && await Phi.AmbientTranscriptKeptAsync(reviewId, HttpContext.RequestAborted);
            Result = Result with { Transcript = null! };
            if (transcript is not null)
            {
                ConversationJson = transcript.Transcript.Replace("<", "\\u003C", StringComparison.Ordinal);
                ConversationText = TextOf(transcript.Transcript);
            }
        }

        var proposals = await Phi.AiProposalsAsync(reviewId, HttpContext.RequestAborted);
        FieldNames = Result?.Fields?.History is not null || proposals.Any(p => p.Field == "history") ? EarlierFields : Fields;
        Proposals = proposals.OrderBy(p => FieldNames.ToList().IndexOf(p.Field)).ToList();
        ChartProposals = (await Phi.ChartProposalsAsync(reviewId, AuditParts.NoteDraft, HttpContext.RequestAborted))
            .Select(row => ProposedFromDictation.From(row, review, Result)).OfType<ProposedFromDictation>().ToList();
        Signed = await Phi.SignedDraftAsync(draftId, patientId, HttpContext.RequestAborted) is not null;
        return null;
    }

    // A transcript's text alone, read past nothing it doesn't need (as stored, it comes first), from a buffer borrowed for
    // the purpose rather than a copy made for each page.
    static string TextOf(string transcript)
    {
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(System.Text.Encoding.UTF8.GetMaxByteCount(transcript.Length));
        try
        {
            var reader = new Utf8JsonReader(buffer.AsSpan(0, System.Text.Encoding.UTF8.GetBytes(transcript, buffer)));
            reader.Read();
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var text = reader.ValueTextEquals("text"u8);
                reader.Read();
                if (text)
                {
                    return reader.GetString() ?? "";
                }

                reader.Skip();
            }

            return "";
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    // Reads a list of segments as none, skipping them.
    sealed class SegmentsUnread : JsonConverter<IReadOnlyList<TranscriptSegment>>
    {
        public override IReadOnlyList<TranscriptSegment> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            reader.Skip();
            return [];
        }

        public override void Write(Utf8JsonWriter writer, IReadOnlyList<TranscriptSegment> value, JsonSerializerOptions options) =>
            throw new NotSupportedException();
    }
}
