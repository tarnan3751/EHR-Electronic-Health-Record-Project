namespace Ehr.Data.Patients;

// Unsigned work owned by the clinician who created it; never a signed chart note.
public sealed class ClinicalDraft
{
    public Guid Id { get; set; }
    public Guid PatientId { get; set; }
    public Guid ClinicianId { get; set; }
    public required string Body { get; set; }
    public long Version { get; set; }
}
