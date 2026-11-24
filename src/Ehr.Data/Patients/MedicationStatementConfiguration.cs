using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ehr.Data.Patients;

// Maps ehr.medication_statements. EhrDbContext picks this up with ApplyConfigurationsFromAssembly.
public sealed class MedicationStatementConfiguration : IEntityTypeConfiguration<MedicationStatement>
{
    public void Configure(EntityTypeBuilder<MedicationStatement> medication)
    {
        medication.ToTable("medication_statements", table =>
            table.HasCheckConstraint("ck_medication_statements_status", $"status IN ({Codes.List(MedicationStatementStatus.All)})"));

        medication.Property(m => m.Code).HasMaxLength(20);
        // As long as a reference code's name can be (ClinicalCode).
        medication.Property(m => m.Display).HasMaxLength(300);
        medication.Property(m => m.Dosage).HasMaxLength(200);
        medication.Property(m => m.Status).HasMaxLength(20);
        // Read by patient, for the chart.
        medication.HasOne<Patient>().WithMany().HasForeignKey(m => m.PatientId).OnDelete(DeleteBehavior.Restrict);
    }
}
