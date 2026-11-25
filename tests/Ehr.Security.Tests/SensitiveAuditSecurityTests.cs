using Ehr.Data.Accounts;
using Ehr.Testing;
using Npgsql;
using Xunit;

namespace Ehr.Security.Tests;

// The expansion plan's steps 2b and 18a: what an admin can see of a write, as the runtime roles, in SQL of its own
// (db/policies/10-grants.sql, 30-rls.sql and 40-audit.sql). The write audit keeps the full record. The runtime roles
// can't read its values column at all; ehr.shown_audit_details gives an admin, of a write to patient data, the names of
// the fields it changed and never a value (step 18a), nothing of a sensitive item's, including the events written before
// the item was marked, and the details of an event about no patient as recorded; and an audit event is never edited, by
// anyone, beyond being marked.
public class SensitiveAuditSecurityTests(MigratedDatabase database) : IClassFixture<MigratedDatabase>
{
    string App => database.ConnectionStringFor("ehr_app", MigratedDatabase.AppPassword);

    string Read => database.ConnectionStringFor("ehr_read", MigratedDatabase.ReadPassword);

    const string Shown = """
        SELECT e.action || ' ' || e.sensitive || ' ' || coalesce(d.details::text, 'fields ' || array_to_string(d.fields, ','), 'hidden')
        FROM ehr.audit_events e LEFT JOIN ehr.shown_audit_details(ARRAY(SELECT id FROM ehr.audit_events WHERE row_id = @row)) d ON d.id = e.id
        WHERE e.row_id = @row ORDER BY e.occurred_at, e.action
        """;

    async Task<(Guid Clinician, Guid Patient, Guid Admin)> AccountsAsync()
    {
        var clinician = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        var patient = await Accounts.PatientAsync(database, clinician);
        await Accounts.AssignAsync(database, clinician, patient);
        return (clinician, patient, await Accounts.StaffAsync(database, StaffRoles.Admin));
    }

    Task<List<string>> AsSuperuserAsync(string sql, params (string Name, object Value)[] parameters) =>
        Accounts.QueryAsync(database.SuperuserConnectionString, null, sql, parameters);

    [Fact]
    public async Task An_admin_sees_the_fields_of_an_ordinary_write_and_nothing_of_a_sensitive_item()
    {
        var (clinician, patient, admin) = await AccountsAsync();
        var hypertension = Guid.CreateVersion7();
        var anxiety = Guid.CreateVersion7();
        const string insert = "INSERT INTO ehr.conditions (id, patient_id, code, display, clinical_status) VALUES (@id, @patient, @code, @display, 'active')";
        await Accounts.ExecuteAsync(App, clinician, insert, ("id", hypertension), ("patient", patient), ("code", "I10"), ("display", "Essential (primary) hypertension"));
        await Accounts.ExecuteAsync(App, clinician, insert, ("id", anxiety), ("patient", patient), ("code", "F41.1"), ("display", "Generalized anxiety disorder"));
        await Accounts.ExecuteAsync(App, clinician, "UPDATE ehr.conditions SET clinical_status = 'resolved' WHERE id = @id", ("id", anxiety));

        foreach (var connection in new[] { App, Read })
        {
            var ordinary = Assert.Single(await Accounts.QueryAsync(connection, admin, Shown, ("row", hypertension)));
            Assert.StartsWith("insert false fields ", ordinary);
            Assert.Contains("clinical_status,code,display", ordinary);
            Assert.DoesNotContain("hypertension", ordinary, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("I10", ordinary);
            Assert.Equal(["insert true hidden", "update true hidden"], await Accounts.QueryAsync(connection, admin, Shown, ("row", anxiety)));
        }

        // The admin still sees that it changed, and who changed it.
        Assert.Equal([$"insert {clinician} {patient} conditions", $"update {clinician} {patient} conditions"], await Accounts.QueryAsync(App, admin,
            "SELECT action || ' ' || actor_id || ' ' || patient_id || ' ' || table_name FROM ehr.audit_events WHERE row_id = @row ORDER BY action", ("row", anxiety)));
        // The full record stays in the audit store.
        Assert.Equal(["2"], await AsSuperuserAsync(
            "SELECT count(*) FROM ehr.audit_events WHERE row_id = @row AND details IS NOT NULL AND details::text LIKE '%'", ("row", anxiety)));
        Assert.Contains("Generalized anxiety disorder", Assert.Single(await AsSuperuserAsync(
            "SELECT details::text FROM ehr.audit_events WHERE row_id = @row AND action = 'insert'", ("row", anxiety))));

        // Nobody but an admin gets anything from the function; and no runtime role reads the column itself.
        foreach (var other in new Guid?[] { clinician, null, Guid.NewGuid() })
        {
            Assert.Empty(await Accounts.QueryAsync(App, other, "SELECT id::text FROM ehr.shown_audit_details(ARRAY[@a, @b])", ("a", hypertension), ("b", anxiety)));
        }

        foreach (var connection in new[] { App, Read })
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() =>
                Accounts.QueryAsync(connection, admin, "SELECT details::text FROM ehr.audit_events WHERE row_id = @row", ("row", hypertension)));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
        }
    }

    // A medication added unmarked and marked later: the events written before it was marked stop showing their values.
    [Fact]
    public async Task Marking_an_item_sensitive_hides_the_values_of_its_earlier_events_too()
    {
        var (clinician, patient, admin) = await AccountsAsync();
        var id = Guid.CreateVersion7();
        await Accounts.ExecuteAsync(App, clinician,
            "INSERT INTO ehr.medication_statements (id, patient_id, code, display, status) VALUES (@id, @patient, '312941', 'sertraline 50 MG Oral Tablet', 'active')",
            ("id", id), ("patient", patient));
        Assert.StartsWith("insert false fields ", Assert.Single(await Accounts.QueryAsync(App, admin, Shown, ("row", id))));

        await Accounts.ExecuteAsync(App, clinician, "UPDATE ehr.medication_statements SET sensitive = true WHERE id = @id", ("id", id));

        Assert.Equal(["insert true hidden", "update true hidden"], await Accounts.QueryAsync(App, admin, Shown, ("row", id)));
        // Unmarking the item later doesn't bring them back: what was hidden stays hidden.
        await Accounts.ExecuteAsync(App, clinician, "UPDATE ehr.medication_statements SET sensitive = false WHERE id = @id", ("id", id));
        Assert.DoesNotContain(await Accounts.QueryAsync(App, admin, Shown, ("row", id)), row => row.Contains("sertraline", StringComparison.Ordinal));
    }

    // Step 18a: what "the fields changed" are, for each kind of write, worked out from what the trigger stored, so the
    // same for an event written before the step. An update names the columns whose value changed; an insert those it gave
    // a value; a delete those that held one, stored under "deleteed" as the trigger has always spelled it. An order's
    // safety_warnings and safety_override_reason are never named: their being set says the patient has an allergy or a
    // medication that weighs against the order. Both runtime roles get the same, and nothing but names.
    [Fact]
    public async Task The_fields_of_a_write_to_patient_data_are_named_and_never_a_value()
    {
        var (clinician, patient, admin) = await AccountsAsync();
        var condition = Guid.CreateVersion7();
        await Accounts.ExecuteAsync(App, clinician,
            "INSERT INTO ehr.conditions (id, patient_id, code, display, clinical_status) VALUES (@id, @patient, 'I10', 'Essential (primary) hypertension', 'active')",
            ("id", condition), ("patient", patient));
        await Accounts.ExecuteAsync(App, clinician, "UPDATE ehr.conditions SET clinical_status = 'resolved' WHERE id = @id", ("id", condition));
        // As the trigger stores a delete, and an order signed with a warning overridden: the runtime roles may do neither.
        var (deleted, order) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        await Accounts.ExecuteAsync(database.SuperuserConnectionString, null,
            """
            INSERT INTO ehr.audit_events (id, occurred_at, actor_id, action, table_name, row_id, patient_id, source, sensitive, details) VALUES
                (uuidv7(), now(), @clinician, 'delete', 'conditions', @deleted, @patient, 'onprem', false,
                 jsonb_build_object('deleteed', jsonb_build_object('id', @deleted, 'patient_id', @patient, 'display', 'Synthetic deleted', 'onset_date', NULL))),
                (uuidv7(), now(), @clinician, 'insert', 'orders', @order, @patient, 'onprem', false,
                 jsonb_build_object('inserted', jsonb_build_object('id', @order, 'patient_id', @patient, 'catalog_code', 'SYN-MED-001', 'status', 'active',
                     'safety_warnings', '[{"kind": "allergy", "against": "Synthetic allergen"}]'::jsonb, 'safety_override_reason', 'Synthetic override',
                     'duplicate_reason', NULL)))
            """,
            ("clinician", clinician), ("deleted", deleted), ("patient", patient), ("order", order));

        foreach (var connection in new[] { App, Read })
        {
            Assert.Equal(["insert false fields clinical_status,code,display,id,patient_id,sensitive", "update false fields clinical_status"],
                await Accounts.QueryAsync(connection, admin, Shown, ("row", condition)));
            Assert.Equal(["delete false fields display,id,patient_id"], await Accounts.QueryAsync(connection, admin, Shown, ("row", deleted)));
            Assert.Equal(["insert false fields catalog_code,id,patient_id,status"], await Accounts.QueryAsync(connection, admin, Shown, ("row", order)));
        }
    }

    // Step 18a: an event about no patient, such as a desktop sign-in, keeps its details as recorded; an event that names a
    // patient gets none, whatever it holds; and details of a shape no write stores never make the function fail.
    [Fact]
    public async Task An_event_about_no_patient_keeps_its_details_and_odd_shapes_show_nothing()
    {
        var (_, patient, admin) = await AccountsAsync();
        var (signIn, named, odd, scalar) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        await Accounts.ExecuteAsync(database.SuperuserConnectionString, null,
            """
            INSERT INTO ehr.audit_events (id, occurred_at, actor_id, action, table_name, row_id, patient_id, source, sensitive, details) VALUES
                (@signIn, now(), NULL, 'sign_in', NULL, NULL, NULL, 'onprem', false, '{"client": "desktop", "device": "Synthetic laptop"}'),
                (@named, now(), NULL, 'sign_in', NULL, NULL, @patient, 'onprem', false, '{"client": "desktop", "note": "Synthetic text"}'),
                (@odd, now(), NULL, 'update', 'conditions', gen_random_uuid(), @patient, 'onprem', false, '[1, 2]'),
                (@scalar, now(), NULL, 'insert', 'orders', gen_random_uuid(), @patient, 'onprem', false, '{"inserted": "Synthetic text"}')
            """,
            ("signIn", signIn), ("named", named), ("odd", odd), ("scalar", scalar), ("patient", patient));

        foreach (var connection in new[] { App, Read })
        {
            Assert.Equal(["{\"client\": \"desktop\", \"device\": \"Synthetic laptop\"} none"], await Accounts.QueryAsync(connection, admin,
                "SELECT d.details::text || ' ' || coalesce(array_to_string(d.fields, ','), 'none') FROM ehr.shown_audit_details(ARRAY[@signIn, @named, @odd, @scalar]) d",
                ("signIn", signIn), ("named", named), ("odd", odd), ("scalar", scalar)));
        }
    }

    // The lists' history rows copy the items, sensitive ones included: an admin never gets their values.
    [Fact]
    public async Task The_history_copies_values_are_never_shown_to_an_admin()
    {
        var (clinician, patient, admin) = await AccountsAsync();
        await Accounts.ExecuteAsync(App, clinician,
            "INSERT INTO ehr.conditions (id, patient_id, code, display, clinical_status) VALUES (@id, @patient, 'F32.A', 'Depression, unspecified', 'active')",
            ("id", Guid.CreateVersion7()), ("patient", patient));

        Assert.Equal(["hidden"], await Accounts.QueryAsync(App, admin,
            """
            SELECT coalesce((SELECT d.details::text FROM ehr.shown_audit_details(ARRAY[e.id]) d), 'hidden')
            FROM ehr.audit_events e WHERE e.patient_id = @patient AND e.table_name = 'chart_history'
            """,
            ("patient", patient)));
    }

    // An audit event is never edited: not by a runtime role, which has no UPDATE, and not by the table's owner or a
    // superuser beyond marking it. Nothing unmarks one.
    [Fact]
    public async Task An_audit_event_takes_no_change_but_being_marked()
    {
        var (clinician, patient, _) = await AccountsAsync();
        var id = Guid.CreateVersion7();
        await Accounts.ExecuteAsync(App, clinician,
            "INSERT INTO ehr.conditions (id, patient_id, code, display, clinical_status) VALUES (@id, @patient, 'I10', 'Synthetic', 'active')",
            ("id", id), ("patient", patient));

        foreach (var sql in new[]
        {
            "UPDATE ehr.audit_events SET details = '{}' WHERE row_id = @row",
            "UPDATE ehr.audit_events SET actor_id = NULL WHERE row_id = @row",
            "UPDATE ehr.audit_events SET sensitive = true, reason = 'changed' WHERE row_id = @row",
            "UPDATE ehr.audit_events SET occurred_at = now() WHERE row_id = @row",
        })
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => Accounts.ExecuteAsync(database.SuperuserConnectionString, null, sql, ("row", id)));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
        }

        await Accounts.ExecuteAsync(database.SuperuserConnectionString, null, "UPDATE ehr.audit_events SET sensitive = true WHERE row_id = @row", ("row", id));
        var unmark = await Assert.ThrowsAsync<PostgresException>(() => Accounts.ExecuteAsync(database.SuperuserConnectionString, null,
            "UPDATE ehr.audit_events SET sensitive = false WHERE row_id = @row", ("row", id)));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, unmark.SqlState);

        var runtime = await Assert.ThrowsAsync<PostgresException>(() => Accounts.ExecuteAsync(App, clinician,
            "UPDATE ehr.audit_events SET sensitive = true WHERE row_id = @row", ("row", id)));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, runtime.SqlState);
    }

    // Events written before the rule, or before a range was added to it, are marked when the policies are applied again.
    [Fact]
    public async Task Applying_the_policies_again_marks_the_events_of_items_that_are_sensitive_now()
    {
        var (clinician, patient, admin) = await AccountsAsync();
        var id = Guid.CreateVersion7();
        await Accounts.ExecuteAsync(App, clinician,
            "INSERT INTO ehr.medication_statements (id, patient_id, code, display, status) VALUES (@id, @patient, '312941', 'sertraline 50 MG Oral Tablet', 'active')",
            ("id", id), ("patient", patient));
        // Marked behind the triggers' back, as data from before the rule would be.
        await Accounts.ExecuteAsync(database.SuperuserConnectionString, null,
            "SET LOCAL session_replication_role = replica; UPDATE ehr.medication_statements SET sensitive = true WHERE id = @id", ("id", id));
        Assert.StartsWith("insert false fields ", Assert.Single(await Accounts.QueryAsync(App, admin, Shown, ("row", id))));

        await database.ApplyAsync();

        Assert.Equal(["insert true hidden"], await Accounts.QueryAsync(App, admin, Shown, ("row", id)));
        Assert.Equal(["true true"], await AsSuperuserAsync(
            "SELECT relrowsecurity || ' ' || relforcerowsecurity FROM pg_class WHERE oid = 'ehr.audit_events'::regclass"));
    }
}
