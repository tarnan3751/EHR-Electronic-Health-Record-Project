using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ehr.Web.Api.Dictation;

namespace Ehr.Web.Tests;

// The dictation service in memory, where the on-prem app's DictationSidecar sends its calls: sessions bound to the
// owner that started them, chunks taken in order, a transcript of one made-up sentence per chunk, and note fields
// sorted from it. It answers as the service does, error bodies with their detail included, and records every request
// it gets. The switches make it fail the ways the service can, or propose more of the note's fields. The tests that
// drive it run one at a time, and each starts it afresh.
public sealed class DictationSidecarStub : HttpMessageHandler
{
    public const string Url = "https://dictation.test";

    public const string ApiKey = "a-dictation-key-for-the-tests-only";

    // What validation threw away in the Discarded mode, which the browser must never see.
    public const string DiscardedText = "Prescribe a drug nobody mentioned";

    static readonly string[] Sentences = ["Cough for three days.", "No fever.", "Chest is clear.", "Likely viral.", "Fluids and rest."];

    readonly ConcurrentDictionary<string, StubSession> sessions = new();

    public ConcurrentQueue<ReceivedRequest> Received { get; } = new();

    public bool IncludePlan { get; set; }

    // An Objective too ("Chest is clear.", from the third chunk), so every one of the four fields is proposed.
    public bool IncludeObjective { get; set; }

    // A History beside the four fields, as no service since the expansion plan's step 14 sends.
    public bool LiveHistory { get; set; }

    // The whole transcript sorted into Subjective, the other three not dictated, with the warning the service puts on it.
    public bool WholeDictation { get; set; }

    public StubSorting Sorting { get; set; }

    // Every request fails to connect.
    public bool Unreachable { get; set; }

    // Every request waits until the caller gives up.
    public bool Slow { get; set; }

    // Stop finds the audio can't be decoded.
    public bool Undecodable { get; set; }

    // Every request gets this answer instead.
    public (HttpStatusCode Status, string Body)? Answer { get; set; }

    public DictationSidecarStub Reset()
    {
        sessions.Clear();
        Received.Clear();
        Sorting = StubSorting.Done;
        IncludePlan = false;
        IncludeObjective = false;
        LiveHistory = false;
        WholeDictation = false;
        Unreachable = false;
        Slow = false;
        Undecodable = false;
        Answer = null;
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var headers = request.Headers.Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())
            .ToDictionary(header => header.Key, header => string.Join(", ", header.Value), StringComparer.OrdinalIgnoreCase);
        var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        Received.Enqueue(new(request.Method.Method, request.RequestUri!.PathAndQuery, headers, body));

        if (Unreachable)
        {
            throw new HttpRequestException("Connection refused", new SocketException((int)SocketError.ConnectionRefused));
        }

        if (Slow)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }

        if (Answer is { } answer)
        {
            return new HttpResponseMessage(answer.Status) { Content = new StringContent(answer.Body, Encoding.UTF8, "application/json") };
        }

        if (!headers.TryGetValue("Authorization", out var key) || key != $"Bearer {ApiKey}")
        {
            return Error(HttpStatusCode.Unauthorized, "unauthorized");
        }

        var owner = headers[DictationSidecar.OwnerHeader];
        var path = request.RequestUri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (path is ["v1", "sessions"] && request.Method == HttpMethod.Post)
        {
            var id = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(24));
            sessions[id] = new StubSession(owner);
            return Json(HttpStatusCode.Created, new { sessionId = id, maxSeconds = 180, maxChunkBytes = 1024 * 1024, expiresAt = DateTimeOffset.UtcNow.AddHours(1) });
        }

        if (path is not ["v1", "sessions", var sessionId, ..] || !sessions.TryGetValue(sessionId, out var session) || session.Owner != owner)
        {
            return Error(HttpStatusCode.NotFound, "not_found");
        }

        lock (session)
        {
            switch (path, request.Method.Method)
            {
                case ([_, _, _, "audio"], "POST"):
                    var seq = int.Parse(request.RequestUri.Query.Split('=')[1], CultureInfo.InvariantCulture);
                    if (session.Stopped)
                    {
                        return Error(HttpStatusCode.Conflict, "stopped");
                    }

                    if (seq != session.Chunks)
                    {
                        return Error(HttpStatusCode.Conflict, "out_of_order", new { expectedSeq = session.Chunks });
                    }

                    session.Chunks++;
                    return Json(HttpStatusCode.OK, new { receivedSeq = seq, audioSeconds = session.Chunks * 2.0, transcript = Transcript(session) });

                case ([_, _, _, "stop"], "POST"):
                    if (Undecodable)
                    {
                        return Error(HttpStatusCode.UnprocessableEntity, "not_audio");
                    }

                    session.Stopped = true;
                    // Sorting takes longer than stop waits, except when the model has already failed.
                    return Json(HttpStatusCode.OK, Result(session, Sorting == StubSorting.Done ? StubSorting.Pending : Sorting));

                case ([_, _, _, "result"], "POST"):
                    return session.Stopped ? Json(HttpStatusCode.OK, Result(session, Sorting)) : Error(HttpStatusCode.Conflict, "not_stopped");

                case ([_, _, _], "DELETE"):
                    sessions.TryRemove(sessionId, out _);
                    return new HttpResponseMessage(HttpStatusCode.NoContent);

                default:
                    return Error(HttpStatusCode.NotFound, "not_found");
            }
        }
    }

    static string Text(StubSession session) => string.Join(' ', Enumerable.Range(0, session.Chunks).Select(chunk => Sentences[chunk % Sentences.Length]));

    static object Transcript(StubSession session)
    {
        var text = new StringBuilder();
        var segments = new List<object>();
        for (var chunk = 0; chunk < session.Chunks; chunk++)
        {
            if (chunk > 0)
            {
                text.Append(' ');
            }

            var start = text.Length;
            text.Append(Sentences[chunk % Sentences.Length]);
            // The last chunk's words may still change until the dictation stops.
            var final = session.Stopped || chunk < session.Chunks - 1;
            segments.Add(new { start, end = text.Length, startMs = chunk * 2000, endMs = chunk * 2000 + 1800, final, unclear = chunk == 2 ? "too_noisy" : null, confidence = 0.42 });
        }

        return new { text = text.ToString(), final = session.Stopped, segments, words = new[] { "extra" } };
    }

    object Result(StubSession session, StubSorting sorting)
    {
        var result = Sorted(session, sorting);
        if (!LiveHistory || sorting != StubSorting.Done)
        {
            return result;
        }

        var withHistory = JsonSerializer.SerializeToNode(result, JsonSerializerOptions.Web)!;
        withHistory["fields"]!["history"] = JsonSerializer.SerializeToNode(Offered(0), JsonSerializerOptions.Web);
        return withHistory;
    }

    object Sorted(StubSession session, StubSorting sorting)
    {
        var transcript = Transcript(session);
        var provenance = new
        {
            speech = new { repository = "google/medasr", commit = "1111111", sha256 = new string('a', 64) },
            language = new { repository = "IFM/K2-Horizon-7B-GGUF", commit = "2222222", file = "K2-Horizon-7B-Q4_K_M.gguf", sha256 = new string('b', 64) },
            llamaServer = "b11669",
            prompt = "note-prompt-1",
            schema = "note-schema-1",
            structuring = "structuring-1",
            code = "sha256:" + new string('c', 64),
        };
        // The clinician's own instruction, in the transcript: where to find it, never the words themselves.
        var notCarriedOver = new[] { new { reason = "order", start = 0, end = 5 } };
        return sorting switch
        {
            StubSorting.Pending => new { transcript, sorting = "pending", fields = (object?)null, notCarriedOver, provenance },
            StubSorting.Done when WholeDictation => new
            {
                transcript,
                sorting = "done",
                fields = (object?)new
                {
                    subjective = new
                    {
                        status = "pending",
                        value = Text(session),
                        sources = new[] { new { start = 0, end = Text(session).Length } },
                        warnings = new[] { new { code = "one_field_holds_dictation", start = 0, end = Text(session).Length } },
                    },
                    objective = new { status = "empty", reasons = new[] { "not_dictated" } },
                    assessment = new { status = "empty", reasons = new[] { "not_dictated" } },
                    plan = new { status = "empty", reasons = new[] { "not_dictated" } },
                },
                notCarriedOver,
                provenance,
            },
            StubSorting.Done => new
            {
                transcript,
                sorting = "done",
                fields = (object?)new
                {
                    subjective = new { status = "pending", value = Sentences[0], sources = new[] { new { start = 0, end = 21 } }, warnings = Array.Empty<object>() },
                    objective = IncludeObjective ? Offered(2) : new { status = "empty", reasons = new[] { "not_dictated" } },
                    assessment = new
                    {
                        status = "pending",
                        value = Sentences[1],
                        sources = new[] { new { start = 22, end = 31 } },
                        warnings = new[]
                        {
                            new { code = "possible_correction", start = 0, end = 5 },
                            new { code = "near_unclear_passage", start = 6, end = 9 },
                        },
                    },
                    plan = IncludePlan ? Offered(4) : new { status = "empty", reasons = new[] { "not_dictated" } },
                },
                notCarriedOver,
                provenance,
            },
            StubSorting.Timeout => new
            {
                transcript,
                sorting = "unavailable",
                reason = "model_timeout",
                // The service's own words, which the API doesn't pass on.
                message = "llama-server took longer than 60 seconds",
                fields = (object?)null,
                notCarriedOver,
                provenance,
            },
            StubSorting.Unavailable => new
            {
                transcript,
                sorting = "unavailable",
                reason = "model_unavailable",
                fields = (object?)new
                {
                    subjective = new { status = "empty", reasons = new[] { "model_unavailable" } },
                    objective = new { status = "empty", reasons = new[] { "model_unavailable" } },
                    assessment = new { status = "empty", reasons = new[] { "model_unavailable" } },
                    plan = new { status = "empty", reasons = new[] { "model_unavailable" } },
                },
                notCarriedOver,
                provenance,
            },
            // What validation discarded, where a careless copy would pass it on: beside the reasons, and in
            // properties the contract doesn't have, with a field's status after its other properties.
            _ => new
            {
                transcript,
                sorting = "done",
                fields = (object?)new
                {
                    subjective = new
                    {
                        value = Sentences[0],
                        sources = new[] { new { start = 0, end = 21 } },
                        warnings = new[] { new { code = "possible_correction", start = 0, end = 5, note = DiscardedText } },
                        status = "pending",
                        note = DiscardedText,
                    },
                    objective = new { status = "empty", reasons = new[] { "not_dictated" } },
                    assessment = new { status = "empty", reasons = new[] { "not_dictated" } },
                    plan = new { status = "empty", reasons = new[] { "not_in_transcript" }, value = DiscardedText, discarded = new[] { DiscardedText } },
                },
                notCarriedOver,
                provenance,
                debug = new { modelOutput = DiscardedText },
            },
        };
    }

    // One chunk's sentence, proposed as a field's value, with its place in the transcript as its source.
    static object Offered(int sentence)
    {
        var start = Sentences.Take(sentence).Sum(s => s.Length + 1);
        return new { status = "pending", value = Sentences[sentence], sources = new[] { new { start, end = start + Sentences[sentence].Length } }, warnings = Array.Empty<object>() };
    }

    static HttpResponseMessage Json(HttpStatusCode status, object body) =>
        new(status) { Content = JsonContent.Create(body, options: JsonSerializerOptions.Web) };

    // The service's error body: a code, its detail in words, and anything else the code carries.
    static HttpResponseMessage Error(HttpStatusCode status, string error, object? extra = null)
    {
        var body = new Dictionary<string, object?> { ["error"] = error, ["detail"] = $"The detail of {error}, not for the browser." };
        if (extra is not null)
        {
            foreach (var property in JsonSerializer.SerializeToElement(extra, JsonSerializerOptions.Web).EnumerateObject())
            {
                body[property.Name] = property.Value;
            }
        }

        return Json(status, body);
    }

    sealed class StubSession(string owner)
    {
        public string Owner { get; } = owner;

        public int Chunks { get; set; }

        public bool Stopped { get; set; }
    }
}

public enum StubSorting
{
    // The fields are pending at stop, and sorted at result.
    Done,

    Pending,

    Timeout,

    Unavailable,

    Discarded,
}

// A request the stub got: its method, path and query, every header, request and content alike, and its body.
public sealed record ReceivedRequest(string Method, string PathAndQuery, IReadOnlyDictionary<string, string> Headers, byte[] Body);
