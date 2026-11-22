using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ehr.Data;
using Ehr.Data.Accounts;
using Ehr.Domain.Accounts;
using Ehr.Domain.Phi;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Ehr.Web.Api.Dictation;

// The clinician dictation API (FR-10 to FR-14, UC 9 and 10), for the note screens to call from the browser. The
// clinician dictates, the audio goes up in chunks, and the transcript comes back while they speak; after stop come the
// transcript and the note fields the model proposed and validation let through, each pending the clinician's approval.
// Nothing is saved (FR-13): this API writes nothing, and a session carries no patient or encounter. Each call goes to
// the on-prem dictation service (DictationSidecar) for the signed-in clinician, whom the service binds the session to.
//
// Who: a clinician's staff cookie (the Clinician policy, on the whole group in Program.cs), plus the page's form token
// in a RequestVerificationToken header. Not signed in as staff (a patient's sign-in isn't): 401. Staff, but not a
// clinician: 403. Never a redirect. The cookie is checked against the account only every few minutes
// (CookieSecurityStamp), so every call here checks it at once: an account disabled or locked out, or whose password or
// authenticator has changed since it signed in, is refused with 401 on its next call. Where: the on-prem app only. Every route is a POST or a DELETE, so the cloud app
// forwards each to the on-prem app, and while it can't reach it answers 503 with its usual text (Forwarding/).
//
// POST /api/dictation/sessions: 201 {sessionId, maxSeconds, maxChunkBytes}. The session takes up to maxSeconds of
// audio, in chunks of up to maxChunkBytes (never over MaxChunkBytes, 2 MB). A session ID is 16 to 64 letters, digits,
// _ or -; any other is 404 here, without asking the service.
//
// POST /api/dictation/sessions/{id}/audio?seq=N, the body one chunk of the recording, numbered from 0, in the audio/*
// type the browser records (passed on as it came; WebKit and Chromium record different ones): 200 {receivedSeq,
// audioSeconds, transcript}, audioSeconds being how much of maxSeconds (3 minutes) the dictation holds, so the screen
// can warn before the cut-off; the transcript may lag a chunk when the service is busy. 409 out_of_order carries
// expectedSeq, the chunk to send next: after a dropped connection, the client sends again from there and the transcript
// carries on.
//
// POST /api/dictation/sessions/{id}/stop: 200 with the result. The service answers within 8 seconds with whatever is
// ready, so the transcript may not be final yet and sorting may still be pending. POST /api/dictation/sessions/{id}/result: 200 with the result, after waiting up to 10
// seconds for sorting to finish; ask again while it is pending. DELETE /api/dictation/sessions/{id}: 204, and the
// dictation is cancelled, its audio and transcript discarded.
//
// The transcript: {text, final, segments: [{start, end, startMs, endMs, final, unclear}]}. start and end are half-open
// UTF-16 offsets into text; unclear is null, "too_quiet", "too_noisy" or "low_confidence".
//
// The result: {transcript, sorting, reason?, message?, fields, notCarriedOver, proposals?, provenance}, with sorting
// "pending", "done" or "unavailable". fields is null, or {subjective, objective, assessment, plan}, each either
// {status: "pending", value, sources:
// [{start, end}], warnings: [{code, start, end}]}, a proposal for the clinician to approve, or {status: "empty",
// reasons: [code, ...]}: nothing dictated, or discarded by validation, with the reasons' codes and never the discarded
// text. A warning is "possible_correction", pointing at a later number, date or drug in the transcript that may be a
// correction the checks didn't hear, "near_unclear_passage", pointing at a passage marked unclear beside one of the
// value's sources, or "one_field_holds_dictation", on the only field proposed when it quotes nearly the whole transcript,
// pointing at what it quotes; the review screen shows it beside the field. When sorting is
// "unavailable" (UC 10), reason is "model_unavailable", "model_timeout", "model_output_invalid",
// "transcript_too_long" or "transcription_failed" (the transcript is then as it was live, not final), fields is null
// or all empty, and message is DictationResult.UnsortedMessage, "The fields couldn't be sorted, so the transcript is
// kept as plain text." notCarriedOver: [{reason: "order" or "allergy_change", start, end}], each instruction in the
// transcript to order something or change the allergy list, which no field carries (FR-14): the screen says it still
// has to be placed, and can highlight the clinician's own words at start and end. proposals (the expansion plan's step
// 15): [{kind: "order" or "allergy_addition", start, end, warnings: [{code, start, end}]}], those listed instructions the
// dictation proposes, each at one's place and of its kind, from a final transcript, at most twenty; staged with the
// review (TrustedResult), never carried into a field. Absent from a service older than step 15. provenance: {speech:
// {repository, commit, sha256}, language: {repository, commit, file, sha256}, llamaServer, prompt, schema, structuring, code};
// language and llamaServer are null while unknown. The JSON is
// in DictationApiContract.cs, and every answer the service gives is pinned in src/Ehr.Dictation/contract, which the
// service's tests write and DictationApiTests reads through this API.
//
// Refusals other than 401 and 403 are {error, message}, words for the clinician, and expectedSeq with out_of_order:
//   400 bad_form_token, or bad_seq
//   404 not_found: an unknown, expired or someone else's session
//   409 out_of_order, stopped (a chunk after stop), or not_stopped (a result before stop)
//   413 too_large: the chunk, or the dictation's bytes; or too_long: the dictation is over its maxSeconds (3 minutes)
//   415 unsupported_type: the body isn't audio/*
//   422 not_audio: "The audio couldn't be decoded."
//   429 busy: the service is busy
//   502 bad_answer: the service answered something this API doesn't know
//   503 unavailable: not configured, the service can't be reached or says it's unavailable, or it refused the key
//   504 timed_out: the service didn't answer within DictationSidecar.DefaultTimeout (20 seconds)
// A model that is unavailable or too slow isn't a refusal: the result says so. What is logged is the operation and
// the service's status, never a body, a transcript or a field.
//
// Listening to a visit (ambient listening, the expansion plan's step 16) has routes of its own under /ambient, in the same
// group: AmbientApi.cs.
public static partial class DictationApi
{
    public const string Path = "/api/dictation/sessions";

    public const int MaxChunkBytes = 2 * 1024 * 1024;

    // How long result waits for the fields: the service caps it at 10 seconds, well inside a call's 20.
    const int ResultWaitSeconds = 10;

    // For the service's answers: properties in any order, and one the contract needs, missing or null, fails the read.
    static readonly JsonSerializerOptions ServiceJson = new(JsonSerializerDefaults.Web)
    {
        AllowOutOfOrderMetadataProperties = true,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
    };

    public static RouteGroupBuilder MapDictationApi(this IEndpointRouteBuilder endpoints)
    {
        var sessions = endpoints.MapGroup(Path).AddEndpointFilter(RequireCurrentAccountAsync).AddEndpointFilter(RequireFormTokenAsync);
        sessions.MapPost("/", StartAsync);
        MapNoteReview(sessions);
        MapAmbient(sessions);
        sessions.MapPost("/{id}/audio", SendAudioAsync).WithMetadata(new RequestSizeLimitAttribute(MaxChunkBytes));
        sessions.MapPost("/{id}/stop", StopAsync);
        sessions.MapPost("/{id}/result", ResultAsync);
        sessions.MapDelete("/{id}", CancelAsync);
        return sessions;
    }

    // The enclosing Desktop group authenticates a revocable bearer session. Web staff cookies and
    // their form tokens stay on the original routes. Every desktop call checks the current panel.
    public static void MapOfflineDictation(this RouteGroupBuilder desktop)
    {
        var sessions = desktop.MapGroup("/offline-dictation").AddEndpointFilter(RequireAssignedPatientAsync);
        sessions.MapPost("/start", StartAsync);
        sessions.MapPost("/{id}/audio", SendAudioAsync).WithMetadata(new RequestSizeLimitAttribute(MaxChunkBytes));
        sessions.MapPost("/{id}/stop", StopAsync);
        sessions.MapPost("/{id}/result", ResultAsync);
    }

    static async ValueTask<object?> RequireAssignedPatientAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        http.Response.Headers.CacheControl = "no-store";
        if (!Guid.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out var owner)
            || !Guid.TryParse(http.Request.Query["patient"], out var patient))
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        var services = http.RequestServices;
        services.GetRequiredService<UserContext>().UserId = owner;
        var db = services.GetRequiredService<EhrDbContext>();
        var cancellationToken = http.RequestAborted;
        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            if (!await services.GetRequiredService<PhiReader>().IsAssignedAsync(patient, cancellationToken))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            await transaction.CommitAsync(cancellationToken);
        }

        return await next(context);
    }

    // The sign-in still holds: the account isn't disabled or locked out, and its security stamp hasn't changed.
    static async ValueTask<object?> RequireCurrentAccountAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next) =>
        await CookieSecurityStamp.CurrentAccountAsync<StaffUser>(context.HttpContext.RequestServices, context.HttpContext.User) is null
            ? Results.Unauthorized()
            : await next(context);

    // The same token every form post on the pages carries, here in a header. It runs after authorization, so a
    // request from nobody is a 401 first.
    static async ValueTask<object?> RequireFormTokenAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try
        {
            await context.HttpContext.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context.HttpContext);
        }
        catch (AntiforgeryValidationException)
        {
            return Refuse(StatusCodes.Status400BadRequest, "bad_form_token", "The page is out of date. Reload it and try again.");
        }

        return await next(context);
    }

    static Task<IResult> StartAsync(HttpContext context, DictationSidecar sidecar) =>
        CallAsync<DictationSession>(context, sidecar, "start", HttpMethod.Post, "v1/sessions", null,
            session => Results.Json(
                session with { MaxChunkBytes = Math.Min(session.MaxChunkBytes, MaxChunkBytes) }, statusCode: StatusCodes.Status201Created));

    static Task<IResult> SendAudioAsync(string id, string? seq, HttpContext context, DictationSidecar sidecar) =>
        ForwardAudioAsync(id, seq, context, sidecar, ambient: false);

    // A chunk of a dictation's audio, or of a visit's (AmbientApi), passed on as it came.
    static async Task<IResult> ForwardAudioAsync(string id, string? seq, HttpContext context, DictationSidecar sidecar, bool ambient)
    {
        if (!SessionId().IsMatch(id))
        {
            return NotFound();
        }

        if (!long.TryParse(seq, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
        {
            return Refuse(StatusCodes.Status400BadRequest, "bad_seq", "Number each chunk in seq, from 0.");
        }

        if (!MediaTypeHeaderValue.TryParse(context.Request.ContentType, out var type)
            || type.MediaType?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) != true)
        {
            return Refuse(StatusCodes.Status415UnsupportedMediaType, "unsupported_type", "Send the recording as audio.");
        }

        // The route's size limit stops a longer body as it's read; this refuses one that says so before reading it.
        if (context.Request.ContentLength > MaxChunkBytes)
        {
            return Refuse(StatusCodes.Status413PayloadTooLarge, "too_large", "This chunk of audio is over the 2 MB limit.");
        }

        using var audio = new MemoryStream();
        await context.Request.Body.CopyToAsync(audio, context.RequestAborted);
        var content = new ByteArrayContent(audio.ToArray()) { Headers = { ContentType = type } };
        return await CallAsync<AudioReceived>(context, sidecar, ambient ? "audio-ambient" : "audio", HttpMethod.Post,
            $"v1/sessions/{id}/audio?seq={number.ToString(CultureInfo.InvariantCulture)}", content, received => Results.Ok(received), ambient);
    }

    static Task<IResult> StopAsync(string id, HttpContext context, DictationSidecar sidecar) =>
        SessionId().IsMatch(id)
            ? CallAsync<DictationResult>(context, sidecar, "stop", HttpMethod.Post, $"v1/sessions/{id}/stop", null, result => Results.Ok(result))
            : Task.FromResult(NotFound());

    static Task<IResult> ResultAsync(string id, HttpContext context, DictationSidecar sidecar) =>
        SessionId().IsMatch(id)
            ? CallAsync<DictationResult>(context, sidecar, "result", HttpMethod.Post,
                $"v1/sessions/{id}/result?wait={ResultWaitSeconds}", null, result => Results.Ok(result))
            : Task.FromResult(NotFound());

    static Task<IResult> CancelAsync(string id, HttpContext context, DictationSidecar sidecar) =>
        SessionId().IsMatch(id)
            ? CallAsync(context, sidecar, "cancel", HttpMethod.Delete, $"v1/sessions/{id}", null, (_, _) => Task.FromResult(Results.NoContent()))
            : Task.FromResult(NotFound());

    // Calls the service, and writes its answer out again from the contract's types.
    static Task<IResult> CallAsync<T>(
        HttpContext context, DictationSidecar sidecar, string operation, HttpMethod method, string path, HttpContent? content,
        Func<T, IResult> answer, bool ambient = false) =>
        CallAsync(context, sidecar, operation, method, path, content, async (body, cancellationToken) =>
            answer(await body.ReadFromJsonAsync<T>(ServiceJson, cancellationToken) ?? throw new JsonException("The answer is null.")), ambient);

    // Calls the service for the signed-in clinician, within the timeout, and turns whatever goes wrong into a refusal.
    static async Task<IResult> CallAsync(
        HttpContext context, DictationSidecar sidecar, string operation, HttpMethod method, string path, HttpContent? content,
        Func<HttpContent, CancellationToken, Task<IResult>> answer, bool ambient = false)
    {
        var log = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(DictationApi));
        if (!sidecar.IsConfigured)
        {
            log.LogWarning("Dictation isn't configured: set Ehr:Dictation:Url and Ehr:Dictation:ApiKey.");
            return Unavailable();
        }

        // The policy has just authenticated the clinician: the owner is whoever that is, whatever the request says.
        var owner = Guid.Parse(context.User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var aborted = context.RequestAborted;
        using var timer = new CancellationTokenSource(sidecar.Timeout);
        using var call = CancellationTokenSource.CreateLinkedTokenSource(aborted, timer.Token);
        using var request = sidecar.Request(method, path, owner, content, ambient);
        try
        {
            using var response = await sidecar.Client.SendAsync(request, call.Token);
            return response.IsSuccessStatusCode
                ? await answer(response.Content, call.Token)
                : await RefusalAsync(response, operation, log, call.Token);
        }
        catch (OperationCanceledException) when (timer.IsCancellationRequested && !aborted.IsCancellationRequested)
        {
            log.LogWarning("The dictation service didn't answer {Operation} within {Timeout}.", operation, sidecar.Timeout);
            return Refuse(StatusCodes.Status504GatewayTimeout, "timed_out", "Dictation didn't answer in time. Try again.");
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or OperationCanceledException
            && !aborted.IsCancellationRequested)
        {
            // A refused or timed-out connection, or one that dropped.
            log.LogWarning(exception, "The dictation service can't be reached.");
            return Unavailable();
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            // Where in the answer, not what: the path names properties, never values.
            log.LogWarning("The dictation service's answer to {Operation} couldn't be read, at {Path}.", operation, (exception as JsonException)?.Path);
            return BadAnswer();
        }
    }

    // The service's refusals, mapped to this API's own. Only a 409's code and expected sequence are read from its body.
    static async Task<IResult> RefusalAsync(HttpResponseMessage response, string operation, ILogger log, CancellationToken cancellationToken)
    {
        switch (response.StatusCode)
        {
            case HttpStatusCode.NotFound:
                return NotFound();
            case HttpStatusCode.Conflict:
                return await response.Content.ReadFromJsonAsync<SidecarError>(ServiceJson, cancellationToken) switch
                {
                    { Error: "out_of_order", ExpectedSeq: { } next } => Refuse(
                        StatusCodes.Status409Conflict, "out_of_order", $"Send the audio again from chunk {next}.", next),
                    { Error: "stopped" } => Refuse(StatusCodes.Status409Conflict, "stopped", "This dictation has stopped, so it takes no more audio."),
                    { Error: "not_stopped" } => Refuse(StatusCodes.Status409Conflict, "not_stopped", "Stop the dictation before asking for its result."),
                    _ => Unexpected(response.StatusCode, operation, log),
                };
            case HttpStatusCode.RequestEntityTooLarge:
                // Too long means stop and start another; too large, a smaller chunk.
                return await ErrorBodyAsync(response, cancellationToken) is { Error: "too_long" }
                    ? Refuse(StatusCodes.Status413PayloadTooLarge, "too_long", "This dictation has reached its 3-minute limit. Stop it and start another.")
                    : Refuse(StatusCodes.Status413PayloadTooLarge, "too_large", "This chunk of audio, or the dictation as a whole, is too large.");
            case HttpStatusCode.UnprocessableEntity:
                // Or, for a visit (AmbientApi), a role missing for a voice the service told apart.
                return await ErrorBodyAsync(response, cancellationToken) is { Error: "speakers_incomplete" }
                    ? SpeakersIncomplete()
                    : Refuse(StatusCodes.Status422UnprocessableEntity, "not_audio", "The audio couldn't be decoded.");
            case HttpStatusCode.TooManyRequests:
                return Refuse(StatusCodes.Status429TooManyRequests, "busy", "Dictation is busy. Try again in a moment.");
            case HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden:
                log.LogError("The dictation service refused the key: Ehr:Dictation:ApiKey doesn't match the service's.");
                return Unavailable();
            default:
                return Unexpected(response.StatusCode, operation, log);
        }
    }

    // The code in a refusal's body, or null if there's no body to read.
    static async Task<SidecarError?> ErrorBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<SidecarError>(ServiceJson, cancellationToken);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // A status, or a 409 code, that isn't in the contract: 503 if the service says it's unavailable, otherwise 502.
    static IResult Unexpected(HttpStatusCode status, string operation, ILogger log)
    {
        log.LogWarning("The dictation service answered {Status} to {Operation}.", (int)status, operation);
        return status == HttpStatusCode.ServiceUnavailable ? Unavailable() : BadAnswer();
    }

    static IResult NotFound() =>
        Refuse(StatusCodes.Status404NotFound, "not_found", "This dictation has ended, or can't be found.");

    static IResult Unavailable() =>
        Refuse(StatusCodes.Status503ServiceUnavailable, "unavailable", "Dictation isn't available right now.");

    static IResult BadAnswer() =>
        Refuse(StatusCodes.Status502BadGateway, "bad_answer", "Dictation isn't working right now.");

    static IResult Refuse(int status, string error, string message, long? expectedSeq = null) =>
        Results.Json(new DictationError(error, message, expectedSeq), statusCode: status);

    // As the service checks them, and nothing that could change the path it's called at.
    [GeneratedRegex(@"^[A-Za-z0-9_-]{16,64}\z")]
    private static partial Regex SessionId();
}
