using Ehr.Data;
using Ehr.Data.Audit;
using Ehr.Data.Patients;
using Ehr.Domain.Audit;
using Microsoft.EntityFrameworkCore;

namespace Ehr.Domain.Phi;

// The one way to read patient information (AGENTS.md). Every read records an audit event for each patient whose
// data it returned, in the same transaction, so a view without its record is impossible. Row-level security decides
// which rows exist for the current user: a clinician sees every patient, a patient only themselves, anyone else
// nothing. Ehr.Architecture.Tests fails if anything outside this namespace touches the patient entity.
public sealed class PhiReader(EhrDbContext db, AuditRecorder audit)
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

    // Patients whose MRN is the query, or whose given or family name starts with it.
    public async Task<IReadOnlyList<Patient>> SearchPatientsAsync(string query, int limit, CancellationToken cancellationToken)
    {
        var term = query.Trim();
        if (term.Length == 0)
        {
            return [];
        }

        var prefix = term.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_") + "%";
        var patients = await db.Patients.AsNoTracking()
            .Where(p => p.Mrn == term || EF.Functions.ILike(p.FamilyName, prefix) || EF.Functions.ILike(p.GivenName, prefix))
            .OrderBy(p => p.FamilyName).ThenBy(p => p.GivenName).ThenBy(p => p.Id)
            .Take(limit)
            .ToListAsync(cancellationToken);
        foreach (var patient in patients)
        {
            audit.Record(AuditActions.Read, patient.Id, PatientsTable, patient.Id);
        }

        if (patients.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return patients;
    }
}
