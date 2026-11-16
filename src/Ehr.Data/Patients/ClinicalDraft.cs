namespace Ehr.Data.Patients;

// Unsigned work owned by the clinician who created it; never a signed chart note.
public sealed class ClinicalDraft
{
    public Guid Id { get; set; }
    public Guid PatientId { get; set; }
    public Guid ClinicianId { get; set; }
    public required string Body { get; set; }
    public long Version { get; set; }

    // Body stays plain text for UC 8 clients. StructuredVersion ties these fields to precisely that
    // version: a legacy desktop update increments Version and leaves these fields stale, never signable.
    public string? History { get; set; }
    public string? Assessment { get; set; }
    public string? Plan { get; set; }
    public long? StructuredVersion { get; set; }

    // Trusted server metadata. Never bound from an editor or draft-sync request. UC 13 must set this
    // transactionally when attaching proposals and clear it only after server-side review of every field.
    public bool HasUnreviewedProposals { get; set; }
}
