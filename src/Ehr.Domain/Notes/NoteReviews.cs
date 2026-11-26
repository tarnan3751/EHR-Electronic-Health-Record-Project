using Ehr.Data;
using Ehr.Data.Audit;
using Ehr.Data.Patients;
using Ehr.Domain.Audit;
using Ehr.Domain.Phi;

namespace Ehr.Domain.Notes;

// Call within the primary transaction. Every mutation shares UC 14's draft row lock and optimistic version.
public sealed class NoteReviews(EhrDbContext db, PhiReader phi, ClinicalNotes notes, UserContext user,
    AuditRecorder audit, NoteWriteAuthority authority)
{
    public async Task<ReviewResult> BeginAsync(Guid id, Guid patient, Guid? draftId, long? version,
        string sessionId, int maxSeconds, int maxChunkBytes, CancellationToken cancel)
    {
        if (!authority.IsPrimaryHost) return ReviewResult.Offline;
        if (id == Guid.Empty) return ReviewResult.Invalid("Supply a review request ID.");
        if (await phi.AiReviewAsync(id, cancel) is { } receipt)
            return receipt.PatientId == patient && (draftId is null || receipt.DraftId == draftId)
                ? new(200, [], receipt) : ReviewResult.Missing;

        var draft = draftId is { } existing ? await phi.LockDraftAsync(existing, patient, cancel)
            : await notes.CreateAsync(patient, cancel);
        if (draft is null) return ReviewResult.Missing;
        if (draftId is not null && draft.Version != version) return ReviewResult.Conflict(draft.Version);
        if (await phi.SignedDraftAsync(draft.Id, patient, cancel) is not null) return ReviewResult.Signed;
        if ((await phi.AiReviewsAsync(draft.Id, AuditParts.NoteDraft, cancel)).Any(r => r.State is AiReviewStates.Pending or AiReviewStates.Recording))
            return ReviewResult.Invalid("Resume or discard the existing dictation review before starting another.");

        var review = new AiNoteReview
        {
            Id = id,
            PatientId = patient,
            DraftId = draft.Id,
            ClinicianId = user.UserId!.Value,
            SessionId = sessionId,
            MaxSeconds = maxSeconds,
            MaxChunkBytes = maxChunkBytes,
            State = AiReviewStates.Recording,
            StartedVersion = draft.Version,
        };
        db.AddRange(review);
        await db.SaveChangesAsync(cancel);
        return new(201, [], review, draft.Version);
    }

    public async Task<ReviewResult> StageAsync(Guid id, long version, TrustedReviewResult result, CancellationToken cancel)
    {
        if (!authority.IsPrimaryHost) return ReviewResult.Offline;
        var (review, draft) = await LockAsync(id, cancel);
        if (review is null || draft is null) return ReviewResult.Missing;
        // Lost acknowledgements retry against the same binding without reattaching or copying any text.
        if (review.State is AiReviewStates.Pending or AiReviewStates.Completed) return new(200, [], review, draft.Version);
        if (review.State != AiReviewStates.Recording) return ReviewResult.Invalid("This dictation was discarded.");
        if (draft.Version != version) return ReviewResult.Conflict(draft.Version);
        if (await phi.SignedDraftAsync(draft.Id, draft.PatientId, cancel) is not null) return ReviewResult.Signed;

        review.ResultJson = result.Json;
        review.State = result.Suggestions.Count == 0 ? AiReviewStates.Completed : AiReviewStates.Pending;
        review.ImportedVersion = result.Suggestions.Count == 0 ? draft.Version : null;
        await db.SaveChangesAsync(cancel);
        foreach (var field in result.Suggestions)
        {
            db.AddRange(new AiFieldProposal
            {
                Id = Guid.CreateVersion7(),
                ReviewId = review.Id,
                PatientId = review.PatientId,
                Field = field.Key,
                OriginalValue = field.Value,
                State = AiProposalStates.Pending,
            });
        }
        await db.SaveChangesAsync(cancel);
        draft.HasUnreviewedProposals = result.Suggestions.Count > 0;
        Advance(draft);
        audit.Record(AuditActions.AiReviewStaged, draft.PatientId, "ai_note_reviews", review.Id);
        await db.SaveChangesAsync(cancel);
        return new(200, [], review, draft.Version);
    }

    public async Task<ReviewResult> DecideAsync(Guid id, Guid proposalId, long version, Guid key,
        string decision, string? editedValue, CancellationToken cancel)
    {
        if (!authority.IsPrimaryHost) return ReviewResult.Offline;
        var (review, draft) = await LockAsync(id, cancel);
        if (review is null || draft is null) return ReviewResult.Missing;
        var proposals = await phi.AiProposalsAsync(id, cancel, tracking: true);
        var proposal = proposals.SingleOrDefault(p => p.Id == proposalId);
        if (proposal is null) return ReviewResult.Missing;
        if (key == Guid.Empty || decision is not (AiProposalStates.Accepted or AiProposalStates.Edited or AiProposalStates.Rejected))
            return ReviewResult.Invalid("Choose Accept, Edit and approve, or Reject.");
        if (proposal.State != AiProposalStates.Pending)
            return proposal.DecisionKey == key && proposal.State == decision
                && (decision != AiProposalStates.Edited || proposal.ApprovedValue == editedValue)
                ? new(200, [], review, draft.Version) : ReviewResult.Conflict(draft.Version);
        if (review.State != AiReviewStates.Pending) return ReviewResult.Invalid("This review is no longer pending.");
        if (draft.Version != version) return ReviewResult.Conflict(draft.Version);
        if (await phi.SignedDraftAsync(draft.Id, draft.PatientId, cancel) is not null) return ReviewResult.Signed;
        var approved = decision switch
        {
            AiProposalStates.Accepted => proposal.OriginalValue,
            AiProposalStates.Edited => editedValue,
            _ => null,
        };
        if (decision == AiProposalStates.Edited && string.IsNullOrWhiteSpace(approved))
            return ReviewResult.Invalid("Enter the corrected value, or reject the proposal.");
        var values = proposals.Where(p => p.ApprovedValue is not null).ToDictionary(p => p.Field, p => p.ApprovedValue!);
        if (approved is not null) values[proposal.Field] = approved;
        if (Merge(NoteContent.From(draft), values).Validate(false) is { Count: > 0 } errors)
            return new(422, errors);

        proposal.State = decision;
        proposal.ApprovedValue = approved;
        proposal.DecisionKey = key;
        // Keep the gate closed until the explicit, version-checked transfer completes, even after the last decision.
        Advance(draft);
        audit.Record(AuditActions.AiFieldReviewed, draft.PatientId, "ai_field_proposals", proposal.Id);
        await db.SaveChangesAsync(cancel);
        return new(200, [], review, draft.Version);
    }

    public async Task<ReviewResult> CompleteAsync(Guid id, long version, bool append, CancellationToken cancel)
    {
        if (!authority.IsPrimaryHost) return ReviewResult.Offline;
        var (review, draft) = await LockAsync(id, cancel);
        if (review is null || draft is null) return ReviewResult.Missing;
        if (review.State == AiReviewStates.Completed) return new(200, [], review, draft.Version);
        if (draft.Version != version) return ReviewResult.Conflict(draft.Version);
        var proposals = await phi.AiProposalsAsync(id, cancel);
        if (review.State != AiReviewStates.Pending) return ReviewResult.Invalid("Retrieve the dictation result before completing review.");
        var pending = proposals.Where(p => p.State == AiProposalStates.Pending).Select(p => p.Field).ToArray();
        if (pending.Length > 0) return ReviewResult.Invalid($"Review the pending fields: {string.Join(", ", pending)}.");
        if (await phi.SignedDraftAsync(draft.Id, draft.PatientId, cancel) is not null) return ReviewResult.Signed;
        var approved = proposals.Where(p => p.ApprovedValue is not null).ToDictionary(p => p.Field, p => p.ApprovedValue!);
        var current = NoteContent.From(draft);
        if (!append && ((approved.ContainsKey("history") && current.History.Length > 0)
            || (approved.ContainsKey("assessment") && current.Assessment.Length > 0)
            || (approved.ContainsKey("plan") && current.Plan.Length > 0)))
            return ReviewResult.Invalid("The draft already contains text. Compare it below and explicitly allow appending approved values; existing text will be preserved.");
        var merged = Merge(current, approved);
        if (merged.Validate(false) is { Count: > 0 } errors) return new(422, errors);
        merged.ApplyTo(draft);
        draft.HasUnreviewedProposals = false;
        await db.SaveChangesAsync(cancel);
        review.State = AiReviewStates.Completed;
        review.ImportedVersion = draft.Version;
        audit.Record(AuditActions.AiReviewCompleted, draft.PatientId, "ai_note_reviews", review.Id);
        await db.SaveChangesAsync(cancel);
        return new(200, [], review, draft.Version);
    }

    public async Task<ReviewResult> DiscardAsync(Guid id, CancellationToken cancel)
    {
        if (!authority.IsPrimaryHost) return ReviewResult.Offline;
        var (review, draft) = await LockAsync(id, cancel);
        if (review is null || draft is null) return ReviewResult.Missing;
        if (review.State == AiReviewStates.Cancelled) return new(200, [], review, draft.Version);
        if (review.State != AiReviewStates.Recording)
            return ReviewResult.Invalid("Saved proposals must each be reviewed; they cannot be discarded as an unreviewed batch.");
        review.State = AiReviewStates.Cancelled;
        await db.SaveChangesAsync(cancel);
        return new(200, [], review, draft.Version);
    }

    async Task<(AiNoteReview? Review, ClinicalDraft? Draft)> LockAsync(Guid id, CancellationToken cancel)
    {
        var snapshot = await phi.AiReviewAsync(id, cancel);
        if (snapshot is null) return (null, null);
        var draft = await phi.LockDraftAsync(snapshot.DraftId, snapshot.PatientId, cancel);
        return draft is null ? (null, null) : (await phi.AiReviewAsync(id, cancel, tracking: true), draft);
    }

    static void Advance(ClinicalDraft draft)
    {
        var structured = draft.StructuredVersion == draft.Version;
        draft.Version++;
        if (structured) draft.StructuredVersion = draft.Version;
    }

    static NoteContent Merge(NoteContent current, IReadOnlyDictionary<string, string> values)
    {
        string Field(string name, string original) => !values.TryGetValue(name, out var value) ? original
            : original.Length == 0 ? value : original + "\n\n" + value;
        return new(Field("history", current.History), Field("assessment", current.Assessment), Field("plan", current.Plan));
    }
}

// Only constructed from the server's sanitized, owner-checked sidecar result. Never an HTTP input model.
public sealed record TrustedReviewResult(string Json, IReadOnlyDictionary<string, string> Suggestions);

public sealed record ReviewResult(int Status, IReadOnlyList<string> Errors, AiNoteReview? Review = null, long? Version = null)
{
    public static ReviewResult Missing => new(404, ["This review is unavailable. Check your patient panel and draft ownership."]);
    public static ReviewResult Offline => new(503, ["The clinic server must be available to save review decisions. Your displayed work is still here."]);
    public static ReviewResult Signed => new(409, ["This note has been signed and cannot receive AI proposals."]);
    public static ReviewResult Invalid(string error) => new(422, [error]);
    public static ReviewResult Conflict(long version) => new(409,
        ["The draft or review changed elsewhere. Your displayed work is still here. Compare the current draft and reload the saved review before retrying."], Version: version);
}
