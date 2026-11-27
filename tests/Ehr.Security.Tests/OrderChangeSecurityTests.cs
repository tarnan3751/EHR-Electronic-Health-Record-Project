using Ehr.Data.Accounts;
using Ehr.Testing;
using Npgsql;
using Xunit;

namespace Ehr.Security.Tests;

// UC 1 (Tahseen Arnan): what the database lets each account do to a signed order, as the runtime roles, in SQL of its
// own, so these hold whatever the app checks (db/policies/10-grants.sql, 30-rls.sql and 60-orders.sql). A signed order
// is never edited in place. The one update is discontinuing an active order, once, with a reason, by a clinician with
// the patient on their panel; the database sets who and when, and the write audit records it. A change is a new order
// and the old one discontinued as replaced, linked both ways, in one transaction or not at all.
public class OrderChangeSecurityTests(MigratedDatabase database) : IClassFixture<MigratedDatabase>
{
    string App => database.ConnectionStringFor("ehr_app", MigratedDatabase.AppPassword);

    const string Insert = """
        INSERT INTO ehr.orders (id, patient_id, type, catalog_code, details, status)
        VALUES (@id, @patient, 'lab', @code, '{"indication": "Synthetic fatigue"}', 'active')
        """;

    const string Discontinue = "UPDATE ehr.orders SET status = 'revoked', discontinue_reason = @reason WHERE id = @id";

    // A change as the app makes it: the replacement, then the old order discontinued and naming it, in one transaction.
    const string Change = """
        INSERT INTO ehr.orders (id, patient_id, type, catalog_code, details, status, replaces_order_id)
        VALUES (@new, @patient, 'lab', @code, '{"indication": "Synthetic change"}', 'active', @old);
        UPDATE ehr.orders SET status = 'revoked', discontinue_reason = 'Replaced by a changed order', replaced_by_order_id = @new WHERE id = @old
        """;

    Task<(Guid Clinician, Guid Patient)> OnPanelAsync() => Accounts.OnPanelAsync(database);

    async Task<Guid> OrderAsync(Guid clinician, Guid patient, string code = "SYN-LAB-001")
    {
        var id = Guid.CreateVersion7();
        await Accounts.ExecuteAsync(App, clinician, Insert, ("id", id), ("patient", patient), ("code", code));
        return id;
    }

    Task<List<string>> RowAsync(Guid order) => Accounts.QueryAsync(database.SuperuserConnectionString, null,
        """
        SELECT status || ' ' || coalesce(discontinue_reason, '-') || ' ' || coalesce(discontinuer_id::text, '-') || ' '
            || coalesce((discontinued_at > now() - interval '1 minute')::text, '-') || ' ' || coalesce(replaces_order_id::text, '-') || ' '
            || coalesce(replaced_by_order_id::text, '-')
        FROM ehr.orders WHERE id = @id
        """,
        ("id", order));

    [Fact]
    public async Task Discontinuing_records_the_reason_and_the_database_sets_who_and_when_and_audits_it()
    {
        var (clinician, patient) = await OnPanelAsync();
        var order = await OrderAsync(clinician, patient);

        await Accounts.ExecuteAsync(App, clinician, Discontinue, ("id", order), ("reason", "Synthetic: no longer needed"));

        Assert.Equal([$"revoked Synthetic: no longer needed {clinician} true - -"], await RowAsync(order));
        // The write audit: the clinician, the patient, and the old and new status.
        Assert.Equal([$"update {clinician} {patient} active revoked"], await Accounts.QueryAsync(database.SuperuserConnectionString, null,
            """
            SELECT action || ' ' || actor_id || ' ' || patient_id || ' ' || (details -> 'status' ->> 'old') || ' ' || (details -> 'status' ->> 'new')
            FROM ehr.audit_events WHERE table_name = 'orders' AND row_id = @id AND action = 'update'
            """,
            ("id", order)));
    }

    // Another clinician with the patient on their panel may discontinue a colleague's order, and is the one recorded.
    [Fact]
    public async Task A_colleague_with_the_patient_on_their_panel_discontinues_as_themselves()
    {
        var (clinician, patient) = await OnPanelAsync();
        var order = await OrderAsync(clinician, patient);
        var colleague = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        await Accounts.AssignAsync(database, colleague, patient);

        await Accounts.ExecuteAsync(App, colleague, Discontinue, ("id", order), ("reason", "Synthetic"));

        Assert.Equal([$"revoked Synthetic {colleague} true - -"], await RowAsync(order));
    }

    [Fact]
    public async Task Nobody_but_a_clinician_with_the_patient_on_their_panel_discontinues_an_order()
    {
        var (clinician, patient) = await OnPanelAsync();
        var order = await OrderAsync(clinician, patient);
        var offPanel = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        var ended = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        await Accounts.AssignAsync(database, ended, patient);
        await Accounts.ExecuteAsync(App, ended, "UPDATE ehr.patient_assignments SET ended_at = now() WHERE clinician_id = @id", ("id", ended));
        var admin = await Accounts.StaffAsync(database, StaffRoles.Admin);
        var patientAccount = await Accounts.PatientAccountAsync(database, patient);
        var disabled = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        await Accounts.AssignAsync(database, disabled, patient);
        await Accounts.LockOutAsync(database, disabled);

        // Row-level security shows them no order to update: nothing changes, and nothing says why.
        foreach (var refused in new Guid?[] { offPanel, ended, admin, patientAccount, disabled, null, Guid.NewGuid() })
        {
            await Accounts.ExecuteAsync(App, refused, Discontinue, ("id", order), ("reason", "Synthetic"));
            Assert.Equal(["active - - - - -"], await RowAsync(order));
        }
    }

    [Fact]
    public async Task Discontinuing_needs_a_reason()
    {
        var (clinician, patient) = await OnPanelAsync();
        var order = await OrderAsync(clinician, patient);

        foreach (var reason in new[] { "", "   " })
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => Accounts.ExecuteAsync(App, clinician, Discontinue, ("id", order), ("reason", reason)));
            Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        }

        var none = await Assert.ThrowsAsync<PostgresException>(() => Accounts.ExecuteAsync(App, clinician,
            "UPDATE ehr.orders SET status = 'revoked' WHERE id = @id", ("id", order)));
        Assert.Equal(PostgresErrorCodes.CheckViolation, none.SqlState);
        Assert.Equal(["active - - - - -"], await RowAsync(order));
    }

    // Active to discontinued, once: nothing reactivates an order, discontinues it twice, rewrites its reason, or gives
    // it any other status.
    [Fact]
    public async Task An_order_changes_only_once_from_active_to_discontinued()
    {
        var (clinician, patient) = await OnPanelAsync();
        var order = await OrderAsync(clinician, patient);
        var other = await OrderAsync(clinician, patient, "SYN-LAB-002");

        foreach (var status in new[] { "completed", "on-hold", "entered-in-error", "draft", "active" })
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => Accounts.ExecuteAsync(App, clinician,
                "UPDATE ehr.orders SET status = @status, discontinue_reason = 'Synthetic' WHERE id = @id", ("id", other), ("status", status)));
            Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        }

        await Accounts.ExecuteAsync(App, clinician, Discontinue, ("id", order), ("reason", "Synthetic first"));
        foreach (var sql in new[]
        {
            Discontinue,
            "UPDATE ehr.orders SET status = 'active', discontinue_reason = @reason WHERE id = @id",
            "UPDATE ehr.orders SET discontinue_reason = @reason WHERE id = @id",
            "UPDATE ehr.orders SET status = 'active', discontinue_reason = NULL WHERE id = @id AND @reason <> ''",
        })
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => Accounts.ExecuteAsync(App, clinician, sql, ("id", order), ("reason", "Synthetic second")));
            Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        }

        Assert.Equal([$"revoked Synthetic first {clinician} true - -"], await RowAsync(order));
        Assert.Equal(["active - - - - -"], await RowAsync(other));
    }

    // Two at once: the second waits for the first at the row, then finds the order discontinued and is refused.
    [Fact]
    public async Task Two_discontinuations_at_once_cannot_both_get_through()
    {
        var (clinician, patient) = await OnPanelAsync();
        var order = await OrderAsync(clinician, patient);

        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(async i =>
        {
            try
            {
                await Accounts.ExecuteAsync(App, clinician, Discontinue, ("id", order), ("reason", $"Synthetic {i}"));
                return true;
            }
            catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.CheckViolation)
            {
                return false;
            }
        }));

        Assert.Single(results, done => done);
        Assert.Equal(["1"], await Accounts.QueryAsync(database.SuperuserConnectionString, null,
            "SELECT count(*) FROM ehr.audit_events WHERE table_name = 'orders' AND row_id = @id AND action = 'update'", ("id", order)));
    }

    [Fact]
    public async Task A_change_signs_a_new_order_and_discontinues_the_old_one_linked_both_ways()
    {
        var (clinician, patient) = await OnPanelAsync();
        var old = await OrderAsync(clinician, patient);
        var replacement = Guid.CreateVersion7();

        // The same item again, with no duplicate reason: the order being replaced isn't a duplicate of its replacement.
        await Accounts.ExecuteAsync(App, clinician, Change, ("new", replacement), ("old", old), ("patient", patient), ("code", "SYN-LAB-001"));

        Assert.Equal([$"revoked Replaced by a changed order {clinician} true - {replacement}"], await RowAsync(old));
        Assert.Equal([$"active - - - {old} -"], await RowAsync(replacement));
    }

    // The duplicate check still applies to a replacement: another active order for the item needs a reason.
    [Fact]
    public async Task A_replacement_that_duplicates_another_active_order_still_needs_a_reason()
    {
        var (clinician, patient) = await OnPanelAsync();
        var old = await OrderAsync(clinician, patient);
        await OrderAsync(clinician, patient, "SYN-LAB-002");

        var error = await Assert.ThrowsAsync<PostgresException>(() => Accounts.ExecuteAsync(App, clinician, Change,
            ("new", Guid.CreateVersion7()), ("old", old), ("patient", patient), ("code", "SYN-LAB-002")));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, error.SqlState);
        Assert.Equal(["active - - - - -"], await RowAsync(old));
    }

    // A replacement signed without discontinuing the order it replaces fails when its transaction commits, so naming an
    // order to replace is no way round the duplicate check.
    [Fact]
    public async Task A_replacement_that_leaves_the_old_order_active_is_not_kept()
    {
        var (clinician, patient) = await OnPanelAsync();
        var old = await OrderAsync(clinician, patient);
        var replacement = Guid.CreateVersion7();

        var error = await Assert.ThrowsAsync<PostgresException>(() => Accounts.ExecuteAsync(App, clinician,
            """
            INSERT INTO ehr.orders (id, patient_id, type, catalog_code, details, status, replaces_order_id)
            VALUES (@new, @patient, 'lab', 'SYN-LAB-001', '{}', 'active', @old)
            """,
            ("new", replacement), ("old", old), ("patient", patient)));

        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        Assert.Empty(await RowAsync(replacement));
        Assert.Equal(["active - - - - -"], await RowAsync(old));

        // Nor one that discontinues it without naming the replacement.
        var unlinked = await Assert.ThrowsAsync<PostgresException>(() => Accounts.ExecuteAsync(App, clinician,
            """
            INSERT INTO ehr.orders (id, patient_id, type, catalog_code, details, status, replaces_order_id)
            VALUES (@new, @patient, 'lab', 'SYN-LAB-001', '{}', 'active', @old);
            UPDATE ehr.orders SET status = 'revoked', discontinue_reason = 'Synthetic' WHERE id = @old
            """,
            ("new", replacement), ("old", old), ("patient", patient)));
        Assert.Equal(PostgresErrorCodes.CheckViolation, unlinked.SqlState);
        Assert.Equal(["active - - - - -"], await RowAsync(old));
    }

    [Fact]
    public async Task An_order_replaces_only_an_active_order_of_the_same_patient_and_type()
    {
        var (clinician, patient) = await OnPanelAsync();
        var discontinued = await OrderAsync(clinician, patient);
        await Accounts.ExecuteAsync(App, clinician, Discontinue, ("id", discontinued), ("reason", "Synthetic"));
        var otherPatient = await Accounts.PatientAsync(database, clinician);
        await Accounts.AssignAsync(database, clinician, otherPatient);
        var theirs = await OrderAsync(clinician, otherPatient);
        var imaging = Guid.CreateVersion7();
        await Accounts.ExecuteAsync(App, clinician,
            "INSERT INTO ehr.orders (id, patient_id, type, catalog_code, details, status) VALUES (@id, @patient, 'imaging', 'SYN-IMG-001', '{}', 'active')",
            ("id", imaging), ("patient", patient));

        foreach (var old in new[] { discontinued, theirs, imaging, Guid.NewGuid() })
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => Accounts.ExecuteAsync(App, clinician, Change,
                ("new", Guid.CreateVersion7()), ("old", old), ("patient", patient), ("code", "SYN-LAB-003")));
            Assert.Contains(error.SqlState, new[] { PostgresErrorCodes.CheckViolation, PostgresErrorCodes.ForeignKeyViolation });
        }

        Assert.Equal(["active - - - - -"], await RowAsync(theirs));
        Assert.Equal(["active - - - - -"], await RowAsync(imaging));
    }

    // "Replaced by" can only name the order that replaces this one: not some other order, and not one of another patient.
    [Fact]
    public async Task An_order_is_marked_replaced_only_by_the_order_that_names_it()
    {
        var (clinician, patient) = await OnPanelAsync();
        var order = await OrderAsync(clinician, patient);
        var unrelated = await OrderAsync(clinician, patient, "SYN-LAB-002");

        foreach (var by in new[] { unrelated, order, Guid.NewGuid() })
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => Accounts.ExecuteAsync(App, clinician,
                "UPDATE ehr.orders SET status = 'revoked', discontinue_reason = 'Synthetic', replaced_by_order_id = @by WHERE id = @id",
                ("id", order), ("by", by)));
            Assert.Contains(error.SqlState, new[] { PostgresErrorCodes.CheckViolation, PostgresErrorCodes.ForeignKeyViolation });
        }

        Assert.Equal(["active - - - - -"], await RowAsync(order));
    }

    // A clinician can't mark an order replaced by a colleague's order: the two halves of a change are one clinician's.
    // Here a colleague signs the replacement, and the signed-in user changes before the old order is discontinued.
    [Fact]
    public async Task The_two_halves_of_a_change_are_the_same_clinicians()
    {
        var (clinician, patient) = await OnPanelAsync();
        var old = await OrderAsync(clinician, patient);
        var colleague = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        await Accounts.AssignAsync(database, colleague, patient);

        var error = await Assert.ThrowsAsync<PostgresException>(() => Accounts.ExecuteAsync(App, colleague,
            """
            INSERT INTO ehr.orders (id, patient_id, type, catalog_code, details, status, replaces_order_id)
            VALUES (@new, @patient, 'lab', 'SYN-LAB-001', '{}', 'active', @old);
            SELECT set_config('app.user_id', @other, true);
            UPDATE ehr.orders SET status = 'revoked', discontinue_reason = 'Synthetic', replaced_by_order_id = @new WHERE id = @old
            """,
            ("new", Guid.CreateVersion7()), ("old", old), ("patient", patient), ("other", clinician.ToString())));

        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        Assert.Equal(["active - - - - -"], await RowAsync(old));
    }

    // An upsert is an update too: it goes through the same trigger.
    [Fact]
    public async Task An_upsert_edits_nothing()
    {
        var (clinician, patient) = await OnPanelAsync();
        var order = await OrderAsync(clinician, patient);

        var error = await Assert.ThrowsAsync<PostgresException>(() => Accounts.ExecuteAsync(App, clinician,
            """
            INSERT INTO ehr.orders (id, patient_id, type, catalog_code, details, status, duplicate_reason)
            VALUES (@id, @patient, 'lab', 'SYN-LAB-001', '{}', 'active', 'Synthetic')
            ON CONFLICT (id) DO UPDATE SET status = 'active', discontinue_reason = 'Synthetic'
            """,
            ("id", order), ("patient", patient)));

        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        Assert.Equal(["active - - - - -"], await RowAsync(order));
    }

    // The grant is on three columns, not the table: 10-grants.sql, repeated here on purpose, as ExpectedPrivileges
    // repeats the table grants (RuntimeRoleTests), where orders stay SELECT and INSERT.
    [Fact]
    public async Task The_app_role_may_update_only_the_columns_discontinuing_writes_and_the_read_role_none()
    {
        const string columns = """
            SELECT string_agg(a.attname, ' ' ORDER BY a.attname)
            FROM pg_attribute a
            WHERE a.attrelid = 'ehr.orders'::regclass AND a.attnum > 0 AND NOT a.attisdropped
              AND has_column_privilege(@role, a.attrelid, a.attnum, 'UPDATE')
            """;

        Assert.Equal(["discontinue_reason replaced_by_order_id status"],
            await Accounts.QueryAsync(database.SuperuserConnectionString, null, columns, ("role", "ehr_app")));
        Assert.Equal([""], await Accounts.QueryAsync(database.SuperuserConnectionString, null, "SELECT coalesce((" + columns + "), '')", ("role", "ehr_read")));
        Assert.Equal(["false false"], await Accounts.QueryAsync(database.SuperuserConnectionString, null,
            "SELECT has_table_privilege('ehr_app', 'ehr.orders', 'UPDATE') || ' ' || has_table_privilege('ehr_app', 'ehr.orders', 'DELETE')"));
    }
}
