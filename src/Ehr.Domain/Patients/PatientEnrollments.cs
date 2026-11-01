using Ehr.Data;
using Ehr.Data.Audit;
using Ehr.Data.Patients;
using Ehr.Domain.Accounts;
using Ehr.Domain.Audit;

namespace Ehr.Domain.Patients;

// Issues portal enrollment codes to patients, at the clinic. The code is returned once, for the clinician to hand
// over; only its hash is kept. The caller has just read the patient through the PHI reader, and the foreign key
// refuses an ID that is no patient. Issuing one is an audit event.
public sealed class PatientEnrollments(EhrDbContext db, UserContext user, AuditRecorder audit)
{
    public static readonly TimeSpan Validity = TimeSpan.FromDays(14);

    public async Task<string> IssueAsync(Guid patientId, CancellationToken cancellationToken)
    {
        var code = EnrollmentCodes.Generate();
        var now = DateTimeOffset.UtcNow;
        db.PatientEnrollments.Add(new PatientEnrollment
        {
            Id = Guid.CreateVersion7(),
            PatientId = patientId,
            CodeHash = EnrollmentCodes.Hash(code),
            IssuedBy = user.UserId,
            IssuedAt = now,
            ExpiresAt = now + Validity,
        });
        audit.Record(AuditActions.EnrollmentIssued, patientId: patientId);
        await db.SaveChangesAsync(cancellationToken);
        return EnrollmentCodes.Format(code);
    }
}
