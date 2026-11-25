using Ehr.Data.Accounts;
using Ehr.Data.Patients;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ehr.Data.Audit;

// The review of one emergency access (the expansion plan's step 18; UC 24, proposed FR-64): an administrator who isn't
// the clinician who used it checked it once it had ended, and recorded whether it was appropriate, with a note. Keyed
// by the access's own ID (ehr.break_glass_accesses), so an access has one review at most. Restricted, like the audit log
// it belongs with: row-level security enabled and forced, admins read and add them, and nobody changes or deletes one;
// who reviewed it and when are the database's, whatever the app sends (db/policies/72-emergency-access-review.sql). It
// names the access, not the patient.
public class EmergencyAccessReview
{
    // The emergency access's ID.
    public Guid Id { get; set; }

    public Guid ReviewerId { get; set; }

    public DateTimeOffset ReviewedAt { get; set; }

    // One of EmergencyAccessOutcomes: required with every review (Tahseen's ruling of 24 November 2026).
    public required string Outcome { get; set; }

    // 10 to 500 characters, kept trimmed.
    public required string Note { get; set; }
}

// What a reviewer found. What follows a finding that it wasn't appropriate (telling the clinician, or the patient) is
// not built: the plan lists it for later.
public static class EmergencyAccessOutcomes
{
    public const string Appropriate = "appropriate";

    public const string NotAppropriate = "not-appropriate";

    public static readonly string[] All = [Appropriate, NotAppropriate];

    public static readonly IReadOnlyDictionary<string, string> Names = new Dictionary<string, string>
    {
        [Appropriate] = "Appropriate",
        [NotAppropriate] = "Not appropriate",
    };
}

// Maps ehr.emergency_access_reviews. EhrDbContext picks this up with ApplyConfigurationsFromAssembly.
public sealed class EmergencyAccessReviewConfiguration : IEntityTypeConfiguration<EmergencyAccessReview>
{
    public void Configure(EntityTypeBuilder<EmergencyAccessReview> review)
    {
        review.ToTable("emergency_access_reviews", table =>
        {
            table.HasCheckConstraint("ck_emergency_access_reviews_outcome", $"outcome IN ({Codes.List(EmergencyAccessOutcomes.All)})");
            table.HasCheckConstraint("ck_emergency_access_reviews_note", "char_length(note) BETWEEN 10 AND 500");
        });
        // The access's ID, which the app gives: no generated column, so an insert needs no RETURNING.
        review.Property(r => r.Id).ValueGeneratedNever();
        review.Property(r => r.Outcome).HasMaxLength(20);
        review.Property(r => r.Note).HasMaxLength(500);
        review.HasOne<BreakGlassAccess>().WithOne().HasForeignKey<EmergencyAccessReview>(r => r.Id).OnDelete(DeleteBehavior.Restrict);
        review.HasOne<StaffUser>().WithMany().HasForeignKey(r => r.ReviewerId).OnDelete(DeleteBehavior.Restrict);
    }
}
