using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ehr.Data.Patients;

// Maps ehr.allergy_intolerances. EhrDbContext picks this up with ApplyConfigurationsFromAssembly. Check constraints hold
// the columns FHIR binds to a fixed list of codes to that list.
public sealed class AllergyIntoleranceConfiguration : IEntityTypeConfiguration<AllergyIntolerance>
{
    public void Configure(EntityTypeBuilder<AllergyIntolerance> allergy)
    {
        allergy.ToTable("allergy_intolerances", table =>
        {
            table.HasCheckConstraint("ck_allergy_intolerances_clinical_status", $"clinical_status IN ({Codes.List(AllergyClinicalStatus.All)})");
            table.HasCheckConstraint("ck_allergy_intolerances_category", $"category IN ({Codes.List(AllergyCategory.All)})");
            table.HasCheckConstraint("ck_allergy_intolerances_criticality", $"criticality IN ({Codes.List(AllergyCriticality.All)})");
        });

        allergy.Property(a => a.Code).HasMaxLength(20);
        allergy.Property(a => a.Display).HasMaxLength(200);
        allergy.Property(a => a.ClinicalStatus).HasMaxLength(20);
        allergy.Property(a => a.Category).HasMaxLength(20);
        allergy.Property(a => a.Criticality).HasMaxLength(20);
        allergy.Property(a => a.Reaction).HasMaxLength(200);
        // Read by patient, for the chart.
        allergy.HasOne<Patient>().WithMany().HasForeignKey(a => a.PatientId).OnDelete(DeleteBehavior.Restrict);
    }
}
