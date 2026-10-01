using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Ehr.Data;

// Makes setting the user context the first statement of every transaction, as AGENTS.md requires. It uses
// set_config(..., true), which lasts only until the transaction ends, so the user never carries over to the next
// request on a pooled connection. Dapper queries that borrow the transaction get the same context.
public sealed class UserContextInterceptor(UserContext user) : DbTransactionInterceptor
{
    // No user is sent as an empty string, not as no setting. Once a connection has used app.user_id,
    // PostgreSQL reads it as '' rather than NULL, so policies have to treat '' as nobody either way:
    // NULLIF(current_setting('app.user_id', true), '')::uuid.
    const string Sql = "SELECT set_config('app.user_id', @user_id, true)";

    public override DbTransaction TransactionStarted(
        DbConnection connection, TransactionEndEventData eventData, DbTransaction result)
    {
        using var command = CreateCommand(connection, result);
        command.ExecuteNonQuery();
        return result;
    }

    public override async ValueTask<DbTransaction> TransactionStartedAsync(
        DbConnection connection, TransactionEndEventData eventData, DbTransaction result,
        CancellationToken cancellationToken = default)
    {
        await using var command = CreateCommand(connection, result);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return result;
    }

    DbCommand CreateCommand(DbConnection connection, DbTransaction transaction)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = Sql;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "user_id";
        parameter.Value = user.UserId?.ToString() ?? "";
        command.Parameters.Add(parameter);
        return command;
    }
}
