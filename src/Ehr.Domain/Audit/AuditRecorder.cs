using Ehr.Data;
using Ehr.Data.Audit;

namespace Ehr.Domain.Audit;

// Records what the app itself must record: PHI reads (PhiReader), sign-ins, and account events. Writes to PHI tables
// are recorded by the database trigger instead (db/policies/40-audit.sql). An event joins the request's unit of work
// and commits with its transaction, so a read without its record is impossible. The actor is the signed-in user
// unless the caller knows better, as at a sign-in, where the user isn't signed in yet. On the cloud site, whose database
// is a replica that takes no writes, the host registers an IAuditQueue and events go there instead.
public sealed class AuditRecorder(EhrDbContext db, UserContext user, AuditSource source, IAuditQueue? queue = null)
{
    public AuditEvent Record(
        string action, Guid? patientId = null, string? tableName = null, Guid? rowId = null, string? reason = null, Guid? actorId = null,
        string? details = null)
    {
        var auditEvent = New(action, patientId, tableName, rowId, reason, actorId, details);
        Store([auditEvent]);
        return auditEvent;
    }

    // A read of several rows at once, such as a list of patients: one event for each, stored together, so the cloud
    // site's queue goes to disk once for the list rather than once a row.
    public void RecordReads(string tableName, IEnumerable<(Guid PatientId, Guid RowId)> rows) =>
        Store([.. rows.Select(row => New(AuditActions.Read, row.PatientId, tableName, row.RowId, reason: null, actorId: null, details: null))]);

    AuditEvent New(string action, Guid? patientId, string? tableName, Guid? rowId, string? reason, Guid? actorId, string? details) => new()
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

    void Store(IReadOnlyCollection<AuditEvent> auditEvents)
    {
        if (queue is null)
        {
            db.AuditEvents.AddRange(auditEvents);
        }
        else
        {
            queue.Enqueue(auditEvents);
        }
    }
}

// Where the cloud site keeps the events it records until the on-prem app has them. Enqueue stores all the events it is
// given, in order, and returns only once they are stored durably; otherwise, including when one is an event the on-prem
// app wouldn't take, it throws and stores none of them, so a read is never shown without its record.
public interface IAuditQueue
{
    void Enqueue(IReadOnlyCollection<AuditEvent> auditEvents);
}

// Which server this app is, as the audit log names it: AuditSources.OnPrem or AuditSources.Cloud. The host registers it.
public sealed record AuditSource(string Name);
