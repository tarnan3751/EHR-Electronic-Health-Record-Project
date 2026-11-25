using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ehr.Data.Patients;

// Maps ehr.allergy_intolerances. EhrDbContext picks this up with ApplyConfigurationsFromAssembly. Check constraints hold
// the columns FHIR binds to a fixed list of codes to that list.
public sealed class AllergyIntoleranceConfiguration : IEntityTypeConfiguration<AllergyIntolerance>
{
    public const string OneNoKnownAllergyIndex = "ix_allergy_intolerances_one_no_known_allergy";

    public void Configure(EntityTypeBuilder<AllergyIntolerance> allergy)
    {
        allergy.ToTable("allergy_intolerances", table =>
        {
            table.HasCheckConstraint("ck_allergy_intolerances_clinical_status", $"clinical_status IN ({Codes.List(AllergyClinicalStatus.All)})");
            table.HasCheckConstraint("ck_allergy_intolerances_category", $"category IN ({Codes.List(AllergyCategory.All)})");
            table.HasCheckConstraint("ck_allergy_intolerances_criticality", $"criticality IN ({Codes.List(AllergyCriticality.All)})");
            table.HasCheckConstraint("ck_allergy_intolerances_severity", $"severity IN ({Codes.List(AllergySeverity.All)})");
        });

        allergy.Property(a => a.Code).HasMaxLength(20);
        // As long as a reference code's name can be (ClinicalCode).
        allergy.Property(a => a.Display).HasMaxLength(300);
        allergy.Property(a => a.ClinicalStatus).HasMaxLength(20);
        allergy.Property(a => a.Category).HasMaxLength(20);
        allergy.Property(a => a.Criticality).HasMaxLength(20);
        allergy.Property(a => a.CodeSystem).HasMaxLength(20);
        allergy.Property(a => a.Reaction).HasMaxLength(200);
        allergy.Property(a => a.Severity).HasMaxLength(20);
        // Read by patient, for the chart.
        allergy.HasOne<Patient>().WithMany().HasForeignKey(a => a.PatientId).OnDelete(DeleteBehavior.Restrict);
        allergy.HasIndex(a => a.PatientId);
        // At most one active "No known allergies" per patient: a second click, or a second tab, can't record another.
        // db/policies/65-chart-lists.sql refuses one first, with this index's name, so the app can tell it apart.
        allergy.HasIndex(a => a.PatientId, OneNoKnownAllergyIndex).IsUnique().HasDatabaseName(OneNoKnownAllergyIndex)
            .HasFilter($"code = '{AllergyIntolerance.NoKnownAllergy}' AND clinical_status = '{AllergyClinicalStatus.Active}'");
    }
}
