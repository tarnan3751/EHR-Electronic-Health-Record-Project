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
    // A note's sections (the expansion plan's step 14): Subjective, Objective, Assessment and Plan.
    public string? Subjective { get; set; }
    public string? Objective { get; set; }
    public string? Assessment { get; set; }
    public string? Plan { get; set; }
    public long? StructuredVersion { get; set; }

    // A draft written before step 14 has a History in place of Subjective and Objective. It is kept as it was until
    // its author saves the draft in four sections, which clears it in the same statement; the database signs no draft
    // that still has one (db/policies/50-clinical-notes.sql), and holds no draft with both (ck_clinical_drafts_sections).
    public string? History { get; set; }

    // The visit type the author chose (step 14): the reference template whose prompts the editor shows beside the
    // sections (Reference.NoteTemplate). Guidance only: nothing of it is in the note, and the signed note doesn't keep it.
    public string? NoteTemplateCode { get; set; }

    // Trusted server metadata. Never bound from an editor or draft-sync request. UC 13 must set this
    // transactionally when attaching proposals and clear it only after server-side review of every field.
    public bool HasUnreviewedProposals { get; set; }
}
