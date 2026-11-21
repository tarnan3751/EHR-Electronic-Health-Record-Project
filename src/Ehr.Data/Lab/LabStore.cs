using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;

namespace Ehr.Data.Lab;

// The apps' only way through the lab's door (the expansion plan's step 12): the functions in db/policies/76-lab.sql,
// each given the hash of the lab's key. Raw SQL, so it lives in Ehr.Data (AGENTS.md); it runs on the context's
// connection and in its transaction. Every argument is a parameter. A call without the right hash, or with someone
// signed in, is refused by the database with insufficient_privilege.
public static class LabStore
{
    // The lab orders signed by transactions from `since` up to the watermark, still waiting for results, as the JSON
    // the database writes; the database records each one handed over.
    public static async Task<LabHandOver> OrdersAsync(EhrDbContext db, string keyHash, ulong since, CancellationToken cancellationToken)
    {
        await using var command = Command(db, "SELECT watermark::text, more, orders::text FROM ehr.lab_orders(@key, @since::xid8)",
            ("key", keyHash, NpgsqlDbType.Text), ("since", since.ToString(CultureInfo.InvariantCulture), NpgsqlDbType.Text));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return new(ulong.Parse(reader.GetString(0), CultureInfo.InvariantCulture), reader.GetBoolean(1), reader.GetString(2));
    }

    // Files the lab's results for one order, all or none: the word the database answers with (76-lab.sql).
    public static async Task<string> FileAsync(
        EhrDbContext db, string keyHash, Guid order, DateTimeOffset collectedAt, IReadOnlyList<(string Code, decimal Value, string Unit)> results,
        CancellationToken cancellationToken)
    {
        await using var command = Command(db, "SELECT ehr.file_lab_results(@key, @order, @collected, @codes, @values, @units)",
            ("key", keyHash, NpgsqlDbType.Text), ("order", order, NpgsqlDbType.Uuid), ("collected", collectedAt, NpgsqlDbType.TimestampTz),
            ("codes", results.Select(r => r.Code).ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Text),
            ("values", results.Select(r => r.Value).ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Numeric),
            ("units", results.Select(r => r.Unit).ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Text));
        return (string)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    // Whether a lab is connected at all (a key's hash is stored): both apps may ask.
    public static async Task<bool> ConnectedAsync(EhrDbContext db, CancellationToken cancellationToken)
    {
        await using var command = Command(db, "SELECT ehr.lab_connected()");
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    static DbCommand Command(EhrDbContext db, string sql, params (string Name, object Value, NpgsqlDbType Type)[] parameters)
    {
        var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = sql;
        foreach (var (name, value, type) in parameters)
        {
            command.Parameters.Add(new NpgsqlParameter(name, type) { Value = value });
        }

        return command;
    }
}

// What the lab is handed: where its next ask starts, whether more was waiting than one answer holds, and the orders,
// as the JSON the database wrote (a list of {id, signedAt, tests, conditions}).
public sealed record LabHandOver(ulong Watermark, bool More, string Orders);
