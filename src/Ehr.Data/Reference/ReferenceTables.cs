using Ehr.Data.Orders;
using Ehr.Data.Patients;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ehr.Data.Reference;

// Reference data beside the clinical codes (Ehr.Data.Patients.ClinicalCode): none of it is about a patient. Grants
// only, read by both apps, changed by nothing at runtime: the rows are db/reference's generated SQL, written from
// pinned source files by tools/reference/import.cs (docs/expansion/reference-data.md has each source and license).

// One thing that can be measured or asked: a lab result, a vital sign or a survey score, by its LOINC code, with the
// UCUM unit it is recorded in and, where this clinic has one, the range it calls normal.
public class ObservationCode
{
    // The LOINC code.
    public required string Code { get; set; }

    // LOINC's long common name.
    public required string Display { get; set; }

    // One of ObservationCodeKind.
    public required string Kind { get; set; }

    // The UCUM unit, such as "mg/dL" or "mm[Hg]".
    public string? Unit { get; set; }

    // This clinic's reference range, in that unit; either end may be missing. Not LOINC's: RangeSource says whose.
    public decimal? ReferenceLow { get; set; }

    public decimal? ReferenceHigh { get; set; }

    public string? RangeSource { get; set; }

    // LOINC's EXTERNAL_COPYRIGHT_NOTICE for the term, when a third party holds rights in it.
    public string? Copyright { get; set; }
}

public static class ObservationCodeKind
{
    public const string Lab = "lab";

    public const string Vital = "vital";

    public const string Survey = "survey";

    public static readonly string[] All = [Lab, Vital, Survey];
}

// A result an orderable produces: a complete blood count gives a hemoglobin, a white cell count and a platelet count.
// Curated for the clinic's common lab orders, so a result can be attached to its order and a simulated lab knows what
// to send back.
public class OrderResultCode
{
    public required string CatalogCode { get; set; }

    public required string ObservationCode { get; set; }
}

// A questionnaire a patient fills in before a visit: PHQ-9 and GAD-7, each by its LOINC panel code. The definition is
// one JSON document: the items with their LOINC codes and texts, the answers with their scores, and the score's
// severity bands.
public class Questionnaire
{
    public required string Code { get; set; }

    public required string Title { get; set; }

    public required string Definition { get; set; }

    // The notice its owner asks for, from LOINC's EXTERNAL_COPYRIGHT_NOTICE.
    public string? Copyright { get; set; }
}

// An ingredient of a drug product, both by RxNorm code: what a medication is made of, for checking it against
// allergies and other medications.
public class DrugIngredient
{
    public required string ProductCode { get; set; }

    public required string IngredientCode { get; set; }
}

// A class an ingredient belongs to: an FDA Established Pharmacologic Class, by its name ("Penicillin-class
// Antibacterial"), from the FDA's indexing files.
public class DrugClass
{
    public required string IngredientCode { get; set; }

    public required string ClassName { get; set; }
}

// Two classes whose members can cross-react in an allergic patient, with what is known and where it comes from. A
// class always cross-reacts with itself, which needs no row. Curated by this project: each row cites its source.
public class DrugClassCrossReaction
{
    public required string ClassA { get; set; }

    public required string ClassB { get; set; }

    public required string Note { get; set; }

    public required string Source { get; set; }
}

// A known interaction between two ingredients, by RxNorm code, the lower code first, with its level. From DDInter 2.0,
// which is licensed CC BY-NC-SA 4.0: an accepted exception to the project's free-software rule, kept in its own folder
// (db/reference/ddinter) with its license and citation.
public class DrugInteraction
{
    public required string IngredientA { get; set; }

    public required string IngredientB { get; set; }

    // One of DrugInteractionLevel.
    public required string Level { get; set; }
}

public static class DrugInteractionLevel
{
    public const string Major = "major";

    public const string Moderate = "moderate";

    public const string Minor = "minor";

    public static readonly string[] All = [Major, Moderate, Minor];
}

// Maps the tables above. EhrDbContext picks these up with ApplyConfigurationsFromAssembly.
public sealed class ObservationCodeConfiguration : IEntityTypeConfiguration<ObservationCode>
{
    public void Configure(EntityTypeBuilder<ObservationCode> code)
    {
        code.ToTable("observation_codes", table =>
            table.HasCheckConstraint("ck_observation_codes_kind", $"kind IN ({Codes.List(ObservationCodeKind.All)})"));
        code.HasKey(c => c.Code);
        code.Property(c => c.Code).HasMaxLength(20);
        code.Property(c => c.Display).HasMaxLength(300);
        code.Property(c => c.Kind).HasMaxLength(20);
        code.Property(c => c.Unit).HasMaxLength(40);
        code.Property(c => c.RangeSource).HasMaxLength(300);
    }
}

public sealed class OrderResultCodeConfiguration : IEntityTypeConfiguration<OrderResultCode>
{
    public void Configure(EntityTypeBuilder<OrderResultCode> result)
    {
        result.ToTable("order_result_codes");
        result.HasKey(r => new { r.CatalogCode, r.ObservationCode });
        result.Property(r => r.CatalogCode).HasMaxLength(20);
        result.Property(r => r.ObservationCode).HasMaxLength(20);
        result.HasOne<OrderCatalogItem>().WithMany().HasForeignKey(r => r.CatalogCode).OnDelete(DeleteBehavior.Cascade);
        result.HasOne<ObservationCode>().WithMany().HasForeignKey(r => r.ObservationCode).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class QuestionnaireConfiguration : IEntityTypeConfiguration<Questionnaire>
{
    public void Configure(EntityTypeBuilder<Questionnaire> questionnaire)
    {
        questionnaire.ToTable("questionnaires", table =>
            table.HasCheckConstraint("ck_questionnaires_definition", "jsonb_typeof(definition) = 'object'"));
        questionnaire.HasKey(q => q.Code);
        questionnaire.Property(q => q.Code).HasMaxLength(20);
        questionnaire.Property(q => q.Title).HasMaxLength(200);
        questionnaire.Property(q => q.Definition).HasColumnType("jsonb");
    }
}

public sealed class DrugIngredientConfiguration : IEntityTypeConfiguration<DrugIngredient>
{
    public void Configure(EntityTypeBuilder<DrugIngredient> ingredient)
    {
        ingredient.ToTable("drug_ingredients");
        ingredient.HasKey(i => new { i.ProductCode, i.IngredientCode });
        ingredient.Property(i => i.ProductCode).HasMaxLength(20);
        ingredient.Property(i => i.IngredientCode).HasMaxLength(20);
    }
}

public sealed class DrugClassConfiguration : IEntityTypeConfiguration<DrugClass>
{
    public void Configure(EntityTypeBuilder<DrugClass> drugClass)
    {
        drugClass.ToTable("drug_classes");
        drugClass.HasKey(c => new { c.IngredientCode, c.ClassName });
        drugClass.Property(c => c.IngredientCode).HasMaxLength(20);
        drugClass.Property(c => c.ClassName).HasMaxLength(200);
        // Every ingredient of a class, for an allergy recorded against one member.
        drugClass.HasIndex(c => c.ClassName);
    }
}

public sealed class DrugClassCrossReactionConfiguration : IEntityTypeConfiguration<DrugClassCrossReaction>
{
    public void Configure(EntityTypeBuilder<DrugClassCrossReaction> reaction)
    {
        reaction.ToTable("drug_class_cross_reactions");
        reaction.HasKey(r => new { r.ClassA, r.ClassB });
        reaction.Property(r => r.ClassA).HasMaxLength(200);
        reaction.Property(r => r.ClassB).HasMaxLength(200);
        reaction.Property(r => r.Note).HasMaxLength(500);
        reaction.Property(r => r.Source).HasMaxLength(500);
    }
}

public sealed class DrugInteractionConfiguration : IEntityTypeConfiguration<DrugInteraction>
{
    public void Configure(EntityTypeBuilder<DrugInteraction> interaction)
    {
        interaction.ToTable("drug_interactions", table =>
        {
            table.HasCheckConstraint("ck_drug_interactions_level", $"level IN ({Codes.List(DrugInteractionLevel.All)})");
            table.HasCheckConstraint("ck_drug_interactions_order", "ingredient_a < ingredient_b");
        });
        interaction.HasKey(i => new { i.IngredientA, i.IngredientB });
        interaction.Property(i => i.IngredientA).HasMaxLength(20);
        interaction.Property(i => i.IngredientB).HasMaxLength(20);
        interaction.Property(i => i.Level).HasMaxLength(20);
        // Looked up from either side.
        interaction.HasIndex(i => i.IngredientB);
    }
}
