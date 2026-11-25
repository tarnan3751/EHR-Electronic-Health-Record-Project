using Ehr.Data;
using Ehr.Data.Patients;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Ehr.Domain.Patients;

// Adding to and changing a patient's allergies, medications and diagnoses (the expansion plan's step 2), and the
// reference codes their entries are picked from. Called inside the page's database transaction
// (DatabaseTransactionFilter). The reference codes aren't PHI, so they are read here; the patient and the entry being
// changed are read through PhiReader. Who may write is the page's rule and the database's (a clinician, for a patient
// on their panel: db/policies/30-rls.sql); the database also keeps the history of every change, marks a diagnosis in a
// sensitive range sensitive whatever is sent, and records the write (65-chart-lists.sql, 40-audit.sql). Nothing
// deletes an entry: it is marked inactive, resolved or stopped.
public sealed partial class ChartLists(EhrDbContext db)
{
    // How many names the search offers as a clinician types.
    public const int Suggestions = 20;

    // The longest name, reaction or dosage the form takes: the width of those columns in all three tables
    // (AllergyIntoleranceConfiguration, MedicationStatementConfiguration, ConditionConfiguration).
    public const int MaximumLength = 200;

    // What "No known allergies" is called, stored and shown: one wording everywhere.
    public const string NoKnownAllergies = AllergyIntolerance.NoKnownAllergyDisplay;

    // What a patient can have when exposed, offered as the clinician types; anything else can be typed.
    public static readonly string[] Reactions =
    [
        "Anaphylaxis", "Contact dermatitis", "Diarrhea", "Hives", "Itching", "Nausea", "Rash", "Shortness of breath", "Sneezing",
        "Stomach cramps", "Swelling of the lips", "Vomiting", "Wheezing",
    ];

    // What the chart calls an allergy entry: "No known allergies" for that entry, whatever name it was stored with.
    public static string DisplayOf(AllergyIntolerance allergy) => DisplayOf(allergy.Code, allergy.Display);

    public static string DisplayOf(string? code, string display) => code == AllergyIntolerance.NoKnownAllergy ? NoKnownAllergies : display;

    // Names of one kind that match what was typed: first those that start with it; then a diagnosis whose code starts
    // with it; then those with words that start with each word typed ("diab mell" finds "Type 2 diabetes mellitus
    // ..."). Each is an index lookup that stops at its first matches (ClinicalCodeConfiguration): there are about a
    // hundred thousand names. Nothing for fewer than two characters.
    public async Task<IReadOnlyList<ClinicalCode>> SearchAsync(string kind, string? typed, CancellationToken cancellationToken)
    {
        var key = typed?.Trim().ToLowerInvariant() ?? "";
        if (key.Length < 2 || !ClinicalCodeKind.All.Contains(kind))
        {
            return [];
        }

        var codes = db.ClinicalCodes.AsNoTracking().Where(c => c.Kind == kind);
        var found = await codes.Where(c => EF.Functions.Like(c.DisplayKey, Like(key) + "%"))
            .OrderBy(c => c.DisplayKey).ThenBy(c => c.Code).Take(Suggestions).ToListAsync(cancellationToken);
        if (found.Count < Suggestions && kind == ClinicalCodeKind.Condition)
        {
            Add(await codes.Where(c => EF.Functions.Like(c.Code, Like(key.ToUpperInvariant()) + "%"))
                .OrderBy(c => c.Code).Take(Suggestions).ToListAsync(cancellationToken));
        }

        // Words of letters and digits only, so nothing typed can be read as the query's own syntax; a word of one
        // character would match too much to be worth asking for.
        var words = Word().Matches(key).Select(match => match.Value).Where(word => word.Length > 1).Take(6).ToList();
        if (found.Count < Suggestions && words.Count > 0)
        {
            var query = string.Join(" & ", words.Select(word => word + ":*"));
            Add(await codes.Where(c => EF.Functions.ToTsVector("simple", c.Display).Matches(EF.Functions.ToTsQuery("simple", query)))
                .OrderBy(c => c.DisplayKey.Length).ThenBy(c => c.DisplayKey).ThenBy(c => c.Code).Take(Suggestions).ToListAsync(cancellationToken));
        }

        return found;

        void Add(List<ClinicalCode> more) =>
            found.AddRange(more.Where(code => found.All(have => (have.System, have.Code) != (code.System, code.Code))).Take(Suggestions - found.Count));
    }

    [System.Text.RegularExpressions.GeneratedRegex("[a-z0-9]+")]
    private static partial System.Text.RegularExpressions.Regex Word();

    // Whether a diagnosis's ICD-10-CM code is sensitive by the rule (ehr.icd10cm_is_sensitive), as the loaded reference
    // codes record it: the entry form shows such a diagnosis's "Sensitive" box checked and fixed. The database marks it
    // whatever the form sends, so a code missing from the reference list is still marked; the form just can't say so.
    public Task<bool> IsSensitiveDiagnosisAsync(string? code, CancellationToken cancellationToken) =>
        code is null
            ? Task.FromResult(false)
            : db.ClinicalCodes.AsNoTracking().AnyAsync(c => c.Kind == ClinicalCodeKind.Condition && c.Code == code && c.Sensitive, cancellationToken);

    // The code whose name is exactly what was picked or typed, in any case; or, for a diagnosis, the ICD-10-CM code
    // itself, typed in any case, with or without its dot ("i10", "E11.9", "e119"), as the form's hint offers.
    public async Task<ClinicalCode?> FindAsync(string kind, string? display, CancellationToken cancellationToken)
    {
        var typed = display?.Trim() ?? "";
        var key = typed.ToLowerInvariant();
        var named = await db.ClinicalCodes.AsNoTracking().Where(c => c.Kind == kind && c.DisplayKey == key).OrderBy(c => c.System).ThenBy(c => c.Code)
            .FirstOrDefaultAsync(cancellationToken);
        if (named is not null || kind != ClinicalCodeKind.Condition || IcdCode(typed) is not { } code)
        {
            return named;
        }

        return await db.ClinicalCodes.AsNoTracking().FirstOrDefaultAsync(c => c.Kind == kind && c.Code == code, cancellationToken);
    }

    // What was typed as an ICD-10-CM code, written as the list writes it (upper case, the dot after the third
    // character), or null when it can't be one: a letter, two characters, then up to four more.
    static string? IcdCode(string typed)
    {
        var bare = typed.Replace(".", "", StringComparison.Ordinal).ToUpperInvariant();
        if (bare.Length is < 3 or > 7 || !char.IsAsciiLetter(bare[0]) || !bare.All(char.IsAsciiLetterOrDigit))
        {
            return null;
        }

        return bare.Length == 3 ? bare : $"{bare[..3]}.{bare[3..]}";
    }

    // Adds the entry, or, when it has an ID already, saves every field of it a form can change. Refused when the
    // database refused: the patient isn't on the clinician's panel, or the entry isn't theirs to change. Nothing is
    // saved then.
    public Task<ChartSave> SaveAsync(AllergyIntolerance allergy, bool isNew, CancellationToken cancellationToken) =>
        SaveAsync(allergy, isNew, cancellationToken, nameof(AllergyIntolerance.Code), nameof(AllergyIntolerance.CodeSystem),
            nameof(AllergyIntolerance.Display), nameof(AllergyIntolerance.ClinicalStatus), nameof(AllergyIntolerance.Category),
            nameof(AllergyIntolerance.Criticality), nameof(AllergyIntolerance.Reaction), nameof(AllergyIntolerance.Severity));

    public Task<ChartSave> SaveAsync(MedicationStatement medication, bool isNew, CancellationToken cancellationToken) =>
        SaveAsync(medication, isNew, cancellationToken, nameof(MedicationStatement.Code), nameof(MedicationStatement.Display),
            nameof(MedicationStatement.Dosage), nameof(MedicationStatement.Status), nameof(MedicationStatement.EffectiveStart),
            nameof(MedicationStatement.EffectiveEnd), nameof(MedicationStatement.Sensitive));

    public Task<ChartSave> SaveAsync(Condition condition, bool isNew, CancellationToken cancellationToken) =>
        SaveAsync(condition, isNew, cancellationToken, nameof(Condition.Code), nameof(Condition.Display), nameof(Condition.ClinicalStatus),
            nameof(Condition.OnsetDate), nameof(Condition.Sensitive));

    // Records that the patient has no known allergies: an entry of its own, so a chart can tell it from no allergy
    // information at all. The database refuses it for a patient with an active allergy recorded, and makes it inactive
    // when one is added (db/policies/65-chart-lists.sql). AllergyRecorded when an active allergy is why it was refused.
    public Task<ChartSave> RecordNoKnownAllergiesAsync(Guid patient, CancellationToken cancellationToken) =>
        SaveAsync(new AllergyIntolerance
        {
            Id = Guid.CreateVersion7(),
            PatientId = patient,
            Code = AllergyIntolerance.NoKnownAllergy,
            Display = NoKnownAllergies,
            ClinicalStatus = AllergyClinicalStatus.Active,
            RecordedAt = DateTimeOffset.UtcNow,
        }, isNew: true, cancellationToken);

    // An insert, or an update of the named columns of the row with the entry's ID. Reads nothing: the page read the
    // entry as this patient's through PhiReader, and row-level security decides whether the write may happen.
    //
    // A new entry's ID comes with the form, chosen when it was opened, so a form posted twice (a double click, or
    // Back and post again) finds its row there already: the insert then becomes an update of it, not a second entry.
    // Updating one of the patient's entries by its ID is what changing it is, with the same policies; an ID that is
    // another patient's entry's is refused, as moving an entry is (65-chart-lists.sql).
    async Task<ChartSave> SaveAsync(object entry, bool isNew, CancellationToken cancellationToken, params string[] changed)
    {
        var saved = await TrySaveAsync(entry, isNew, cancellationToken, changed);
        // The update names the patient too: for another patient's row that would be a move, which the guard refuses,
        // so a form can only ever write its own patient's entry.
        return saved == RepeatedInsert ? await TrySaveAsync(entry, isNew: false, cancellationToken, [.. changed, "PatientId"]) : saved;
    }

    // An insert that found its ID taken: try it as an update. Never returned by SaveAsync.
    const ChartSave RepeatedInsert = (ChartSave)(-1);

    async Task<ChartSave> TrySaveAsync(object entry, bool isNew, CancellationToken cancellationToken, string[] changed)
    {
        const string savepoint = "chart_entry";
        var transaction = db.Database.CurrentTransaction!;
        await transaction.CreateSavepointAsync(savepoint, cancellationToken);
        var tracked = isNew ? db.Add(entry) : db.Attach(entry);
        if (!isNew)
        {
            foreach (var property in changed)
            {
                tracked.Property(property).IsModified = true;
            }
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.ReleaseSavepointAsync(savepoint, cancellationToken);
            return ChartSave.Saved;
        }
        catch (DbUpdateConcurrencyException)
        {
            // No row: not an entry this clinician may change.
            await transaction.RollbackToSavepointAsync(savepoint, cancellationToken);
            return ChartSave.Refused;
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.InsufficientPrivilege })
        {
            // Refused by row-level security.
            await transaction.RollbackToSavepointAsync(savepoint, cancellationToken);
            return ChartSave.Refused;
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.CheckViolation }
            && entry is AllergyIntolerance { Code: AllergyIntolerance.NoKnownAllergy })
        {
            // "No known allergies" only: refused because the patient has an allergy recorded. Any other broken
            // constraint is a fault, and is thrown.
            await transaction.RollbackToSavepointAsync(savepoint, cancellationToken);
            return ChartSave.AllergyRecorded;
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } unique
            && unique.ConstraintName == AllergyIntoleranceConfiguration.OneNoKnownAllergyIndex
            && entry is AllergyIntolerance { Code: AllergyIntolerance.NoKnownAllergy })
        {
            // "No known allergies" is already recorded (a second click, or another tab): nothing more to save.
            await transaction.RollbackToSavepointAsync(savepoint, cancellationToken);
            return ChartSave.AlreadyRecorded;
        }
        catch (DbUpdateException e) when (isNew && e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: { } key }
            && key.StartsWith("pk_", StringComparison.Ordinal))
        {
            // The form was posted before: its entry exists.
            await transaction.RollbackToSavepointAsync(savepoint, cancellationToken);
            return RepeatedInsert;
        }
        finally
        {
            tracked.State = EntityState.Detached;
        }
    }

    // What was typed, with LIKE's own characters taken literally.
    static string Like(string text) => text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);
}

// What saving a chart entry did. Nothing was saved unless Saved.
public enum ChartSave
{
    Saved,

    // The database refused the write: the patient isn't on the clinician's panel, or the entry isn't theirs.
    Refused,

    // "No known allergies" only: refused because the patient has an active allergy recorded.
    AllergyRecorded,

    // "No known allergies" only: it is already recorded, so nothing more was saved. As good as Saved to the clinician.
    AlreadyRecorded,
}
