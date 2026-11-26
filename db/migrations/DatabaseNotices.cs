using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace Ehr.Migrations;

// Writes the database's notices (RAISE NOTICE) as they arrive, one to a line. A migration that changes patient data
// says this way what it changed, and the bundle (infra/migrate) prints it in the migration job's output: Npgsql itself
// logs notices only at the Debug level, which the bundle doesn't show.
public sealed class DatabaseNotices(TextWriter writer) : DbConnectionInterceptor
{
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData) => Listen(connection);

    public override Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        Listen(connection);
        return Task.CompletedTask;
    }

    void Listen(DbConnection connection)
    {
        if (connection is NpgsqlConnection npgsql)
        {
            // Once, however many times the same connection is opened.
            npgsql.Notice -= Write;
            npgsql.Notice += Write;
        }
    }

    void Write(object sender, NpgsqlNoticeEventArgs notice) => writer.WriteLine(notice.Notice.MessageText);
}
