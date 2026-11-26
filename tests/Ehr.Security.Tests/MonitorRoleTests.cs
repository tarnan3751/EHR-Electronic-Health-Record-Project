using System.Text.RegularExpressions;
using Ehr.Testing;
using Npgsql;
using Xunit;

namespace Ehr.Security.Tests;

// The expansion plan's step 21: the monitor role, which the monitoring profile's postgres_exporter logs in as, is
// created when the primary is first initialized (infra/compose/postgres/primary/initdb/10-infra-roles.sh), which this
// fixture doesn't run, so the test creates it with the script's own statement. It reads the replication slots' state
// and nothing else: no elevated attribute, no role granted to it, no privilege on the ehr schema or any of its tables,
// so it reads no row of patient data; and no REPLICATION, so it can't stream WAL.
public partial class MonitorRoleTests(MigratedDatabase database) : IClassFixture<MigratedDatabase>, IAsyncLifetime
{
    const string Password = "test-monitor";

    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    // Once for the class's database: xUnit makes a new instance for each test.
    public async ValueTask InitializeAsync()
    {
        if ((await AsSuperuserAsync("SELECT 1 FROM pg_roles WHERE rolname = 'monitor'", reader => "exists")).Count > 0)
        {
            return;
        }

        var script = await File.ReadAllTextAsync(RepositoryRoot.Combine("infra/compose/postgres/primary/initdb/10-infra-roles.sh"), Cancel);
        var create = CreateRole().Match(script);
        Assert.True(create.Success, "10-infra-roles.sh no longer creates the monitor role in one statement");
        await MigratedDatabase.ExecuteAsync(database.SuperuserConnectionString, create.Value);
        await MigratedDatabase.ExecuteAsync(database.SuperuserConnectionString, $"ALTER ROLE monitor PASSWORD '{Password}'");
    }

    public ValueTask DisposeAsync() => default;

    [Fact]
    public async Task The_monitor_role_has_no_elevated_attribute_and_no_role_granted_to_it()
    {
        // Superuser, create role, create database, replication, bypass row-level security, and its connection limit.
        Assert.Equal(["false false false false false 3"], await AsSuperuserAsync(
            """
            SELECT concat_ws(' ', rolsuper::text, rolcreaterole::text, rolcreatedb::text, rolreplication::text, rolbypassrls::text, rolconnlimit::text)
            FROM pg_roles WHERE rolname = 'monitor'
            """,
            reader => reader.GetString(0)));
        Assert.Empty(await AsSuperuserAsync(
            "SELECT g.rolname FROM pg_auth_members m JOIN pg_roles r ON r.oid = m.member JOIN pg_roles g ON g.oid = m.roleid WHERE r.rolname = 'monitor'",
            reader => reader.GetString(0)));
    }

    [Fact]
    public async Task The_monitor_role_can_read_no_table_of_the_ehr_schema()
    {
        var readable = await AsSuperuserAsync(
            """
            SELECT c.relname FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'ehr' AND c.relkind IN ('r', 'p', 'v', 'm')
              AND (has_table_privilege('monitor', c.oid, 'SELECT') OR has_any_column_privilege('monitor', c.oid, 'SELECT'))
            """,
            reader => reader.GetString(0));
        Assert.Empty(readable);
        Assert.Equal(["false"], await AsSuperuserAsync("SELECT has_schema_privilege('monitor', 'ehr', 'USAGE')::text", reader => reader.GetString(0)));

        await using var connection = new NpgsqlConnection(database.ConnectionStringFor("monitor", Password));
        await connection.OpenAsync(Cancel);
        await using var command = new NpgsqlCommand("SELECT count(*) FROM ehr.patients", connection);
        var refused = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteScalarAsync(Cancel));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, refused.SqlState);
    }

    // What postgres_exporter's replication_slots collector asks (v0.20.1, collector/pg_replication_slots.go), the one
    // collector the profile turns on: it runs as the monitor role with no grant.
    [Fact]
    public async Task The_monitor_role_can_read_the_replication_slots_state()
    {
        await using var connection = new NpgsqlConnection(database.ConnectionStringFor("monitor", Password));
        await connection.OpenAsync(Cancel);
        foreach (var sql in new[]
        {
            "SELECT slot_name, database, active, (case pg_is_in_recovery() when 't' then pg_wal_lsn_diff(pg_last_wal_receive_lsn(), restart_lsn) else pg_wal_lsn_diff(pg_current_wal_lsn(), restart_lsn) end) as pg_wal_lsn_diff FROM pg_replication_slots",
            "SELECT slot_name, slot_type, CASE WHEN pg_is_in_recovery() THEN pg_last_wal_receive_lsn() - '0/0' ELSE pg_current_wal_lsn() - '0/0' END AS current_wal_lsn, COALESCE(confirmed_flush_lsn, '0/0') - '0/0' AS confirmed_flush_lsn, active, safe_wal_size, wal_status FROM pg_replication_slots",
        })
        {
            await using var command = new NpgsqlCommand(sql, connection);
            await using var reader = await command.ExecuteReaderAsync(Cancel);
            while (await reader.ReadAsync(Cancel))
            {
            }
        }
    }

    async Task<List<string>> AsSuperuserAsync(string sql, Func<NpgsqlDataReader, string> read)
    {
        await using var connection = new NpgsqlConnection(database.SuperuserConnectionString);
        await connection.OpenAsync(Cancel);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(Cancel);
        var rows = new List<string>();
        while (await reader.ReadAsync(Cancel))
        {
            rows.Add(read(reader));
        }

        return rows;
    }

    [GeneratedRegex(@"CREATE ROLE monitor\b[^;]*;")]
    private static partial Regex CreateRole();
}
