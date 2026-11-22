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

    public const int MaximumLength = 200;

    // What a patient can have when exposed, offered as the clinician types; anything else can be typed.
    public static readonly string[] Reactions =
    [
        "Anaphylaxis", "Contact dermatitis", "Diarrhea", "Hives", "Itching", "Nausea", "Rash", "Shortness of breath", "Sneezing",
        "Stomach cramps", "Swelling of the lips", "Vomiting", "Wheezing",
    ];

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

    // The code whose name is exactly what was picked or typed, in any case.
    public Task<ClinicalCode?> FindAsync(string kind, string? display, CancellationToken cancellationToken)
    {
        var key = display?.Trim().ToLowerInvariant() ?? "";
        return db.ClinicalCodes.AsNoTracking().Where(c => c.Kind == kind && c.DisplayKey == key).OrderBy(c => c.System).ThenBy(c => c.Code)
            .FirstOrDefaultAsync(cancellationToken);
    }

    // Adds the entry, or, when it has an ID already, saves every field of it a form can change. False when the database
    // refused: the patient isn't on the clinician's panel, or the entry isn't theirs to change. Nothing is saved then.
    public Task<bool> SaveAsync(AllergyIntolerance allergy, bool isNew, CancellationToken cancellationToken) =>
        SaveAsync(allergy, isNew, cancellationToken, nameof(AllergyIntolerance.Code), nameof(AllergyIntolerance.CodeSystem),
            nameof(AllergyIntolerance.Display), nameof(AllergyIntolerance.ClinicalStatus), nameof(AllergyIntolerance.Category),
            nameof(AllergyIntolerance.Criticality), nameof(AllergyIntolerance.Reaction), nameof(AllergyIntolerance.Severity));

    public Task<bool> SaveAsync(MedicationStatement medication, bool isNew, CancellationToken cancellationToken) =>
        SaveAsync(medication, isNew, cancellationToken, nameof(MedicationStatement.Code), nameof(MedicationStatement.Display),
            nameof(MedicationStatement.Dosage), nameof(MedicationStatement.Status), nameof(MedicationStatement.EffectiveStart),
            nameof(MedicationStatement.EffectiveEnd), nameof(MedicationStatement.Sensitive));

    public Task<bool> SaveAsync(Condition condition, bool isNew, CancellationToken cancellationToken) =>
        SaveAsync(condition, isNew, cancellationToken, nameof(Condition.Code), nameof(Condition.Display), nameof(Condition.ClinicalStatus),
            nameof(Condition.OnsetDate), nameof(Condition.Sensitive));

    // Adds an allergy the clinician's dictation proposed (the expansion plan's step 15) and decides the proposal with it,
    // in one savepoint: both, or neither. The allergy is added as SaveAsync adds any; the database holds the proposal to a
    // coded allergy added now by this clinician (55-ai-review.sql). Refused as SaveAsync refuses; Decided when the
    // proposal was decided first: nothing is saved then, the allergy included.
    public async Task<ProposedAllergy> AddProposedAsync(AllergyIntolerance allergy, Guid proposal, CancellationToken cancellationToken)
    {
        const string savepoint = "proposed_allergy";
        var transaction = db.Database.CurrentTransaction!;
        await transaction.CreateSavepointAsync(savepoint, cancellationToken);
        if (!await SaveAsync(allergy, isNew: true, cancellationToken))
        {
            await transaction.RollbackToSavepointAsync(savepoint, cancellationToken);
            return ProposedAllergy.Refused;
        }

        // A stand-in for the row, with its key and the state it must still be in: only the decision is sent.
        var row = new AiChartProposal
        {
            Id = proposal,
            PatientId = allergy.PatientId,
            Kind = AiChartProposalKinds.AllergyAddition,
            State = AiChartProposalStates.Pending,
        };
        var entry = db.Attach((object)row);
        row.State = AiChartProposalStates.Recorded;
        row.AllergyId = allergy.Id;
        entry.Property(nameof(AiChartProposal.State)).IsModified = true;
        entry.Property(nameof(AiChartProposal.AllergyId)).IsModified = true;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.ReleaseSavepointAsync(savepoint, cancellationToken);
            return ProposedAllergy.Saved;
        }
        catch (DbUpdateConcurrencyException)
        {
            // No waiting proposal this clinician may decide: decided first, in another tab.
            await transaction.RollbackToSavepointAsync(savepoint, cancellationToken);
            return ProposedAllergy.Decided;
        }
        finally
        {
            entry.State = EntityState.Detached;
        }
    }

    // Records that the patient has no known allergies: an entry of its own, so a chart can tell it from no allergy
    // information at all. The database refuses it for a patient with an active allergy recorded, and makes it inactive
    // when one is added (db/policies/65-chart-lists.sql). False when it was refused.
    public Task<bool> RecordNoKnownAllergiesAsync(Guid patient, CancellationToken cancellationToken) =>
        SaveAsync(new AllergyIntolerance
        {
            Id = Guid.CreateVersion7(),
            PatientId = patient,
            Code = AllergyIntolerance.NoKnownAllergy,
            Display = "No known allergy",
            ClinicalStatus = AllergyClinicalStatus.Active,
            RecordedAt = DateTimeOffset.UtcNow,
        }, isNew: true, cancellationToken);

    // An insert, or an update of the named columns of the row with the entry's ID. Reads nothing: the page read the
    // entry as this patient's through PhiReader, and row-level security decides whether the write may happen.
    async Task<bool> SaveAsync(object entry, bool isNew, CancellationToken cancellationToken, params string[] changed)
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
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            // No row: not an entry this clinician may change.
            await transaction.RollbackToSavepointAsync(savepoint, cancellationToken);
            return false;
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: var state }
            // Refused by row-level security; or, for "No known allergies" only, by the rule that a patient with an
            // allergy recorded has none. Any other broken constraint is a fault, and is thrown.
            && (state == PostgresErrorCodes.InsufficientPrivilege
                || state == PostgresErrorCodes.CheckViolation && entry is AllergyIntolerance { Code: AllergyIntolerance.NoKnownAllergy }))
        {
            await transaction.RollbackToSavepointAsync(savepoint, cancellationToken);
            return false;
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

// How adding an allergy a dictation proposed went (ChartLists.AddProposedAsync).
public enum ProposedAllergy
{
    Saved,

    // The database refused the allergy: the patient isn't on the clinician's panel. Nothing is saved.
    Refused,

    // The proposal was decided first, in another tab. Nothing is saved, the allergy included.
    Decided,
}
