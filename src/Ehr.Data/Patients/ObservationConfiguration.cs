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
            table.HasCheckConstraint("ck_observations_reference_kind", $"reference_kind IN ({Codes.List(ReferenceKind.All)})");
            table.HasCheckConstraint("ck_observations_category", $"category IN ({Codes.List(ObservationCategory.All)})");
            // A reason, a person and a time belong to an entry in error, and to nothing else.
            table.HasCheckConstraint("ck_observations_entered_in_error",
                $"(entered_in_error_reason IS NULL AND entered_in_error_by IS NULL AND entered_in_error_at IS NULL) OR status = '{ObservationStatus.EnteredInError}'");
            // A reading belongs to a blood pressure value, and every blood pressure value has one (step 9c).
            table.HasCheckConstraint("ck_observations_reading",
                $"(reading_id IS NOT NULL) = (category = '{ObservationCategory.VitalSigns}' AND code IS NOT NULL AND code IN ({Codes.List(VitalSign.BloodPressure)}))");
        });

        observation.Property(o => o.Code).HasMaxLength(20);
        observation.Property(o => o.Category).HasMaxLength(20).HasDefaultValue(ObservationCategory.Laboratory);
        // A visit's vital signs, read with the visit.
        observation.HasOne<Encounter>().WithMany().HasForeignKey(o => o.EncounterId).OnDelete(DeleteBehavior.Restrict);
        // A patient's values of one test or vital sign over time: a trend, and the latest of each.
        observation.HasIndex(o => new { o.PatientId, o.Category, o.Code, o.EffectiveAt });
        // One standing value of a vital sign for a patient at a moment, whatever the visit (step 9c): a form sent twice
        // records once, and a different value waits until the one standing is marked entered in error. An entry in
        // error doesn't count (step 9b): the correct value is recorded for the same moment.
        observation.HasIndex(o => new { o.PatientId, o.Code, o.EffectiveAt })
            .IsUnique().HasFilter($"category = 'vital-signs' AND status <> '{ObservationStatus.EnteredInError}'")
            .HasDatabaseName("ux_observations_vital_sign_once");
        // A blood pressure reading has one systolic and one diastolic, standing or not (step 9c); also how the check
        // that a reading is whole finds its values (db/policies/75-results.sql).
        observation.HasIndex(o => new { o.ReadingId, o.Code })
            .IsUnique().HasFilter("reading_id IS NOT NULL")
            .HasDatabaseName("ux_observations_reading");
        observation.Property(o => o.EnteredInErrorReason).HasMaxLength(500);
        observation.HasOne<Accounts.StaffUser>().WithMany().HasForeignKey(o => o.EnteredInErrorBy).OnDelete(DeleteBehavior.Restrict);
        observation.Property(o => o.Display).HasMaxLength(200);
        observation.Property(o => o.Status).HasMaxLength(20);
        observation.Property(o => o.Value).HasPrecision(12, 4);
        observation.Property(o => o.Unit).HasMaxLength(20);
        observation.Property(o => o.ReferenceLow).HasPrecision(12, 4);
        observation.Property(o => o.ReferenceHigh).HasPrecision(12, 4);
        observation.Property(o => o.ReferenceKind).HasMaxLength(20).HasDefaultValue(ReferenceKind.Normal);
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
