using Ehr.Testing;
using Npgsql;
using Xunit;

namespace Ehr.Security.Tests;

// CI gate from AGENTS.md: no runtime role owns a table or has BYPASSRLS. The runtime roles are ehr_app and
// ehr_read; ehr_owner runs migrations only.
public class RuntimeRoleTests(MigratedDatabase database) : IClassFixture<MigratedDatabase>
{
    static readonly string[] RuntimeRoles = ["ehr_app", "ehr_read"];

    // The privileges each runtime role should have, table by table. This repeats db/policies/10-grants.sql on
    // purpose: changing a grant means changing both, and a reviewer sees both.
    static readonly Dictionary<(string Role, string Table), string[]> ExpectedPrivileges = new()
    {
        [("ehr_app", "quick_texts")] = ["SELECT", "INSERT", "UPDATE"],
        [("ehr_read", "quick_texts")] = ["SELECT"],
        [("ehr_app", "data_protection_keys")] = ["SELECT", "INSERT"],
        [("ehr_read", "data_protection_keys")] = ["SELECT"],
        [("ehr_app", "change_log")] = ["SELECT"],
        [("ehr_read", "change_log")] = ["SELECT"],
        [("ehr_app", "sync_operations")] = ["SELECT", "INSERT"],
        [("ehr_read", "sync_operations")] = [],
        [("ehr_app", "__ef_migrations_history")] = [],
        [("ehr_read", "__ef_migrations_history")] = [],
    };

    static readonly string[] TablePrivileges = ["SELECT", "INSERT", "UPDATE", "DELETE", "TRUNCATE", "REFERENCES", "TRIGGER"];

    [Fact]
    public async Task Runtime_roles_have_no_elevated_attributes()
    {
        var roles = await QueryAsync(
            """
            SELECT rolname, rolsuper, rolcreaterole, rolcreatedb, rolreplication, rolbypassrls
            FROM pg_roles WHERE rolname = ANY(@roles)
            """,
            reader => (Name: reader.GetString(0), Elevated: Enumerable.Range(1, 5).Any(reader.GetBoolean)),
            ("roles", RuntimeRoles));

        Assert.Equal(RuntimeRoles.Order(), roles.Select(r => r.Name).Order());
        Assert.All(roles, role => Assert.False(role.Elevated, $"{role.Name} has an elevated attribute"));
    }

    [Fact]
    public async Task Migration_owner_cannot_bypass_security()
    {
        var owner = await QueryAsync(
            "SELECT rolsuper, rolreplication, rolbypassrls FROM pg_roles WHERE rolname = 'ehr_owner'",
            reader => reader.GetBoolean(0) || reader.GetBoolean(1) || reader.GetBoolean(2));

        Assert.Equal([false], owner);
    }

    [Fact]
    public async Task Runtime_roles_own_nothing()
    {
        var owned = await QueryAsync(
            """
            SELECT 'relation ' || c.relname FROM pg_class c JOIN pg_roles r ON r.oid = c.relowner WHERE r.rolname = ANY(@roles)
            UNION ALL SELECT 'schema ' || n.nspname FROM pg_namespace n JOIN pg_roles r ON r.oid = n.nspowner WHERE r.rolname = ANY(@roles)
            UNION ALL SELECT 'function ' || p.proname FROM pg_proc p JOIN pg_roles r ON r.oid = p.proowner WHERE r.rolname = ANY(@roles)
            UNION ALL SELECT 'database ' || d.datname FROM pg_database d JOIN pg_roles r ON r.oid = d.datdba WHERE r.rolname = ANY(@roles)
            """,
            reader => reader.GetString(0),
            ("roles", RuntimeRoles));

        Assert.Empty(owned);
    }

    [Fact]
    public async Task Every_table_has_a_deliberate_privilege_decision()
    {
        var tables = await QueryAsync(
            "SELECT tablename FROM pg_tables WHERE schemaname = 'ehr'",
            reader => reader.GetString(0));

        var undecided = tables.Where(table => RuntimeRoles.Any(role => !ExpectedPrivileges.ContainsKey((role, table))));
        Assert.True(!undecided.Any(),
            $"Add these tables to db/policies/10-grants.sql and to ExpectedPrivileges: {string.Join(", ", undecided)}");
    }

    [Fact]
    public async Task Runtime_roles_have_exactly_their_granted_privileges()
    {
        await AssertExactPrivilegesAsync();
    }

    [Fact]
    public async Task Applying_again_removes_privileges_granted_by_hand()
    {
        await MigratedDatabase.ExecuteAsync(database.SuperuserConnectionString,
            "GRANT DELETE, TRUNCATE ON ehr.quick_texts TO ehr_app; GRANT INSERT ON ehr.data_protection_keys TO ehr_read");

        await database.ApplyAsync();

        await AssertExactPrivilegesAsync();
    }

    [Theory]
    [InlineData("ehr_app", MigratedDatabase.AppPassword, "CREATE TABLE ehr.extra (id int)")]
    [InlineData("ehr_app", MigratedDatabase.AppPassword, "DROP TABLE ehr.quick_texts")]
    [InlineData("ehr_app", MigratedDatabase.AppPassword, "DELETE FROM ehr.quick_texts")]
    [InlineData("ehr_app", MigratedDatabase.AppPassword, "CREATE ROLE intruder")]
    [InlineData("ehr_read", MigratedDatabase.ReadPassword, "INSERT INTO ehr.quick_texts VALUES (gen_random_uuid(), '.x', 'x', 1, now())")]
    [InlineData("ehr_app", MigratedDatabase.AppPassword, "INSERT INTO ehr.change_log (table_name, row_id) VALUES ('quick_texts', gen_random_uuid())")]
    [InlineData("ehr_app", MigratedDatabase.AppPassword, "DELETE FROM ehr.change_log")]
    [InlineData("ehr_app", MigratedDatabase.AppPassword, "DELETE FROM ehr.sync_operations")]
    [InlineData("ehr_read", MigratedDatabase.ReadPassword, "SELECT * FROM ehr.sync_operations")]
    public async Task Runtime_roles_are_refused_outside_their_grants(string role, string password, string sql)
    {
        var error = await Assert.ThrowsAsync<PostgresException>(
            () => MigratedDatabase.ExecuteAsync(database.ConnectionStringFor(role, password), sql));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
    }

    // A function that runs as its owner (SECURITY DEFINER) can do what the owner can, so nobody else may call it
    // directly, and its search_path is pinned so a caller can't put their own objects in front of the ones it uses.
    [Fact]
    public async Task Functions_that_run_as_their_owner_are_locked_down()
    {
        var functions = await QueryAsync(
            """
            SELECT p.proname,
                   coalesce(exists (SELECT FROM unnest(p.proconfig) AS c WHERE c LIKE 'search_path=%'), false),
                   p.proacl IS NOT NULL AND NOT exists (SELECT FROM aclexplode(p.proacl) WHERE grantee = 0)
            FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
            WHERE n.nspname = 'ehr' AND p.prosecdef
            """,
            reader => (Name: reader.GetString(0), PinsSearchPath: reader.GetBoolean(1), NotPublic: reader.GetBoolean(2)));

        Assert.NotEmpty(functions);
        Assert.All(functions, function =>
        {
            Assert.True(function.PinsSearchPath, $"{function.Name} doesn't set search_path");
            Assert.True(function.NotPublic, $"{function.Name} can be called by PUBLIC");
        });
    }

    [Fact]
    public async Task Applying_everything_again_is_harmless()
    {
        await database.ApplyAsync();
    }

    async Task AssertExactPrivilegesAsync()
    {
        foreach (var ((role, table), expected) in ExpectedPrivileges)
        {
            var actual = await QueryAsync(
                "SELECT p FROM unnest(@privileges) AS p WHERE has_table_privilege(@role, 'ehr.' || quote_ident(@table), p)",
                reader => reader.GetString(0),
                ("privileges", TablePrivileges), ("role", role), ("table", table));

            Assert.True(expected.Order().SequenceEqual(actual.Order()),
                $"{role} on {table}: expected [{string.Join(", ", expected)}], has [{string.Join(", ", actual)}]");
        }
    }

    async Task<List<T>> QueryAsync<T>(string sql, Func<NpgsqlDataReader, T> read, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(database.SuperuserConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var rows = new List<T>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            rows.Add(read(reader));
        }

        return rows;
    }
}
