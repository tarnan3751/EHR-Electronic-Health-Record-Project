using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ehr.Data.Patients;

// The transcript of a recorded visit's conversation (ambient listening, the expansion plan's step 16; UC 23), with who
// said each passage, kept only while its review is open: written when the review is staged, deleted in the transaction
// that finishes it, and deleted 7 days after it was written if nobody has (db/policies/56-ambient-listening.sql). The
// review itself keeps no text of it (AiNoteReview.ResultJson, whose transcript text is empty for a conversation). The
// write audit stores its hash and length, never its text (db/policies/40-audit.sql). A sensitive item always. PHI:
// row-level security is enabled and forced on it; the cloud app can see that one exists but never read it. Synthetic
// data only.
public sealed class AmbientTranscript
{
    // The review it belongs to (AiNoteReview.Id): one transcript a review, keyed by its review's ID, which is also the
    // row the write audit names.
    public Guid Id { get; set; }

    public Guid PatientId { get; set; }

    // {text, segments: [{start, end, startMs, endMs, final, unclear, speaker}]}, offsets as the dictation API's.
    public required string Transcript { get; set; }

    // Always true, and can't be anything else.
    public bool Sensitive { get; set; } = true;

    // When it was written, on the database's clock: what the 7 days are counted from.
    public DateTimeOffset CreatedAt { get; set; }
}

// Maps ehr.ambient_transcripts. EhrDbContext picks this up with ApplyConfigurationsFromAssembly.
public sealed class AmbientTranscriptConfiguration : IEntityTypeConfiguration<AmbientTranscript>
{
    public void Configure(EntityTypeBuilder<AmbientTranscript> transcript)
    {
        transcript.ToTable("ambient_transcripts", table =>
        {
            table.HasCheckConstraint("ck_ambient_transcripts_sensitive", "sensitive");
            table.HasCheckConstraint("ck_ambient_transcripts_transcript",
                "jsonb_typeof(transcript) = 'object' AND octet_length(transcript::text) <= 1048576");
        });
        transcript.HasKey(t => t.Id);
        transcript.Property(t => t.Transcript).HasColumnType("jsonb");
        transcript.Property(t => t.CreatedAt).HasDefaultValueSql("now()");
        transcript.HasOne<AiNoteReview>().WithMany().HasForeignKey(t => new { t.Id, t.PatientId })
            .HasPrincipalKey(r => new { r.Id, r.PatientId }).OnDelete(DeleteBehavior.Restrict);
    }
}
