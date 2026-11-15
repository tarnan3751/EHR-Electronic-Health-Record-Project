using Ehr.Data;
using Ehr.Data.Audit;
using Ehr.Data.Patients;
using Ehr.Domain.Audit;
using Microsoft.EntityFrameworkCore;

namespace Ehr.Domain.Phi;

// The one way to read patient information (AGENTS.md). Every read records an audit event for each patient whose
// data it returned, in the same transaction, so a view without its record is impossible. Row-level security decides
// which rows exist for the current user: a clinician sees every patient, a patient only themselves, anyone else
// nothing. Ehr.Architecture.Tests fails if anything outside this namespace touches the patient entity or the rest of
// the chart.
public sealed class PhiReader(EhrDbContext db, AuditRecorder audit, UserContext user)
{
    public const string PatientsTable = "patients";

    public async Task<Patient?> PatientAsync(Guid id, CancellationToken cancellationToken)
    {
        var patient = await db.Patients.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (patient is not null)
        {
            audit.Record(AuditActions.Read, patient.Id, PatientsTable, patient.Id);
            await db.SaveChangesAsync(cancellationToken);
        }

        return patient;
    }

    // A patient's chart: the demographics, the allergies, medications and diagnoses they have now, and their latest
    // visits, up to `visits`, newest first. It is one view of one patient, so it is one recorded read of the patient,
    // however many rows the sections hold: the same event a list of patients records for each, and the only kind of
    // read the cloud app's audit queue takes (CloudAuditEvents).
    public async Task<PatientChart?> ChartAsync(Guid id, int visits, CancellationToken cancellationToken)
    {
        var patient = await db.Patients.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (patient is null)
        {
            return null;
        }

        var allergies = await db.AllergyIntolerances.AsNoTracking()
            .Where(a => a.PatientId == id && a.ClinicalStatus == AllergyClinicalStatus.Active)
            .OrderBy(a => a.Criticality == AllergyCriticality.High ? 0 : 1).ThenBy(a => a.Display).ThenBy(a => a.Id)
            .ToListAsync(cancellationToken);
        var medications = await db.MedicationStatements.AsNoTracking()
            .Where(m => m.PatientId == id && m.Status == MedicationStatementStatus.Active)
            .OrderBy(m => m.Display).ThenBy(m => m.Id)
            .ToListAsync(cancellationToken);
        var conditions = await db.Conditions.AsNoTracking()
            .Where(c => c.PatientId == id && ConditionClinicalStatus.Current.Contains(c.ClinicalStatus))
            .OrderBy(c => c.OnsetDate == null).ThenByDescending(c => c.OnsetDate).ThenBy(c => c.Display).ThenBy(c => c.Id)
            .ToListAsync(cancellationToken);
        var encounters = await db.Encounters.AsNoTracking()
            .Where(e => e.PatientId == id && !EncounterStatus.NotHeld.Contains(e.Status))
            .OrderByDescending(e => e.PeriodStart).ThenBy(e => e.Id)
            .Take(visits)
            .ToListAsync(cancellationToken);

        audit.Record(AuditActions.Read, patient.Id, PatientsTable, patient.Id);
        await db.SaveChangesAsync(cancellationToken);
        return new PatientChart(patient, allergies, medications, conditions, encounters);
    }

    // The shortest name a search takes. Every patient listed is a recorded read, and one letter matches hundreds.
    public const int MinimumNameLength = 2;

    // Patients matching every criterion given: a given or family name that starts with the name, in any case; the date
    // of birth; the MRN, exactly. At least one is required, and a name of at least MinimumNameLength characters.
    public Task<PatientList> SearchPatientsAsync(string? name, DateOnly? birthDate, string? mrn, int limit, CancellationToken cancellationToken)
    {
        if (name is null && birthDate is null && mrn is null)
        {
            throw new ArgumentException("A search needs a name, a date of birth or an MRN.");
        }

        if (name is { Length: < MinimumNameLength })
        {
            throw new ArgumentException($"A name to search for has at least {MinimumNameLength} characters.", nameof(name));
        }

        var patients = db.Patients.AsQueryable();
        if (name is not null)
        {
            // A name that starts with the prefix, in any case, is a key from the lower-cased prefix up to, not including,
            // the first string after all those that start with it (PatientConfiguration keeps the keys, in the C
            // collation, which the comparisons take from the column). What was typed is lower-cased in the database,
            // by the lower() that keeps the keys, and the range's end is worked out there from that (NameKeys): made
            // from the query's parameter alone, both are constants to the plan. The range is a condition of its own,
            // plain comparisons, which are leakproof, so under row-level security the keys' indexes can answer it. The
            // prefix check that decides, a second condition, isn't leakproof, so it runs on the rows the range found.
            // When nothing comes after the prefix, for a name of nothing but U+10FFFF, a noncharacter no name holds,
            // the end is null and the range finds nobody. Left so on purpose: an open end (IS NULL OR ...) keeps the
            // index's upper bound only in a custom plan, and in a generic one, as prepared statements get, the scan runs
            // from the prefix to the end of the index (22.7 ms rather than 1.8 at 50,000 patients).
            patients = patients
                .Where(p =>
                    (string.Compare(p.FamilyNameKey, NameKeys.Lower(name)) >= 0
                        && string.Compare(p.FamilyNameKey, NameKeys.After(NameKeys.Lower(name))) < 0)
                    || (string.Compare(p.GivenNameKey, NameKeys.Lower(name)) >= 0
                        && string.Compare(p.GivenNameKey, NameKeys.After(NameKeys.Lower(name))) < 0))
                .Where(p => p.FamilyNameKey.StartsWith(NameKeys.Lower(name)) || p.GivenNameKey.StartsWith(NameKeys.Lower(name)));
        }

        if (birthDate is { } date)
        {
            patients = patients.Where(p => p.BirthDate == date);
        }

        if (mrn is not null)
        {
            patients = patients.Where(p => p.Mrn == mrn);
        }

        return ReadAsync(patients, limit, cancellationToken);
    }

    // The patients on the signed-in clinician's panel (PatientAssignment), the ones the clinic's home shows first, and
    // whether more are on it than the limit.
    public Task<PatientList> PanelAsync(int limit, CancellationToken cancellationToken) =>
        ReadAsync(
            db.Patients.Where(p => db.PatientAssignments.Any(a => a.PatientId == p.Id && a.ClinicianId == user.UserId && a.EndedAt == null)),
            limit, cancellationToken);

    // Called in one repeatable-read transaction so the assignments and chart data are from the same snapshot.
    // Read only actively assigned charts; emergency access never enters the offline cache.
    public async Task<IReadOnlyList<PatientChart>> OfflinePanelAsync(int limit, int visits, CancellationToken cancellationToken)
    {
        var ids = await db.Patients.AsNoTracking()
            .Where(p => db.PatientAssignments.Any(a => a.PatientId == p.Id && a.ClinicianId == user.UserId && a.EndedAt == null))
            .OrderBy(p => p.FamilyName).ThenBy(p => p.GivenName).ThenBy(p => p.Id)
            .Select(p => p.Id)
            .Take(limit)
            .ToListAsync(cancellationToken);

        var charts = new List<PatientChart>(ids.Count);
        foreach (var id in ids)
        {
            if (await ChartAsync(id, visits, cancellationToken) is { } chart)
            {
                charts.Add(chart);
            }
        }

        return charts;
    }

    const string DraftsTable = "clinical_drafts";

    public Task<bool> IsAssignedAsync(Guid patient, CancellationToken cancellationToken) =>
        db.PatientAssignments.AnyAsync(a => a.PatientId == patient && a.ClinicianId == user.UserId && a.EndedAt == null, cancellationToken);

    public async Task<ClinicalDraft?> ReadDraftAsync(Guid id, Guid patient, CancellationToken cancellationToken)
    {
        var draft = await db.ClinicalDrafts.SingleOrDefaultAsync(
            d => d.Id == id && d.PatientId == patient && d.ClinicianId == user.UserId, cancellationToken);
        if (draft is not null)
        {
            audit.Record(AuditActions.Read, draft.PatientId, DraftsTable, draft.Id);
            await db.SaveChangesAsync(cancellationToken);
        }

        return draft;
    }

    public async Task<IReadOnlyList<ClinicalDraft>> ChangedDraftsAsync(Guid[] patients, ulong since, ulong until, CancellationToken cancellationToken)
    {
        var drafts = await ChangeLog.ClinicalDraftsChanged(db, since, until).AsNoTracking()
            .Where(d => d.ClinicianId == user.UserId && patients.Contains(d.PatientId)).ToListAsync(cancellationToken);
        if (drafts.Count > 0)
        {
            audit.RecordReads(DraftsTable, drafts.Select(d => (d.PatientId, d.Id)));
            await db.SaveChangesAsync(cancellationToken);
        }

        return drafts;
    }

    // A list of patients, in name order, each one a recorded read, and whether more match than the limit.
    async Task<PatientList> ReadAsync(IQueryable<Patient> query, int limit, CancellationToken cancellationToken)
    {
        var ordered = query.AsNoTracking().OrderBy(p => p.FamilyName).ThenBy(p => p.GivenName).ThenBy(p => p.Id);
        var patients = await ordered.Take(limit).ToListAsync(cancellationToken);
        // Only a yes or no comes back, nobody's data, so there is nothing more to record.
        var more = patients.Count == limit && await ordered.Skip(limit).AnyAsync(cancellationToken);
        if (patients.Count > 0)
        {
            audit.RecordReads(PatientsTable, patients.Select(p => (p.Id, p.Id)));
            await db.SaveChangesAsync(cancellationToken);
        }

        return new PatientList(patients, more);
    }
}

// Patients listed by PhiReader, each already recorded as a read, and whether more matched than were listed.
public sealed record PatientList(IReadOnlyList<Patient> Patients, bool More);
