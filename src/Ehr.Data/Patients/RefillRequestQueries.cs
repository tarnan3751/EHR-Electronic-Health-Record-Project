using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Ehr.Data.Patients;

// A refill request as the portal is given it (ehr.portal_refill_requests): the medication it was asked for, its entry on
// the chart and its name then, the patient's note, whether the reader asked it ("you") or, for the patient themselves,
// someone they share their record with ("proxy"), when, its state (RefillRequestStatus), what was ordered or the care
// team's message, when it was answered, and whether it is sensitive; and nothing else of it.
public sealed record PortalRefillRequest(
    Guid MedicationStatementId, string MedicationDisplay, string? Note, string? Asked, DateTimeOffset RequestedAt, string Status,
    string? OrderedDisplay, string? DeclineMessage, DateTimeOffset? DecidedAt, bool Sensitive);

// Asks the database about refill requests (step 13d, db/policies/77-refill-requests.sql): for the portal, which reads no
// row of the table, the requests of the record the session is looking at; and, for an approval, the locks on the
// request's medication. Raw SQL, so it lives here (AGENTS.md); it runs in the caller's transaction, where the user
// context is set.
public static class RefillRequestQueries
{
    // Every waiting one and the latest answered ones, up to the limit, latest first, under the patient's rules for
    // messages and sensitive items.
    public static async Task<List<PortalRefillRequest>> PortalAsync(EhrDbContext db, int limit, CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText =
            """
            SELECT medication_statement_id, medication_display, note, asked, requested_at, status, ordered_display, decline_message, decided_at, sensitive
            FROM ehr.portal_refill_requests(@limit)
            """;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "limit";
        parameter.Value = limit;
        command.Parameters.Add(parameter);
        var rows = new List<PortalRefillRequest>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new PortalRefillRequest(reader.GetGuid(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetFieldValue<DateTimeOffset>(4), reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8), reader.GetBoolean(9)));
        }

        return rows;
    }

    // An approval's locks on the request's medication, taken before the request's row is updated: its row, then its
    // advisory lock, the order marking it sensitive takes them in (db/policies/77-refill-requests.sql, "Lock order").
    // Taken after the request's row, as the request's own trigger would, the two could each wait for the other.
    public const string LockMedicationSql =
        """
        SELECT 1 FROM ehr.medication_statements m
        WHERE m.id = (SELECT r.medication_statement_id FROM ehr.refill_requests r WHERE r.id = @request)
        FOR SHARE OF m;
        SELECT pg_advisory_xact_lock(hashtextextended('ehr.medication_statements:' || r.medication_statement_id::text, 0))
        FROM ehr.refill_requests r WHERE r.id = @request;
        """;

    public static async Task LockMedicationAsync(EhrDbContext db, Guid request, CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = LockMedicationSql;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "request";
        parameter.Value = request;
        command.Parameters.Add(parameter);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
