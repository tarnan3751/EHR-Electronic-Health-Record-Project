using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ehr.Data.Patients;

// One code a clinician can pick for a chart entry: a diagnosis (ICD-10-CM), a medication (an RxNorm clinical drug) or
// an allergen (an RxNorm ingredient, or an FDA UNII substance for foods and the like). Not PHI: reference data, grants
// only, read by both apps. Nothing at runtime changes it: its rows are db/reference/*.sql, generated from pinned source
// files by tools/reference/import.cs and applied after db/policies (docs/expansion/reference-data.md).
public class ClinicalCode
{
    // One of ClinicalCodeKind.
    public required string Kind { get; set; }

    // One of ClinicalCodeSystem.
    public required string System { get; set; }

    public required string Code { get; set; }

    // The source's own name for it.
    public required string Display { get; set; }

    // The name in lower case, kept by the database, for the search as a clinician types.
    public string DisplayKey { get; set; } = "";

    // For an allergen, one of AllergyCategory.
    public string? Category { get; set; }

    // For a diagnosis: its code is in a sensitive range (ehr.icd10cm_is_sensitive, db/policies/65-chart-lists.sql).
    public bool Sensitive { get; set; }
}

public static class ClinicalCodeKind
{
    public const string Condition = "condition";

    public const string Medication = "medication";

    public const string Allergen = "allergen";

    public static readonly string[] All = [Condition, Medication, Allergen];
}

public static class ClinicalCodeSystem
{
    public const string Icd10Cm = "icd-10-cm";

    public const string RxNorm = "rxnorm";

    public const string Unii = "unii";

    // Not a system of the clinical codes table: lab tests and results, in the order catalog and the observation codes.
    public const string Loinc = "loinc";

    public static readonly string[] All = [Icd10Cm, RxNorm, Unii];
}

// Maps ehr.clinical_codes. EhrDbContext picks this up with ApplyConfigurationsFromAssembly.
public sealed class ClinicalCodeConfiguration : IEntityTypeConfiguration<ClinicalCode>
{
    public void Configure(EntityTypeBuilder<ClinicalCode> code)
    {
        code.ToTable("clinical_codes", table =>
        {
            table.HasCheckConstraint("ck_clinical_codes_kind", $"kind IN ({Codes.List(ClinicalCodeKind.All)})");
            table.HasCheckConstraint("ck_clinical_codes_system", $"system IN ({Codes.List(ClinicalCodeSystem.All)})");
            table.HasCheckConstraint("ck_clinical_codes_category", $"category IN ({Codes.List(AllergyCategory.All)})");
        });

        code.HasKey(c => new { c.Kind, c.System, c.Code });
        code.Property(c => c.Kind).HasMaxLength(20);
        code.Property(c => c.System).HasMaxLength(20);
        code.Property(c => c.Code).HasMaxLength(20);
        code.Property(c => c.Display).HasMaxLength(300);
        code.Property(c => c.DisplayKey).HasMaxLength(300).HasComputedColumnSql("lower(display)", stored: true);
        code.Property(c => c.Category).HasMaxLength(20);
        // The search: a kind's names that start with what was typed, and the form's check of a name picked from it;
        // a diagnosis by the start of its code; and names with a word that starts with what was typed ("diabetes"
        // finds "Type 2 diabetes mellitus ..."), through PostgreSQL's own text search, which needs no extension: a
        // hundred thousand names are too many to scan on every keystroke.
        code.HasIndex(c => new { c.Kind, c.DisplayKey }).HasOperators(null!, "text_pattern_ops");
        code.HasIndex(c => new { c.Kind, c.Code }).HasOperators(null!, "text_pattern_ops");
        code.HasIndex(c => c.Display).HasMethod("GIN").IsTsVectorExpressionIndex("simple");
    }
}
