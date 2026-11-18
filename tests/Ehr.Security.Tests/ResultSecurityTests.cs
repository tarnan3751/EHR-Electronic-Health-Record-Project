using Ehr.Data.Accounts;
using Ehr.Testing;
using Npgsql;
using Xunit;

namespace Ehr.Security.Tests;

// The expansion plan's step 7: what the database lets each account do with an order's results and their
// acknowledgment, as the runtime roles, in SQL of its own (db/policies/10-grants.sql, 30-rls.sql, 60-orders.sql and
// 75-results.sql). A clinician with the patient on their panel files results, only for the tests an open lab order
// reports, with the database's own name, unit and reference range; the order becomes completed by its results and no
// other way; the clinician who signed it is told, with no values in the inbox; an acknowledgment is made once, by a
// panel clinician, as themselves, and ends the filing; nothing is edited or deleted.
public class ResultSecurityTests(MigratedDatabase database) : IClassFixture<MigratedDatabase>
{
    string App => database.ConnectionStringFor("ehr_app", MigratedDatabase.AppPassword);

    string Read => database.ConnectionStringFor("ehr_read", MigratedDatabase.ReadPassword);

    // A complete blood count reports hemoglobin (718-7, 12.0 to 17.5 g/dL), hematocrit, leukocytes and platelets.
    const string Order = "INSERT INTO ehr.orders (id, patient_id, type, catalog_code, details, status) VALUES (@id, @patient, @type, @code, '{}', 'active')";

    // Everything a client could try to choose for itself is sent wrong.
    const string Result = """
        INSERT INTO ehr.observations (id, patient_id, order_id, code, display, status, value, unit, reference_low, reference_high, effective_at, issued_at)
        VALUES (@id, @patient, @order, @code, 'Forged name', 'preliminary', @value, 'forged', 0, 1000, now() - interval '1 hour', '2000-01-01')
        """;

    const string Acknowledge = "INSERT INTO ehr.result_acknowledgments (id, order_id, patient_id, acknowledged_by, acknowledged_at) VALUES (gen_random_uuid(), @order, @patient, @by, '2000-01-01')";

    async Task<(Guid Clinician, Guid Patient, Guid Order)> OrderedAsync(string type = "lab", string code = "SYN-LAB-001")
    {
        var clinician = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        var patient = await Accounts.PatientAsync(database, clinician);
        await Accounts.AssignAsync(database, clinician, patient);
        var order = Guid.CreateVersion7();
        await Accounts.ExecuteAsync(App, clinician, Order, ("id", order), ("patient", patient), ("type", type), ("code", code));
        return (clinician, patient, order);
    }

    Task FileAsync(Guid? user, Guid patient, Guid order, string code = "718-7", decimal value = 9.1m, string? connection = null) =>
        Accounts.ExecuteAsync(connection ?? App, user, Result, ("id", Guid.CreateVersion7()), ("patient", patient), ("order", order), ("code", code), ("value", value));

    Task AcknowledgeAsync(Guid? user, Guid patient, Guid order, Guid? claimed = null) =>
        Accounts.ExecuteAsync(App, user, Acknowledge, ("order", order), ("patient", patient), ("by", claimed ?? user ?? Guid.NewGuid()));

    Task<List<string>> AsSuperuserAsync(string sql, params (string Name, object Value)[] parameters) =>
        Accounts.QueryAsync(database.SuperuserConnectionString, null, sql, parameters);

    async Task RefusedAsync(string state, Func<Task> attempt) =>
        Assert.Equal(state, (await Assert.ThrowsAsync<PostgresException>(attempt)).SqlState);

    [Fact]
    public async Task A_filed_result_has_the_databases_name_unit_and_range_completes_its_order_and_tells_the_signer()
    {
        var (clinician, patient, order) = await OrderedAsync();
        var colleague = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        await Accounts.AssignAsync(database, colleague, patient);

        // A colleague on the panel files it; the clinician who signed the order is the one told.
        await FileAsync(colleague, patient, order);

        Assert.Equal(["Hemoglobin [Mass/volume] in Blood|g/dL|12.0000|17.5000|final|true|9.1000"], await AsSuperuserAsync(
            """
            SELECT display || '|' || unit || '|' || reference_low || '|' || reference_high || '|' || status || '|' || (issued_at > now() - interval '1 minute') || '|' || value
            FROM ehr.observations WHERE order_id = @order
            """, ("order", order)));
        Assert.Equal(["completed"], await AsSuperuserAsync("SELECT status FROM ehr.orders WHERE id = @order", ("order", order)));
        // One item, for the signer, with no value in it.
        Assert.Equal([$"{clinician} result orders Results are in {patient}"], await AsSuperuserAsync(
            "SELECT recipient_id || ' ' || kind || ' ' || source_table || ' ' || summary || ' ' || patient_id FROM ehr.inbox_items WHERE source_id = @order", ("order", order)));

        // More results arrive for the same order: added, with no second item; the same test twice is refused.
        await FileAsync(clinician, patient, order, "777-3", 250m);
        await RefusedAsync(PostgresErrorCodes.UniqueViolation, () => FileAsync(clinician, patient, order, "777-3", 251m));
        Assert.Equal(["2 1"], await AsSuperuserAsync(
            "SELECT (SELECT count(*) FROM ehr.observations WHERE order_id = @order) || ' ' || (SELECT count(*) FROM ehr.inbox_items WHERE source_id = @order)", ("order", order)));

        // The patient sees them at once, released; another patient never does.
        var account = await Accounts.PatientAccountAsync(database, patient);
        var other = await Accounts.PatientAccountAsync(database, await Accounts.PatientAsync(database, clinician));
        foreach (var connection in new[] { App, Read })
        {
            Assert.Equal(["2"], await Accounts.QueryAsync(connection, account, "SELECT count(*) FROM ehr.observations WHERE order_id = @order", ("order", order)));
            Assert.Equal(["0"], await Accounts.QueryAsync(connection, other, "SELECT count(*) FROM ehr.observations WHERE order_id = @order", ("order", order)));
        }
    }

    [Fact]
    public async Task Only_a_panel_clinician_files_and_only_for_a_test_of_an_open_lab_order_of_that_patient()
    {
        var (clinician, patient, order) = await OrderedAsync();
        var offPanel = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        var admin = await Accounts.StaffAsync(database, StaffRoles.Admin);
        var account = await Accounts.PatientAccountAsync(database, patient);

        // Refused by row-level security, or, for someone who can't even see the order, by the trigger that looks for it.
        foreach (var user in new Guid?[] { offPanel, admin, account, null })
        {
            var refused = await Assert.ThrowsAsync<PostgresException>(() => FileAsync(user, patient, order));
            Assert.Contains(refused.SqlState, new[] { PostgresErrorCodes.InsufficientPrivilege, PostgresErrorCodes.CheckViolation });
        }

        await RefusedAsync(PostgresErrorCodes.InsufficientPrivilege, () => FileAsync(clinician, patient, order, connection: Read));

        // Not a test this order reports (hemoglobin A1c is another order's), no value, no order, a sample from the future.
        await RefusedAsync(PostgresErrorCodes.CheckViolation, () => FileAsync(clinician, patient, order, "4548-4"));
        await RefusedAsync(PostgresErrorCodes.CheckViolation, () => FileAsync(clinician, patient, order, "not-a-code"));
        foreach (var sql in new[]
        {
            "INSERT INTO ehr.observations (id, patient_id, order_id, code, display, status, effective_at) VALUES (gen_random_uuid(), @patient, @order, '718-7', 'x', 'final', now())",
            "INSERT INTO ehr.observations (id, patient_id, code, display, status, value, effective_at) VALUES (gen_random_uuid(), @patient, '718-7', 'x', 'final', 1, now())",
            "INSERT INTO ehr.observations (id, patient_id, order_id, code, display, status, value, effective_at) VALUES (gen_random_uuid(), @patient, @order, '718-7', 'x', 'final', 1, now() + interval '1 day')",
        })
        {
            await RefusedAsync(PostgresErrorCodes.CheckViolation, () => Accounts.ExecuteAsync(App, clinician, sql.Replace("@order", $"'{order}'"), ("patient", patient)));
        }

        // Not under another patient of the same clinician, with this order or without.
        var second = await Accounts.PatientAsync(database, clinician);
        await Accounts.AssignAsync(database, clinician, second);
        await RefusedAsync(PostgresErrorCodes.CheckViolation, () => FileAsync(clinician, second, order));

        // Not for an order that isn't a lab order, or one that was discontinued.
        var (prescriber, treated, medication) = await OrderedAsync("medication", "SYN-MED-001");
        await RefusedAsync(PostgresErrorCodes.CheckViolation, () => FileAsync(prescriber, treated, medication));
        await Accounts.ExecuteAsync(App, clinician, "UPDATE ehr.orders SET status = 'revoked', discontinue_reason = 'Synthetic' WHERE id = @order", ("order", order));
        await RefusedAsync(PostgresErrorCodes.CheckViolation, () => FileAsync(clinician, patient, order));

        Assert.Equal(["0 0"], await AsSuperuserAsync(
            "SELECT (SELECT count(*) FROM ehr.observations WHERE patient_id IN (@a, @b, @c)) || ' ' || (SELECT count(*) FROM ehr.inbox_items WHERE patient_id IN (@a, @b, @c))",
            ("a", patient), ("b", second), ("c", treated)));
    }

    [Fact]
    public async Task An_order_is_completed_only_by_its_results_and_then_never_changes()
    {
        var (clinician, patient, order) = await OrderedAsync();

        await RefusedAsync(PostgresErrorCodes.CheckViolation, () =>
            Accounts.ExecuteAsync(App, clinician, "UPDATE ehr.orders SET status = 'completed' WHERE id = @order", ("order", order)));
        await FileAsync(clinician, patient, order);

        foreach (var sql in new[]
        {
            "UPDATE ehr.orders SET status = 'active' WHERE id = @order",
            "UPDATE ehr.orders SET status = 'revoked', discontinue_reason = 'Synthetic' WHERE id = @order",
            "UPDATE ehr.orders SET status = 'completed' WHERE id = @order",
        })
        {
            await RefusedAsync(PostgresErrorCodes.CheckViolation, () => Accounts.ExecuteAsync(App, clinician, sql, ("order", order)));
        }

        // A filed result isn't changed or removed.
        foreach (var sql in new[] { "UPDATE ehr.observations SET value = 14 WHERE order_id = @order", "DELETE FROM ehr.observations WHERE order_id = @order" })
        {
            await RefusedAsync(PostgresErrorCodes.InsufficientPrivilege, () => Accounts.ExecuteAsync(App, clinician, sql, ("order", order)));
        }
    }

    [Fact]
    public async Task Results_are_acknowledged_once_by_a_panel_clinician_as_themselves_and_that_ends_the_filing()
    {
        var (clinician, patient, order) = await OrderedAsync();
        var colleague = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        await Accounts.AssignAsync(database, colleague, patient);
        var offPanel = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        var admin = await Accounts.StaffAsync(database, StaffRoles.Admin);
        var account = await Accounts.PatientAccountAsync(database, patient);

        // Nothing to acknowledge before there are results.
        await RefusedAsync(PostgresErrorCodes.CheckViolation, () => AcknowledgeAsync(clinician, patient, order));
        await FileAsync(clinician, patient, order);

        foreach (var user in new Guid?[] { offPanel, admin, account, null })
        {
            var refused = await Assert.ThrowsAsync<PostgresException>(() => AcknowledgeAsync(user, patient, order));
            Assert.Contains(refused.SqlState, new[] { PostgresErrorCodes.InsufficientPrivilege, PostgresErrorCodes.CheckViolation });
        }

        // Not under another patient.
        var second = await Accounts.PatientAsync(database, clinician);
        await Accounts.AssignAsync(database, clinician, second);
        var crossed = await Assert.ThrowsAsync<PostgresException>(() => AcknowledgeAsync(clinician, second, order));
        Assert.Contains(crossed.SqlState, new[] { PostgresErrorCodes.CheckViolation, PostgresErrorCodes.ForeignKeyViolation });
        Assert.Equal(["1"], await AsSuperuserAsync("SELECT count(*) FROM ehr.inbox_items WHERE source_id = @order AND done_at IS NULL", ("order", order)));

        // A colleague on the panel acknowledges, claiming to be the signer and an old time: it is theirs, at the
        // database's time, and the signer's inbox item is done.
        await AcknowledgeAsync(colleague, patient, order, claimed: clinician);
        Assert.Equal([$"{colleague} true"], await AsSuperuserAsync(
            "SELECT acknowledged_by || ' ' || (acknowledged_at > now() - interval '1 minute') FROM ehr.result_acknowledgments WHERE order_id = @order", ("order", order)));
        Assert.Equal(["0"], await AsSuperuserAsync("SELECT count(*) FROM ehr.inbox_items WHERE source_id = @order AND done_at IS NULL", ("order", order)));
        Assert.Equal([$"{colleague} {patient}"], await AsSuperuserAsync(
            "SELECT actor_id || ' ' || patient_id FROM ehr.audit_events WHERE table_name = 'result_acknowledgments' AND action = 'insert' AND patient_id = @patient", ("patient", patient)));

        // Once; and nothing is added to acknowledged results, changed or removed.
        await RefusedAsync(PostgresErrorCodes.UniqueViolation, () => AcknowledgeAsync(clinician, patient, order));
        await RefusedAsync(PostgresErrorCodes.CheckViolation, () => FileAsync(clinician, patient, order, "777-3", 250m));
        foreach (var sql in new[] { "UPDATE ehr.result_acknowledgments SET acknowledged_at = now() WHERE order_id = @order", "DELETE FROM ehr.result_acknowledgments WHERE order_id = @order" })
        {
            await RefusedAsync(PostgresErrorCodes.InsufficientPrivilege, () => Accounts.ExecuteAsync(App, colleague, sql, ("order", order)));
        }

        // Clinicians read who acknowledged; a patient, an admin and nobody don't.
        const string read = "SELECT count(*) FROM ehr.result_acknowledgments WHERE order_id = @order";
        foreach (var connection in new[] { App, Read })
        {
            Assert.Equal(["1"], await Accounts.QueryAsync(connection, offPanel, read, ("order", order)));
            foreach (var refused in new Guid?[] { account, admin, null })
            {
                Assert.Equal(["0"], await Accounts.QueryAsync(connection, refused, read, ("order", order)));
            }
        }
    }
}
