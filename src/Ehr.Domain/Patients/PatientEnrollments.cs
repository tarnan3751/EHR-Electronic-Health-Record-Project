using Ehr.Data;
using Ehr.Data.Accounts;
using Ehr.Data.Audit;
using Ehr.Data.Patients;
using Ehr.Domain.Accounts;
using Ehr.Domain.Audit;

namespace Ehr.Domain.Patients;

// Issues portal enrollment codes to patients, at the clinic. The code is returned once, for the clinician to hand
// over; only its keyed hash is kept (CodeHasher). The caller has just read the patient through the PHI reader, and the foreign key
// refuses an ID that is no patient. Issuing one is an audit event.
public sealed class PatientEnrollments(EhrDbContext db, AuditRecorder audit, CodeHasher hasher)
{
    public static readonly TimeSpan Validity = TimeSpan.FromDays(14);

    public async Task<string> IssueAsync(Guid patientId, CancellationToken cancellationToken)
    {
        var code = EnrollmentCodes.Generate();
        // Through the database's function, which sets who issued it, when, and that it lasts fourteen days; the app
        // roles can't write the table themselves (db/policies/45-codes.sql).
        await CodeStore.IssueEnrollmentAsync(db, Guid.CreateVersion7(), patientId, hasher.Enrollment(code), cancellationToken);
        audit.Record(AuditActions.EnrollmentIssued, patientId: patientId);
        await db.SaveChangesAsync(cancellationToken);
        return EnrollmentCodes.Format(code);
    }
}
