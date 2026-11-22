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
// owner that started them, in the language each was started in, chunks taken in order up to the session's limit, a
// transcript of one made-up sentence per chunk, and note fields sorted from it. It answers as the service does, error bodies with their detail included, and records every request
// it gets. The switches make it fail the ways the service can, or propose more of the note's fields. The tests that
// drive it run one at a time, and each starts it afresh.
public sealed class DictationSidecarStub : HttpMessageHandler
{
    public const string Url = "https://dictation.test";

    public const string ApiKey = "a-dictation-key-for-the-tests-only";

    // What validation threw away in the Discarded mode, which the browser must never see.
    public const string DiscardedText = "Prescribe a drug nobody mentioned";

    static readonly string[] Sentences = ["Cough for three days.", "No fever.", "Chest is clear.", "Likely viral.", "Fluids and rest."];

    // The Spanish mode's dictation, a sentence a chunk: the third spoken in English, which the service marks as not
    // Spanish, the fourth with an order in it, whose "í" is written as an i and a combining accent (U+0301), so a source
    // can be cut between the two, and the last long, so a quote of it has to wrap.
    public static readonly string[] SpanishSentences =
    [
        "Tos desde hace tres días y niega fiebre.",
        "Pulmones limpios.",
        "Patient was counseled on diet.",
        "Probable cuadro viral, pido una radiografi\u0301a de tórax.",
        "Líquidos abundantes y reposo relativo durante los próximos tres a cinco días, paracetamol 1.000 mg si tiene dolor o malestar general, hierro cada tercer día, y regresar a consulta si aparece dificultad para respirar o fiebre que no cede.",
    ];

    // What the Spanish mode proposes from those five chunks: for each field, each English sentence with the Spanish words
    // of the transcript it was translated from.
    public static readonly (string Field, (string English, string Spanish)[] Statements)[] SpanishFields =
    [
        ("subjective", [("Cough for three days.", "Tos desde hace tres días"), ("Denies fever.", "niega fiebre")]),
        ("objective", [("Lungs clear.", "Pulmones limpios.")]),
        ("assessment", [("Likely viral illness.", "Probable cuadro viral")]),
        ("plan", [("Plenty of fluids and relative rest for the next three to five days, acetaminophen 1000 mg if there is pain or general malaise, iron every other day, and return to clinic if difficulty breathing or a fever that does not go away appears.", SpanishSentences[4])]),
    ];

    readonly ConcurrentDictionary<string, StubSession> sessions = new();

    public ConcurrentQueue<ReceivedRequest> Received { get; } = new();

    public bool IncludePlan { get; set; }

    // An Objective too ("Chest is clear.", from the third chunk), so every one of the four fields is proposed.
    public bool IncludeObjective { get; set; }

    // A History beside the four fields, as no service since the expansion plan's step 14 sends.
    public bool LiveHistory { get; set; }

    // The whole transcript sorted into Subjective, the other three not dictated, with the warning the service puts on it.
    public bool WholeDictation { get; set; }

    // Two plain instructions said after the chunks' sentences, once the dictation has stopped: an order and an allergy to
    // add, each listed as not carried over and proposed (the expansion plan's step 15), the allergy with a warning. Both
    // kinds, so a test that follows a proposal to a signed order or a saved allergy has one of each to follow.
    public bool Proposals { get; set; }

    // Said before the order, in its sentence, when set: a long one, for a page to wrap.
    public string? ProposalLead { get; set; }

    public const string OrderSaid = "Go ahead and order a lipid panel";

    public const string AllergySaid = "Add penicillin to her allergy list";

    string ProposalsSaid => (ProposalLead is null ? "" : ProposalLead + " ") + OrderSaid + ". " + AllergySaid + ".";

    public StubSorting Sorting { get; set; }

    // A dictation in Spanish (the expansion plan's step 17): SpanishSentences, a chunk each, and when it is sorted, every
    // one of the four fields proposed in English with its statements (SpanishFields), from the five chunks. For a session
    // started in Spanish ({"spokenLanguage": "es"}), or for every session while this is set.
    public bool Spanish { get; set; }

    // How much audio a session started in Spanish takes, as the service's 2 minutes; a test sets less, so the limit is
    // reached in a few chunks of the stub's 2 seconds each. A session in English takes the service's 3 minutes.
    public int SpanishMaxSeconds { get; set; } = 120;

    // Whisper isn't installed: a session started in Spanish is refused, as the service refuses it.
    public bool SpanishUnavailable { get; set; }

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
        Proposals = false;
        ProposalLead = null;
        Spanish = false;
        SpanishMaxSeconds = 120;
        SpanishUnavailable = false;
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
            // As the service reads it: no body is English, otherwise {"spokenLanguage": "en" or "es"} and nothing else.
            var spanish = false;
            if (body.Length > 0)
            {
                try
                {
                    var start = JsonDocument.Parse(body).RootElement;
                    var language = start.ValueKind == JsonValueKind.Object && start.EnumerateObject().All(p => p.Name == "spokenLanguage")
                        && start.TryGetProperty("spokenLanguage", out var said) && said.ValueKind == JsonValueKind.String ? said.GetString() : null;
                    spanish = language is "es" ? true : language is "en" ? false : throw new JsonException();
                }
                catch (JsonException)
                {
                    return Error(HttpStatusCode.BadRequest, "bad_language");
                }
            }

            if (spanish && SpanishUnavailable)
            {
                return Error(HttpStatusCode.ServiceUnavailable, "language_unavailable");
            }

            var id = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(24));
            var maxSeconds = spanish ? SpanishMaxSeconds : 180;
            sessions[id] = new StubSession(owner, spanish, maxSeconds);
            return Json(HttpStatusCode.Created, new { sessionId = id, maxSeconds, maxChunkBytes = 1024 * 1024, expiresAt = DateTimeOffset.UtcNow.AddHours(1) });
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

                    // Two seconds a chunk: one that would take the dictation past its limit is refused, and isn't kept.
                    if ((session.Chunks + 1) * 2 > session.MaxSeconds)
                    {
                        return Error(HttpStatusCode.RequestEntityTooLarge, "too_long");
                    }

                    session.Chunks++;
                    return Json(HttpStatusCode.OK, new { receivedSeq = seq, audioSeconds = session.Chunks * 2.0, transcript = Transcript(session, Spoken(session)) });

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

    bool InSpanish(StubSession session) => Spanish || session.Spanish;

    string[] Spoken(StubSession session) => InSpanish(session) ? SpanishSentences : Sentences;

    static string Text(StubSession session, string[] sentences) =>
        string.Join(' ', Enumerable.Range(0, session.Chunks).Select(chunk => sentences[chunk % sentences.Length]));

    object Transcript(StubSession session, string[] sentences)
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
            text.Append(sentences[chunk % sentences.Length]);
            // The last chunk's words may still change until the dictation stops.
            var final = session.Stopped || chunk < session.Chunks - 1;
            var unclear = chunk != 2 ? null : sentences == SpanishSentences ? "other_language" : "too_noisy";
            segments.Add(new { start, end = text.Length, startMs = chunk * 2000, endMs = chunk * 2000 + 1800, final, unclear, confidence = 0.42 });
        }

        if (Proposals && session.Stopped)
        {
            var start = text.Append(' ').Length;
            text.Append(ProposalsSaid);
            segments.Add(new { start, end = text.Length, startMs = session.Chunks * 2000, endMs = session.Chunks * 2000 + 1800, final = true, unclear = (string?)null, confidence = 0.9 });
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
        if (InSpanish(session) && sorting is not StubSorting.Discarded)
        {
            return SortedSpanish(session, sorting);
        }

        var transcript = Transcript(session, Sentences);
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
        object? proposals = null;
        if (Proposals && session.Stopped)
        {
            var at = Text(session, Sentences).Length + 1 + (ProposalLead is null ? 0 : ProposalLead.Length + 1);
            var (order, allergy) = ((Start: at, End: at + OrderSaid.Length), (Start: at + OrderSaid.Length + 2, End: at + OrderSaid.Length + 2 + AllergySaid.Length));
            notCarriedOver = [new { reason = "order", start = order.Start, end = order.End }, new { reason = "allergy_change", start = allergy.Start, end = allergy.End }];
            proposals = new object[]
            {
                new { kind = "order", start = order.Start, end = order.End, warnings = Array.Empty<object>() },
                new { kind = "allergy_addition", start = allergy.Start, end = allergy.End, warnings = new[] { new { code = "possible_correction", start = allergy.Start + 4, end = allergy.Start + 14 } } },
            };
        }

        return sorting switch
        {
            StubSorting.Pending => new { transcript, sorting = "pending", fields = (object?)null, notCarriedOver, proposals, provenance },
            StubSorting.Done when WholeDictation => new
            {
                transcript,
                sorting = "done",
                fields = (object?)new
                {
                    subjective = new
                    {
                        status = "pending",
                        value = Text(session, Sentences),
                        sources = new[] { new { start = 0, end = Text(session, Sentences).Length } },
                        warnings = new[] { new { code = "one_field_holds_dictation", start = 0, end = Text(session, Sentences).Length } },
                    },
                    objective = new { status = "empty", reasons = new[] { "not_dictated" } },
                    assessment = new { status = "empty", reasons = new[] { "not_dictated" } },
                    plan = new { status = "empty", reasons = new[] { "not_dictated" } },
                },
                notCarriedOver,
                proposals,
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
                proposals,
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
                proposals,
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
                proposals,
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
                proposals,
                provenance,
                debug = new { modelOutput = DiscardedText },
            },
        };
    }

    // The Spanish mode's answer: the transcript in Spanish, the order it holds not carried over, and from five chunks or
    // more, each field's English sentences, joined by a space as the value, each citing the Spanish words it was
    // translated from; the plan's 1.000 mg and "cada tercer día" carry the warnings that they read two ways. With the
    // model too slow or unavailable, the Spanish transcript and why, as for English.
    object SortedSpanish(StubSession session, StubSorting sorting)
    {
        var transcript = Transcript(session, SpanishSentences);
        var text = Text(session, SpanishSentences);
        var provenance = new
        {
            speech = new { repository = "openai/whisper-large-v3-turbo", commit = "3333333", sha256 = new string('d', 64) },
            language = new { repository = "IFM/K2-Horizon-7B-GGUF", commit = "2222222", file = "K2-Horizon-7B-Q4_K_M.gguf", sha256 = new string('b', 64) },
            llamaServer = "b11669",
            prompt = "note-prompt-es-1",
            schema = "note-schema-es-1",
            structuring = "structuring-es-1",
            code = "sha256:" + new string('c', 64),
        };
        const string Order = "pido una radiografi\u0301a de tórax";
        var order = text.IndexOf(Order, StringComparison.Ordinal);
        var notCarriedOver = new[] { new { reason = "order", start = order, end = order + Order.Length } }.Where(item => item.start >= 0).ToArray();
        if (sorting == StubSorting.Pending)
        {
            return new { transcript, sorting = "pending", fields = (object?)null, notCarriedOver, provenance, spokenLanguage = "es" };
        }

        if (sorting is StubSorting.Timeout or StubSorting.Unavailable)
        {
            var reason = sorting == StubSorting.Timeout ? "model_timeout" : "model_unavailable";
            return new { transcript, sorting = "unavailable", reason, fields = (object?)null, notCarriedOver, provenance, spokenLanguage = "es" };
        }

        var fields = new Dictionary<string, object>();
        if (session.Chunks < SpanishSentences.Length)
        {
            foreach (var (field, _) in SpanishFields)
            {
                fields[field] = new { status = "empty", reasons = new[] { "not_dictated" } };
            }

            return new { transcript, sorting = "done", fields, notCarriedOver, provenance, spokenLanguage = "es" };
        }

        foreach (var (field, statements) in SpanishFields)
        {
            var value = new StringBuilder();
            var translated = new List<object>();
            var sources = new List<object>();
            foreach (var (english, spanish) in statements)
            {
                if (value.Length > 0)
                {
                    value.Append(' ');
                }

                var at = text.IndexOf(spanish, StringComparison.Ordinal);
                var source = new { start = at, end = at + spanish.Length };
                translated.Add(new { start = value.Length, end = value.Length + english.Length, sources = new[] { source } });
                sources.Add(source);
                value.Append(english);
            }

            var warnings = new List<object>();
            if (field == "plan")
            {
                var number = text.IndexOf("1.000 mg", StringComparison.Ordinal);
                warnings.Add(new { code = "ambiguous_number", start = number, end = number + "1.000 mg".Length });
                var frequency = text.IndexOf("cada tercer día", StringComparison.Ordinal);
                warnings.Add(new { code = "ambiguous_frequency", start = frequency, end = frequency + "cada tercer día".Length });
            }

            fields[field] = new { status = "pending", value = value.ToString(), sources, warnings, statements = translated };
        }

        return new { transcript, sorting = "done", fields, notCarriedOver, provenance, spokenLanguage = "es" };
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

    sealed class StubSession(string owner, bool spanish, int maxSeconds)
    {
        public string Owner { get; } = owner;

        public bool Spanish { get; } = spanish;

        public int MaxSeconds { get; } = maxSeconds;

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
