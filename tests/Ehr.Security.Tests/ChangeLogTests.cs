using Ehr.Testing;
using Npgsql;
using Xunit;

namespace Ehr.Security.Tests;

// db/policies/20-change-log.sql: the trigger logs every write to a synced table under the writing transaction's ID,
// even though the app role can't write the log itself.
public class ChangeLogTests(MigratedDatabase database) : IClassFixture<MigratedDatabase>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Each_insert_and_update_is_logged_under_its_transaction()
    {
        var id = Guid.CreateVersion7();
        await using var connection = new NpgsqlConnection(database.ConnectionStringFor("ehr_app", MigratedDatabase.AppPassword));
        await connection.OpenAsync(Cancel);

        var inserted = await WriteAsync(connection, "INSERT INTO ehr.quick_texts VALUES (@id, '.logged', 'Text', 1, now())", id);
        var updated = await WriteAsync(connection, "UPDATE ehr.quick_texts SET body = 'Changed', version = 2 WHERE id = @id", id);

        await using var superuser = new NpgsqlConnection(database.SuperuserConnectionString);
        await superuser.OpenAsync(Cancel);
        await using var command = new NpgsqlCommand(
            "SELECT transaction_id::text FROM ehr.change_log WHERE table_name = 'quick_texts' AND row_id = @id ORDER BY id",
            superuser);
        command.Parameters.AddWithValue("id", id);
        var logged = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync(Cancel))
        {
            while (await reader.ReadAsync(Cancel))
            {
                logged.Add(reader.GetString(0));
            }
        }

        Assert.Equal([inserted, updated], logged);
    }

    // Runs one statement in its own transaction and returns that transaction's ID.
    static async Task<string> WriteAsync(NpgsqlConnection connection, string sql, Guid id)
    {
        await using var transaction = await connection.BeginTransactionAsync(Cancel);
        await using (var write = new NpgsqlCommand(sql, connection, transaction))
        {
            write.Parameters.AddWithValue("id", id);
            await write.ExecuteNonQueryAsync(Cancel);
        }

        await using var transactionId = new NpgsqlCommand("SELECT pg_current_xact_id()::text", connection, transaction);
        var result = (string)(await transactionId.ExecuteScalarAsync(Cancel))!;
        await transaction.CommitAsync(Cancel);
        return result;
    }
}
