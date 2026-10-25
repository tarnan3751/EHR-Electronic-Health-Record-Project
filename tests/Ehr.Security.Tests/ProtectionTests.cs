using Ehr.Testing;
using Npgsql;
using Xunit;

namespace Ehr.Security.Tests;

// CI gates from AGENTS.md: every PHI table has row-level security enabled and forced plus a write-audit trigger, and
// every table has a deliberate decision. The classes repeat the comments in db/policies/10-grants.sql on purpose,
// like ExpectedPrivileges: changing one means changing both, and a reviewer sees both.
public class ProtectionTests(MigratedDatabase database) : IClassFixture<MigratedDatabase>
{
    enum Protection
    {
        // Row-level security enabled and forced, and every write recorded by trigger (db/policies/40-audit.sql).
        Phi,

        // Row-level security enabled and forced; no trigger.
        Restricted,

        // Grants only.
        Internal,
    }

    static readonly Dictionary<string, Protection> ExpectedProtection = new()
    {
        ["quick_texts"] = Protection.Internal,
        ["data_protection_keys"] = Protection.Internal,
        ["change_log"] = Protection.Internal,
        ["sync_operations"] = Protection.Internal,
        ["__ef_migrations_history"] = Protection.Internal,
        // Sign-in has to find an account before anyone is signed in, so the account tables are grants-only.
        ["staff_users"] = Protection.Internal,
        ["staff_user_claims"] = Protection.Internal,
        ["staff_user_logins"] = Protection.Internal,
        ["staff_user_tokens"] = Protection.Internal,
        ["patient_users"] = Protection.Internal,
        ["patient_user_claims"] = Protection.Internal,
        ["patient_user_logins"] = Protection.Internal,
        ["patient_user_tokens"] = Protection.Internal,
        ["patients"] = Protection.Phi,
        ["audit_events"] = Protection.Restricted,
    };

    [Fact]
    public async Task Every_table_has_a_deliberate_protection_decision()
    {
        var tables = await QueryAsync("SELECT tablename FROM pg_tables WHERE schemaname = 'ehr'", reader => reader.GetString(0));

        var undecided = tables.Where(table => !ExpectedProtection.ContainsKey(table)).ToList();
        Assert.True(undecided.Count == 0,
            $"Decide these tables' protection in db/policies and add them to ExpectedProtection: {string.Join(", ", undecided)}");
    }

    [Fact]
    public async Task Row_level_security_is_enabled_and_forced_exactly_where_decided()
    {
        var actual = await QueryAsync(
            """
            SELECT c.relname, c.relrowsecurity, c.relforcerowsecurity
            FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'ehr' AND c.relkind = 'r'
            """,
            reader => (Table: reader.GetString(0), Enabled: reader.GetBoolean(1), Forced: reader.GetBoolean(2)));

        foreach (var (table, protection) in ExpectedProtection)
        {
            var row = Assert.Single(actual, a => a.Table == table);
            var expected = protection != Protection.Internal;
            Assert.True(row.Enabled == expected, $"{table}: row-level security enabled should be {expected}");
            Assert.True(row.Forced == expected, $"{table}: row-level security forced should be {expected}");
        }
    }

    [Fact]
    public async Task The_write_audit_trigger_is_on_every_phi_table_and_nowhere_else()
    {
        var audited = await QueryAsync(
            """
            SELECT c.relname FROM pg_trigger t
            JOIN pg_class c ON c.oid = t.tgrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            JOIN pg_proc p ON p.oid = t.tgfoid
            WHERE n.nspname = 'ehr' AND NOT t.tgisinternal AND p.proname = 'record_write'
            """,
            reader => reader.GetString(0));

        var expected = ExpectedProtection.Where(e => e.Value == Protection.Phi).Select(e => e.Key);
        Assert.Equal(expected.Order(), audited.Order());
    }

    async Task<List<T>> QueryAsync<T>(string sql, Func<NpgsqlDataReader, T> read)
    {
        await using var connection = new NpgsqlConnection(database.SuperuserConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var rows = new List<T>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            rows.Add(read(reader));
        }

        return rows;
    }
}
