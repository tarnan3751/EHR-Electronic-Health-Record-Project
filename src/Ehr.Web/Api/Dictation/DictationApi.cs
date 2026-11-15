using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;

namespace Ehr.Web.Api.Dictation;

// The clinician dictation API (FR-10 to FR-14, UC 9 and 10), for the note screens to call from the browser. The
// clinician dictates, the audio goes up in chunks, and the transcript comes back while they speak; after stop come the
// transcript and the note fields the model proposed and validation let through, each pending the clinician's approval.
// Nothing is saved (FR-13): this API writes nothing, and a session carries no patient or encounter. Each call goes to
// the on-prem dictation service (DictationSidecar) for the signed-in clinician, whom the service binds the session to.
//
// Who: a clinician's staff cookie (the Clinician policy, on the whole group in Program.cs), plus the page's form token
// in a RequestVerificationToken header. Not signed in as staff (a patient's sign-in isn't): 401. Staff, but not a
// clinician: 403. Never a redirect. Where: the on-prem app only. Every route is a POST or a DELETE, so the cloud app
// forwards each to the on-prem app, and while it can't reach it answers 503 with its usual text (Forwarding/).
//
// POST /api/dictation/sessions: 201 {sessionId, maxSeconds, maxChunkBytes}. The session takes up to maxSeconds of
// audio, in chunks of up to maxChunkBytes (never over MaxChunkBytes, 2 MB). A session ID is 16 to 64 letters, digits,
// _ or -; any other is 404 here, without asking the service.
//
// POST /api/dictation/sessions/{id}/audio?seq=N, the body one chunk of the recording, numbered from 0, in the audio/*
// type the browser records (passed on as it came; WebKit and Chromium record different ones): 200 {receivedSeq,
// transcript}. 409 out_of_order carries expectedSeq, the chunk to send next: after a dropped connection, the client
// sends again from there and the transcript carries on.
//
// POST /api/dictation/sessions/{id}/stop: 200 with the result. The service waits up to 8 seconds for the fields, so
// sorting may still be pending. POST /api/dictation/sessions/{id}/result: 200 with the result, after waiting up to 10
// seconds for sorting to finish; ask again while it is pending. DELETE /api/dictation/sessions/{id}: 204, and the
// dictation is cancelled, its audio and transcript discarded.
//
// The transcript: {text, final, segments: [{start, end, startMs, endMs, final, unclear}]}. start and end are half-open
// UTF-16 offsets into text; unclear is null, "too_quiet", "too_noisy" or "low_confidence".
//
// The result: {transcript, sorting, reason?, message?, fields, provenance}, with sorting "pending", "done" or
// "unavailable". fields is null, or {history, assessment, plan}, each either {status: "pending", value, sources:
// [{start, end}]}, a proposal for the clinician to approve, or {status: "empty", reasons: [code, ...]}: nothing
// dictated, or discarded by validation, with the reasons' codes and never the discarded text. When sorting is
// "unavailable" (UC 10), reason is "model_unavailable", "model_timeout" or "model_output_invalid", fields is null or
// all empty, and message is DictationResult.UnsortedMessage, "The fields couldn't be sorted, so the transcript is kept
// as plain text." provenance: {speech: {repository, commit, sha256}, language: {repository, commit, file, sha256},
// llamaServer, prompt, schema, structuring, validation}; language and llamaServer are null while unknown. The JSON is
// in DictationApiContract.cs, and every answer the service gives is pinned in src/Ehr.Dictation/contract, which the
// service's tests write and DictationApiTests reads through this API.
//
// Refusals other than 401 and 403 are {error, message}, words for the clinician, and expectedSeq with out_of_order:
//   400 bad_form_token, or bad_seq
//   404 not_found: an unknown, expired or someone else's session
//   409 out_of_order, stopped (a chunk after stop), or not_stopped (a result before stop)
//   413 too_large: the chunk, or the dictation's bytes; or too_long: the dictation is over its maxSeconds
//   415 unsupported_type: the body isn't audio/*
//   422 not_audio: "The audio couldn't be decoded."
//   429 busy: the service is busy
//   502 bad_answer: the service answered something this API doesn't know
//   503 unavailable: not configured, the service can't be reached or says it's unavailable, or it refused the key
//   504 timed_out: the service didn't answer within DictationSidecar.DefaultTimeout (20 seconds)
// A model that is unavailable or too slow isn't a refusal: the result says so. What is logged is the operation and
// the service's status, never a body, a transcript or a field.
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
        var sessions = endpoints.MapGroup(Path).AddEndpointFilter(RequireFormTokenAsync);
        sessions.MapPost("/", StartAsync);
        sessions.MapPost("/{id}/audio", SendAudioAsync).WithMetadata(new RequestSizeLimitAttribute(MaxChunkBytes));
        sessions.MapPost("/{id}/stop", StopAsync);
        sessions.MapPost("/{id}/result", ResultAsync);
        sessions.MapDelete("/{id}", CancelAsync);
        return sessions;
    }

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

    static async Task<IResult> SendAudioAsync(string id, string? seq, HttpContext context, DictationSidecar sidecar)
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
        return await CallAsync<AudioReceived>(context, sidecar, "audio", HttpMethod.Post,
            $"v1/sessions/{id}/audio?seq={number.ToString(CultureInfo.InvariantCulture)}", content, received => Results.Ok(received));
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
        Func<T, IResult> answer) =>
        CallAsync(context, sidecar, operation, method, path, content, async (body, cancellationToken) =>
            answer(await body.ReadFromJsonAsync<T>(ServiceJson, cancellationToken) ?? throw new JsonException("The answer is null.")));

    // Calls the service for the signed-in clinician, within the timeout, and turns whatever goes wrong into a refusal.
    static async Task<IResult> CallAsync(
        HttpContext context, DictationSidecar sidecar, string operation, HttpMethod method, string path, HttpContent? content,
        Func<HttpContent, CancellationToken, Task<IResult>> answer)
    {
        var log = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(DictationApi));
        if (!sidecar.IsConfigured)
        {
            log.LogWarning("Dictation isn't configured: set Ehr:Dictation:Url and Ehr:Dictation:ApiKey.");
            return Unavailable();
        }

        // The policy has just authenticated the staff cookie: the owner is whoever that is, whatever the request says.
        var owner = Guid.Parse(context.User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var aborted = context.RequestAborted;
        using var timer = new CancellationTokenSource(sidecar.Timeout);
        using var call = CancellationTokenSource.CreateLinkedTokenSource(aborted, timer.Token);
        using var request = sidecar.Request(method, path, owner, content);
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
                    ? Refuse(StatusCodes.Status413PayloadTooLarge, "too_long", "This dictation has reached its length limit. Stop it and start another.")
                    : Refuse(StatusCodes.Status413PayloadTooLarge, "too_large", "This chunk of audio, or the dictation as a whole, is too large.");
            case HttpStatusCode.UnprocessableEntity:
                return Refuse(StatusCodes.Status422UnprocessableEntity, "not_audio", "The audio couldn't be decoded.");
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
