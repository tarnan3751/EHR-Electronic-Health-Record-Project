using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ehr.Data.Patients;

// Maps ehr.patient_enrollments. EhrDbContext picks this up with ApplyConfigurationsFromAssembly.
public sealed class PatientEnrollmentConfiguration : IEntityTypeConfiguration<PatientEnrollment>
{
    public void Configure(EntityTypeBuilder<PatientEnrollment> enrollment)
    {
        enrollment.ToTable("patient_enrollments");
        enrollment.Property(e => e.CodeHash).HasMaxLength(64);
        // Registration finds the code by its hash; two codes never share one.
        enrollment.HasIndex(e => e.CodeHash).IsUnique();
        enrollment.HasIndex(e => e.PatientId);
        enrollment.HasOne<Patient>().WithMany().HasForeignKey(e => e.PatientId).OnDelete(DeleteBehavior.Restrict);
    }
}
