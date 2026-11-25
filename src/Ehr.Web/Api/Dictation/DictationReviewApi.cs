using System.Globalization;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Ehr.Data;
using Ehr.Data.Patients;
using Ehr.Domain.Notes;
using Ehr.Domain.Phi;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Ehr.Web.Api.Dictation;

public static partial class DictationApi
{
    // SpokenLanguage (the expansion plan's step 17): "en" or "es" exactly, English when absent; any other is a 400.
    public sealed record NoteDictationRequest(Guid ReviewId, Guid PatientId, Guid? DraftId, long? Version,
        SpokenLanguage SpokenLanguage = SpokenLanguage.English);
    public sealed record StageReviewRequest(long Version);

    static void MapNoteReview(RouteGroupBuilder sessions)
    {
        sessions.MapPost("/for-note", StartForNoteAsync);
        sessions.MapPost("/review/{reviewId:guid}/stage", StageReviewAsync);
        sessions.MapPost("/review/{reviewId:guid}/discard", DiscardReviewAsync);
    }

    static void IdentifyReviewUser(HttpContext context) =>
        context.RequestServices.GetRequiredService<UserContext>().UserId = Guid.Parse(context.User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    static async Task<IResult> StartForNoteAsync(NoteDictationRequest input, HttpContext context, DictationSidecar sidecar,
        EhrDbContext db, PhiReader phi, NoteReviews reviews)
    {
        IdentifyReviewUser(context);
        context.Response.Headers.CacheControl = "no-store";
        var cancel = context.RequestAborted;
        if (input.ReviewId == Guid.Empty || input.PatientId == Guid.Empty || input.DraftId == Guid.Empty)
            return ReviewAnswer(context, ReviewResult.Invalid("Supply a patient and a review request ID."));
        await using (var transaction = await db.Database.BeginTransactionAsync(cancel))
        {
            if (!await phi.IsAssignedAsync(input.PatientId, cancel)) return ReviewAnswer(context, ReviewResult.Missing);
            if (await phi.AiReviewAsync(input.ReviewId, cancel) is { } existing)
            {
                await transaction.CommitAsync(cancel);
                await ReportReviewWalAsync(context, db);
                // A visit's binding (step 16) is never handed to a dictation, as AmbientApi's start never hands one a dictation's.
                return existing.PatientId == input.PatientId && existing.Mode == AiReviewModes.Dictation
                    && (input.DraftId is null || input.DraftId == existing.DraftId)
                    ? BoundSession(context, existing) : ReviewAnswer(context, ReviewResult.Missing);
            }
            if (input.DraftId is { } draftId)
            {
                var draft = await phi.ReadDraftAsync(draftId, input.PatientId, cancel, tracking: false);
                if (draft is null) { await transaction.CommitAsync(cancel); return ReviewAnswer(context, ReviewResult.Missing); }
                if (await phi.SignedDraftAsync(draftId, input.PatientId, cancel) is not null)
                { await transaction.CommitAsync(cancel); return ReviewAnswer(context, ReviewResult.Signed); }
                if (draft.Version != input.Version)
                { await transaction.CommitAsync(cancel); return ReviewAnswer(context, ReviewResult.Conflict(draft.Version)); }
            }
            await transaction.CommitAsync(cancel);
        }

        // Only a newly created sidecar session may be bound. A posted session ID/result is never accepted.
        return await CallAsync(context, sidecar, "start-review", HttpMethod.Post, "v1/sessions", Start(input.SpokenLanguage),
            async (body, token) =>
            {
                var session = await body.ReadFromJsonAsync<DictationSession>(ServiceJson, token) ?? throw new JsonException();
                if (!SessionId().IsMatch(session.SessionId)) return BadAnswer();
                return await ReviewWriteAsync(async () =>
                {
                    await using var transaction = await db.Database.BeginTransactionAsync(token);
                    var result = await reviews.BeginAsync(input.ReviewId, input.PatientId, input.DraftId, input.Version,
                        session.SessionId, session.MaxSeconds, Math.Min(session.MaxChunkBytes, MaxChunkBytes), token);
                    await transaction.CommitAsync(CancellationToken.None);
                    await ReportReviewWalAsync(context, db);
                    return result.Review is { } review ? BoundSession(context, review) : ReviewAnswer(context, result);
                });
            });
    }

    static async Task<IResult> StageReviewAsync(Guid reviewId, StageReviewRequest input, HttpContext context,
        DictationSidecar sidecar, EhrDbContext db, PhiReader phi, NoteReviews reviews)
    {
        IdentifyReviewUser(context);
        context.Response.Headers.CacheControl = "no-store";
        AiNoteReview? binding;
        await using (var transaction = await db.Database.BeginTransactionAsync(context.RequestAborted))
        {
            binding = await phi.AiReviewAsync(reviewId, context.RequestAborted);
            await transaction.CommitAsync(context.RequestAborted);
        }
        if (binding is null) return ReviewAnswer(context, ReviewResult.Missing);
        if (binding.State is AiReviewStates.Pending or AiReviewStates.Completed)
        {
            await ReportReviewWalAsync(context, db);
            return ReviewAnswer(context, new(200, [], binding));
        }
        if (binding.State != AiReviewStates.Recording) return ReviewAnswer(context, ReviewResult.Invalid("This dictation was discarded."));

        // The session is read from the persisted binding, and the owner header from the current account. A recorded visit's
        // (step 16) is asked for as one, and discarded at the service once staged: its result isn't needed there any more.
        var ambient = binding.Mode == AiReviewModes.Ambient;
        return await CallAsync(context, sidecar, "stage-review", HttpMethod.Post,
            $"v1/sessions/{binding.SessionId}/result?wait={ResultWaitSeconds}", null, async (body, token) =>
            {
                var result = await body.ReadFromJsonAsync<DictationResult>(ServiceJson, token) ?? throw new JsonException();
                // A visit whose voices were never named can't be drafted from here: only from its Listen page, before it
                // was left.
                if (result.Sorting is Sorting.AwaitingSpeakers)
                    return Refuse(409, "speakers_not_named", "This visit's voices were never named, so it can't be drafted. Discard it to dictate or listen again.");
                if (result.Sorting is Sorting.Pending)
                    return Refuse(409, "not_ready", "The dictation result is still being prepared. Try again shortly.");
                var trusted = TrustedResult(result, ambient);
                if (trusted is null) return Refuse(502, "invalid_result", "This result could not be staged safely. Keep the transcript and continue manually, or retry dictation.");
                var staged = false;
                var answer = await ReviewWriteAsync(async () =>
                {
                    await using var transaction = await db.Database.BeginTransactionAsync(token);
                    var outcome = await reviews.StageAsync(reviewId, input.Version, trusted, token);
                    await transaction.CommitAsync(CancellationToken.None);
                    staged = outcome.Status == StatusCodes.Status200OK;
                    await ReportReviewWalAsync(context, db);
                    return ReviewAnswer(context, outcome);
                });
                if (staged && ambient)
                {
                    await DeleteServiceSessionAsync(context, sidecar, binding.SessionId, ambient: true);
                }

                return answer;
            }, ambient);
    }

    static async Task<IResult> DiscardReviewAsync(Guid reviewId, HttpContext context, EhrDbContext db, NoteReviews reviews)
    {
        IdentifyReviewUser(context);
        context.Response.Headers.CacheControl = "no-store";
        return await ReviewWriteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(context.RequestAborted);
            var result = await reviews.DiscardAsync(reviewId, context.RequestAborted);
            await transaction.CommitAsync(CancellationToken.None);
            await ReportReviewWalAsync(context, db);
            return ReviewAnswer(context, result);
        });
    }

    static async Task ReportReviewWalAsync(HttpContext context, EhrDbContext db) =>
        context.Response.Headers[DatabaseTransactionFilter.WalPositionHeader] =
            await WalPosition.OfPrimaryAsync(db, CancellationToken.None);

    static IResult BoundSession(HttpContext context, AiNoteReview review) => Results.Json(new
    {
        review.SessionId,
        review.MaxSeconds,
        review.MaxChunkBytes,
        ReviewId = review.Id,
        review.DraftId,
        Version = review.StartedVersion,
        review.State,
        url = ReviewUrl(review),
        editorUrl = EditorUrl(review),
    }, statusCode: 201);

    static IResult ReviewAnswer(HttpContext context, ReviewResult result) => Results.Json(new
    {
        result.Errors,
        result.Version,
        url = result.Review is { } review ? ReviewUrl(review) : null,
        editorUrl = result.Review is { } saved ? EditorUrl(saved) : null,
    }, statusCode: result.Status);

    static string ReviewUrl(AiNoteReview review) => $"/patients/{review.PatientId}/notes/drafts/{review.DraftId}/review/{review.Id}";
    static string EditorUrl(AiNoteReview review) => $"/patients/{review.PatientId}/notes/drafts/{review.DraftId}";

    static async Task<IResult> ReviewWriteAsync(Func<Task<IResult>> operation)
    {
        try { return await operation(); }
        catch (DbUpdateConcurrencyException) { return Refuse(409, "review_conflict", "This draft or review changed. Reopen the saved review and compare before retrying."); }
        catch (DbUpdateException error) when (error.InnerException is PostgresException
        { SqlState: PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.CheckViolation })
        {
            return Refuse(409, "review_conflict", "This draft or review changed or was signed. Your displayed work is still here; compare the saved draft before retrying.");
        }
    }

    // Keep exactly UC 9/10's sanitized contract and half-open UTF-16 ranges. Grounding/clinical checks remain in
    // the trusted sidecar pipeline; malformed spans/results are never promoted into reviewable records here.
    // A recorded visit's (step 16, `ambient`) is in English and proposes nothing beyond its note, says who said every
    // passage and source, marks a Subjective value as the patient's own words, and is stored with its transcript's text
    // apart (TrustedReviewResult.Transcript); a dictation's, in either language, says nothing of speakers.
    internal static TrustedReviewResult? TrustedResult(DictationResult result, bool ambient = false)
    {
        var text = result.Transcript.Text;
        if (text.Length > 100_000 || text.Contains('\0') || result.Sorting == Sorting.Done
            && (!result.Transcript.Final || result.Fields is null || result.Transcript.Segments.Any(s => !s.Final))) return null;
        bool Range(int start, int end) => start >= 0 && end > start && end <= text.Length
            && !(start > 0 && char.IsLowSurrogate(text[start]) && char.IsHighSurrogate(text[start - 1]))
            && !(end < text.Length && char.IsLowSurrogate(text[end]) && char.IsHighSurrogate(text[end - 1]));
        if (result.Transcript.Segments.Any(s => !Range(s.Start, s.End))
            || result.NotCarriedOver.Any(s => !Range(s.Start, s.End))) return null;
        // What a dictation proposes (the expansion plan's step 15) is one of its listed instructions, of the same kind, each
        // once, at most AiChartProposal.MaxPerReview, from a final transcript, with a proposal's warnings only, and what
        // was heard after it (step 15.1) after it in the transcript, with no speaker.
        var proposals = result.Proposals ?? [];
        // Nothing is proposed from a dictation in Spanish (the expansion plan's step 17): its instructions are listed for the
        // clinician to place, and a result from Spanish that proposes any is refused whole.
        if (result.SpokenLanguage == SpokenLanguage.Spanish && proposals.Count > 0) return null;
        if (proposals.Count > AiChartProposal.MaxPerReview || proposals.Count > 0 && !result.Transcript.Final
            || proposals.DistinctBy(p => (p.Start, p.End)).Count() != proposals.Count
            || proposals.Any(p => !result.NotCarriedOver.Any(i => i.Start == p.Start && i.End == p.End
                    && i.Reason == (p.Kind == ProposalKind.Order ? Instruction.Order : Instruction.AllergyChange))
                || p.Warnings.Any(w => w.Code == WarningCode.OneFieldHoldsDictation || !Range(w.Start, w.End))
                || p.HeardAfter is { } after && (!Range(after.Start, after.End) || after.Start < p.End || after.Speaker is not null))) return null;
        var spoken = result.Transcript.Segments.Any(s => s.Speaker is not null || s.SpeakerGroup is not null) || result.Speakers is not null
            || result.NotCarriedOver.Any(i => i.Speaker is not null)
            || result.Fields is { } said && new[] { said.Subjective, said.Objective, said.Assessment, said.Plan }
                .OfType<PendingField>().Any(f => f.Sources.Any(s => s.Speaker is not null));
        // A visit's says who said every passage, whether or not its fields could be sorted: its review shows it a turn at a
        // time, and a passage with no one's name would read as the turn before's. Each instruction it lists was said by the
        // clinician, or in a passage whose speaker couldn't be told: never the patient's or someone else's.
        if (ambient
            ? proposals.Count > 0 || result.SpokenLanguage != SpokenLanguage.English || result.Transcript.Segments.Any(s => s.Speaker is null)
                || result.NotCarriedOver.Any(i => i.Speaker is not (Speaker.Clinician or Speaker.Unsure))
                || !Bare(result)
            : spoken) return null;
        // A live result has the note's four fields only (the expansion plan's step 14). A History is the first of the three
        // fields before then, which only a review stored then holds, read from the database and never through here.
        if (result.Fields?.History is not null) return null;
        // A source never starts or ends inside a letter and its combining marks ("año" written as n and U+0303, cut after the
        // n, would show "an"): the surrogate rule above guards only a character in two halves (the expansion plan's step 17).
        bool Joined(int at) => at > 0 && at < text.Length && Rune.TryGetRuneAt(text, at, out var rune)
            && Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;
        bool Source(SourceSpan source) => Range(source.Start, source.End) && !Joined(source.Start) && !Joined(source.End);
        // A passage speech recognition didn't hear as Spanish was left out of what the model sorts, as the review says: no
        // source may cite it (step 17). The web checks no other kind of unclear passage here; the dictation service does.
        var notSpanish = result.Transcript.Segments.Where(s => s.Unclear == Unclear.OtherLanguage).ToList();
        bool InPassageNotSpanish(SourceSpan source) => notSpanish.Any(s => source.Start < s.End && s.Start < source.End);
        // A value proposed from Spanish (step 17) is English sentences, each with the Spanish words it was translated from.
        // The sentences follow each other through the value with exactly one space between them, none at either end and
        // none doubled; each cites, in order and without overlap, sources of its own that are whole (Source), at most
        // TranslatedStatement.MaxSpanishLength of the transcript in all; and between them they cite every source the field
        // has and no other. So the review can put each sentence beside what the clinician said, and a sentence can't cite
        // more Spanish than the schema lets it quote. A value proposed from English has none.
        bool Translated(PendingField field)
        {
            if (field.Statements is not { } statements) return false;
            var at = 0;
            foreach (var statement in statements)
            {
                if (statement.Start != at || statement.End <= statement.Start || statement.End > field.Value.Length
                    || char.IsWhiteSpace(field.Value[statement.Start]) || char.IsWhiteSpace(field.Value[statement.End - 1])
                    || statement.End < field.Value.Length && field.Value[statement.End] != ' '
                    || statement.Sources.Count == 0
                    || statement.Sources.Zip(statement.Sources.Skip(1)).Any(pair => pair.Second.Start < pair.First.End)
                    || !statement.Sources.All(Source)
                    || statement.Sources.Sum(s => s.End - s.Start) > TranslatedStatement.MaxSpanishLength) return false;
                at = statement.End + 1;
            }

            return at == field.Value.Length + 1 && statements.SelectMany(s => s.Sources).ToHashSet().SetEquals(field.Sources);
        }

        var suggestions = new Dictionary<string, string>();
        if (result.Fields is { } fields)
        {
            foreach (var (name, field) in new[] { ("subjective", fields.Subjective), ("objective", fields.Objective), ("assessment", fields.Assessment), ("plan", fields.Plan) })
            {
                if (field is not PendingField pending) continue;
                if (result.Sorting != Sorting.Done || string.IsNullOrWhiteSpace(pending.Value) || pending.Value.Contains('\0')
                    || pending.Value.Length > NoteContent.MaximumFieldLength || pending.Sources.Count == 0
                    || !pending.Sources.All(Source) || pending.Sources.Any(InPassageNotSpanish)
                    || ambient && pending.Sources.Any(s => s.Speaker is null)
                    || pending.Warnings.Any(w => !Range(w.Start, w.End))
                    || (result.SpokenLanguage == SpokenLanguage.Spanish ? !Translated(pending) : pending.Statements is not null)
                    // A visit's Subjective is the patient's own words, and says so (UC 23).
                    || ambient && name == "subjective" && !pending.Warnings.Any(w => w.Code == WarningCode.PatientsOwnWords)) return null;
                suggestions.Add(name, pending.Value);
            }
        }
        var kinds = proposals.Select(p => p.Kind == ProposalKind.Order ? AiChartProposalKinds.Order : AiChartProposalKinds.AllergyAddition).ToList();
        if (ambient)
        {
            // The visit's words go apart, with their segments, and only while the review is open; the review keeps its
            // proposals, each with where its words were and who said them. Not the segments again: once the transcript is
            // gone they point at nothing, and while it is kept, reading them twice cost the review page its render time.
            var transcript = text.Length == 0 ? null : JsonSerializer.Serialize(result.Transcript, ServiceJson);
            var kept = JsonSerializer.Serialize(result with { Transcript = result.Transcript with { Text = "", Segments = [] }, Speakers = null }, ServiceJson);
            return System.Text.Encoding.UTF8.GetByteCount(kept) > 1_048_576 || transcript is not null && System.Text.Encoding.UTF8.GetByteCount(transcript) > 900_000
                ? null : new(kept, suggestions, kinds, transcript);
        }

        var json = JsonSerializer.Serialize(result, ServiceJson);
        return System.Text.Encoding.UTF8.GetByteCount(json) > 1_048_576 ? null : new(json, suggestions, kinds);
    }

    // What the database holds a visit's stored result to besides (ehr.ambient_result_is_bare), here first, so a result
    // it would refuse isn't staged: an empty field's reasons are the service's codes, each once at most, and provenance's
    // names are short and code-shaped.
    static bool Bare(DictationResult result)
    {
        static bool Code(string? value) => value is null
            || value.Length is > 0 and <= 128 && value.All(c => char.IsAsciiLetterOrDigit(c) || "._:/+-".Contains(c));
        var empty = result.Fields is { } fields
            ? new[] { fields.Subjective, fields.Objective, fields.Assessment, fields.Plan }.OfType<EmptyField>()
            : [];
        var p = result.Provenance;
        return empty.All(e => e.Reasons.Count <= EmptyField.Codes.Length && e.Reasons.Distinct().Count() == e.Reasons.Count
                && e.Reasons.All(EmptyField.Codes.Contains))
            && new[] { p.Speech, p.Language is { } l ? new SpeechModel(l.Repository, l.Commit, l.Sha256) : null, p.Speaker }
                .All(m => m is null || Code(m.Repository) && Code(m.Commit) && Code(m.Sha256))
            && Code(p.Language?.File) && Code(p.LlamaServer) && Code(p.Prompt) && Code(p.Schema) && Code(p.Structuring) && Code(p.Code);
    }
}
