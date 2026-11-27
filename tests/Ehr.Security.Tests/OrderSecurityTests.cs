using Ehr.Data.Accounts;
using Ehr.Testing;
using Npgsql;
using Xunit;
using static Ehr.Testing.TestRun;

namespace Ehr.Security.Tests;

// UC 1 Place an order (Tahseen Arnan): what the database lets each account do with orders, as the runtime roles, in SQL
// of its own, so these hold whatever the app checks (db/policies/30-rls.sql and 60-orders.sql). Only a clinician signs,
// only as themselves, only for a patient on their panel; nothing edits or deletes an order (discontinuing and changing
// one are OrderChangeSecurityTests'); the database sets the time; a second active order for the same item needs a reason, even when two arrive at once,
// and so does one for the same medication product under another item; and the write audit names the clinician and the
// time.
public class OrderSecurityTests(MigratedDatabase database) : IClassFixture<MigratedDatabase>
{
    string App => database.ConnectionStringFor("ehr_app", MigratedDatabase.AppPassword);

    string Read => database.ConnectionStringFor("ehr_read", MigratedDatabase.ReadPassword);

    // A complete blood count, with a signing time from the client that the database must ignore.
    const string InsertOrder = """
        INSERT INTO ehr.orders (id, patient_id, type, catalog_code, details, status, signed_at)
        VALUES (@id, @patient, 'lab', 'SYN-LAB-001', '{"indication": "Synthetic fatigue"}', 'active', '2000-01-01')
        """;

    const string InsertWithReason = """
        INSERT INTO ehr.orders (id, patient_id, type, catalog_code, details, status, duplicate_reason)
        VALUES (@id, @patient, 'lab', 'SYN-LAB-001', '{"indication": "Synthetic fatigue"}', 'active', @reason)
        """;

    // A medication order, by catalog item, with a duplicate's reason or none.
    const string InsertMedication = """
        INSERT INTO ehr.orders (id, patient_id, type, catalog_code, details, status, duplicate_reason)
        VALUES (@id, @patient, 'medication', @code, '{}', 'active', @reason)
        """;

    const string InsertAsSigner = """
        INSERT INTO ehr.orders (id, patient_id, type, catalog_code, details, status, signer_id)
        VALUES (@id, @patient, 'lab', 'SYN-LAB-001', '{}', 'active', @signer)
        """;

    Task<(Guid Clinician, Guid Patient)> OnPanelAsync() => Accounts.OnPanelAsync(database);

    async Task<Guid> OrderAsync(string connection, Guid? user, Guid patient, string sql = InsertOrder, params (string Name, object Value)[] extra)
    {
        var id = Guid.CreateVersion7();
        await Accounts.ExecuteAsync(connection, user, sql, [("id", id), ("patient", patient), .. extra]);
        return id;
    }

    Task<List<string>> CountAsync(Guid patient) =>
        Accounts.QueryAsync(database.SuperuserConnectionString, null, "SELECT count(*) FROM ehr.orders WHERE patient_id = @id", ("id", patient));

    [Fact]
    public async Task The_database_sets_the_signing_time_and_the_audit_row_names_the_clinician_and_the_time()
    {
        var (clinician, patient) = await OnPanelAsync();

        var order = await OrderAsync(App, clinician, patient);

        Assert.Equal([$"{clinician} true"], await Accounts.QueryAsync(App, clinician,
            "SELECT signer_id || ' ' || (signed_at > now() - interval '1 minute') FROM ehr.orders WHERE id = @id", ("id", order)));
        // The trigger's audit row: the clinician as the actor, against the patient, at the signing time: the transaction's
        // start for the audit row, the clock a moment later for the signature.
        Assert.Equal([$"insert {clinician} {patient} onprem true"], await Accounts.QueryAsync(database.SuperuserConnectionString, null,
            """
            SELECT e.action || ' ' || e.actor_id || ' ' || e.patient_id || ' ' || e.source || ' ' || (o.signed_at - e.occurred_at BETWEEN interval '0' AND interval '5 seconds')
            FROM ehr.audit_events e JOIN ehr.orders o ON o.id = e.row_id
            WHERE e.table_name = 'orders' AND e.row_id = @id
            """,
            ("id", order)));
    }

    [Fact]
    public async Task Inserts_by_an_admin_a_patient_a_disabled_clinician_nobody_or_the_read_role_are_refused()
    {
        var (clinician, patient) = await OnPanelAsync();
        var admin = await Accounts.StaffAsync(database, StaffRoles.Admin);
        var patientAccount = await Accounts.PatientAccountAsync(database, patient);
        var disabled = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        await Accounts.AssignAsync(database, disabled, patient);
        await Accounts.LockOutAsync(database, disabled);

        foreach (var refused in new Guid?[] { admin, patientAccount, disabled, null, Guid.NewGuid() })
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => OrderAsync(App, refused, patient));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
        }

        // The cloud app's role has no INSERT at all, a clinician's context or not.
        var readRole = await Assert.ThrowsAsync<PostgresException>(() => OrderAsync(Read, clinician, patient));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, readRole.SqlState);
        Assert.Equal(["0"], await CountAsync(patient));
    }

    [Fact]
    public async Task Signing_as_someone_else_is_refused()
    {
        var (clinician, patient) = await OnPanelAsync();
        var colleague = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        await Accounts.AssignAsync(database, colleague, patient);

        var error = await Assert.ThrowsAsync<PostgresException>(() => OrderAsync(App, clinician, patient, InsertAsSigner, ("signer", colleague)));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
        Assert.Equal(["0"], await CountAsync(patient));
    }

    [Fact]
    public async Task An_order_for_a_patient_off_the_clinicians_panel_is_refused()
    {
        var clinician = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        var never = await Accounts.PatientAsync(database, clinician);
        var ended = await Accounts.PatientAsync(database, clinician);
        await Accounts.AssignAsync(database, clinician, ended);
        await Accounts.ExecuteAsync(App, clinician, "UPDATE ehr.patient_assignments SET ended_at = now() WHERE patient_id = @id", ("id", ended));

        foreach (var patient in new[] { never, ended })
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => OrderAsync(App, clinician, patient));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
            Assert.Equal(["0"], await CountAsync(patient));
        }
    }

    // Nothing deletes an order, and the cloud app's role updates nothing. The one update ehr_app has is discontinuing,
    // which OrderChangeSecurityTests covers; here, the columns outside its grant can't be written at all.
    [Fact]
    public async Task Orders_cannot_be_deleted_or_edited()
    {
        var (clinician, patient) = await OnPanelAsync();
        var order = await OrderAsync(App, clinician, patient);

        foreach (var (connection, sql) in new[]
        {
            (App, "DELETE FROM ehr.orders WHERE id = @id"),
            (App, "UPDATE ehr.orders SET details = '{}' WHERE id = @id"),
            (App, "UPDATE ehr.orders SET catalog_code = 'SYN-LAB-002' WHERE id = @id"),
            (App, "UPDATE ehr.orders SET signer_id = signer_id, signed_at = now() WHERE id = @id"),
            (App, "UPDATE ehr.orders SET status = 'revoked', discontinue_reason = 'Synthetic', discontinued_at = '2000-01-01' WHERE id = @id"),
            (App, "UPDATE ehr.orders SET status = 'revoked', discontinue_reason = 'Synthetic', discontinuer_id = signer_id WHERE id = @id"),
            (App, "UPDATE ehr.orders SET replaces_order_id = NULL WHERE id = @id"),
            (Read, "UPDATE ehr.orders SET status = 'revoked', discontinue_reason = 'Synthetic' WHERE id = @id"),
            (Read, "DELETE FROM ehr.orders WHERE id = @id"),
        })
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => Accounts.ExecuteAsync(connection, clinician, sql, ("id", order)));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
        }

        // Nor is the catalog changed at runtime.
        var catalog = await Assert.ThrowsAsync<PostgresException>(() => Accounts.ExecuteAsync(App, clinician,
            "UPDATE ehr.order_catalog_items SET display = 'Changed' WHERE code = 'SYN-LAB-001'"));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, catalog.SqlState);

        Assert.Equal(["active"], await Accounts.QueryAsync(App, clinician, "SELECT status FROM ehr.orders WHERE id = @id", ("id", order)));
    }

    [Fact]
    public async Task A_duplicate_active_order_without_a_reason_is_refused_and_one_with_a_reason_is_kept()
    {
        var (clinician, patient) = await OnPanelAsync();
        await OrderAsync(App, clinician, patient);

        foreach (var blank in new[] { "", "   " })
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => OrderAsync(App, clinician, patient, InsertWithReason, ("reason", blank)));
            Assert.Equal(PostgresErrorCodes.UniqueViolation, error.SqlState);
        }

        var duplicate = await Assert.ThrowsAsync<PostgresException>(() => OrderAsync(App, clinician, patient));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicate.SqlState);

        var again = await OrderAsync(App, clinician, patient, InsertWithReason, ("reason", "Synthetic repeat after a hemolysed sample"));
        Assert.Equal(["Synthetic repeat after a hemolysed sample"], await Accounts.QueryAsync(App, clinician,
            "SELECT duplicate_reason FROM ehr.orders WHERE id = @id", ("id", again)));
        Assert.Equal(["2"], await CountAsync(patient));
    }

    // A medication is on order whichever of its catalog items was picked: the clinic's common item for lisinopril 10 mg
    // (SYN-MED-001) and RxNorm's own (314076) are one product, both ways round, as the review of step 13d found a
    // refill's approval could order the one beside the other. Another strength (314077) and another drug (SYN-MED-003,
    // atorvastatin) are not on order.
    [Fact]
    public async Task A_medication_on_order_under_another_item_for_the_same_product_needs_a_reason()
    {
        foreach (var (first, then) in new[] { ("SYN-MED-001", "314076"), ("314076", "SYN-MED-001") })
        {
            var (clinician, patient) = await OnPanelAsync();
            await OrderAsync(App, clinician, patient, InsertMedication, ("code", first), ("reason", DBNull.Value));

            var duplicate = await Assert.ThrowsAsync<PostgresException>(
                () => OrderAsync(App, clinician, patient, InsertMedication, ("code", then), ("reason", DBNull.Value)));
            Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicate.SqlState);

            var again = await OrderAsync(App, clinician, patient, InsertMedication, ("code", then), ("reason", "Synthetic: a second supply."));
            Assert.Equal(["Synthetic: a second supply."], await Accounts.QueryAsync(App, clinician,
                "SELECT duplicate_reason FROM ehr.orders WHERE id = @id", ("id", again)));
            await OrderAsync(App, clinician, patient, InsertMedication, ("code", "314077"), ("reason", DBNull.Value));
            await OrderAsync(App, clinician, patient, InsertMedication, ("code", "SYN-MED-003"), ("reason", DBNull.Value));
            Assert.Equal(["4"], await CountAsync(patient));
        }
    }

    // The lock the trigger takes makes the second of two submissions at once wait for the first, and then see it.
    [Fact]
    public async Task Two_duplicates_at_once_cannot_both_get_through()
    {
        var (clinician, patient) = await OnPanelAsync();
        await using var first = new NpgsqlConnection(App);
        await using var second = new NpgsqlConnection(App);
        await first.OpenAsync(Cancel);
        await second.OpenAsync(Cancel);
        await using var firstTransaction = await first.BeginTransactionAsync(Cancel);
        await using var secondTransaction = await second.BeginTransactionAsync(Cancel);
        foreach (var (connection, transaction) in new[] { (first, firstTransaction), (second, secondTransaction) })
        {
            await using var set = new NpgsqlCommand("SELECT set_config('app.user_id', @actor, true)", connection, transaction);
            set.Parameters.AddWithValue("actor", clinician.ToString());
            await set.ExecuteNonQueryAsync(Cancel);
        }

        await using var signFirst = Insert(first, firstTransaction);
        await signFirst.ExecuteNonQueryAsync(Cancel);
        await using var signSecond = Insert(second, secondTransaction);
        var waiting = signSecond.ExecuteNonQueryAsync(Cancel);
        // Watch the second one actually wait on the first, rather than relying on timing.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            var blocked = await Accounts.QueryAsync(database.SuperuserConnectionString, null,
                "SELECT cardinality(pg_blocking_pids(@pid))", ("pid", second.ProcessID));
            if (blocked[0] != "0")
            {
                break;
            }

            await Task.Delay(25, Cancel);
        }

        Assert.False(waiting.IsCompleted);
        await firstTransaction.CommitAsync(Cancel);
        var error = await Assert.ThrowsAsync<PostgresException>(async () => await waiting);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, error.SqlState);
        Assert.Equal(["1"], await CountAsync(patient));

        NpgsqlCommand Insert(NpgsqlConnection connection, NpgsqlTransaction transaction)
        {
            var command = new NpgsqlCommand(InsertOrder, connection, transaction);
            command.Parameters.AddWithValue("id", Guid.CreateVersion7());
            command.Parameters.AddWithValue("patient", patient);
            return command;
        }
    }

    // The duplicate check reads what the order it waited for committed, which only READ COMMITTED lets it see. A
    // caller at a stricter level, as the desktop sync APIs are, would check against an old snapshot, so it is refused.
    [Theory]
    [InlineData(System.Data.IsolationLevel.RepeatableRead)]
    [InlineData(System.Data.IsolationLevel.Serializable)]
    public async Task An_order_outside_a_read_committed_transaction_is_refused(System.Data.IsolationLevel level)
    {
        var (clinician, patient) = await OnPanelAsync();
        await using var connection = new NpgsqlConnection(App);
        await connection.OpenAsync(Cancel);
        await using var transaction = await connection.BeginTransactionAsync(level, Cancel);
        await using (var set = new NpgsqlCommand("SELECT set_config('app.user_id', @actor, true)", connection, transaction))
        {
            set.Parameters.AddWithValue("actor", clinician.ToString());
            await set.ExecuteNonQueryAsync(Cancel);
        }

        await using var sign = new NpgsqlCommand(InsertOrder, connection, transaction);
        sign.Parameters.AddWithValue("id", Guid.CreateVersion7());
        sign.Parameters.AddWithValue("patient", patient);
        var error = await Assert.ThrowsAsync<PostgresException>(() => sign.ExecuteNonQueryAsync(Cancel));

        Assert.Equal(PostgresErrorCodes.FeatureNotSupported, error.SqlState);
        await transaction.RollbackAsync(Cancel);
        Assert.Equal(["0"], await CountAsync(patient));
    }

    // An order is signed active: only discontinuing changes its status afterwards.
    [Fact]
    public async Task An_order_that_is_not_active_is_refused()
    {
        var (clinician, patient) = await OnPanelAsync();

        var error = await Assert.ThrowsAsync<PostgresException>(() => Accounts.ExecuteAsync(App, clinician,
            "INSERT INTO ehr.orders (id, patient_id, type, catalog_code, details, status) VALUES (@id, @patient, 'lab', 'SYN-LAB-001', '{}', 'completed')",
            ("id", Guid.CreateVersion7()), ("patient", patient)));

        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        Assert.Equal(["0"], await CountAsync(patient));
    }

    [Fact]
    public async Task An_order_names_a_catalog_item_of_its_own_type()
    {
        var (clinician, patient) = await OnPanelAsync();

        var error = await Assert.ThrowsAsync<PostgresException>(() => Accounts.ExecuteAsync(App, clinician,
            "INSERT INTO ehr.orders (id, patient_id, type, catalog_code, details, status) VALUES (@id, @patient, 'imaging', 'SYN-LAB-001', '{}', 'active')",
            ("id", Guid.CreateVersion7()), ("patient", patient)));

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, error.SqlState);
        Assert.NotEmpty(await Accounts.QueryAsync(Read, null, "SELECT code FROM ehr.order_catalog_items WHERE type = 'imaging'"));
    }

    [Fact]
    public async Task Clinicians_read_every_order_and_nobody_else_reads_any()
    {
        var (clinician, patient) = await OnPanelAsync();
        var order = await OrderAsync(App, clinician, patient);
        var colleague = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        var admin = await Accounts.StaffAsync(database, StaffRoles.Admin);
        var patientAccount = await Accounts.PatientAccountAsync(database, patient);
        var disabled = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        await Accounts.LockOutAsync(database, disabled);
        const string read = "SELECT count(*) FROM ehr.orders WHERE id = @id";

        foreach (var connection in new[] { App, Read })
        {
            Assert.Equal(["1"], await Accounts.QueryAsync(connection, colleague, read, ("id", order)));
            foreach (var refused in new Guid?[] { admin, patientAccount, disabled, null, Guid.NewGuid() })
            {
                Assert.Equal(["0"], await Accounts.QueryAsync(connection, refused, read, ("id", order)));
            }
        }
    }
}
