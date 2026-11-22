using Ehr.Data;
using Ehr.Data.Audit;
using Ehr.Data.Patients;
using Ehr.Domain.Audit;
using Ehr.Domain.Phi;
using Microsoft.EntityFrameworkCore;

namespace Ehr.Domain.Notes;

// Call within the primary transaction. Every mutation shares UC 14's draft row lock and optimistic version.
public sealed class NoteReviews(EhrDbContext db, PhiReader phi, ClinicalNotes notes, UserContext user,
    AuditRecorder audit, NoteWriteAuthority authority)
{
    // With `ambient`, the review of a recorded visit's conversation (step 16): its consent is written first, in the same
    // transaction, and the database holds the two together (db/policies/56-ambient-listening.sql).
    public async Task<ReviewResult> BeginAsync(Guid id, Guid patient, Guid? draftId, long? version,
        string sessionId, int maxSeconds, int maxChunkBytes, CancellationToken cancel, AmbientStart? ambient = null)
    {
        if (!authority.IsPrimaryHost) return ReviewResult.Offline;
        if (id == Guid.Empty) return ReviewResult.Invalid("Supply a review request ID.");
        if (await phi.AiReviewAsync(id, cancel) is { } receipt)
            return receipt.PatientId == patient && (draftId is null || receipt.DraftId == draftId)
                && receipt.Mode == (ambient is null ? AiReviewModes.Dictation : AiReviewModes.Ambient)
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
        if (ambient is not null)
        {
            var consent = new AmbientConsent
            {
                Id = Guid.CreateVersion7(),
                PatientId = patient,
                ClinicianId = user.UserId!.Value,
                Speakers = ambient.Speakers,
                GivenBy = ambient.GivenBy,
            };
            db.AddRange(consent);
            review.Mode = AiReviewModes.Ambient;
            review.ConsentId = consent.Id;
            review.Sensitive = true;
        }

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
        // A recorded visit's (step 16) is pending with its transcript, kept apart and only while the review is open, even
        // with nothing proposed, so the clinician can read it; it goes when they finish (CompleteAsync).
        var open = result.Suggestions.Count > 0 || result.Transcript is not null;
        review.State = open ? AiReviewStates.Pending : AiReviewStates.Completed;
        review.ImportedVersion = open ? null : draft.Version;
        await db.SaveChangesAsync(cancel);
        if (result.Transcript is { } transcript)
        {
            db.AddRange(new AmbientTranscript { Id = review.Id, PatientId = review.PatientId, Transcript = transcript });
        }

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
        // The orders and allergies to add it proposes (the expansion plan's step 15), each at its place in the stored result,
        // to be decided through the order and allergy forms or dismissed. They hold up neither this review nor signing.
        for (var place = 0; place < result.ChartProposals.Count; place++)
        {
            db.AddRange(new AiChartProposal
            {
                Id = Guid.CreateVersion7(),
                ReviewId = review.Id,
                PatientId = review.PatientId,
                Ordinal = (short)place,
                Kind = result.ChartProposals[place],
                State = AiChartProposalStates.Pending,
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
        // Step 16: with a recorded visit's transcript gone, nobody can check a proposal against what was said (the
        // database refuses the same, db/policies/56-ambient-listening.sql).
        if (decision == AiProposalStates.Accepted && review.Mode == AiReviewModes.Ambient && !await phi.AmbientTranscriptKeptAsync(review.Id, cancel))
            return ReviewResult.Invalid(TranscriptGone);
        var values = proposals.Where(p => p.ApprovedValue is not null).ToDictionary(p => p.Field, p => p.ApprovedValue!);
        if (approved is not null) values[proposal.Field] = approved;
        if (Merge(NoteContent.From(draft), values).Validate(false, NoteContent.OriginOf(draft)) is { Count: > 0 } errors)
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
        if (!append && approved.Keys.Any(field => current.Sections[Section(field)].Text.Length > 0))
            return ReviewResult.Invalid("The draft already contains text. Compare it below and explicitly allow appending approved values; existing text will be preserved.");
        var merged = Merge(current, approved);
        if (merged.Validate(false, NoteContent.OriginOf(draft)) is { Count: > 0 } errors) return new(422, errors);
        merged.ApplyTo(draft);
        draft.HasUnreviewedProposals = false;
        await db.SaveChangesAsync(cancel);
        await FinishAsync(review, draft, cancel);
        return new(200, [], review, draft.Version);
    }

    // A recorded visit's review (step 16), for a patient who withdraws their consent after the visit: every proposal
    // still waiting is rejected, nothing is added to the note, and the review is finished, so its transcript goes.
    public async Task<ReviewResult> RejectAllAndFinishAsync(Guid id, long version, CancellationToken cancel)
    {
        if (!authority.IsPrimaryHost) return ReviewResult.Offline;
        var (review, draft) = await LockAsync(id, cancel);
        if (review is null || draft is null) return ReviewResult.Missing;
        if (review.Mode != AiReviewModes.Ambient) return ReviewResult.Invalid("Only a recorded visit's review is finished this way.");
        if (review.State == AiReviewStates.Completed) return new(200, [], review, draft.Version);
        if (review.State != AiReviewStates.Pending) return ReviewResult.Invalid("Retrieve the visit's draft before finishing its review.");
        if (draft.Version != version) return ReviewResult.Conflict(draft.Version);
        if (await phi.SignedDraftAsync(draft.Id, draft.PatientId, cancel) is not null) return ReviewResult.Signed;
        var waiting = (await phi.AiProposalsAsync(id, cancel, tracking: true)).Where(p => p.State == AiProposalStates.Pending).ToList();
        foreach (var proposal in waiting)
        {
            proposal.State = AiProposalStates.Rejected;
            proposal.DecisionKey = Guid.NewGuid();
            audit.Record(AuditActions.AiFieldReviewed, draft.PatientId, "ai_field_proposals", proposal.Id);
        }

        draft.HasUnreviewedProposals = false;
        Advance(draft);
        await db.SaveChangesAsync(cancel);
        await FinishAsync(review, draft, cancel);
        return new(200, [], review, draft.Version);
    }

    public const string TranscriptGone =
        "This visit's transcript was deleted after 7 days, so its proposals can't be accepted as offered: reject each, or edit it into your own words.";

    // The review is complete at the draft's version; a recorded visit's transcript goes with it, in the same transaction
    // (the database refuses a finished review that still has one).
    async Task FinishAsync(AiNoteReview review, ClinicalDraft draft, CancellationToken cancel)
    {
        review.State = AiReviewStates.Completed;
        review.ImportedVersion = draft.Version;
        audit.Record(AuditActions.AiReviewCompleted, draft.PatientId, "ai_note_reviews", review.Id);
        await db.SaveChangesAsync(cancel);
        if (review.Mode == AiReviewModes.Ambient)
        {
            await db.AmbientTranscripts.Where(t => t.Id == review.Id).ExecuteDeleteAsync(cancel);
        }
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

    // Dismisses one of the orders or allergies to add the dictation proposes (the expansion plan's step 15): decided once,
    // by the review's clinician, with nothing written to the chart. It needs no draft lock and no unsigned draft: a
    // proposal waits for its decision whatever becomes of the note. Dismissing one already dismissed changes nothing.
    public async Task<ReviewResult> DismissAsync(Guid id, Guid proposalId, CancellationToken cancel)
    {
        if (!authority.IsPrimaryHost) return ReviewResult.Offline;
        if (await phi.AiReviewAsync(id, cancel) is not { } review) return ReviewResult.Missing;
        var proposal = (await phi.ChartProposalsAsync(id, AuditParts.NoteDraft, cancel, tracking: true)).SingleOrDefault(p => p.Id == proposalId);
        if (proposal is null) return ReviewResult.Missing;
        if (proposal.State != AiChartProposalStates.Pending)
            return proposal.State == AiChartProposalStates.Dismissed ? new(200, [], review) : ReviewResult.Decided;
        proposal.State = AiChartProposalStates.Dismissed;
        try
        {
            await db.SaveChangesAsync(cancel);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Decided in another tab since it was read. Not tracked any more, so the page's reads after this save nothing.
            db.Entry(proposal).State = EntityState.Detached;
            return ReviewResult.Decided;
        }
        return new(200, [], review);
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

    // Which of the note's sections (NoteContent.Sections) a proposed field goes into. A dictation sorted before the
    // expansion plan's step 14, whose review may still be waiting, proposes a History, which goes into Subjective, where
    // the editor asks for what belongs in Objective to be moved there (the same as an earlier draft's History).
    static int Section(string field) => field switch
    {
        "history" or "subjective" => 0,
        "objective" => 1,
        "assessment" => 2,
        "plan" => 3,
        _ => throw new InvalidOperationException("A proposal names a field the note doesn't have."),
    };

    static NoteContent Merge(NoteContent current, IReadOnlyDictionary<string, string> values)
    {
        var texts = current.Sections.Select(section => section.Text).ToArray();
        foreach (var (field, value) in values)
        {
            var section = Section(field);
            texts[section] = texts[section].Length == 0 ? value : texts[section] + "\n\n" + value;
        }

        return new(texts[0], texts[1], texts[2], texts[3]);
    }
}

// What a clinician confirmed before listening to a visit: how many people would speak (2 or 3) and who agreed for the
// patient (AmbientConsentGivers). Checked by the API before the consent is written, and again by the database.
public sealed record AmbientStart(short Speakers, string GivenBy);

// Only constructed from the server's sanitized, owner-checked sidecar result. Never an HTTP input model. ChartProposals are
// the kinds of the result's proposals, in its order (AiChartProposalKinds).
// A recorded visit's (step 16) also carries its Transcript, kept apart (AmbientTranscript); its Json then has none.
public sealed record TrustedReviewResult(string Json, IReadOnlyDictionary<string, string> Suggestions, IReadOnlyList<string> ChartProposals,
    string? Transcript = null);

public sealed record ReviewResult(int Status, IReadOnlyList<string> Errors, AiNoteReview? Review = null, long? Version = null)
{
    public static ReviewResult Missing => new(404, ["This review is unavailable. Check your patient panel and draft ownership."]);
    public static ReviewResult Offline => new(503, ["The clinic server must be available to save review decisions. Your displayed work is still here."]);
    public static ReviewResult Signed => new(409, ["This note has been signed and cannot receive AI proposals."]);
    public static ReviewResult Decided => new(409, ["Not saved: this proposal was already decided, in another tab or window. Nothing was changed."]);
    public static ReviewResult Invalid(string error) => new(422, [error]);
    public static ReviewResult Conflict(long version) => new(409,
        ["The draft or review changed elsewhere. Your displayed work is still here. Compare the current draft and reload the saved review before retrying."], Version: version);
}
