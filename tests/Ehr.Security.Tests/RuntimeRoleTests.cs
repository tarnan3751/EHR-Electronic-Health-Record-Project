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
        [("ehr_app", "staff_users")] = ["SELECT", "INSERT", "UPDATE"],
        [("ehr_read", "staff_users")] = ["SELECT"],
        [("ehr_app", "staff_user_claims")] = ["SELECT", "INSERT", "UPDATE"],
        [("ehr_read", "staff_user_claims")] = ["SELECT"],
        [("ehr_app", "staff_user_logins")] = ["SELECT", "INSERT", "UPDATE"],
        [("ehr_read", "staff_user_logins")] = ["SELECT"],
        [("ehr_app", "staff_user_tokens")] = ["SELECT", "INSERT", "UPDATE"],
        [("ehr_read", "staff_user_tokens")] = ["SELECT"],
        [("ehr_app", "patient_users")] = ["SELECT", "INSERT", "UPDATE"],
        [("ehr_read", "patient_users")] = ["SELECT"],
        [("ehr_app", "patient_user_claims")] = ["SELECT", "INSERT", "UPDATE"],
        [("ehr_read", "patient_user_claims")] = ["SELECT"],
        [("ehr_app", "patient_user_logins")] = ["SELECT", "INSERT", "UPDATE"],
        [("ehr_read", "patient_user_logins")] = ["SELECT"],
        [("ehr_app", "patient_user_tokens")] = ["SELECT", "INSERT", "UPDATE"],
        [("ehr_read", "patient_user_tokens")] = ["SELECT"],
        [("ehr_app", "desktop_sessions")] = ["SELECT", "INSERT", "UPDATE"],
        [("ehr_read", "desktop_sessions")] = ["SELECT"],
        [("ehr_app", "patients")] = ["SELECT", "INSERT", "UPDATE"],
        [("ehr_read", "patients")] = ["SELECT"],
        [("ehr_app", "patient_assignments")] = ["SELECT", "INSERT", "UPDATE"],
        [("ehr_read", "patient_assignments")] = ["SELECT"],
        [("ehr_app", "break_glass_accesses")] = ["SELECT", "INSERT"],
        [("ehr_read", "break_glass_accesses")] = ["SELECT"],
        // Chart lists: clinicians add and change entries, and nothing deletes one. Their history is the database's to write.
        [("ehr_app", "allergy_intolerances")] = ["SELECT", "INSERT", "UPDATE"],
        // Messages: sent by insert, never edited; marking one read is a column grant.
        [("ehr_app", "result_acknowledgments")] = ["SELECT", "INSERT"],
        [("ehr_read", "result_acknowledgments")] = ["SELECT"],
        // Step 6c: no privilege at all. Enrollment codes and invitations are reached only through the functions in
        // db/policies/45-codes.sql, one code at a time (CodeLookupSecurityTests).
        [("ehr_app", "patient_enrollments")] = [],
        [("ehr_read", "patient_enrollments")] = [],
        [("ehr_app", "proxy_invitations")] = [],
        [("ehr_read", "proxy_invitations")] = [],
        // Step 12: no privilege at all. The lab's key is found only by the functions of its door, from its hash
        // (db/policies/76-lab.sql, LabSecurityTests).
        [("ehr_app", "lab_keys")] = [],
        [("ehr_read", "lab_keys")] = [],
        [("ehr_app", "proxy_grants")] = ["SELECT", "INSERT"],
        [("ehr_read", "proxy_grants")] = ["SELECT"],
        [("ehr_app", "message_threads")] = ["SELECT", "INSERT"],
        [("ehr_read", "message_threads")] = ["SELECT"],
        [("ehr_app", "messages")] = ["SELECT", "INSERT"],
        [("ehr_read", "messages")] = ["SELECT"],
        // Appointments: booked by insert; rescheduling, cancelling, checking in and no-shows are column grants.
        [("ehr_app", "appointments")] = ["SELECT", "INSERT"],
        [("ehr_read", "appointments")] = ["SELECT"],
        // The staff inbox: read; marking an item done is a column grant.
        [("ehr_app", "inbox_items")] = ["SELECT"],
        [("ehr_read", "inbox_items")] = ["SELECT"],
        [("ehr_app", "chart_history")] = ["SELECT"],
        [("ehr_read", "chart_history")] = ["SELECT"],
        [("ehr_app", "clinical_codes")] = ["SELECT"],
        // The rest of the reference data: read only.
        [("ehr_app", "observation_codes")] = ["SELECT"],
        [("ehr_read", "observation_codes")] = ["SELECT"],
        [("ehr_app", "vital_reference_ranges")] = ["SELECT"],
        [("ehr_read", "vital_reference_ranges")] = ["SELECT"],
        [("ehr_app", "order_result_codes")] = ["SELECT"],
        [("ehr_read", "order_result_codes")] = ["SELECT"],
        [("ehr_app", "questionnaires")] = ["SELECT"],
        [("ehr_read", "questionnaires")] = ["SELECT"],
        [("ehr_app", "drug_ingredients")] = ["SELECT"],
        [("ehr_read", "drug_ingredients")] = ["SELECT"],
        [("ehr_app", "drug_classes")] = ["SELECT"],
        [("ehr_read", "drug_classes")] = ["SELECT"],
        [("ehr_app", "drug_class_cross_reactions")] = ["SELECT"],
        [("ehr_read", "drug_class_cross_reactions")] = ["SELECT"],
        [("ehr_app", "drug_interactions")] = ["SELECT"],
        [("ehr_read", "drug_interactions")] = ["SELECT"],
        [("ehr_read", "clinical_codes")] = ["SELECT"],
        [("ehr_read", "allergy_intolerances")] = ["SELECT"],
        [("ehr_app", "medication_statements")] = ["SELECT", "INSERT", "UPDATE"],
        [("ehr_read", "medication_statements")] = ["SELECT"],
        [("ehr_app", "conditions")] = ["SELECT", "INSERT", "UPDATE"],
        [("ehr_read", "conditions")] = ["SELECT"],
        [("ehr_app", "ai_note_reviews")] = ["SELECT", "INSERT", "UPDATE"],
        [("ehr_read", "ai_note_reviews")] = ["SELECT"],
        [("ehr_app", "ai_field_proposals")] = ["SELECT", "INSERT", "UPDATE"],
        [("ehr_read", "ai_field_proposals")] = ["SELECT"],
        [("ehr_app", "clinical_notes")] = ["SELECT", "INSERT"],
        [("ehr_read", "clinical_notes")] = ["SELECT"],
        [("ehr_app", "note_addenda")] = ["SELECT", "INSERT"],
        [("ehr_read", "note_addenda")] = ["SELECT"],
        // Step 13a: a visit summary is released, never edited or deleted. Withdrawing it is a column grant
        // (ExpectedColumnPrivileges).
        [("ehr_app", "visit_summaries")] = ["SELECT", "INSERT"],
        [("ehr_read", "visit_summaries")] = ["SELECT"],
        [("ehr_app", "clinical_drafts")] = ["SELECT", "INSERT", "UPDATE"],
        // UC 14 adds replica draft reads under the same private-author/active-panel RLS as the primary.
        [("ehr_read", "clinical_drafts")] = ["SELECT"],
        // UC 1: an order is never edited or deleted. Discontinuing one updates three of its columns, which is a column
        // grant (ExpectedColumnPrivileges), not a table one. The catalog is read only.
        [("ehr_app", "orders")] = ["SELECT", "INSERT"],
        [("ehr_read", "orders")] = ["SELECT"],
        [("ehr_app", "order_catalog_items")] = ["SELECT"],
        [("ehr_read", "order_catalog_items")] = ["SELECT"],
        // A visit is created by checking a patient in for an appointment; nothing updates or deletes one.
        [("ehr_app", "encounters")] = ["SELECT", "INSERT"],
        [("ehr_read", "encounters")] = ["SELECT"],
        [("ehr_app", "observations")] = ["SELECT", "INSERT"],
        [("ehr_read", "observations")] = ["SELECT"],
        // Read by column (ExpectedColumnPrivileges): every one but details, the values a write changed.
        [("ehr_app", "audit_events")] = ["INSERT"],
        [("ehr_read", "audit_events")] = [],
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
    [InlineData("ehr_app", MigratedDatabase.AppPassword, "DELETE FROM ehr.patients")]
    [InlineData("ehr_app", MigratedDatabase.AppPassword, "DELETE FROM ehr.staff_users")]
    [InlineData("ehr_app", MigratedDatabase.AppPassword, "INSERT INTO ehr.allergy_intolerances (id, patient_id, display, clinical_status, recorded_at) VALUES (gen_random_uuid(), gen_random_uuid(), 'x', 'active', now())")]
    // Chart lists are added to and changed (ChartListSecurityTests), never deleted; their history and the reference
    // codes are never written at runtime.
    [InlineData("ehr_app", MigratedDatabase.AppPassword, "DELETE FROM ehr.medication_statements")]
    [InlineData("ehr_app", MigratedDatabase.AppPassword, "DELETE FROM ehr.allergy_intolerances")]
    [InlineData("ehr_read", MigratedDatabase.ReadPassword, "UPDATE ehr.medication_statements SET display = 'changed'")]
    [InlineData("ehr_app", MigratedDatabase.AppPassword, "UPDATE ehr.chart_history SET after = '{}'")]
    [InlineData("ehr_app", MigratedDatabase.AppPassword, "DELETE FROM ehr.chart_history")]
    [InlineData("ehr_app", MigratedDatabase.AppPassword, "INSERT INTO ehr.chart_history (id, patient_id, table_name, row_id, changed_at, after) VALUES (gen_random_uuid(), gen_random_uuid(), 'conditions', gen_random_uuid(), now(), '{}')")]
    [InlineData("ehr_app", MigratedDatabase.AppPassword, "UPDATE ehr.clinical_codes SET display = 'changed'")]
    [InlineData("ehr_app", MigratedDatabase.AppPassword, "DELETE FROM ehr.clinical_codes")]
    [InlineData("ehr_app", MigratedDatabase.AppPassword, "DELETE FROM ehr.conditions")]
    [InlineData("ehr_read", MigratedDatabase.ReadPassword, "INSERT INTO ehr.encounters (id, patient_id, status, class_code, period_start) VALUES (gen_random_uuid(), gen_random_uuid(), 'finished', 'AMB', now())")]
    // The apps update two columns of an observation, to mark a vital sign entered in error (step 9b), and a trigger
    // holds that to exactly one change (VitalSignCorrectionSecurityTests). Its value, and every other column, never.
    [InlineData("ehr_app", MigratedDatabase.AppPassword, "UPDATE ehr.observations SET value = 1")]
    [InlineData("ehr_app", MigratedDatabase.AppPassword, "UPDATE ehr.observations SET status = 'cancelled', issued_at = now()")]
    [InlineData("ehr_app", MigratedDatabase.AppPassword, "UPDATE ehr.audit_events SET reason = 'changed'")]
    [InlineData("ehr_app", MigratedDatabase.AppPassword, "DELETE FROM ehr.audit_events")]
    [InlineData("ehr_app", MigratedDatabase.AppPassword, "SELECT details FROM ehr.audit_events")]
    [InlineData("ehr_app", MigratedDatabase.AppPassword, "SELECT * FROM ehr.audit_events")]
    [InlineData("ehr_read", MigratedDatabase.ReadPassword, "SELECT details FROM ehr.audit_events")]
    [InlineData("ehr_app", MigratedDatabase.AppPassword, "UPDATE ehr.audit_events SET sensitive = true")]
    [InlineData("ehr_read", MigratedDatabase.ReadPassword, "INSERT INTO ehr.audit_events (id, occurred_at, action, source) VALUES (uuidv7(), now(), 'read', 'cloud')")]
    // Step 12: the lab's key is nobody's to read or set but the owner's, and only the on-prem app's role uses the door.
    [InlineData("ehr_app", MigratedDatabase.AppPassword, "SELECT * FROM ehr.lab_keys")]
    [InlineData("ehr_app", MigratedDatabase.AppPassword, "INSERT INTO ehr.lab_keys (key_hash) VALUES (repeat('a', 64))")]
    [InlineData("ehr_app", MigratedDatabase.AppPassword, "SELECT ehr.set_lab_key(repeat('a', 64))")]
    [InlineData("ehr_app", MigratedDatabase.AppPassword, "SELECT ehr.require_lab(repeat('a', 64))")]
    [InlineData("ehr_read", MigratedDatabase.ReadPassword, "SELECT * FROM ehr.lab_keys")]
    [InlineData("ehr_read", MigratedDatabase.ReadPassword, "SELECT * FROM ehr.lab_orders(repeat('a', 64), '0')")]
    [InlineData("ehr_read", MigratedDatabase.ReadPassword, "SELECT ehr.file_lab_results(repeat('a', 64), gen_random_uuid(), now(), ARRAY['718-7'], ARRAY[1.0], ARRAY['g/dL'])")]
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

    // Grants on single columns, which has_table_privilege doesn't see: each one a runtime role holds beyond its table
    // grants. This repeats db/policies/10-grants.sql on purpose, as ExpectedPrivileges does. A column grant made
    // anywhere else, or not listed here, fails.
    static readonly Dictionary<(string Role, string Table, string Privilege), string[]> ExpectedColumnPrivileges = new()
    {
        // UC 1: discontinuing an order.
        [("ehr_app", "orders", "UPDATE")] = ["discontinue_reason", "replaced_by_order_id", "status"],
        [("ehr_app", "appointments", "UPDATE")] = ["cancel_reason", "encounter_id", "minutes", "reason", "starts_at", "status"],
        [("ehr_app", "messages", "UPDATE")] = ["read_at"],
        // FR-19: revoking a proxy grant, and the patient allowing sensitive items.
        [("ehr_app", "proxy_grants", "UPDATE")] = ["include_sensitive", "revoked_at", "revoked_by"],
        // Step 10b: marking a signed note or addendum sensitive, or unmarking it.
        [("ehr_app", "clinical_notes", "UPDATE")] = ["sensitive", "sensitive_reason"],
        [("ehr_app", "note_addenda", "UPDATE")] = ["sensitive", "sensitive_reason"],
        // Step 13a: withdrawing a visit summary, and marking it sensitive or unmarking it, each with a reason.
        [("ehr_app", "visit_summaries", "UPDATE")] = ["sensitive", "sensitive_reason", "withdrawn_reason"],
        // Step 9b: marking a vital sign entered in error.
        [("ehr_app", "observations", "UPDATE")] = ["entered_in_error_reason", "status"],
        // Marking an inbox item done.
        [("ehr_app", "inbox_items", "UPDATE")] = ["done_at"],
        // Audit events without details: admins get those from ehr.shown_audit_details, never a sensitive item's.
        [("ehr_app", "audit_events", "SELECT")] = ["action", "actor_id", "id", "occurred_at", "patient_id", "reason", "row_id", "sensitive", "source", "table_name"],
        [("ehr_read", "audit_events", "SELECT")] = ["action", "actor_id", "id", "occurred_at", "patient_id", "reason", "row_id", "sensitive", "source", "table_name"],
    };

    async Task AssertExactPrivilegesAsync()
    {
        var columns = await QueryAsync(
            """
            SELECT r.role, c.relname, p.privilege, string_agg(a.attname, ' ' ORDER BY a.attname)
            FROM unnest(@roles) AS r(role)
            CROSS JOIN unnest(ARRAY['SELECT', 'INSERT', 'UPDATE', 'REFERENCES']) AS p(privilege)
            JOIN pg_class c ON c.relnamespace = 'ehr'::regnamespace AND c.relkind IN ('r', 'p', 'v', 'm')
            JOIN pg_attribute a ON a.attrelid = c.oid AND a.attnum > 0 AND NOT a.attisdropped
            WHERE has_column_privilege(r.role, c.oid, a.attnum, p.privilege) AND NOT has_table_privilege(r.role, c.oid, p.privilege)
            GROUP BY r.role, c.relname, p.privilege
            """,
            reader => (Key: (reader.GetString(0), reader.GetString(1), reader.GetString(2)), Columns: reader.GetString(3)),
            ("roles", RuntimeRoles));
        Assert.Equal(
            ExpectedColumnPrivileges.Select(expected => $"{expected.Key}: {string.Join(' ', expected.Value.Order(StringComparer.Ordinal))}").Order(),
            columns.Select(actual => $"{actual.Key}: {actual.Columns}").Order());

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
