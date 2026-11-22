using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;

namespace Ehr.Data.Audit;

// Inserts audit events the app has just recorded in one statement, where EF Core sends a statement for each: for a page
// that records a read of each of many rows (AuditRecorder.RecordAllAsync in Ehr.Domain). The events' own IDs and times,
// as EF Core would insert them; a plain INSERT, which the table's policies allow everyone (AuditEvent says why nothing
// is read back). Raw SQL, so it lives here (AGENTS.md); it runs at once, in the caller's transaction, so the events
// commit with it.
public static class AuditEventBatch
{
    public static async Task InsertAsync(EhrDbContext db, IReadOnlyCollection<AuditEvent> events, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO ehr.audit_events (id, occurred_at, actor_id, action, table_name, row_id, patient_id, source, reason, details, part)
            SELECT * FROM unnest(@ids, @times, @actors, @actions, @tables, @rows, @patients, @sources, @reasons, @details::jsonb[], @parts)
            """, (NpgsqlConnection)db.Database.GetDbConnection(), (NpgsqlTransaction?)db.Database.CurrentTransaction?.GetDbTransaction());
        command.Parameters.Add(new NpgsqlParameter<Guid[]>("ids", [.. events.Select(e => e.Id)]));
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset[]>("times", [.. events.Select(e => e.OccurredAt)]));
        command.Parameters.Add(new NpgsqlParameter<Guid?[]>("actors", [.. events.Select(e => e.ActorId)]));
        command.Parameters.Add(new NpgsqlParameter<string[]>("actions", [.. events.Select(e => e.Action)]));
        command.Parameters.Add(new NpgsqlParameter<string?[]>("tables", [.. events.Select(e => e.TableName)]));
        command.Parameters.Add(new NpgsqlParameter<Guid?[]>("rows", [.. events.Select(e => e.RowId)]));
        command.Parameters.Add(new NpgsqlParameter<Guid?[]>("patients", [.. events.Select(e => e.PatientId)]));
        command.Parameters.Add(new NpgsqlParameter<string[]>("sources", [.. events.Select(e => e.Source)]));
        command.Parameters.Add(new NpgsqlParameter<string?[]>("reasons", [.. events.Select(e => e.Reason)]));
        command.Parameters.Add(new NpgsqlParameter<string?[]>("details", [.. events.Select(e => e.Details)]) { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text });
        command.Parameters.Add(new NpgsqlParameter<string?[]>("parts", [.. events.Select(e => e.Part)]));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
