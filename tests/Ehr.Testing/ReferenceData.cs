using System.Text;
using Npgsql;

namespace Ehr.Testing;

// Applies a file of db/reference as psql does: plain SQL, with its rows in COPY blocks, as pg_dump writes them
// ("COPY table (columns) FROM stdin;", tab-separated lines, then "\."). psql reads such a file as it stands
// (infra/migrate); a database driver can't, so this sends each statement run and each block in turn, on one
// connection, since the files load into temporary tables inside a transaction.
public static class ReferenceData
{
    public static async Task ApplyAsync(string connectionString, string path)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        using var file = new StreamReader(path, Encoding.UTF8);
        var sql = new StringBuilder();
        while (await file.ReadLineAsync() is { } line)
        {
            if (!line.StartsWith("COPY ", StringComparison.Ordinal) || !line.EndsWith(" FROM stdin;", StringComparison.Ordinal))
            {
                sql.Append(line).Append('\n');
                continue;
            }

            await ExecuteAsync(connection, sql);
            await using var rows = await connection.BeginTextImportAsync(line[..^1].Replace(" FROM stdin", " FROM STDIN", StringComparison.Ordinal));
            while (await file.ReadLineAsync() is { } row && row != "\\.")
            {
                await rows.WriteAsync(row);
                await rows.WriteAsync("\n");
            }
        }

        await ExecuteAsync(connection, sql);
    }

    static async Task ExecuteAsync(NpgsqlConnection connection, StringBuilder sql)
    {
        if (sql.ToString().Trim().Length > 0)
        {
            await using var command = new NpgsqlCommand(sql.ToString(), connection) { CommandTimeout = 300 };
            await command.ExecuteNonQueryAsync();
        }

        sql.Clear();
    }
}
