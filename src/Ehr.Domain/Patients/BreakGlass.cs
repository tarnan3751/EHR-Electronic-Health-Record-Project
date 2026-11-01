using Ehr.Data;
using Ehr.Data.Audit;
using Ehr.Data.Patients;
using Ehr.Domain.Audit;
using Microsoft.EntityFrameworkCore;

namespace Ehr.Domain.Patients;

// Emergency access. Every clinician may open every chart, but a chart off their panel asks for a reason first. The
// reason is recorded twice in one transaction: as a break_glass audit event, which is what admins review, and as a
// BreakGlassAccess row that lets this clinician back into the chart for Duration without asking again. This is the
// app's gate, not the database's: row-level security lets clinicians read every patient, as the team decided, and the
// audit log is the control. A patient on the clinician's panel never needs it (PatientAssignments).
public sealed class BreakGlass(EhrDbContext db, UserContext user, AuditRecorder audit)
{
    public static readonly TimeSpan Duration = TimeSpan.FromHours(4);

    public const int MinimumReasonLength = 10;

    public const int MaximumReasonLength = 500;

    // Until when the clinician's emergency access to the patient lasts, or null when they have none now.
    public async Task<DateTimeOffset?> ActiveUntilAsync(Guid patientId, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var until = await db.BreakGlassAccesses.AsNoTracking()
            .Where(a => a.PatientId == patientId && a.ClinicianId == user.UserId && a.ExpiresAt > now)
            .MaxAsync(a => (DateTimeOffset?)a.ExpiresAt, cancellationToken);
        return until;
    }

    // Grants the clinician the chart for Duration, and records why. The foreign key refuses an ID that is no patient.
    public async Task<DateTimeOffset> GrantAsync(Guid patientId, string reason, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var access = new BreakGlassAccess
        {
            Id = Guid.CreateVersion7(),
            PatientId = patientId,
            ClinicianId = user.UserId!.Value,
            Reason = reason,
            GrantedAt = now,
            ExpiresAt = now + Duration,
        };
        db.BreakGlassAccesses.Add(access);
        audit.Record(AuditActions.BreakGlass, patientId, "break_glass_accesses", access.Id, reason);
        await db.SaveChangesAsync(cancellationToken);
        return access.ExpiresAt;
    }
}
