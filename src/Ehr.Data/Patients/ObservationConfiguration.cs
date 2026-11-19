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
            // A reason, a person and a time belong to an entry in error, and to nothing else.
            table.HasCheckConstraint("ck_observations_entered_in_error",
                $"(entered_in_error_reason IS NULL AND entered_in_error_by IS NULL AND entered_in_error_at IS NULL) OR status = '{ObservationStatus.EnteredInError}'");
        });

        observation.Property(o => o.Code).HasMaxLength(20);
        observation.Property(o => o.Category).HasMaxLength(20).HasDefaultValue(ObservationCategory.Laboratory);
        // A visit's vital signs, read with the visit.
        observation.HasOne<Encounter>().WithMany().HasForeignKey(o => o.EncounterId).OnDelete(DeleteBehavior.Restrict);
        // A patient's values of one test or vital sign over time: a trend, and the latest of each.
        observation.HasIndex(o => new { o.PatientId, o.Category, o.Code, o.EffectiveAt });
        // One value of a vital sign for a patient at a moment (and a visit): a form sent twice records once. A
        // visit's absence counts as a value, so two with no visit collide too. An entry in error doesn't count
        // (step 9b): the correct value is recorded for the same moment.
        observation.HasIndex(o => new { o.PatientId, o.Code, o.EffectiveAt, o.EncounterId })
            .IsUnique().AreNullsDistinct(false).HasFilter($"category = 'vital-signs' AND status <> '{ObservationStatus.EnteredInError}'")
            .HasDatabaseName("ux_observations_vital_sign_once");
        observation.Property(o => o.EnteredInErrorReason).HasMaxLength(500);
        observation.HasOne<Accounts.StaffUser>().WithMany().HasForeignKey(o => o.EnteredInErrorBy).OnDelete(DeleteBehavior.Restrict);
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
