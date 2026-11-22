namespace Ehr.Data.Patients;

// The signed snapshot, whose text never changes; its sensitivity is the one thing that can (step 10b). One final
// record per draft; the draft remains as a frozen sync record.
public sealed class ClinicalNote
{
    public Guid Id { get; set; }
    public Guid DraftId { get; set; }
    public Guid PatientId { get; set; }
    public long DraftVersion { get; set; }

    // Signed in four sections, Subjective, Objective, Assessment and Plan (the expansion plan's step 14); or, signed
    // before then, in History, Assessment and Plan, which it keeps exactly as signed. One shape or the other, never
    // both (ck_clinical_notes_sections).
    public string? Subjective { get; set; }
    public string? Objective { get; set; }
    public string? History { get; set; }
    public required string Assessment { get; set; }
    public required string Plan { get; set; }
    public Guid SignerId { get; set; }
    public DateTimeOffset SignedAt { get; set; }

    // Sensitive (the expansion plan's step 10): left out of the chart until asked for, each such view recorded, its
    // values never shown in the audit log, never kept offline, like any sensitive item. Set when the note is signed,
    // or afterwards with a reason (step 10b): the one thing about a signed note that changes. Its text never does.
    public bool Sensitive { get; set; }

    // Who made it sensitive (the expansion plan's step 10b): its signer or author when it was signed sensitive, or
    // whoever marked it afterwards. Null while it isn't sensitive. Only that person, or the note's signer, unmarks it.
    public Guid? SensitiveMarkedBy { get; set; }

    // Why it was last marked or unmarked after signing, and when. Null if its sensitivity never changed.
    public string? SensitiveReason { get; set; }

    public DateTimeOffset? SensitiveChangedAt { get; set; }
}
