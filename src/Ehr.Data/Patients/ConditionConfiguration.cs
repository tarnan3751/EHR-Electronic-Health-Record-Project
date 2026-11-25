using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ehr.Data.Patients;

// Maps ehr.conditions. EhrDbContext picks this up with ApplyConfigurationsFromAssembly.
public sealed class ConditionConfiguration : IEntityTypeConfiguration<Condition>
{
    public void Configure(EntityTypeBuilder<Condition> condition)
    {
        condition.ToTable("conditions", table =>
            table.HasCheckConstraint("ck_conditions_clinical_status", $"clinical_status IN ({Codes.List(ConditionClinicalStatus.All)})"));

        condition.Property(c => c.Code).HasMaxLength(20);
        // As long as a reference code's name can be (ClinicalCode).
        condition.Property(c => c.Display).HasMaxLength(300);
        condition.Property(c => c.ClinicalStatus).HasMaxLength(20);
        // Read by patient, for the chart.
        condition.HasOne<Patient>().WithMany().HasForeignKey(c => c.PatientId).OnDelete(DeleteBehavior.Restrict);
    }
}
