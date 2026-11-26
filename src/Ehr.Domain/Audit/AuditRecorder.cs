using Ehr.Data;
using Ehr.Data.Audit;

namespace Ehr.Domain.Audit;

// Records what the app itself must record: PHI reads (PhiReader), sign-ins, and account events. Writes to PHI tables
// are recorded by the database trigger instead (db/policies/40-audit.sql). An event joins the request's unit of work
// and commits with its transaction, so a read without its record is impossible; RecordAllAsync writes its events at
// once, in that transaction, so they commit with it too; so does TogetherAsync. The actor is the signed-in user
// unless the caller knows better, as at a sign-in, where the user isn't signed in yet. On the cloud site, whose database
// is a replica that takes no writes, the host registers an IAuditQueue and events go there instead. A read says which
// part of the record it was for (AuditParts, step 13c2), which the PHI reader gives; no other event has one. A read or
// download recorded without one is refused here, so a page whose read forgot it fails its tests rather than record it.
public sealed class AuditRecorder(EhrDbContext db, UserContext user, AuditSource source, IAuditQueue? queue = null)
{
    // The events held while a page's reads are recorded together (TogetherAsync); null otherwise.
    List<AuditEvent>? held;

    public AuditEvent Record(
        string action, Guid? patientId = null, string? tableName = null, Guid? rowId = null, string? reason = null, Guid? actorId = null,
        string? details = null, string? part = null)
    {
        var auditEvent = New(action, patientId, tableName, rowId, reason, actorId, details, part);
        Store([auditEvent]);
        return auditEvent;
    }

    // A read of several rows at once, such as a list of patients: one event for each, written together as RecordAllAsync
    // writes them. Saved with the unit of work, each would take a statement of its own, and each statement prepares the
    // table's checks again: for a list of 100 patients, step 13c2's check on the part cost the clinic's home 1.2 to 1.4
    // ms that way (12 to 14 µs a statement), where one statement prepares it once.
    public Task RecordReadsAsync(string tableName, string part, IEnumerable<(Guid PatientId, Guid RowId)> rows, CancellationToken cancellationToken) =>
        RecordAllAsync(part, rows.Select(row => (AuditActions.Read, row.PatientId, tableName, row.RowId)), cancellationToken);

    // Several reads at once, such as a page with a read of the patient and of each of many of their rows: written
    // together, on-site in one statement at once, in the caller's transaction (AuditEventBatch), where events recorded
    // one by one each take a statement of their own when the unit of work is saved; on the cloud site, given to the
    // queue together, so the queue goes to disk once for them rather than once an event. One page's reads, so one part.
    public async Task RecordAllAsync(string part, IEnumerable<(string Action, Guid PatientId, string TableName, Guid RowId)> reads, CancellationToken cancellationToken)
    {
        AuditEvent[] auditEvents = [.. reads.Select(read => New(read.Action, read.PatientId, read.TableName, read.RowId, reason: null, actorId: null, details: null, part))];
        if (held is not null)
        {
            held.AddRange(auditEvents);
        }
        else
        {
            await WriteAsync(auditEvents, cancellationToken);
        }
    }

    // The reads one page makes through several of the PHI reader's calls, recorded together: within `reads`, every
    // event is held, then all are written at once as RecordAllAsync writes them, in one statement in the page's
    // transaction or given to the queue together, when `reads` returns, whatever it returns. Recorded call by call,
    // each took a statement and a round trip of its own: the review of a recorded visit (the expansion plan's step 16)
    // took five. If `reads` throws, nothing held is written, and the transaction it read in is rolled back, so nothing
    // it read is shown.
    public async Task<T> TogetherAsync<T>(Func<Task<T>> reads, CancellationToken cancellationToken)
    {
        List<AuditEvent> together = held = [];
        try
        {
            var result = await reads();
            held = null;
            if (together.Count > 0)
            {
                await WriteAsync(together, cancellationToken);
            }

            return result;
        }
        finally
        {
            held = null;
        }
    }

    async Task WriteAsync(IReadOnlyCollection<AuditEvent> auditEvents, CancellationToken cancellationToken)
    {
        if (queue is null)
        {
            await AuditEventBatch.InsertAsync(db, auditEvents, cancellationToken);
        }
        else
        {
            queue.Enqueue(auditEvents);
        }
    }

    AuditEvent New(string action, Guid? patientId, string? tableName, Guid? rowId, string? reason, Guid? actorId, string? details, string? part)
    {
        if (part is null && AuditActions.Reads.Contains(action))
        {
            throw new InvalidOperationException($"A '{action}' is recorded with the part of the record it was for (AuditParts).");
        }

        return new()
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
            Part = part,
        };
    }

    void Store(IReadOnlyCollection<AuditEvent> auditEvents)
    {
        if (held is not null)
        {
            held.AddRange(auditEvents);
        }
        else if (queue is null)
        {
            db.AuditEvents.AddRange(auditEvents);
        }
        else
        {
            queue.Enqueue(auditEvents);
        }
    }
}

// Where the cloud site puts the events it records until the on-prem app has them. Enqueue takes the events it is
// given, in order, or throws when one is an event the on-prem app wouldn't take. It does not promise they are on the
// disk when it returns: within a request the cloud site holds them and writes them together, durably, before any of
// the response is sent, and fails the request if it can't (RequestAuditEvents and CloudAuditFlushMiddleware in
// Ehr.Web, the expansion plan's step 10c). That, not this call, is what keeps a read from being shown without its
// record: whatever records through this must send its response through that middleware.
public interface IAuditQueue
{
    void Enqueue(IReadOnlyCollection<AuditEvent> auditEvents);
}

// Which server this app is, as the audit log names it: AuditSources.OnPrem or AuditSources.Cloud. The host registers it.
public sealed record AuditSource(string Name);
