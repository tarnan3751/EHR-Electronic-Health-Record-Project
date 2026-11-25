using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ehr.Web.Api.Dictation;

// The JSON that DictationApi sends (camelCase on the wire). The dictation service's answers are read into these types
// and written out again, so nothing else it sends reaches the browser: a property not here is dropped, a sorting,
// reason, unclear or status code not here fails the read (502), and an empty field has no property that could carry
// the text validation discarded.

// What the service allows a session: up to MaxSeconds of audio, in chunks of up to MaxChunkBytes each. Mode is "ambient"
// for a session listening to a visit (step 16), which a service too old to listen to one doesn't send.
public sealed record DictationSession(string SessionId, int MaxSeconds, int MaxChunkBytes,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Mode = null);

// What starting a session asks the service for: the language the clinician dictates in (the expansion plan's step 17),
// always sent, English unless a note's dictation was started in Spanish.
public sealed record SessionStart(SpokenLanguage SpokenLanguage);

// The chunk just taken, how many seconds of audio the dictation now holds (of the session's MaxSeconds, so a screen can
// warn before the cut-off), and the transcript so far.
// Listening to a visit (step 16), the transcript stays empty until it stops, and QuietSeconds says how long nobody has
// spoken, so the screen can stop on its own.
public sealed record AudioReceived(long ReceivedSeq, double AudioSeconds, Transcript Transcript,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? QuietSeconds = null);

// Final once the dictation has stopped and every segment is final.
public sealed record Transcript(string Text, bool Final, IReadOnlyList<TranscriptSegment> Segments);

// Start and End are half-open UTF-16 offsets into the transcript's text (a JavaScript string's indexes); StartMs and
// EndMs are where the segment is in the audio. Unclear says why the words may be wrong, or is null. A recorded visit's
// segments (step 16) say who spoke: SpeakerGroup, the voice the service told apart (1 to AmbientApi.MaxVoices, 9), and
// once the clinician has said which group is whom, Speaker. A dictation's have neither.
public sealed record TranscriptSegment(int Start, int End, long StartMs, long EndMs, bool Final, Unclear? Unclear = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? SpeakerGroup = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Speaker? Speaker = null);

// Who said a passage of a recorded visit: the clinician, the patient, someone else in the room, or a voice that couldn't be
// told apart with confidence, or that the clinician couldn't tell ("I can't tell": a fragment of someone's, say).
[JsonConverter(typeof(JsonStringEnumConverter<Speaker>))]
public enum Speaker
{
    [JsonStringEnumMemberName("clinician")]
    Clinician,

    [JsonStringEnumMemberName("patient")]
    Patient,

    [JsonStringEnumMemberName("other")]
    Other,

    [JsonStringEnumMemberName("unsure")]
    Unsure,
}

// Each voice told apart in a recorded visit, and how long it spoke, for the clinician to say who it is (step 16).
public sealed record SpeakerVoice(int Group, double Seconds);

[JsonConverter(typeof(JsonStringEnumConverter<Unclear>))]
public enum Unclear
{
    [JsonStringEnumMemberName("too_quiet")]
    TooQuiet,

    [JsonStringEnumMemberName("too_noisy")]
    TooNoisy,

    [JsonStringEnumMemberName("low_confidence")]
    LowConfidence,

    // In a dictation in Spanish, a passage speech recognition didn't hear as Spanish (the expansion plan's step 17): left
    // out of what the model sorts, for the clinician to dictate again in Spanish or type.
    [JsonStringEnumMemberName("other_language")]
    OtherLanguage,
}

// After stop, and from result. Fields is null until the model has sorted the transcript, or when it couldn't.
// NotCarriedOver lists every instruction in the transcript to order something or change the allergy list, which no field
// carries (FR-14), so the screen can say what still has to be placed in Orders or the allergy list. Proposals are those of
// them the dictation proposes (the expansion plan's step 15; FR-14 as reworded): each an order or an allergy to add,
// dictated plainly, at the place of its listed instruction. Null from a service older than step 15, and in a result
// stored before it: none. Never written when it is null. SpokenLanguage is the language the clinician dictated in (the
// expansion plan's step 17), never written for English, so a result in English, and every one saved before Spanish,
// reads and is stored as it was. Nothing is proposed from a dictation in Spanish (TrustedResult).
public sealed record DictationResult(
    Transcript Transcript,
    Sorting Sorting,
    NoteFields? Fields,
    IReadOnlyList<NotCarriedOver> NotCarriedOver,
    Provenance Provenance,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SortingFailure? Reason = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<ChartProposal>? Proposals = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] SpokenLanguage SpokenLanguage = SpokenLanguage.English,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<SpeakerVoice>? Speakers = null)
{
    public const string UnsortedMessage = "The fields couldn't be sorted, so the transcript is kept as plain text.";
    public const string BusyMessage = "The dictation service was busy for too long to draft this visit. The conversation is kept "
        + "with this review until you finish it: write the note from it, or discard it.";
    public const string NotEnglishMessage = "This dictation doesn't read as English, so no fields were proposed and the transcript "
        + "is kept as plain text. If you dictated in Spanish, choose Spanish as the language and dictate it again.";

    // Ours, never the service's, and only when the fields couldn't be sorted (UC 10): a visit left undrafted because
    // dictations had K2 Horizon all along (model_busy) says so, and a dictation in English with too few English words
    // (transcript_not_english) points at the Spanish choice.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message => Sorting != Sorting.Unavailable ? null : Reason switch
    {
        SortingFailure.ModelBusy => BusyMessage,
        SortingFailure.TranscriptNotEnglish => NotEnglishMessage,
        _ => UnsortedMessage,
    };

    // Whether a stored review's dictation was in Spanish, from its saved result, for its line among the saved reviews.
    // Never throws, whatever the stored evidence holds: only a result whose spokenLanguage is exactly "es" is Spanish, and
    // anything else, a result saved before Spanish, none, or one that can't be read, isn't called Spanish, so the line
    // says nothing of a language rather than something false.
    public static SpokenLanguage SpokenLanguageOf(string? resultJson)
    {
        if (resultJson is null) return SpokenLanguage.English;
        try
        {
            using var json = JsonDocument.Parse(resultJson);
            return json.RootElement.ValueKind == JsonValueKind.Object
                && json.RootElement.TryGetProperty("spokenLanguage", out var language)
                && language.ValueKind == JsonValueKind.String && language.ValueEquals("es")
                ? SpokenLanguage.Spanish : SpokenLanguage.English;
        }
        catch (JsonException)
        {
            return SpokenLanguage.English;
        }
    }
}

// The language of a dictation (the expansion plan's step 17). A dictation in Spanish has a Spanish transcript, and its
// fields are proposed in English, each sentence with the Spanish it was translated from (TranslatedStatement). Read
// only from the exact strings "en" and "es" (SpokenLanguageConverter).
[JsonConverter(typeof(SpokenLanguageConverter))]
public enum SpokenLanguage
{
    English,

    Spanish,
}

// "en" or "es", exactly: a number, another spelling, a padded name or a list of names fails the read, where the
// framework's enum reading would take 1, "1", " es" and "en, es" for a language.
public sealed class SpokenLanguageConverter : JsonConverter<SpokenLanguage>
{
    public override SpokenLanguage Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType != JsonTokenType.String ? throw new JsonException("A dictation's language is a string.")
        : reader.ValueTextEquals("en") ? SpokenLanguage.English
        : reader.ValueTextEquals("es") ? SpokenLanguage.Spanish
        : throw new JsonException("A dictation's language is \"en\" or \"es\".");

    public override void Write(Utf8JsonWriter writer, SpokenLanguage value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value == SpokenLanguage.Spanish ? "es" : "en");
}

[JsonConverter(typeof(JsonStringEnumConverter<Sorting>))]
public enum Sorting
{
    // The model is still at it: ask for the result.
    [JsonStringEnumMemberName("pending")]
    Pending,

    [JsonStringEnumMemberName("done")]
    Done,

    // A recorded visit (step 16) is transcribed and its voices told apart: the clinician says which is whom
    // (Speakers) before the note is drafted.
    [JsonStringEnumMemberName("awaiting_speakers")]
    AwaitingSpeakers,

    // The transcript stays as plain text, and Reason says why.
    [JsonStringEnumMemberName("unavailable")]
    Unavailable,
}

// Why the fields couldn't be sorted. A new reason from the service is added here, with its JsonStringEnumMemberName, and in
// the one other place, the list of reasons in ehr.ambient_result_is_bare (db/policies/56-ambient-listening.sql, marked
// there): DictationApiTests fails until the two lists are the same.
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

    // A recorded visit (step 16) whose windows waited ten minutes in all for K2 Horizon, which dictations had first.
    [JsonStringEnumMemberName("model_busy")]
    ModelBusy,

    // A dictation in English fewer than two thirds of whose words are English (the owner's ruling of 2026-11-25): Spanish
    // dictated with English chosen, which MedASR writes as words of English and of no language. Nothing is offered.
    [JsonStringEnumMemberName("transcript_not_english")]
    TranscriptNotEnglish,
}

// An instruction the clinician dictated that the note doesn't carry: its kind, and where they said it in the transcript
// (offsets as in TranscriptSegment). The clinician's own words, already in the transcript; never anything the model wrote.
// A recorded visit's (step 16) says who said it: the clinician, or "unsure", a passage whose speaker couldn't be told; a
// patient's request is never listed. A dictation's has no Speaker.
public sealed record NotCarriedOver(Instruction Reason, int Start, int End,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Speaker? Speaker = null);

[JsonConverter(typeof(JsonStringEnumConverter<Instruction>))]
public enum Instruction
{
    [JsonStringEnumMemberName("order")]
    Order,

    [JsonStringEnumMemberName("allergy_change")]
    AllergyChange,
}

// An instruction the dictation proposes (the expansion plan's step 15): an order, or an allergy to add, which the clinician
// dictated plainly. Where they said it, the place of its listed instruction (offsets as in TranscriptSegment), and where
// something may have changed it: possible_correction and near_unclear_passage, as on a field. Never anything the model
// wrote. It is proposed, never placed or saved: only the clinician who dictated it may open it in the order form or the
// allergy form, which fill in nothing from it, or dismiss it. HeardAfter is where what the clinician said after it is
// (the sentence after its own, and those after that up to another instruction; validation.heard_after()), which the
// review shows beside it, so a retraction the service's rules don't read is in view; null when nothing follows, and in a
// result stored before step 15.1. Never written when it is null.
public sealed record ChartProposal(ProposalKind Kind, int Start, int End, IReadOnlyList<FieldWarning> Warnings,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SourceSpan? HeardAfter = null);

[JsonConverter(typeof(JsonStringEnumConverter<ProposalKind>))]
public enum ProposalKind
{
    // An order of any type the order form takes: the clinician picks its type and item there.
    [JsonStringEnumMemberName("order")]
    Order,

    // An allergy to add. Nothing that removes, resolves or changes one is ever proposed.
    [JsonStringEnumMemberName("allergy_addition")]
    AllergyAddition,
}

// The four fields (the expansion plan's step 14). History is the first of the three a dictation was sorted into before
// then: the service never sends it now, a live result that has one is refused at staging (TrustedResult), and it is
// read only from a review saved then (Notes/Review), where its approved value goes into Subjective (NoteReviews). Never
// written when it is null.
public sealed record NoteFields(NoteField Subjective, NoteField Objective, NoteField Assessment, NoteField Plan,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] NoteField? History = null);

// Each field is pending clinician approval, or empty. This result alone never approves clinical content (FR-13).
[JsonPolymorphic(TypeDiscriminatorPropertyName = "status")]
[JsonDerivedType(typeof(PendingField), "pending")]
[JsonDerivedType(typeof(EmptyField), "empty")]
public abstract record NoteField;

// The model's proposal, with the parts of the transcript it came from (offsets as in TranscriptSegment), and where the
// clinician may have corrected what it says. From a dictation in Spanish (step 17), Statements says which sentence of the
// English value was translated from which Spanish words; never written for English.
public sealed record PendingField(string Value, IReadOnlyList<SourceSpan> Sources, IReadOnlyList<FieldWarning> Warnings,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<TranslatedStatement>? Statements = null) : NoteField;

// One sentence of a value proposed from Spanish: its place in the value (half-open UTF-16 offsets into Value), and the
// Spanish words of the transcript it was translated from (offsets as in TranscriptSegment). The statements follow each
// other through the value, one space between, and cite every source the field has and no other (TrustedResult).
public sealed record TranslatedStatement(int Start, int End, IReadOnlyList<SourceSpan> Sources)
{
    // The most of the transcript one sentence may cite, in UTF-16 code units: the Spanish note schema allows a sentence
    // two quotes of 200 characters (note-fields-es.schema.json), and the dictation service refuses more (translation.py).
    public const int MaxSpanishLength = 400;
}

// Nothing dictated, or what was proposed failed validation: the reasons' codes, never the text. Codes are those the
// service gives (validation.py's REASONS: DictationApiTests reads that file, and db/policies/56-ambient-listening.sql,
// so the three lists can't drift); a visit's are held to them (TrustedResult, and the database).
public sealed record EmptyField(IReadOnlyList<string> Reasons) : NoteField
{
    public static readonly string[] Codes =
    [
        "unknown_field", "not_dictated", "too_long", "bad_format", "order_or_allergy_change", "no_sources", "source_too_short",
        "source_not_in_transcript", "source_in_unclear_passage", "wrong_speaker", "source_is_a_question", "bare_answer",
        "source_superseded", "number_not_in_sources", "date_not_in_sources", "drug_not_in_sources", "negation_dropped",
        "experiencer_dropped", "certainty_dropped", "history_dropped", "reported_dropped", "words_out_of_order",
        "unsupported_content",
    ];
}

// In a recorded visit (step 16), who said it.
public sealed record SourceSpan(int Start, int End,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Speaker? Speaker = null);

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

    // The only field proposed, whatever became of the others (not dictated, or discarded by the checks), quotes nearly
    // all of the transcript (nine words in ten): the whole dictation, plan and findings alike, went into one section, for
    // the clinician to sort before accepting it.
    [JsonStringEnumMemberName("one_field_holds_dictation")]
    OneFieldHoldsDictation,

    // From a dictation in Spanish (step 17): a frequency that reads two ways ("cada tercer día", every other day or
    // every third day); it points at those words. A number that reads two ways ("5.000 unidades") is no warning: the
    // service refuses its field (ambiguous_number, a reason), so a result warning of one is a bad answer.
    [JsonStringEnumMemberName("ambiguous_frequency")]
    AmbiguousFrequency,

    // A recorded visit's (step 16). The value comes from a passage whose speaker couldn't be told with confidence; from
    // someone else in the room, who may be speaking of themselves; or, in Subjective, from the patient's own words, in
    // the first person, to be reworded as the clinician's note. Or part of the visit had a value for this field that
    // didn't pass the checks: it points at that part.
    [JsonStringEnumMemberName("speaker_unsure")]
    SpeakerUnsure,

    [JsonStringEnumMemberName("other_speaker")]
    OtherSpeaker,

    [JsonStringEnumMemberName("patients_own_words")]
    PatientsOwnWords,

    [JsonStringEnumMemberName("part_not_offered")]
    PartNotOffered,
}

// What produced the transcript and the fields, for the note to store with them. Language is null when the service's
// models folder records no K2 Horizon file, and LlamaServer until the service has reached llama-server, which is
// always the case when it couldn't (model_unavailable). Code is a hash of the service's own code.
// A recorded visit's (step 16) also names the speaker-embedding model that told the voices apart.
public sealed record Provenance(
    SpeechModel Speech, LanguageModel? Language, string? LlamaServer, string Prompt, string Schema, string Structuring, string Code,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SpeechModel? Speaker = null);

public sealed record SpeechModel(string Repository, string Commit, string Sha256);

public sealed record LanguageModel(string Repository, string Commit, string File, string Sha256);

// Every refusal the API makes itself: a code to act on, and words for the clinician. ExpectedSeq comes with
// out_of_order only: the chunk to send again from.
public sealed record DictationError(
    string Error, string Message, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? ExpectedSeq = null);

// The service's error body, of which only the code and ExpectedSeq are read: its detail never reaches the browser.
public sealed record SidecarError(string Error, long? ExpectedSeq = null);
