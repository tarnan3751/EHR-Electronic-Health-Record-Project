using Ehr.Data.Accounts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ehr.Data.Patients;

public sealed class ClinicalNoteConfiguration : IEntityTypeConfiguration<ClinicalNote>
{
    public void Configure(EntityTypeBuilder<ClinicalNote> note)
    {
        note.HasOne<ClinicalDraft>().WithMany().HasForeignKey(n => n.DraftId).OnDelete(DeleteBehavior.Restrict);
        note.HasIndex(n => n.DraftId).IsUnique();
        note.HasOne<Patient>().WithMany().HasForeignKey(n => n.PatientId).OnDelete(DeleteBehavior.Restrict);
        note.HasOne<StaffUser>().WithMany().HasForeignKey(n => n.SignerId).OnDelete(DeleteBehavior.Restrict);
        note.HasIndex(n => new { n.PatientId, n.SignedAt });
        note.Property(n => n.SignerId).HasDefaultValueSql("NULLIF(current_setting('app.user_id', true), '')::uuid");
        note.Property(n => n.SignedAt).HasDefaultValueSql("now()");
        note.Property(n => n.SensitiveReason).HasMaxLength(500);
        note.HasOne<StaffUser>().WithMany().HasForeignKey(n => n.SensitiveMarkedBy).OnDelete(DeleteBehavior.Restrict);
    }
}
