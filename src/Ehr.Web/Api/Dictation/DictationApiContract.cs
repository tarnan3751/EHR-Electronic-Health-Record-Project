using System.Text.Json.Serialization;

namespace Ehr.Web.Api.Dictation;

// The JSON that DictationApi sends (camelCase on the wire). The dictation service's answers are read into these types
// and written out again, so nothing else it sends reaches the browser: a property not here is dropped, a sorting,
// reason, unclear or status code not here fails the read (502), and an empty field has no property that could carry
// the text validation discarded.

// What the service allows a session: up to MaxSeconds of audio, in chunks of up to MaxChunkBytes each.
public sealed record DictationSession(string SessionId, int MaxSeconds, int MaxChunkBytes);

// The chunk just taken, how many seconds of audio the dictation now holds (of the session's MaxSeconds, so a screen can
// warn before the cut-off), and the transcript so far.
public sealed record AudioReceived(long ReceivedSeq, double AudioSeconds, Transcript Transcript);

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
// NotCarriedOver lists every instruction in the transcript to order something or change the allergy list, which no field
// carries (FR-14), so the screen can say what still has to be placed in Orders or the allergy list.
public sealed record DictationResult(
    Transcript Transcript,
    Sorting Sorting,
    NoteFields? Fields,
    IReadOnlyList<NotCarriedOver> NotCarriedOver,
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

    // The transcript wouldn't fit the model's context, so it was never sent: a dictation over the session's limit.
    [JsonStringEnumMemberName("transcript_too_long")]
    TranscriptTooLong,

    // Speech recognition failed on the rest of the recording after stop: the transcript is as it was live, not final.
    [JsonStringEnumMemberName("transcription_failed")]
    TranscriptionFailed,
}

// An instruction the clinician dictated that the note doesn't carry: its kind, and where they said it in the transcript
// (offsets as in TranscriptSegment). The clinician's own words, already in the transcript; never anything the model wrote.
public sealed record NotCarriedOver(Instruction Reason, int Start, int End);

[JsonConverter(typeof(JsonStringEnumConverter<Instruction>))]
public enum Instruction
{
    [JsonStringEnumMemberName("order")]
    Order,

    [JsonStringEnumMemberName("allergy_change")]
    AllergyChange,
}

public sealed record NoteFields(NoteField History, NoteField Assessment, NoteField Plan);

// Each field is a proposal for the clinician to approve, or empty. Nothing is saved either way (FR-13).
[JsonPolymorphic(TypeDiscriminatorPropertyName = "status")]
[JsonDerivedType(typeof(PendingField), "pending")]
[JsonDerivedType(typeof(EmptyField), "empty")]
public abstract record NoteField;

// The model's proposal, with the parts of the transcript it came from (offsets as in TranscriptSegment), and where the
// clinician may have corrected what it says.
public sealed record PendingField(string Value, IReadOnlyList<SourceSpan> Sources, IReadOnlyList<FieldWarning> Warnings) : NoteField;

// Nothing dictated, or what was proposed failed validation: the reasons' codes, never the text.
public sealed record EmptyField(IReadOnlyList<string> Reasons) : NoteField;

public sealed record SourceSpan(int Start, int End);

// A place in the transcript (offsets as in TranscriptSegment) the review screen must point to beside the field: the
// value is still a proposal, but something there may have changed it. A code and a place, never text from the model.
public sealed record FieldWarning(WarningCode Code, int Start, int End);

[JsonConverter(typeof(JsonStringEnumConverter<WarningCode>))]
public enum WarningCode
{
    // The transcript says another number, date or drug name of the same kind just after one the value uses, with a
    // correction-like word between: the clinician may have corrected it in words speech recognition got wrong
    // ("no weight" for "no wait").
    [JsonStringEnumMemberName("possible_correction")]
    PossibleCorrection,

    // A passage marked unclear sits beside one of the value's sources: speech recognition couldn't hear something
    // right next to what the value was sorted from.
    [JsonStringEnumMemberName("near_unclear_passage")]
    NearUnclearPassage,
}

// What produced the transcript and the fields, for the note to store with them. Language is null when the service's
// models folder records no K2 Horizon file, and LlamaServer until the service has reached llama-server, which is
// always the case when it couldn't (model_unavailable). Code is a hash of the service's own code.
public sealed record Provenance(
    SpeechModel Speech, LanguageModel? Language, string? LlamaServer, string Prompt, string Schema, string Structuring, string Code);

public sealed record SpeechModel(string Repository, string Commit, string Sha256);

public sealed record LanguageModel(string Repository, string Commit, string File, string Sha256);

// Every refusal the API makes itself: a code to act on, and words for the clinician. ExpectedSeq comes with
// out_of_order only: the chunk to send again from.
public sealed record DictationError(
    string Error, string Message, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? ExpectedSeq = null);

// The service's error body, of which only the code and ExpectedSeq are read: its detail never reaches the browser.
public sealed record SidecarError(string Error, long? ExpectedSeq = null);
