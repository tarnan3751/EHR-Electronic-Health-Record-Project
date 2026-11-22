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

// A questionnaire a patient fills in before a visit: PHQ-9 and GAD-7, each by its LOINC panel code, worded as their
// authors' forms are (the expansion plan's step 13e). Its rows are hand-written in db/reference/36-questionnaires.sql,
// which describes the definition's shape and cites each source.
public class Questionnaire
{
    public required string Code { get; set; }

    public required string Title { get; set; }

    // One JSON document: the prompt, the questions with their LOINC codes and texts, the lists of answers with their
    // scores, the total, its severity bands, the score that needs follow-up, and the authors' attribution line.
    public required string Definition { get; set; }

    // The notice its owner asks for, from LOINC's EXTERNAL_COPYRIGHT_NOTICE.
    public string? Copyright { get; set; }
}

// The notices a questionnaire's alert question may name (its definition's help): the portal words each (FormNotices in
// the web app) and shows it with an answer to that question that needs review. A definition naming any other fails
// QuestionnaireDefinitionTests, and FormNotices lacking words for one fails the web tests.
public static class QuestionnaireHelp
{
    // Thoughts of self-harm: what to do now, and the crisis line.
    public const string SelfHarm = "self-harm";

    public static readonly string[] All = [SelfHarm];
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

// A vital sign's reference range for one age, and for a child's blood pressure one sex (the expansion plan's steps 9b
// and 9d): heart rate, respiratory rate and blood pressure differ by age, so a child's value is flagged against a
// child's range. Loaded from db/reference/35-vital-ranges.sql, which names the source of every row. A sign with no row
// here keeps the one range ehr.observation_codes gives it (temperature, oxygen saturation, body mass index).
public class VitalReferenceRange
{
    // The vital sign's LOINC code.
    public required string Code { get; set; }

    // One of VitalRangeSex: the sex the row is for, or any.
    public string Sex { get; set; } = VitalRangeSex.Any;

    // The ages in whole months: from FromMonths, up to but not including ToMonths; null for no upper end.
    public int FromMonths { get; set; }

    public int? ToMonths { get; set; }

    // A normal range: a value below Low or above High is outside it, and either end may be missing where no source
    // gives one. A screening value (Kind ReferenceKind.Screening, a child's blood pressure): High only, and a value at
    // or above it needs follow-up.
    public decimal? Low { get; set; }

    public decimal? High { get; set; }

    // One of ReferenceKind.
    public string Kind { get; set; } = ReferenceKind.Normal;

    // Where the numbers come from.
    public required string Source { get; set; }
}

// The sexes a range row is for (step 9d): a child's blood pressure differs by sex; every other row is for any.
public static class VitalRangeSex
{
    public const string Any = "any";

    public static readonly string[] All = [AdministrativeGender.Male, AdministrativeGender.Female, Any];
}

// The ranges themselves (steps 9b and 9d), once, for the seed and for a test that holds db/reference/35-vital-ranges.sql
// to them. Children's heart and respiratory rates are the 1st and 99th centiles of Fleming et al.'s 13 age groups, and
// a child's blood pressure the AAP guideline's Table 6 by year of age and sex, as printed: values at or above which a
// reading needs follow-up. The sources, the pages each value came from and what was decided are in
// docs/expansion/reference-data.md.
public static class VitalRanges
{
    const string Breathing = "Fleming S et al., Lancet 2011;377:1011-18, Web Appendix, Web Table 4 (respiratory rate), 1st and 99th centiles, page 12";
    const string HeartRates = "Fleming S et al., Lancet 2011;377:1011-18, Web Appendix, Web Table 5 (heart rate), 1st and 99th centiles, page 12";
    const string Table6 = "Flynn JT et al., Pediatrics 2017;140(3):e20171904, Table 6 (Screening BP Values Requiring Further Evaluation), page 15";
    const string AdultRates = "MedlinePlus (NIH), Vital signs, reviewed 2025-01-01";
    const string AdultBreathing = "StatPearls, Vital Sign Assessment (NIH Bookshelf NBK553213)";
    const string AdultPressure = "MedlinePlus (NIH), Vital signs (low); AHA/ACC adult categories, stage 1 from 130 or 80 (high)";

    // Fleming's 13 age groups, in whole months: 0-3, 3-6, 6-9, 9-12, 12-18 and 18-24 months, then 2-3, 3-4, 4-6, 6-8,
    // 8-12, 12-15 and 15-18 years. Its "Birth" row (the immediate neonatal period) is not used: 0-3 months applies from
    // birth.
    static readonly int[] Groups = [0, 3, 6, 9, 12, 18, 24, 36, 48, 72, 96, 144, 180, 216];

    // Web Table 4 and Web Table 5, 1st and 99th centiles, for each group in order.
    static readonly (int Low, int High)[] RespiratoryRates = [(25, 66), (24, 64), (23, 61), (22, 58), (21, 53), (19, 46), (18, 38), (17, 33), (17, 29), (16, 27), (14, 25), (12, 23), (11, 22)];
    static readonly (int Low, int High)[] HeartRateCentiles = [(107, 181), (104, 175), (98, 168), (93, 161), (88, 156), (82, 149), (76, 142), (70, 136), (65, 131), (59, 123), (52, 115), (47, 108), (43, 104)];

    // Table 6, systolic and diastolic, for ages 1 to 12 and then 13 and over (to 18, when the adults' range applies).
    static readonly (int Systolic, int Diastolic)[] Boys = [(98, 52), (100, 55), (101, 58), (102, 60), (103, 63), (105, 66), (106, 68), (107, 69), (107, 70), (108, 72), (110, 74), (113, 75), (120, 80)];
    static readonly (int Systolic, int Diastolic)[] Girls = [(98, 54), (101, 58), (102, 60), (103, 62), (104, 64), (105, 67), (106, 68), (107, 69), (108, 71), (109, 72), (111, 74), (114, 75), (120, 80)];

    public static readonly IReadOnlyList<(string Code, string Sex, int FromMonths, int? ToMonths, decimal? Low, decimal? High, string Kind, string Source)> All =
    [
        .. Children(VitalSign.HeartRate, HeartRateCentiles, HeartRates), (VitalSign.HeartRate, VitalRangeSex.Any, 216, null, 60, 100, ReferenceKind.Normal, AdultRates),
        .. Children(VitalSign.RespiratoryRate, RespiratoryRates, Breathing), (VitalSign.RespiratoryRate, VitalRangeSex.Any, 216, null, 12, 20, ReferenceKind.Normal, AdultBreathing),
        .. Pressures(VitalSign.Systolic, AdministrativeGender.Male, Boys.Select(b => b.Systolic)), .. Pressures(VitalSign.Systolic, AdministrativeGender.Female, Girls.Select(g => g.Systolic)),
        (VitalSign.Systolic, VitalRangeSex.Any, 216, null, 90, 129, ReferenceKind.Normal, AdultPressure),
        .. Pressures(VitalSign.Diastolic, AdministrativeGender.Male, Boys.Select(b => b.Diastolic)), .. Pressures(VitalSign.Diastolic, AdministrativeGender.Female, Girls.Select(g => g.Diastolic)),
        (VitalSign.Diastolic, VitalRangeSex.Any, 216, null, 60, 79, ReferenceKind.Normal, AdultPressure),
    ];

    static IEnumerable<(string, string, int, int?, decimal?, decimal?, string, string)> Children(string code, (int Low, int High)[] centiles, string source) =>
        centiles.Select((centile, i) => (code, VitalRangeSex.Any, Groups[i], (int?)Groups[i + 1], (decimal?)centile.Low, (decimal?)centile.High, ReferenceKind.Normal, source));

    // Year by year from 1 (12 months) to 12, then 13 to 18 (156 to 216 months).
    static IEnumerable<(string, string, int, int?, decimal?, decimal?, string, string)> Pressures(string code, string sex, IEnumerable<int> values) =>
        values.Select((value, i) => (code, sex, (i + 1) * 12, (int?)(i == 12 ? 216 : (i + 2) * 12), (decimal?)null, (decimal?)value, ReferenceKind.Screening,
            $"{Table6}, {(sex == AdministrativeGender.Male ? "boys" : "girls")}"));

    // The range for a sign at an age in months, for a patient of that sex, as ehr.file_result picks it
    // (db/policies/75-results.sql): null when the sign has no ranges by age (it keeps its one range); no ends when it
    // has none for this age (a blood pressure under 1 year). A sex other than male or female is held to both sexes'
    // rows: the higher low and the lower high.
    public static (decimal? Low, decimal? High, string Kind)? For(string code, int months, string sex)
    {
        // A plain loop over the sign's own rows: the seed asks this for every vital sign it makes.
        if (!ByCode.TryGetValue(code, out var rows))
        {
            return null;
        }

        var (low, high, kind) = ((decimal?)null, (decimal?)null, ReferenceKind.Normal);
        var either = sex != AdministrativeGender.Male && sex != AdministrativeGender.Female;
        foreach (var range in rows)
        {
            if (months < range.FromMonths || months >= range.ToMonths || (range.Sex != VitalRangeSex.Any && range.Sex != sex && !either))
            {
                continue;
            }

            low = range.Low > low || low is null ? range.Low : low;
            high = range.High < high || high is null ? range.High : high;
            kind = range.Kind == ReferenceKind.Screening ? range.Kind : kind;
        }

        return (low, high, kind);
    }

    static readonly Dictionary<string, (string Code, string Sex, int FromMonths, int? ToMonths, decimal? Low, decimal? High, string Kind, string Source)[]> ByCode =
        All.GroupBy(range => range.Code).ToDictionary(group => group.Key, group => group.ToArray());

    public static bool IsBanded(string code) => All.Any(range => range.Code == code);
}

// The five age bands the seed draws its synthetic children's values by (step 9b): the ranges themselves now go by
// Fleming's age groups and the year of age (VitalRanges), not by these.
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
            table.HasCheckConstraint("ck_vital_reference_ranges_sex", $"sex IN ({Codes.List(VitalRangeSex.All)})");
            table.HasCheckConstraint("ck_vital_reference_ranges_kind", $"kind IN ({Codes.List(ReferenceKind.All)})");
            table.HasCheckConstraint("ck_vital_reference_ranges_range", "low <= high AND (low IS NOT NULL OR high IS NOT NULL)");
            table.HasCheckConstraint("ck_vital_reference_ranges_ages", "from_months >= 0 AND (to_months IS NULL OR to_months > from_months)");
            // A screening value is a single value at or above which a reading needs follow-up.
            table.HasCheckConstraint("ck_vital_reference_ranges_screening", $"kind <> '{ReferenceKind.Screening}' OR (low IS NULL AND high IS NOT NULL)");
        });
        range.HasKey(r => new { r.Code, r.Sex, r.FromMonths });
        range.Property(r => r.Code).HasMaxLength(20);
        range.Property(r => r.Sex).HasMaxLength(10).HasDefaultValue(VitalRangeSex.Any);
        range.Property(r => r.Kind).HasMaxLength(20).HasDefaultValue(ReferenceKind.Normal);
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
