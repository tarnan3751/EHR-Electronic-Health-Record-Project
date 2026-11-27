using System.Globalization;
using System.Text.Json;
using Ehr.Data;
using Ehr.Data.Patients;
using Ehr.Domain.Notes;
using Ehr.Domain.Phi;
using Microsoft.AspNetCore.Mvc;

namespace Ehr.Web.Api.Dictation;

// Listening to a visit (ambient listening, the expansion plan's step 16; UC 23, proposed FR-62), for the Listen page
// (Areas/Clinical/Pages/Patients/Listen) to call from the browser. The same group as the rest of this API, so the same
// Clinician policy, sign-in check and form token; the on-prem app only, and never forwarded by the cloud app
// (Forwarding/CloudForwardingMiddleware): a visit is recorded at the clinic alone.
//
// POST /api/dictation/sessions/ambient/start {reviewId, patientId, draftId?, version?, speakers, givenBy, agreed,
// spokenLanguage?}: the clinician confirms that everyone who will speak (speakers: 2 or 3) agreed aloud (agreed: true),
// and who agreed for the patient (givenBy: "patient" or "representative"). The service's session is created first; then,
// in one transaction, the consent (ehr.ambient_consents, which the write audit records) and the review bound to it, with
// a new draft unless draftId names one. 201 with the binding, as the dictation's start answers. Nothing about the
// microphone happens before this answer: the page opens it only then. A retry with the same reviewId gets the saved
// binding. Refused with 422: no agreement, speakers other than 2 or 3, an unknown givenBy, or a spoken language other
// than English ("en"), which is all listening works in; and with 409 sign_in_ending, before anything else, when the
// sign-in's 12-hour end comes before a 20-minute visit could finish (DictationApi.cs, step 18).
//
// Then, for the session that start named, each call checked against its binding first (RequireOpenCaptureAsync): the
// clinician's own, of a visit, with its consent, and still recording, neither staged nor ended; otherwise 409
// not_listening, and nothing reaches the service.
//   POST /ambient/{id}/audio?seq=N   a chunk, as the dictation's audio route; the answer also has quietSeconds, how long
//                                    nobody has spoken, and no transcript until the recording stops
//   POST /ambient/{id}/stop          the result, as stop's: its sorting is "awaiting_speakers" once the visit is
//                                    transcribed and its voices told apart, with speakers: [{group, seconds}] and each
//                                    segment's speakerGroup
//   POST /ambient/{id}/speakers      {speakers: {"1": "clinician", "2": "patient", "3": "unsure"}}: who each voice is,
//                                    every one of them, numbered from 1 with none left out (at most MaxVoices): one
//                                    "clinician", one "patient", "other" at most once, and "unsure" ("I can't tell")
//                                    for any number more, as a voice told apart may be a fragment of someone's; and no
//                                    more named, all but "unsure", than the consent said would speak. Then the note is
//                                    drafted, and the result follows as result's, an "unsure" voice's segments with
//                                    speaker "unsure". 422 speakers_incomplete otherwise
//   POST /ambient/{id}/result        the result, as the dictation's result route
// DELETE /ambient/{id}: 204, and the service discards the session at once, whatever its state; the page also discards the
// review (/review/{reviewId}/discard). Staging, deciding and finishing are the review's routes (DictationReviewApi). A
// start that the service has begun but whose consent and binding aren't saved, refused or failed, discards the service's
// session there and then.
//
// Every call to the service for these sessions carries DictationSidecar.ModeHeader: the service answers a dictation's
// call for a visit's session, or the reverse, as it would for no session at all.
public static partial class DictationApi
{
    public const string AmbientPath = Path + "/ambient";

    public sealed record AmbientStartRequest(
        Guid ReviewId, Guid PatientId, Guid? DraftId, long? Version, int Speakers, string? GivenBy, bool Agreed, string? SpokenLanguage = null);

    public sealed record SpeakersRequest(IReadOnlyDictionary<string, string>? Speakers);

    static readonly string[] Roles = ["clinician", "patient", "other", "unsure"];

    // The most voices a visit's mapping names: three people and a few fragments of them.
    public const int MaxVoices = 9;

    static void MapAmbient(RouteGroupBuilder sessions)
    {
        var ambient = sessions.MapGroup("/ambient");
        ambient.MapPost("/start", StartAmbientAsync);
        ambient.MapPost("/{id}/audio", (string id, string? seq, HttpContext context, DictationSidecar sidecar) =>
                ForwardAudioAsync(id, seq, context, sidecar, ambient: true))
            .AddEndpointFilter(RequireOpenCaptureAsync).WithMetadata(new RequestSizeLimitAttribute(MaxChunkBytes));
        ambient.MapPost("/{id}/stop", (string id, HttpContext context, DictationSidecar sidecar) =>
                CallAsync<DictationResult>(context, sidecar, "stop-ambient", HttpMethod.Post, $"v1/sessions/{id}/stop", null,
                    result => Results.Ok(result), ambient: true))
            .AddEndpointFilter(RequireOpenCaptureAsync);
        ambient.MapPost("/{id}/speakers", SpeakersAsync).AddEndpointFilter(RequireOpenCaptureAsync);
        ambient.MapPost("/{id}/result", (string id, HttpContext context, DictationSidecar sidecar) =>
                CallAsync<DictationResult>(context, sidecar, "result-ambient", HttpMethod.Post,
                    $"v1/sessions/{id}/result?wait={ResultWaitSeconds}", null, result => Results.Ok(result), ambient: true))
            .AddEndpointFilter(RequireOpenCaptureAsync);
        ambient.MapDelete("/{id}", (string id, HttpContext context, DictationSidecar sidecar) => SessionId().IsMatch(id)
            ? DeleteAmbientSessionAsync(id, context, sidecar)
            : Task.FromResult(NotFound()));
    }

    static async Task<IResult> StartAmbientAsync(AmbientStartRequest input, HttpContext context, DictationSidecar sidecar,
        EhrDbContext db, PhiReader phi, NoteReviews reviews)
    {
        IdentifyReviewUser(context);
        context.Response.Headers.CacheControl = "no-store";
        if (await SignInEndsTooSoonAsync(context, ambient: true) is { } tooSoon) return tooSoon;
        var cancel = context.RequestAborted;
        if (!input.Agreed)
            return ReviewAnswer(ReviewResult.Invalid("Confirm that everyone who will speak has agreed, aloud, to the visit being recorded."));
        if (input.Speakers is not (2 or 3) || input.GivenBy is not (AmbientConsentGivers.Patient or AmbientConsentGivers.Representative))
            return ReviewAnswer(ReviewResult.Invalid("Say who will speak, and who agreed for the patient."));
        if (input.SpokenLanguage is { } language && !string.Equals(language, "en", StringComparison.OrdinalIgnoreCase))
            return ReviewAnswer(ReviewResult.Invalid("Listening to a visit works in English only."));
        if (input.ReviewId == Guid.Empty || input.PatientId == Guid.Empty || input.DraftId == Guid.Empty)
            return ReviewAnswer(ReviewResult.Invalid("Supply a patient and a review request ID."));

        await using (var transaction = await db.Database.BeginTransactionAsync(cancel))
        {
            if (!await phi.IsAssignedAsync(input.PatientId, cancel)) return ReviewAnswer(ReviewResult.Missing);
            if (await phi.AiReviewAsync(input.ReviewId, cancel) is { } existing)
            {
                await transaction.CommitAsync(cancel);
                await ReportReviewWalAsync(context, db);
                return existing.PatientId == input.PatientId && existing.Mode == AiReviewModes.Ambient
                    && (input.DraftId is null || input.DraftId == existing.DraftId)
                    ? BoundSession(existing) : ReviewAnswer(ReviewResult.Missing);
            }
            if (input.DraftId is { } draftId)
            {
                var draft = await phi.ReadDraftAsync(draftId, input.PatientId, cancel, tracking: false);
                if (draft is null) { await transaction.CommitAsync(cancel); return ReviewAnswer(ReviewResult.Missing); }
                if (await phi.SignedDraftAsync(draftId, input.PatientId, cancel) is not null)
                { await transaction.CommitAsync(cancel); return ReviewAnswer(ReviewResult.Signed); }
                if (draft.Version != input.Version)
                { await transaction.CommitAsync(cancel); return ReviewAnswer(ReviewResult.Conflict(draft.Version)); }
            }
            await transaction.CommitAsync(cancel);
        }

        // The service's session first. Nobody has its ID until the consent and the binding are saved below, so no audio can
        // reach it before then; if they aren't saved, it is discarded.
        return await CallAsync(context, sidecar, "start-ambient", HttpMethod.Post,
            $"v1/sessions?mode=ambient&speakers={input.Speakers.ToString(CultureInfo.InvariantCulture)}", null,
            async (body, token) =>
            {
                var session = await body.ReadFromJsonAsync<DictationSession>(ServiceJson, token) ?? throw new JsonException();
                if (!SessionId().IsMatch(session.SessionId)) return BadAnswer();
                if (session.Mode != AiReviewModes.Ambient)
                {
                    // A service too old to listen to a visit started a dictation instead: it is discarded as one.
                    await DeleteServiceSessionAsync(context, sidecar, session.SessionId, ambient: false);
                    return Refuse(StatusCodes.Status503ServiceUnavailable, "unavailable",
                        "Listening isn't available. The dictation service doesn't support it yet.");
                }

                // Whatever stops the consent and the binding from being saved, a refusal or an exception (the panel ending
                // meanwhile, a timeout), the service's session goes.
                var begun = false;
                try
                {
                    return await ReviewWriteAsync(async () =>
                    {
                        await using var transaction = await db.Database.BeginTransactionAsync(token);
                        var result = await reviews.BeginAsync(input.ReviewId, input.PatientId, input.DraftId, input.Version,
                            session.SessionId, session.MaxSeconds, Math.Min(session.MaxChunkBytes, MaxChunkBytes), token,
                            new AmbientStart((short)input.Speakers, input.GivenBy));
                        await transaction.CommitAsync(CancellationToken.None);
                        begun = result.Status == StatusCodes.Status201Created;
                        await ReportReviewWalAsync(context, db);
                        return result.Review is { } review ? BoundSession(review) : ReviewAnswer(result);
                    });
                }
                finally
                {
                    if (!begun)
                    {
                        await DeleteServiceSessionAsync(context, sidecar, session.SessionId, ambient: true);
                    }
                }
            }, ambient: true);
    }

    // Who each voice is: a role for every voice the clinician was shown, numbered 1 to however many with none left out,
    // one clinician and one patient among them, someone else at most once, and "unsure" for any more; and no more named,
    // all but "unsure", than the consent said would speak (RequireOpenCaptureAsync read it). The service also refuses a
    // mapping that leaves out a voice it told apart.
    static Task<IResult> SpeakersAsync(string id, SpeakersRequest input, HttpContext context, DictationSidecar sidecar)
    {
        var speakers = input.Speakers ?? new Dictionary<string, string>();
        var roles = speakers.Values.ToList();
        var complete = speakers.Count is >= 2 and <= MaxVoices
            && Enumerable.Range(1, speakers.Count).All(group => speakers.ContainsKey(group.ToString(CultureInfo.InvariantCulture)))
            && roles.All(role => Roles.Contains(role))
            && roles.Count(role => role == "clinician") == 1 && roles.Count(role => role == "patient") == 1
            && roles.Count(role => role == "other") <= 1
            && context.Items[ConsentedSpeakers] is short consented && roles.Count(role => role != "unsure") <= consented;
        if (!complete)
        {
            return Task.FromResult(SpeakersIncomplete());
        }

        var content = JsonContent.Create(new { speakers }, options: ServiceJson);
        return CallAsync<DictationResult>(context, sidecar, "speakers-ambient", HttpMethod.Post, $"v1/sessions/{id}/speakers", content,
            result => Results.Ok(result), ambient: true);
    }

    static IResult SpeakersIncomplete() =>
        Refuse(StatusCodes.Status422UnprocessableEntity, "speakers_incomplete",
            "Say who each speaker is: every voice needs a role, with one clinician and one patient.");

    static Task<IResult> DeleteAmbientSessionAsync(string id, HttpContext context, DictationSidecar sidecar) =>
        CallAsync(context, sidecar, "cancel-ambient", HttpMethod.Delete, $"v1/sessions/{id}", null,
            (_, _) => Task.FromResult(Results.NoContent()), ambient: true);

    // Discards a session at the service, whatever comes back: one that isn't there any more has nothing left to discard.
    static Task DeleteServiceSessionAsync(HttpContext context, DictationSidecar sidecar, string sessionId, bool ambient) =>
        CallAsync(context, sidecar, ambient ? "cancel-ambient" : "cancel", HttpMethod.Delete, $"v1/sessions/{sessionId}", null,
            (_, _) => Task.FromResult(Results.NoContent()), ambient);

    // Every call for a listening session but its start and its discard: the session must be the clinician's own, of a
    // visit, with its consent recorded, and still recording. The audio route above sends nothing to the service otherwise.
    // How many people the consent said would speak is kept for the request, under ConsentedSpeakers.
    static async ValueTask<object?> RequireOpenCaptureAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        if (http.Request.RouteValues["id"] is not string id || !SessionId().IsMatch(id))
        {
            return NotFound();
        }

        IdentifyReviewUser(http);
        var services = http.RequestServices;
        var db = services.GetRequiredService<EhrDbContext>();
        short? speakers;
        await using (var transaction = await db.Database.BeginTransactionAsync(http.RequestAborted))
        {
            speakers = await services.GetRequiredService<PhiReader>().AmbientCaptureAsync(id, http.RequestAborted);
            await transaction.CommitAsync(http.RequestAborted);
        }

        if (speakers is not { } consented)
        {
            return Refuse(StatusCodes.Status409Conflict, "not_listening",
                "This listening has ended, or has no recorded consent, so it takes nothing more.");
        }

        http.Items[ConsentedSpeakers] = consented;
        return await next(context);
    }

    static readonly object ConsentedSpeakers = new();
}
