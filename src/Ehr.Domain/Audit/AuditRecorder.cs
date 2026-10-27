using Ehr.Data;
using Ehr.Data.Audit;

namespace Ehr.Domain.Audit;

// Records what the app itself must record: PHI reads (PhiReader), sign-ins, and account events. Writes to PHI tables
// are recorded by the database trigger instead (db/policies/40-audit.sql). An event joins the request's unit of work
// and commits with its transaction, so a read without its record is impossible. The actor is the signed-in user
// unless the caller knows better, as at a sign-in, where the user isn't signed in yet.
public sealed class AuditRecorder(EhrDbContext db, UserContext user, AuditSource source)
{
    public AuditEvent Record(
        string action, Guid? patientId = null, string? tableName = null, Guid? rowId = null, string? reason = null, Guid? actorId = null,
        string? details = null)
    {
        var auditEvent = new AuditEvent
        {
            Id = Guid.CreateVersion7(),
            OccurredAt = DateTimeOffset.UtcNow,
            Action = action,
            Source = source.Name,
            ActorId = actorId ?? user.UserId,
            PatientId = patientId,
            TableName = tableName,
            RowId = rowId,
            Reason = reason,
            Details = details,
        };
        db.AuditEvents.Add(auditEvent);
        return auditEvent;
    }
}

// Which server this app is, as the audit log names it: AuditSources.OnPrem or AuditSources.Cloud. The host registers it.
public sealed record AuditSource(string Name);
