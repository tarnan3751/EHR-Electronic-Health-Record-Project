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

    // Patients whose MRN is the query, or whose given or family name starts with it.
    public async Task<IReadOnlyList<Patient>> SearchPatientsAsync(string query, int limit, CancellationToken cancellationToken)
    {
        var term = query.Trim();
        if (term.Length == 0)
        {
            return [];
        }

        var prefix = term.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_") + "%";
        return await ReadAsync(
            db.Patients.Where(p => p.Mrn == term || EF.Functions.ILike(p.FamilyName, prefix) || EF.Functions.ILike(p.GivenName, prefix)),
            limit, cancellationToken);
    }

    // The patients on the signed-in clinician's panel (PatientAssignment), the ones the clinic's home shows first.
    public Task<IReadOnlyList<Patient>> PanelAsync(int limit, CancellationToken cancellationToken) =>
        ReadAsync(
            db.Patients.Where(p => db.PatientAssignments.Any(a => a.PatientId == p.Id && a.ClinicianId == user.UserId && a.EndedAt == null)),
            limit, cancellationToken);

    // A list of patients, in name order, each one a recorded read.
    async Task<IReadOnlyList<Patient>> ReadAsync(IQueryable<Patient> query, int limit, CancellationToken cancellationToken)
    {
        var patients = await query.AsNoTracking()
            .OrderBy(p => p.FamilyName).ThenBy(p => p.GivenName).ThenBy(p => p.Id)
            .Take(limit)
            .ToListAsync(cancellationToken);
        if (patients.Count > 0)
        {
            audit.RecordReads(PatientsTable, patients.Select(p => (p.Id, p.Id)));
            await db.SaveChangesAsync(cancellationToken);
        }

        return patients;
    }
}
