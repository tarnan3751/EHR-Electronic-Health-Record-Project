using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;

namespace Ehr.Data.Audit;

// One line of ehr.record_views (db/policies/42-record-views.sql, the expansion plan's step 13c): a view of the record a
// portal session is looking at. Who is "you", "staff", "proxy" or "lab"; Name is a staff member's name or, for the
// patient, a proxy's address; Role is "clinician" or "administrator" for staff; Part is one of AuditParts, the part the
// read was recorded for (step 13c2), or of RecordViewParts, for one recorded without; Sensitive is whether the view
// showed the patient's sensitive items; More is whether the log has more lines past this page, the same on every line.
// No ID of anything.
public sealed record RecordViewRow(DateTimeOffset OccurredAt, string Who, string? Name, string? Role, string Part, bool Sensitive, bool Emergency, bool More);

// Reads the function. Raw SQL, so it lives here (AGENTS.md); it runs in the caller's transaction, where the user
// context is set, and takes the page's two moments only: the database decides whose record it is.
public static class RecordViews
{
    public static async Task<List<RecordViewRow>> ReadAsync(EhrDbContext db, DateTimeOffset? olderThan, DateTimeOffset? newerThan, CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = "SELECT occurred_at, who, name, role, part, sensitive, emergency, more FROM ehr.record_views(@older, @newer)";
        command.Parameters.Add(new NpgsqlParameter("older", NpgsqlDbType.TimestampTz) { Value = (object?)olderThan ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("newer", NpgsqlDbType.TimestampTz) { Value = (object?)newerThan ?? DBNull.Value });
        var rows = new List<RecordViewRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new RecordViewRow(reader.GetFieldValue<DateTimeOffset>(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4), reader.GetBoolean(5), reader.GetBoolean(6), reader.GetBoolean(7)));
        }

        return rows;
    }
}

// The parts ehr.record_views names a view recorded without its part (before step 13c2, or by an app older than its
// migration) by, from the table it read and the action: most pages recorded a read of the patient's row whatever part of
// the record they showed, so most such views are Record. A view recorded with its part is named by that (AuditParts),
// or Record to a viewer who may not be told it. The portal puts each in words; one it doesn't know reads as Record.
public static class RecordViewParts
{
    public const string Record = "record";
    public const string RecordSensitive = "record-sensitive";
    public const string Download = "download";
    public const string DownloadSensitive = "download-sensitive";
    public const string Note = "note";

    // A pre-visit form (step 13e), from its two tables: the same key as the part its pages' reads are recorded with
    // (AuditParts.Forms), so a form's view has one name and one wording however it was recorded.
    public const string Form = AuditParts.Forms;
    public const string Summary = "summary";
    public const string Messages = "messages";
    public const string LabOrder = "lab-order";
    public const string Views = "views";

    public static readonly string[] All = [Record, RecordSensitive, Download, DownloadSensitive, Note, Form, Summary, Messages, LabOrder, Views];
}
