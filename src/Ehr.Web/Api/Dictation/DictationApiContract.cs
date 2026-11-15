using System.Text.Json.Serialization;

namespace Ehr.Web.Api.Dictation;

// The JSON that DictationApi sends (camelCase on the wire). The dictation service's answers are read into these types
// and written out again, so nothing else it sends reaches the browser: a property not here is dropped, a sorting,
// reason, unclear or status code not here fails the read (502), and an empty field has no property that could carry
// the text validation discarded.

// What the service allows a session: up to MaxSeconds of audio, in chunks of up to MaxChunkBytes each.
public sealed record DictationSession(string SessionId, int MaxSeconds, int MaxChunkBytes);

// The chunk just taken, and the transcript so far.
public sealed record AudioReceived(long ReceivedSeq, Transcript Transcript);

// Final once the dictation has stopped and every segment is final.
public sealed record Transcript(string Text, bool Final, IReadOnlyList<TranscriptSegment> Segments);

// Start and End are half-open UTF-16 offsets into the transcript's text (a JavaScript string's indexes); StartMs and
// EndMs are where the segment is in the audio. Unclear says why the words may be wrong, or is null.
public sealed record TranscriptSegment(int Start, int End, long StartMs, long EndMs, bool Final, Unclear? Unclear = null);

[JsonConverter(typeof(JsonStringEnumConverter<Unclear>))]
public enum Unclear
{
    [JsonStringEnumMemberName("too_quiet")]
    TooQuiet,

    [JsonStringEnumMemberName("too_noisy")]
    TooNoisy,

    [JsonStringEnumMemberName("low_confidence")]
    LowConfidence,
}

// After stop, and from result. Fields is null until the model has sorted the transcript, or when it couldn't.
public sealed record DictationResult(
    Transcript Transcript,
    Sorting Sorting,
    NoteFields? Fields,
    Provenance Provenance,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SortingFailure? Reason = null)
{
    public const string UnsortedMessage = "The fields couldn't be sorted, so the transcript is kept as plain text.";

    // Ours, never the service's, and only when the fields couldn't be sorted (UC 10).
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message => Sorting == Sorting.Unavailable ? UnsortedMessage : null;
}

[JsonConverter(typeof(JsonStringEnumConverter<Sorting>))]
public enum Sorting
{
    // The model is still at it: ask for the result.
    [JsonStringEnumMemberName("pending")]
    Pending,

    [JsonStringEnumMemberName("done")]
    Done,

    // The transcript stays as plain text, and Reason says why.
    [JsonStringEnumMemberName("unavailable")]
    Unavailable,
}

[JsonConverter(typeof(JsonStringEnumConverter<SortingFailure>))]
public enum SortingFailure
{
    [JsonStringEnumMemberName("model_unavailable")]
    ModelUnavailable,

    [JsonStringEnumMemberName("model_timeout")]
    ModelTimeout,

    [JsonStringEnumMemberName("model_output_invalid")]
    ModelOutputInvalid,
}

public sealed record NoteFields(NoteField History, NoteField Assessment, NoteField Plan);

// Each field is a proposal for the clinician to approve, or empty. Nothing is saved either way (FR-13).
[JsonPolymorphic(TypeDiscriminatorPropertyName = "status")]
[JsonDerivedType(typeof(PendingField), "pending")]
[JsonDerivedType(typeof(EmptyField), "empty")]
public abstract record NoteField;

// The model's proposal, with the parts of the transcript it came from (offsets as in TranscriptSegment).
public sealed record PendingField(string Value, IReadOnlyList<SourceSpan> Sources) : NoteField;

// Nothing dictated, or what was proposed failed validation: the reasons' codes, never the text.
public sealed record EmptyField(IReadOnlyList<string> Reasons) : NoteField;

public sealed record SourceSpan(int Start, int End);

// What produced the transcript and the fields, for the note to store with them. Language is null when the service's
// models folder records no K2 Horizon file, and LlamaServer until the service has reached llama-server, which is
// always the case when it couldn't (model_unavailable).
public sealed record Provenance(
    SpeechModel Speech, LanguageModel? Language, string? LlamaServer, string Prompt, string Schema, string Structuring, int Validation);

public sealed record SpeechModel(string Repository, string Commit, string Sha256);

public sealed record LanguageModel(string Repository, string Commit, string File, string Sha256);

// Every refusal the API makes itself: a code to act on, and words for the clinician. ExpectedSeq comes with
// out_of_order only: the chunk to send again from.
public sealed record DictationError(
    string Error, string Message, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? ExpectedSeq = null);

// The service's error body, of which only the code and ExpectedSeq are read: its detail never reaches the browser.
public sealed record SidecarError(string Error, long? ExpectedSeq = null);
