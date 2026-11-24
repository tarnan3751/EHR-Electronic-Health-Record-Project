using Ehr.Data.Accounts;
using Ehr.Data.Orders;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ehr.Data.Patients;

public sealed class AiReviewConfiguration : IEntityTypeConfiguration<AiNoteReview>, IEntityTypeConfiguration<AiFieldProposal>,
    IEntityTypeConfiguration<AiChartProposal>
{
    public void Configure(EntityTypeBuilder<AiNoteReview> review)
    {
        review.HasOne<ClinicalDraft>().WithMany().HasForeignKey(r => r.DraftId).OnDelete(DeleteBehavior.Restrict);
        review.HasOne<Patient>().WithMany().HasForeignKey(r => r.PatientId).OnDelete(DeleteBehavior.Restrict);
        review.HasOne<StaffUser>().WithMany().HasForeignKey(r => r.ClinicianId).OnDelete(DeleteBehavior.Restrict);
        review.HasAlternateKey(r => new { r.Id, r.PatientId });
        review.HasIndex(r => r.SessionId).IsUnique();
        review.HasIndex(r => new { r.DraftId, r.State });
        review.Property(r => r.SessionId).HasMaxLength(64);
        review.Property(r => r.State).HasMaxLength(16).IsConcurrencyToken();
        review.Property(r => r.ResultJson).HasColumnType("jsonb");
        review.Property(r => r.CreatedAt).HasDefaultValueSql("now()");
        review.Property(r => r.CompletedAt).ValueGeneratedOnAddOrUpdate();
        // Ambient listening (step 16): a conversation's review names its consent, which allows no other review.
        review.ToTable(table =>
        {
            table.HasCheckConstraint("ck_ai_note_reviews_mode", "mode IN ('dictation', 'ambient')");
            table.HasCheckConstraint("ck_ai_note_reviews_consent", "(mode = 'ambient') = (consent_id IS NOT NULL)");
            table.HasCheckConstraint("ck_ai_note_reviews_sensitive", "sensitive = (mode = 'ambient')");
        });
        review.Property(r => r.Mode).HasMaxLength(16).HasDefaultValue(AiReviewModes.Dictation);
        review.HasOne<AmbientConsent>().WithMany().HasForeignKey(r => new { r.ConsentId, r.PatientId })
            .HasPrincipalKey(c => new { c.Id, c.PatientId }).OnDelete(DeleteBehavior.Restrict);
        review.HasIndex(r => r.ConsentId).IsUnique();
    }

    public void Configure(EntityTypeBuilder<AiFieldProposal> proposal)
    {
        proposal.HasOne<AiNoteReview>().WithMany().HasForeignKey(p => new { p.ReviewId, p.PatientId })
            .HasPrincipalKey(r => new { r.Id, r.PatientId }).OnDelete(DeleteBehavior.Restrict);
        proposal.HasOne<StaffUser>().WithMany().HasForeignKey(p => p.ReviewerId).OnDelete(DeleteBehavior.Restrict);
        proposal.HasIndex(p => new { p.ReviewId, p.Field }).IsUnique();
        proposal.HasIndex(p => p.DecisionKey).IsUnique();
        proposal.Property(p => p.Field).HasMaxLength(16);
        proposal.Property(p => p.State).HasMaxLength(16).IsConcurrencyToken();
        proposal.Property(p => p.ReviewerId).ValueGeneratedOnAddOrUpdate();
        proposal.Property(p => p.ReviewedAt).ValueGeneratedOnAddOrUpdate();
    }

    // The expansion plan's step 15: the orders and allergies to add a dictation proposes, decided once
    // (db/policies/55-ai-review.sql holds what each decision needs).
    public void Configure(EntityTypeBuilder<AiChartProposal> proposal)
    {
        proposal.ToTable("ai_chart_proposals", table =>
        {
            table.HasCheckConstraint("ck_ai_chart_proposals_kind", $"kind IN ({Codes.List(AiChartProposalKinds.All)})");
            table.HasCheckConstraint("ck_ai_chart_proposals_state", $"state IN ({Codes.List(AiChartProposalStates.All)})");
            table.HasCheckConstraint("ck_ai_chart_proposals_ordinal", $"ordinal BETWEEN 0 AND {AiChartProposal.MaxPerReview - 1}");
            // Waiting with no decision; ordered with its order, an order's proposal only; recorded with its allergy, an
            // allergy's only; dismissed with neither; decided with who and when.
            table.HasCheckConstraint("ck_ai_chart_proposals_decision",
                $"(state = '{AiChartProposalStates.Pending}') = (decided_at IS NULL) AND (decided_at IS NULL) = (decided_by IS NULL) "
                + $"AND (state = '{AiChartProposalStates.Ordered}') = (order_id IS NOT NULL) "
                + $"AND (state = '{AiChartProposalStates.Recorded}') = (allergy_id IS NOT NULL) "
                + $"AND (order_id IS NULL OR kind = '{AiChartProposalKinds.Order}') "
                + $"AND (allergy_id IS NULL OR kind = '{AiChartProposalKinds.AllergyAddition}')");
        });

        proposal.HasOne<AiNoteReview>().WithMany().HasForeignKey(p => new { p.ReviewId, p.PatientId })
            .HasPrincipalKey(r => new { r.Id, r.PatientId }).OnDelete(DeleteBehavior.Restrict);
        proposal.HasOne<Order>().WithMany().HasForeignKey(p => p.OrderId).OnDelete(DeleteBehavior.Restrict);
        proposal.HasOne<AllergyIntolerance>().WithMany().HasForeignKey(p => p.AllergyId).OnDelete(DeleteBehavior.Restrict);
        proposal.HasOne<StaffUser>().WithMany().HasForeignKey(p => p.DecidedBy).OnDelete(DeleteBehavior.Restrict);
        // A proposal at each place of its review's result, once.
        proposal.HasIndex(p => new { p.ReviewId, p.Ordinal }).IsUnique();
        // An order, or an allergy, decides one proposal.
        proposal.HasIndex(p => p.OrderId).IsUnique();
        proposal.HasIndex(p => p.AllergyId).IsUnique();
        proposal.Property(p => p.Kind).HasMaxLength(16);
        proposal.Property(p => p.State).HasMaxLength(16).IsConcurrencyToken();
        proposal.Property(p => p.DecidedBy).ValueGeneratedOnAddOrUpdate();
        proposal.Property(p => p.DecidedAt).ValueGeneratedOnAddOrUpdate();
    }
}
