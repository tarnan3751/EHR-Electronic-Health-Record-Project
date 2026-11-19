using Ehr.Data.Orders;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ehr.Data.Patients;

// Maps ehr.observations. EhrDbContext picks this up with ApplyConfigurationsFromAssembly.
public sealed class ObservationConfiguration : IEntityTypeConfiguration<Observation>
{
    public void Configure(EntityTypeBuilder<Observation> observation)
    {
        observation.ToTable("observations", table =>
        {
            table.HasCheckConstraint("ck_observations_status", $"status IN ({Codes.List(ObservationStatus.All)})");
            table.HasCheckConstraint("ck_observations_reference_range", "reference_low <= reference_high");
            table.HasCheckConstraint("ck_observations_category", $"category IN ({Codes.List(ObservationCategory.All)})");
        });

        observation.Property(o => o.Code).HasMaxLength(20);
        observation.Property(o => o.Category).HasMaxLength(20).HasDefaultValue(ObservationCategory.Laboratory);
        // A visit's vital signs, read with the visit.
        observation.HasOne<Encounter>().WithMany().HasForeignKey(o => o.EncounterId).OnDelete(DeleteBehavior.Restrict);
        // A patient's values of one test or vital sign over time: a trend, and the latest of each.
        observation.HasIndex(o => new { o.PatientId, o.Category, o.Code, o.EffectiveAt });
        observation.Property(o => o.Display).HasMaxLength(200);
        observation.Property(o => o.Status).HasMaxLength(20);
        observation.Property(o => o.Value).HasPrecision(12, 4);
        observation.Property(o => o.Unit).HasMaxLength(20);
        observation.Property(o => o.ReferenceLow).HasPrecision(12, 4);
        observation.Property(o => o.ReferenceHigh).HasPrecision(12, 4);
        observation.Property(o => o.Note).HasMaxLength(500);
        // Read by patient, newest first, for the portal's results page.
        observation.HasOne<Patient>().WithMany().HasForeignKey(o => o.PatientId).OnDelete(DeleteBehavior.Restrict);
        observation.HasIndex(o => new { o.PatientId, o.EffectiveAt });
        // Keyed on the patient too, so a result can't answer another patient's order.
        observation.HasOne<Order>().WithMany()
            .HasForeignKey(o => new { o.OrderId, o.PatientId }).HasPrincipalKey(o => new { o.Id, o.PatientId })
            .OnDelete(DeleteBehavior.Restrict);
        // One result for each test of an order: filing it again is refused, not doubled.
        observation.HasIndex(o => new { o.OrderId, o.Code }).IsUnique().HasFilter("order_id IS NOT NULL AND code IS NOT NULL");
    }
}
