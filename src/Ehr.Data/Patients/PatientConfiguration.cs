using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ehr.Data.Patients;

// Maps ehr.patients. EhrDbContext picks this up with ApplyConfigurationsFromAssembly.
public sealed class PatientConfiguration : IEntityTypeConfiguration<Patient>
{
    public void Configure(EntityTypeBuilder<Patient> patient)
    {
        // Only FHIR's administrative gender codes, enforced where the data lives.
        var genders = string.Join(", ", AdministrativeGender.All.Select(gender => $"'{gender}'"));
        patient.ToTable("patients", table => table.HasCheckConstraint("ck_patients_gender", $"gender IN ({genders})"));

        patient.Property(p => p.Mrn).HasMaxLength(20);
        patient.HasIndex(p => p.Mrn).IsUnique();
        patient.Property(p => p.GivenName).HasMaxLength(100);
        patient.Property(p => p.FamilyName).HasMaxLength(100);
        patient.Property(p => p.Gender).HasMaxLength(10);
        patient.Property(p => p.Version).IsConcurrencyToken();
    }
}
