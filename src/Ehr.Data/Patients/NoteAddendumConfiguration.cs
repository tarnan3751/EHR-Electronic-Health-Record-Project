using Ehr.Data.Accounts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ehr.Data.Patients;

public sealed class NoteAddendumConfiguration : IEntityTypeConfiguration<NoteAddendum>
{
    public const int MaxLength = 16000;

    public void Configure(EntityTypeBuilder<NoteAddendum> addendum)
    {
        addendum.ToTable("note_addenda", table =>
            table.HasCheckConstraint("ck_note_addenda_text", $"text ~ '[^[:space:]]' AND char_length(text) <= {MaxLength}"));

        // To the note and its patient together: an addendum can't name another patient's note.
        addendum.HasOne<ClinicalNote>().WithMany().HasForeignKey(a => new { a.NoteId, a.PatientId })
            .HasPrincipalKey(n => new { n.Id, n.PatientId }).OnDelete(DeleteBehavior.Restrict);
        addendum.HasOne<Patient>().WithMany().HasForeignKey(a => a.PatientId).OnDelete(DeleteBehavior.Restrict);
        addendum.HasOne<StaffUser>().WithMany().HasForeignKey(a => a.AuthorId).OnDelete(DeleteBehavior.Restrict);
        // A note's addenda in the order they were signed.
        addendum.HasIndex(a => new { a.NoteId, a.SignedAt });
        addendum.HasIndex(a => a.PatientId);
        addendum.Property(a => a.AuthorId).HasDefaultValueSql("NULLIF(current_setting('app.user_id', true), '')::uuid");
        addendum.Property(a => a.SignedAt).HasDefaultValueSql("now()");
    }
}
