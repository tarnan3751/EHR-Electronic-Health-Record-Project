using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ehr.Data.Patients;

// Maps ehr.encounters. EhrDbContext picks this up with ApplyConfigurationsFromAssembly. The class has no check
// constraint: FHIR lets its list of codes grow.
public sealed class EncounterConfiguration : IEntityTypeConfiguration<Encounter>
{
    public void Configure(EntityTypeBuilder<Encounter> encounter)
    {
        encounter.ToTable("encounters", table =>
        {
            table.HasCheckConstraint("ck_encounters_status", $"status IN ({Codes.List(EncounterStatus.All)})");
            table.HasCheckConstraint("ck_encounters_vital_signs", "systolic_mm_hg > 0 AND diastolic_mm_hg > 0 AND heart_rate_per_minute > 0");
        });

        encounter.Property(e => e.Status).HasMaxLength(20);
        encounter.Property(e => e.ClassCode).HasMaxLength(10);
        encounter.Property(e => e.Reason).HasMaxLength(200);
        // Read by patient, for the chart.
        encounter.HasOne<Patient>().WithMany().HasForeignKey(e => e.PatientId).OnDelete(DeleteBehavior.Restrict);
    }
}
