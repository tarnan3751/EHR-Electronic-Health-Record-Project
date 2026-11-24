namespace Ehr.Data.Patients;

// Server-created binding to an owner-checked sidecar session. ResultJson is a sanitized trusted result, not browser JSON.
public sealed class AiNoteReview
{
    public Guid Id { get; set; }
    public Guid PatientId { get; set; }
    public Guid DraftId { get; set; }
    public Guid ClinicianId { get; set; }
    public required string SessionId { get; set; }
    public int MaxSeconds { get; set; }
    public int MaxChunkBytes { get; set; }
    public required string State { get; set; }
    public long StartedVersion { get; set; }
    public long? ImportedVersion { get; set; }
    public string? ResultJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    // A dictation, or a recorded visit's conversation (ambient listening, the expansion plan's step 16), which names the
    // consent that allowed it, and whose transcript is kept apart and only while the review is open (AmbientTranscript).
    // A conversation's review is a sensitive item from the start. The database holds all three together and never lets
    // them change (db/policies/56-ambient-listening.sql).
    public string Mode { get; set; } = AiReviewModes.Dictation;

    public Guid? ConsentId { get; set; }

    public bool Sensitive { get; set; }
}

public static class AiReviewModes
{
    public const string Dictation = "dictation";
    public const string Ambient = "ambient";
}

public static class AiReviewStates
{
    public const string Recording = "recording";
    public const string Pending = "pending";
    public const string Completed = "completed";
    public const string Cancelled = "cancelled";
}
