using Ehr.Data.Accounts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ehr.Data.Patients;

public sealed class VisitSummaryConfiguration : IEntityTypeConfiguration<VisitSummary>
{
    // The most each of a summary's three parts holds: it is a summary, written for the patient.
    public const int MaxLength = 4000;

    public const int MaxReasonLength = 500;

    public void Configure(EntityTypeBuilder<VisitSummary> summary)
    {
        summary.ToTable("visit_summaries", table =>
        {
            foreach (var part in new[] { "talked_about", "found", "next_steps" })
            {
                table.HasCheckConstraint($"ck_visit_summaries_{part}", $"{part} ~ '[^[:space:]]' AND char_length({part}) <= {MaxLength}");
            }

            // Only a sensitive summary can be sensitive by inheritance.
            table.HasCheckConstraint("ck_visit_summaries_inherited", "sensitive OR NOT sensitive_inherited");
            // Withdrawn with who, when and why, or standing with none of them.
            table.HasCheckConstraint("ck_visit_summaries_withdrawn",
                "(withdrawn_at IS NULL AND withdrawn_by IS NULL AND withdrawn_reason IS NULL) OR (withdrawn_at IS NOT NULL AND withdrawn_by IS NOT NULL AND withdrawn_reason ~ '[^[:space:]]')");
        });

        // To the note and its patient together: a summary can't name another patient's note.
        summary.HasOne<ClinicalNote>().WithMany().HasForeignKey(s => new { s.NoteId, s.PatientId })
            .HasPrincipalKey(n => new { n.Id, n.PatientId }).OnDelete(DeleteBehavior.Restrict);
        summary.HasOne<Patient>().WithMany().HasForeignKey(s => s.PatientId).OnDelete(DeleteBehavior.Restrict);
        summary.HasOne<StaffUser>().WithMany().HasForeignKey(s => s.ReleasedBy).OnDelete(DeleteBehavior.Restrict);
        summary.HasOne<StaffUser>().WithMany().HasForeignKey(s => s.WithdrawnBy).OnDelete(DeleteBehavior.Restrict);
        // One standing summary for a note; withdrawn ones beside it, as many as there were.
        summary.HasIndex(s => s.NoteId).IsUnique().HasFilter("withdrawn_at IS NULL");
        // The portal's list: a patient's, newest first.
        summary.HasIndex(s => new { s.PatientId, s.NoteSignedAt });
        summary.Property(s => s.ReleasedBy).HasDefaultValueSql("NULLIF(current_setting('app.user_id', true), '')::uuid");
        summary.Property(s => s.ReleasedAt).HasDefaultValueSql("now()");
        summary.Property(s => s.WithdrawnReason).HasMaxLength(MaxReasonLength);
        summary.Property(s => s.SensitiveReason).HasMaxLength(MaxReasonLength);
        summary.HasOne<StaffUser>().WithMany().HasForeignKey(s => s.SensitiveMarkedBy).OnDelete(DeleteBehavior.Restrict);
    }
}
