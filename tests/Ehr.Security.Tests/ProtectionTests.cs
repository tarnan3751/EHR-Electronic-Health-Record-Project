using Ehr.Testing;
using Npgsql;
using Xunit;

namespace Ehr.Security.Tests;

// CI gates from AGENTS.md: every PHI table has row-level security enabled and forced plus a write-audit trigger, and
// every table has a deliberate decision. The classes repeat the comments in db/policies/10-grants.sql on purpose,
// like ExpectedPrivileges: changing one means changing both, and a reviewer sees both.
public class ProtectionTests(MigratedDatabase database) : IClassFixture<MigratedDatabase>
{
    internal enum Protection
    {
        // Row-level security enabled and forced, and every write recorded by trigger (db/policies/40-audit.sql).
        Phi,

        // Row-level security enabled and forced; no trigger.
        Restricted,

        // Grants only.
        Internal,
    }

    internal static readonly Dictionary<string, Protection> ExpectedProtection = new()
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
        // Step 19: a staff account's passkeys, read and updated by a sign-in before anyone is signed in.
        ["staff_user_passkeys"] = Protection.Internal,
        // Every sync request looks its session up before anyone is signed in.
        ["desktop_sessions"] = Protection.Internal,
        ["patients"] = Protection.Phi,
        ["patient_assignments"] = Protection.Phi,
        ["break_glass_accesses"] = Protection.Phi,
        ["allergy_intolerances"] = Protection.Phi,
        ["medication_statements"] = Protection.Phi,
        ["conditions"] = Protection.Phi,
        ["chart_history"] = Protection.Phi,
        ["inbox_items"] = Protection.Phi,
        ["appointments"] = Protection.Phi,
        // Pre-visit forms (step 13e): sensitive items always.
        ["questionnaire_requests"] = Protection.Phi,
        ["questionnaire_responses"] = Protection.Phi,
        ["proxy_grants"] = Protection.Phi,
        ["result_acknowledgments"] = Protection.Phi,
        ["message_threads"] = Protection.Phi,
        ["messages"] = Protection.Phi,
        ["clinical_codes"] = Protection.Internal,
        ["observation_codes"] = Protection.Internal,
        ["vital_reference_ranges"] = Protection.Internal,
        ["order_result_codes"] = Protection.Internal,
        ["questionnaires"] = Protection.Internal,
        ["note_templates"] = Protection.Internal,
        ["drug_ingredients"] = Protection.Internal,
        ["drug_classes"] = Protection.Internal,
        ["drug_class_cross_reactions"] = Protection.Internal,
        ["drug_interactions"] = Protection.Internal,
        ["ai_note_reviews"] = Protection.Phi,
        ["ai_field_proposals"] = Protection.Phi,
        ["ai_chart_proposals"] = Protection.Phi,
        // Step 16: a clinician's consent to record a visit, and the conversation's transcript while its review is open.
        ["ambient_consents"] = Protection.Phi,
        ["ambient_transcripts"] = Protection.Phi,
        ["clinical_notes"] = Protection.Phi,
        ["note_addenda"] = Protection.Phi,
        ["visit_summaries"] = Protection.Phi,
        ["refill_requests"] = Protection.Phi,
        ["clinical_drafts"] = Protection.Phi,
        ["encounters"] = Protection.Phi,
        ["orders"] = Protection.Phi,
        // What can be ordered: a fixed catalog, no patient's data.
        ["order_catalog_items"] = Protection.Internal,
        ["observations"] = Protection.Phi,
        // Registration looks a code up before anyone is signed in, so this is grants-only; it holds hashes, not codes.
        ["patient_enrollments"] = Protection.Internal,
        ["proxy_invitations"] = Protection.Internal,
        // The lab's key, as its hash, read only by the functions of the lab's door.
        ["lab_keys"] = Protection.Internal,
        ["audit_events"] = Protection.Restricted,
        // An administrator's review of an emergency access (step 18): kept with the audit log, and like it restricted;
        // the row is its own record, never changed.
        ["emergency_access_reviews"] = Protection.Restricted,
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
