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

// A known interaction between two ingredients, by RxNorm code, the lower code first, as one source gives it: a pair
// can have a row from each source, and a warning names the source it came from. Three sources (step 8):
//   ddinter    DDInter 2.0, with its level. CC BY-NC-SA 4.0: an accepted exception to the project's free-software
//              rule, kept in its own folder (db/reference/ddinter) with its license and citation.
//   onc        The ONC high-priority list (Phansalkar et al., 2012), as Wikidata's "significant drug interaction"
//              statements record it. CC0. Every pair is "high-priority".
//   fda-label  A short hand-written list for important pairs neither covers, each citing the FDA label
//              (DailyMed, public domain) whose Drug Interactions section it paraphrases.
public class DrugInteraction
{
    public required string IngredientA { get; set; }

    public required string IngredientB { get; set; }

    // One of DrugInteractionSource.
    public string Source { get; set; } = DrugInteractionSource.DdInter;

    // One of DrugInteractionLevel.
    public required string Level { get; set; }

    // What the source says about the pair, in the project's own words: only the hand-written rows have one.
    public string? Detail { get; set; }

    // Where a reader can check it: the label's title and DailyMed address, or the paper.
    public string? Citation { get; set; }
}

public static class DrugInteractionSource
{
    public const string DdInter = "ddinter";

    public const string Onc = "onc";

    public const string FdaLabel = "fda-label";

    public static readonly string[] All = [DdInter, Onc, FdaLabel];
}

public static class DrugInteractionLevel
{
    public const string Major = "major";

    public const string Moderate = "moderate";

    public const string Minor = "minor";

    // The ONC list's own grade: pairs it says should always interrupt the prescriber.
    public const string HighPriority = "high-priority";

    public static readonly string[] All = [Major, Moderate, Minor, HighPriority];
}

// A vital sign's reference range for one age band (the expansion plan's step 9b): heart rate, respiratory rate and
// blood pressure differ by age, so a child's value is flagged against a child's range. Loaded from
// db/reference/35-vital-ranges.sql, which names the source of every row. A sign with no row here keeps the one range
// ehr.observation_codes gives it (temperature, oxygen saturation, body mass index).
public class VitalReferenceRange
{
    // The vital sign's LOINC code.
    public required string Code { get; set; }

    // One of AgeBand.
    public required string Band { get; set; }

    // The band's ages in whole months: from FromMonths, up to but not including ToMonths; null for no upper end.
    public int FromMonths { get; set; }

    public int? ToMonths { get; set; }

    // A value below Low or above High is outside the range. Either end may be missing, where no source gives one.
    public decimal? Low { get; set; }

    public decimal? High { get; set; }

    // Where the two numbers come from.
    public required string Source { get; set; }
}

// The ranges themselves (step 9b), once, for the seed and for a test that holds db/reference/35-vital-ranges.sql to
// them. The sources, what each one defines and how its figures were fitted to five bands are in
// docs/expansion/reference-data.md. A blood pressure source gives the value at or above which a reading needs
// attention; the High here is one below it, so "above High" says the same.
public static class VitalRanges
{
    const string Bonafide = "Bonafide CP et al., Pediatrics 2013;131(4):e1150 (hospitalized children), 1st and 99th percentiles, widest across the band's ages";
    const string Table6 = "Flynn JT et al., AAP Clinical Practice Guideline, Pediatrics 2017;140(3):e20171904, Table 6 screening values, highest across the band's ages and both sexes";
    const string Pals = "; low: PALS hypotension threshold (70 mmHg under 1 year, 70 + 2 x age to 10 years, 90 above), at the band's youngest age";
    const string AdultRates = "MedlinePlus (NIH), Vital signs, reviewed 2025-01-01";
    const string AdultBreathing = "StatPearls, Vital Sign Assessment (NIH Bookshelf NBK553213)";
    const string AdultPressure = "MedlinePlus (NIH), Vital signs (low); AHA/ACC adult categories, stage 1 from 130 or 80 (high)";

    public static readonly IReadOnlyList<(string Code, string Band, decimal? Low, decimal? High, string Source)> All =
    [
        ("8867-4", AgeBand.Infant, 91, 186, Bonafide), ("8867-4", AgeBand.YoungChild, 66, 173, Bonafide), ("8867-4", AgeBand.OlderChild, 56, 155, Bonafide),
        ("8867-4", AgeBand.Adolescent, 48, 138, Bonafide), ("8867-4", AgeBand.Adult, 60, 100, AdultRates),
        ("9279-1", AgeBand.Infant, 19, 76, Bonafide), ("9279-1", AgeBand.YoungChild, 14, 60, Bonafide), ("9279-1", AgeBand.OlderChild, 13, 46, Bonafide),
        ("9279-1", AgeBand.Adolescent, 11, 35, Bonafide), ("9279-1", AgeBand.Adult, 12, 20, AdultBreathing),
        ("8480-6", AgeBand.Infant, 70, null, "No source gives an upper value under 1 year" + Pals), ("8480-6", AgeBand.YoungChild, 72, 103, Table6 + Pals),
        ("8480-6", AgeBand.OlderChild, 82, 110, Table6 + Pals), ("8480-6", AgeBand.Adolescent, 90, 119, Table6 + Pals), ("8480-6", AgeBand.Adult, 90, 129, AdultPressure),
        ("8462-4", AgeBand.YoungChild, null, 63, Table6 + "; no source gives a lower value"), ("8462-4", AgeBand.OlderChild, null, 73, Table6 + "; no source gives a lower value"),
        ("8462-4", AgeBand.Adolescent, null, 79, Table6 + "; no source gives a lower value"), ("8462-4", AgeBand.Adult, 60, 79, AdultPressure),
    ];

    // The range for a sign at an age in months, or null when the sign has none for that age (it keeps its one range,
    // or has none, as a diastolic pressure under 1 year).
    public static (decimal? Low, decimal? High)? For(string code, int months)
    {
        var band = AgeBand.Of(months);
        foreach (var range in All)
        {
            if (range.Code == code && range.Band == band)
            {
                return (range.Low, range.High);
            }
        }

        return null;
    }

    public static bool IsBanded(string code) => All.Any(range => range.Code == code);
}

public static class AgeBand
{
    public const string Infant = "infant";

    public const string YoungChild = "young-child";

    public const string OlderChild = "older-child";

    public const string Adolescent = "adolescent";

    public const string Adult = "adult";

    public static readonly string[] All = [Infant, YoungChild, OlderChild, Adolescent, Adult];

    // The bands' ages in whole months, as Tahseen set them (2026-11-19): under 1 year, 1 to 5, 6 to 11, 12 to 17, and
    // 18 and over.
    public static readonly IReadOnlyList<(string Band, int FromMonths, int? ToMonths)> Ages =
        [(Infant, 0, 12), (YoungChild, 12, 72), (OlderChild, 72, 144), (Adolescent, 144, 216), (Adult, 216, null)];

    // A person's age in whole months on a day.
    public static int Months(DateOnly birth, DateOnly on) =>
        Math.Max(0, ((on.Year - birth.Year) * 12) + on.Month - birth.Month - (on.Day < birth.Day ? 1 : 0));

    public static string Of(int months) => Ages.First(age => months >= age.FromMonths && (age.ToMonths is null || months < age.ToMonths)).Band;
}

public sealed class VitalReferenceRangeConfiguration : IEntityTypeConfiguration<VitalReferenceRange>
{
    public void Configure(EntityTypeBuilder<VitalReferenceRange> range)
    {
        range.ToTable("vital_reference_ranges", table =>
        {
            table.HasCheckConstraint("ck_vital_reference_ranges_band", $"band IN ({Codes.List(AgeBand.All)})");
            table.HasCheckConstraint("ck_vital_reference_ranges_range", "low <= high AND (low IS NOT NULL OR high IS NOT NULL)");
            table.HasCheckConstraint("ck_vital_reference_ranges_ages", "from_months >= 0 AND (to_months IS NULL OR to_months > from_months)");
        });
        range.HasKey(r => new { r.Code, r.Band });
        range.Property(r => r.Code).HasMaxLength(20);
        range.Property(r => r.Band).HasMaxLength(20);
        range.Property(r => r.Low).HasPrecision(12, 4);
        range.Property(r => r.High).HasPrecision(12, 4);
        range.Property(r => r.Source).HasMaxLength(500);
        range.HasOne<ObservationCode>().WithMany().HasForeignKey(r => r.Code).OnDelete(DeleteBehavior.Restrict);
    }
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
            table.HasCheckConstraint("ck_drug_interactions_source", $"source IN ({Codes.List(DrugInteractionSource.All)})");
        });
        interaction.HasKey(i => new { i.IngredientA, i.IngredientB, i.Source });
        interaction.Property(i => i.IngredientA).HasMaxLength(20);
        interaction.Property(i => i.IngredientB).HasMaxLength(20);
        interaction.Property(i => i.Source).HasMaxLength(20).HasDefaultValue(DrugInteractionSource.DdInter);
        interaction.Property(i => i.Level).HasMaxLength(20);
        interaction.Property(i => i.Detail).HasMaxLength(1000);
        interaction.Property(i => i.Citation).HasMaxLength(500);
        // Looked up from either side.
        interaction.HasIndex(i => i.IngredientB);
    }
}
