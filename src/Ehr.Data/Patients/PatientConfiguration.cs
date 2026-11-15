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

        // The search (PhiReader). Row-level security is forced on this table, so a condition can run before the policy,
        // and so be answered by an index, only if it is leakproof, which ILIKE and lower() aren't and plain comparisons
        // are. So each name is also kept lower-cased by the database, in the C collation, which sorts by code point: a
        // name prefix is then a range of these keys, which their indexes answer. lower() runs under the name's own
        // collation, and only the result is "C". The search lower-cases what was typed with the same lower() (NameKeys),
        // so the two always agree: under the official image's en_US.utf8 for any letter, and in a cluster initialised
        // with --locale=C for ASCII only, on both sides alike. Stored generated columns, which EF Core reads back after
        // an insert or update; the patients' SELECT policy lets the clinicians who may write them.
        patient.Property(p => p.GivenNameKey).HasComputedColumnSql("lower(given_name)", stored: true).UseCollation("C");
        patient.Property(p => p.FamilyNameKey).HasComputedColumnSql("lower(family_name)", stored: true).UseCollation("C");
        patient.HasIndex(p => p.GivenNameKey);
        patient.HasIndex(p => p.FamilyNameKey);
        patient.HasIndex(p => p.BirthDate);
    }
}
