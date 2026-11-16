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
}
