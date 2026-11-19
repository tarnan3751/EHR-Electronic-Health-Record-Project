namespace Ehr.Data.Patients;

// Immutable signed snapshot. One final record per draft; the draft remains as a frozen sync record.
public sealed class ClinicalNote
{
    public Guid Id { get; set; }
    public Guid DraftId { get; set; }
    public Guid PatientId { get; set; }
    public long DraftVersion { get; set; }
    public required string History { get; set; }
    public required string Assessment { get; set; }
    public required string Plan { get; set; }
    public Guid SignerId { get; set; }
    public DateTimeOffset SignedAt { get; set; }

    // Marked sensitive by its signer (the expansion plan's step 10): left out of the chart until asked for, each such
    // view recorded, its values never shown in the audit log, never kept offline, like any sensitive item. Set when
    // the note is signed and, like the rest of it, never changed.
    public bool Sensitive { get; set; }
}
