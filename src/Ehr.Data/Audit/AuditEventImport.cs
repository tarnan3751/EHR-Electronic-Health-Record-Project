using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Ehr.Data.Audit;

// Inserts audit events the cloud app recorded and shipped (Ehr.Web, CloudAudit), keeping their IDs and times. The
// cloud app ships a batch again if it never heard back, so an event already here is skipped, not an error. Not ON
// CONFLICT DO NOTHING: that needs the table's SELECT policy, which only admins pass (db/policies/30-rls.sql). Each
// insert gets a savepoint instead, and a duplicate ID rolls back to it. Raw SQL, since the casts let a NULL parameter
// take its column's type. Runs inside the caller's transaction.
public static class AuditEventImport
{
    const string Savepoint = "audit_event";

    public static async Task ImportAsync(EhrDbContext db, IEnumerable<AuditEvent> events, CancellationToken cancellationToken)
    {
        var transaction = db.Database.CurrentTransaction
            ?? throw new InvalidOperationException("Import audit events inside a transaction.");
        foreach (var e in events)
        {
            await transaction.CreateSavepointAsync(Savepoint, cancellationToken);
            try
            {
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                    INSERT INTO ehr.audit_events (id, occurred_at, actor_id, action, table_name, row_id, patient_id, source, reason, details)
                    VALUES ({e.Id}, {e.OccurredAt}, {e.ActorId}::uuid, {e.Action}, {e.TableName}::text, {e.RowId}::uuid,
                            {e.PatientId}::uuid, {e.Source}, {e.Reason}::text, {e.Details}::jsonb)
                    """,
                    cancellationToken);
                await transaction.ReleaseSavepointAsync(Savepoint, cancellationToken);
            }
            catch (PostgresException duplicate) when (duplicate.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                await transaction.RollbackToSavepointAsync(Savepoint, cancellationToken);
            }
        }
    }
}
