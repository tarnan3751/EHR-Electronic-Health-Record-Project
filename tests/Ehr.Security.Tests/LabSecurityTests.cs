using System.Text.Json;
using Ehr.Data.Accounts;
using Ehr.Testing;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace Ehr.Security.Tests;

// The expansion plan's step 12 (UC 20): the lab's door in the database (db/policies/76-lab.sql), called as the on-prem
// app's role the way its /api/lab does, and as anyone else. Only a caller that names the hash of the lab's key, with
// nobody signed in, is handed anything or files anything. The hand-over gives the lab orders signed since the lab last
// asked that are still waiting, each with its waiting tests and the patient's diagnoses that aren't sensitive, and
// records each order handed over. A filing goes through ehr.file_result as a clinician's does, all or none and once,
// completes the order, tells its signer, and is audited with the lab as the source and no account.
public class LabSecurityTests(MigratedDatabase database) : IClassFixture<MigratedDatabase>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    string App => database.ConnectionStringFor("ehr_app", MigratedDatabase.AppPassword);

    string Read => database.ConnectionStringFor("ehr_read", MigratedDatabase.ReadPassword);

    static string Key => MigratedDatabase.LabKeyHash;

    // With a reason to order again, so one patient can have several of the same.
    const string Order = "INSERT INTO ehr.orders (id, patient_id, type, catalog_code, details, status, duplicate_reason) VALUES (@id, @patient, @type, @code, '{}', 'active', 'Synthetic repeat')";

    const string ByHand = """
        INSERT INTO ehr.observations (id, patient_id, order_id, code, display, status, value, effective_at)
        VALUES (gen_random_uuid(), @patient, @order, @code, 'x', 'final', @value, now() - interval '1 hour')
        """;

    const string Acknowledge = "INSERT INTO ehr.result_acknowledgments (id, order_id, patient_id, acknowledged_by, acknowledged_at) VALUES (gen_random_uuid(), @order, @patient, @by, now())";

    // A clinician with a patient on their panel.
    async Task<(Guid Clinician, Guid Patient)> PanelAsync()
    {
        var clinician = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        var patient = await Accounts.PatientAsync(database, clinician);
        await Accounts.AssignAsync(database, clinician, patient);
        return (clinician, patient);
    }

    // An order the clinician signs, each in a transaction of its own, as the order form does.
    async Task<Guid> SignAsync(Guid clinician, Guid patient, string code = "SYN-LAB-001", string type = "lab")
    {
        var order = Guid.CreateVersion7();
        await Accounts.ExecuteAsync(App, clinician, Order, ("id", order), ("patient", patient), ("type", type), ("code", code));
        return order;
    }

    // Where the lab would start asking now: every transaction before it has finished.
    async Task<string> NowAsync() =>
        (await AsSuperuserAsync("SELECT pg_snapshot_xmin(pg_current_snapshot())::text")).Single();

    Task<List<string>> AsSuperuserAsync(string sql, params (string Name, object Value)[] parameters) =>
        Accounts.QueryAsync(database.SuperuserConnectionString, null, sql, parameters);

    Task ConditionAsync(Guid patient, string code, string status = "active") =>
        Accounts.ExecuteAsync(database.SuperuserConnectionString, null,
            "INSERT INTO ehr.conditions (id, patient_id, code, display, clinical_status) VALUES (gen_random_uuid(), @patient, @code, 'Synthetic', @status)",
            ("patient", patient), ("code", code), ("status", status));

    // The hand-over, as the on-prem app's role, committed: the watermark, whether there is more, and the orders.
    async Task<(string Watermark, bool More, JsonElement Orders)> HandOverAsync(string? key, string since, Guid? user = null, string? connectionString = null)
    {
        await using var connection = new NpgsqlConnection(connectionString ?? App);
        await connection.OpenAsync(Cancel);
        await using var transaction = await connection.BeginTransactionAsync(Cancel);
        await SetUserAsync(connection, transaction, user);
        await using var command = new NpgsqlCommand("SELECT watermark::text, more, orders::text FROM ehr.lab_orders(@key, @since::xid8)", connection, transaction);
        command.Parameters.Add(new NpgsqlParameter("key", NpgsqlDbType.Text) { Value = (object?)key ?? DBNull.Value });
        command.Parameters.AddWithValue("since", since);
        (string, bool, JsonElement) answer;
        await using (var reader = await command.ExecuteReaderAsync(Cancel))
        {
            await reader.ReadAsync(Cancel);
            answer = (reader.GetString(0), reader.GetBoolean(1), JsonDocument.Parse(reader.GetString(2)).RootElement.Clone());
        }

        await transaction.CommitAsync(Cancel);
        return answer;
    }

    // A filing, as the on-prem app's role, committed: the word the door answers with.
    async Task<string> FileAsync(
        string? key, Guid order, (string Code, decimal Value, string Unit)[] results, DateTimeOffset? collectedAt = null, Guid? user = null,
        NpgsqlConnection? open = null, Func<Task>? beforeCommit = null)
    {
        await using var owned = open is null ? new NpgsqlConnection(App) : null;
        var connection = open ?? owned!;
        if (owned is not null)
        {
            await owned.OpenAsync(Cancel);
        }

        await using var transaction = await connection.BeginTransactionAsync(Cancel);
        await SetUserAsync(connection, transaction, user);
        await using var command = new NpgsqlCommand("SELECT ehr.file_lab_results(@key, @order, @collected, @codes, @values, @units)", connection, transaction);
        command.Parameters.Add(new NpgsqlParameter("key", NpgsqlDbType.Text) { Value = (object?)key ?? DBNull.Value });
        command.Parameters.AddWithValue("order", order);
        command.Parameters.AddWithValue("collected", collectedAt ?? DateTimeOffset.UtcNow.AddMinutes(-30));
        command.Parameters.AddWithValue("codes", results.Select(r => r.Code).ToArray());
        command.Parameters.AddWithValue("values", results.Select(r => r.Value).ToArray());
        command.Parameters.AddWithValue("units", results.Select(r => r.Unit).ToArray());
        var outcome = (string)(await command.ExecuteScalarAsync(Cancel))!;
        if (beforeCommit is not null)
        {
            await beforeCommit();
        }

        await transaction.CommitAsync(Cancel);
        return outcome;
    }

    static async Task SetUserAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid? user)
    {
        await using var set = new NpgsqlCommand("SELECT set_config('app.user_id', @user, true), set_config('app.acting_grant', '', true)", connection, transaction);
        set.Parameters.AddWithValue("user", user?.ToString() ?? "");
        await set.ExecuteNonQueryAsync(Cancel);
    }

    static async Task RefusedAsync(string state, Func<Task> attempt) =>
        Assert.Equal(state, (await Assert.ThrowsAsync<PostgresException>(attempt)).SqlState);

    static readonly (string, decimal, string)[] Hemoglobin = [("718-7", 9.1m, "g/dL")];

    // What the audit log holds about an order and its patient: action, table, actor or "-", source.
    async Task<List<string>> EventsAsync(Guid patient) =>
        await AsSuperuserAsync(
            "SELECT action || ' ' || coalesce(table_name, '-') || ' ' || coalesce(actor_id::text, '-') || ' ' || source FROM ehr.audit_events WHERE patient_id = @patient ORDER BY occurred_at, id",
            ("patient", patient));

    [Fact]
    public async Task Without_the_labs_key_or_with_someone_signed_in_the_door_gives_nothing_and_files_nothing()
    {
        var (clinician, patient) = await PanelAsync();
        var since = await NowAsync();
        var order = await SignAsync(clinician, patient);
        var account = await Accounts.PatientAccountAsync(database, patient);
        var before = await EventsAsync(patient);

        // No hash, an empty one, a wrong one, the key itself rather than its hash, and the hash in capitals.
        foreach (var key in new[] { null, "", new string('0', 64), MigratedDatabase.LabKey, Key.ToUpperInvariant() })
        {
            await RefusedAsync(PostgresErrorCodes.InsufficientPrivilege, () => HandOverAsync(key, since));
            await RefusedAsync(PostgresErrorCodes.InsufficientPrivilege, () => FileAsync(key, order, Hemoglobin));
        }

        // The right hash, but an account signed in: a clinician on the panel, a patient's portal account.
        foreach (var user in new[] { clinician, account })
        {
            await RefusedAsync(PostgresErrorCodes.InsufficientPrivilege, () => HandOverAsync(Key, since, user));
            await RefusedAsync(PostgresErrorCodes.InsufficientPrivilege, () => FileAsync(Key, order, Hemoglobin, user: user));
        }

        // The read-only role may not call the door at all, even with the hash.
        await RefusedAsync(PostgresErrorCodes.InsufficientPrivilege, () => HandOverAsync(Key, since, connectionString: Read));

        Assert.Equal(before, await EventsAsync(patient));
        Assert.Equal(["0 active"], await AsSuperuserAsync(
            "SELECT (SELECT count(*) FROM ehr.observations WHERE order_id = @order) || ' ' || (SELECT status FROM ehr.orders WHERE id = @order)", ("order", order)));
    }

    [Fact]
    public async Task Whether_a_lab_is_connected_is_all_either_role_learns_and_without_a_key_nothing_gets_in()
    {
        foreach (var connection in new[] { App, Read })
        {
            Assert.Equal(["True"], await Accounts.QueryAsync(connection, null, "SELECT ehr.lab_connected()"));
        }

        var (clinician, patient) = await PanelAsync();
        var since = await NowAsync();
        var order = await SignAsync(clinician, patient);
        try
        {
            // The migration job with no EHR_LAB_KEY stores none: then not even the old hash opens the door.
            await MigratedDatabase.ExecuteAsync(database.OwnerConnectionString, "SELECT ehr.set_lab_key(NULL)");
            foreach (var connection in new[] { App, Read })
            {
                Assert.Equal(["False"], await Accounts.QueryAsync(connection, null, "SELECT ehr.lab_connected()"));
            }

            await RefusedAsync(PostgresErrorCodes.InsufficientPrivilege, () => HandOverAsync(Key, since));
            await RefusedAsync(PostgresErrorCodes.InsufficientPrivilege, () => FileAsync(Key, order, Hemoglobin));
        }
        finally
        {
            await MigratedDatabase.ExecuteAsync(database.OwnerConnectionString, $"SELECT ehr.set_lab_key('{Key}')");
        }

        Assert.Equal(["0"], await AsSuperuserAsync("SELECT count(*) FROM ehr.observations WHERE order_id = @order", ("order", order)));
    }

    [Fact]
    public async Task The_hand_over_gives_each_waiting_lab_order_its_waiting_tests_and_only_what_a_lab_needs_and_records_each()
    {
        var (clinician, patient) = await PanelAsync();
        await ConditionAsync(patient, "E11.9");
        await ConditionAsync(patient, "E11.9", "recurrence");
        await ConditionAsync(patient, "I10", "resolved");
        // Sensitive by its code (65-chart-lists.sql): never handed over.
        await ConditionAsync(patient, "F32.A");
        Assert.Equal(["True"], await AsSuperuserAsync("SELECT sensitive FROM ehr.conditions WHERE patient_id = @patient AND code = 'F32.A'", ("patient", patient)));
        var since = await NowAsync();

        var count = await SignAsync(clinician, patient);
        var a1c = await SignAsync(clinician, patient, "SYN-LAB-005");
        await SignAsync(clinician, patient, "SYN-MED-001", "medication");
        // Reports nothing a result can be filed for.
        await SignAsync(clinician, patient, "SYN-LAB-007");
        var discontinued = await SignAsync(clinician, patient, "SYN-LAB-006");
        await Accounts.ExecuteAsync(App, clinician, "UPDATE ehr.orders SET status = 'revoked', discontinue_reason = 'Synthetic' WHERE id = @order", ("order", discontinued));
        var acknowledged = await SignAsync(clinician, patient, "SYN-LAB-008");
        await Accounts.ExecuteAsync(App, clinician, ByHand, ("patient", patient), ("order", acknowledged), ("code", "62292-8"), ("value", 41m));
        await Accounts.ExecuteAsync(App, clinician, Acknowledge, ("order", acknowledged), ("patient", patient), ("by", clinician));
        // A clinician files one of the count's four tests by hand: the lab is handed the other three.
        await Accounts.ExecuteAsync(App, clinician, ByHand, ("patient", patient), ("order", count), ("code", "718-7"), ("value", 13.1m));
        var before = await EventsAsync(patient);

        var (watermark, more, orders) = await HandOverAsync(Key, since);

        Assert.False(more);
        Assert.True(ulong.Parse(watermark) > ulong.Parse(since));
        Assert.Equal([count, a1c], orders.EnumerateArray().Select(o => o.GetProperty("id").GetGuid()));
        foreach (var handed in orders.EnumerateArray())
        {
            Assert.Equal(["conditions", "id", "signedAt", "tests"], handed.EnumerateObject().Select(p => p.Name).Order());
            Assert.Equal(["E11.9"], handed.GetProperty("conditions").EnumerateArray().Select(c => c.GetString()));
            Assert.All(handed.GetProperty("tests").EnumerateArray(), test =>
                Assert.Equal(["code", "decimals", "high", "low", "unit"], test.EnumerateObject().Select(p => p.Name).Order()));
        }

        Assert.Equal(
            ["4544-3 % 36 50 0", "6690-2 10*3/uL 4.5 11.0 1", "777-3 10*3/uL 150 400 0"],
            orders[0].GetProperty("tests").EnumerateArray().Select(t =>
                $"{t.GetProperty("code")} {t.GetProperty("unit")} {t.GetProperty("low").GetRawText()} {t.GetProperty("high").GetRawText()} {t.GetProperty("decimals")}"));
        Assert.Equal(["4548-4 % 4.0 5.6 1"], orders[1].GetProperty("tests").EnumerateArray().Select(t =>
            $"{t.GetProperty("code")} {t.GetProperty("unit")} {t.GetProperty("low").GetRawText()} {t.GetProperty("high").GetRawText()} {t.GetProperty("decimals")}"));
        Assert.Equal(
            (await AsSuperuserAsync("SELECT signed_at FROM ehr.orders WHERE id = @order", ("order", count))).Single(),
            (await AsSuperuserAsync("SELECT (@at::text)::timestamptz", ("at", orders[0].GetProperty("signedAt").GetString()!))).Single());

        // One disclosure recorded for each order handed over: no account, the lab as the source, the order as the row.
        Assert.Equal([.. before, "sent_to_lab orders - lab", "sent_to_lab orders - lab"], await EventsAsync(patient));
        Assert.Equal([$"{count}", $"{a1c}"], await AsSuperuserAsync(
            "SELECT row_id::text FROM ehr.audit_events WHERE patient_id = @patient AND action = 'sent_to_lab' AND details IS NULL AND NOT sensitive ORDER BY row_id",
            ("patient", patient)));

        // Asked again from where it got to: nothing new, and nothing recorded.
        var (next, _, none) = await HandOverAsync(Key, watermark);
        Assert.Equal(0, none.GetArrayLength());
        Assert.True(ulong.Parse(next) >= ulong.Parse(watermark));
        Assert.Equal(2, (await EventsAsync(patient)).Count - before.Count);

        // A lab starting again asks from 0, and is handed what is still waiting again, a hundred signing transactions
        // at a time (other tests' orders may wait too), recorded again.
        var again = new List<Guid>();
        var resumeFrom = "0";
        bool morePages;
        do
        {
            (resumeFrom, morePages, var page) = await HandOverAsync(Key, resumeFrom);
            again.AddRange(page.EnumerateArray().Select(o => o.GetProperty("id").GetGuid()));
        }
        while (morePages);

        Assert.Contains(count, again);
        Assert.Contains(a1c, again);
        Assert.Equal(4, (await EventsAsync(patient)).Count - before.Count);
    }

    [Fact]
    public async Task More_than_a_hundred_signing_transactions_are_handed_over_a_hundred_at_a_time_and_none_is_missed()
    {
        var (clinician, patient) = await PanelAsync();
        var since = await NowAsync();
        var signed = new List<Guid>();
        for (var i = 0; i < 101; i++)
        {
            signed.Add(await SignAsync(clinician, patient));
        }

        var (watermark, more, first) = await HandOverAsync(Key, since);
        var (_, last, rest) = await HandOverAsync(Key, watermark);

        Assert.True(more);
        Assert.False(last);
        Assert.Equal(100, first.GetArrayLength());
        Assert.Equal(signed, first.EnumerateArray().Concat(rest.EnumerateArray()).Select(o => o.GetProperty("id").GetGuid()));
    }

    [Fact]
    public async Task A_lab_filing_follows_the_clinicians_rules_completes_the_order_tells_its_signer_and_names_the_lab()
    {
        var (clinician, patient) = await PanelAsync();
        var order = await SignAsync(clinician, patient);
        var collected = DateTimeOffset.UtcNow.AddMinutes(-20);
        var before = await EventsAsync(patient);

        Assert.Equal("filed", await FileAsync(Key, order, [("718-7", 9.1m, "g/dL"), ("777-3", 250m, "10*3/uL")], collected));

        // The database's own name, unit and range, final and released, as for a clinician's filing (ResultSecurityTests).
        Assert.Equal(
            ["718-7|Hemoglobin [Mass/volume] in Blood|g/dL|12.0000|17.5000|final|true|9.1000|laboratory|", "777-3|Platelets [#/volume] in Blood by Automated count|10*3/uL|150.0000|400.0000|final|true|250.0000|laboratory|"],
            await AsSuperuserAsync(
                """
                SELECT code || '|' || display || '|' || unit || '|' || reference_low || '|' || reference_high || '|' || status || '|'
                       || (issued_at > now() - interval '1 minute') || '|' || value || '|' || category || '|' || coalesce(reading_id::text, '')
                FROM ehr.observations WHERE order_id = @order ORDER BY code
                """, ("order", order)));
        Assert.Equal(["completed"], await AsSuperuserAsync("SELECT status FROM ehr.orders WHERE id = @order", ("order", order)));
        Assert.Equal([$"{clinician} result Results are in"], await AsSuperuserAsync(
            "SELECT recipient_id || ' ' || kind || ' ' || summary FROM ehr.inbox_items WHERE source_id = @order", ("order", order)));
        // Every write it made is audited as the lab's, with no account: the two results, the order completed, the item.
        Assert.Equal(
            [.. before, "insert observations - lab", "update orders - lab", "insert inbox_items - lab", "insert observations - lab"],
            await EventsAsync(patient));

        // The same post again is answered the same and files nothing; a different value for a filed test is refused
        // whole, the rest of the post with it; then the remaining tests are filed.
        var after = await EventsAsync(patient);
        Assert.Equal("already-filed", await FileAsync(Key, order, [("718-7", 9.10m, "g/dL"), ("777-3", 250m, "10*3/uL")], collected));
        Assert.Equal("different-result", await FileAsync(Key, order, [("718-7", 9.2m, "g/dL"), ("6690-2", 5.0m, "10*3/uL")], collected));
        Assert.Equal("different-result", await FileAsync(Key, order, [("718-7", 9.1m, "g/dL"), ("6690-2", 5.0m, "10*3/uL")], collected.AddMinutes(1)));
        Assert.Equal(after, await EventsAsync(patient));
        Assert.Equal("filed", await FileAsync(Key, order, [("718-7", 9.1m, "g/dL"), ("6690-2", 5.0m, "10*3/uL"), ("4544-3", 30m, "%")], collected));
        Assert.Equal(["4"], await AsSuperuserAsync("SELECT count(*) FROM ehr.observations WHERE order_id = @order", ("order", order)));
        Assert.Equal(["1"], await AsSuperuserAsync("SELECT count(*) FROM ehr.inbox_items WHERE source_id = @order", ("order", order)));
    }

    [Fact]
    public async Task Each_refusal_files_nothing_and_says_why_in_a_word()
    {
        var (clinician, patient) = await PanelAsync();
        var open = await SignAsync(clinician, patient);
        var medication = await SignAsync(clinician, patient, "SYN-MED-001", "medication");
        var discontinued = await SignAsync(clinician, patient, "SYN-LAB-006");
        await Accounts.ExecuteAsync(App, clinician, "UPDATE ehr.orders SET status = 'revoked', discontinue_reason = 'Synthetic' WHERE id = @order", ("order", discontinued));
        // Part filed by hand and acknowledged: closed, whatever is still missing.
        var acknowledged = await SignAsync(clinician, patient);
        await Accounts.ExecuteAsync(App, clinician, ByHand, ("patient", patient), ("order", acknowledged), ("code", "718-7"), ("value", 13.1m));
        await Accounts.ExecuteAsync(App, clinician, Acknowledge, ("order", acknowledged), ("patient", patient), ("by", clinician));
        var before = await EventsAsync(patient);

        Assert.Equal("unknown-order", await FileAsync(Key, Guid.CreateVersion7(), Hemoglobin));
        Assert.Equal("unknown-order", await FileAsync(Key, medication, Hemoglobin));
        Assert.Equal("discontinued", await FileAsync(Key, discontinued, [("3016-3", 2.1m, "m[IU]/L")]));
        Assert.Equal("closed", await FileAsync(Key, acknowledged, [("777-3", 250m, "10*3/uL")]));
        Assert.Equal("not-reported", await FileAsync(Key, open, [("718-7", 9.1m, "g/dL"), ("4548-4", 6.1m, "%")]));
        Assert.Equal("wrong-unit", await FileAsync(Key, open, [("718-7", 91m, "g/L")]));
        Assert.Equal("in-the-future", await FileAsync(Key, open, Hemoglobin, DateTimeOffset.UtcNow.AddMinutes(30)));
        foreach (var malformed in new (string, decimal, string)[][]
        {
            [],
            [("718-7", 9.1m, "g/dL"), ("718-7", 9.1m, "g/dL")],
            [("718-7", -1m, "g/dL")],
            [("718-7", 100_000_000m, "g/dL")],
            [("718-7", 9.12345m, "g/dL")],
            [.. Enumerable.Range(0, 51).Select(i => ($"code-{i}", 1m, "u"))],
        })
        {
            Assert.Equal("malformed", await FileAsync(Key, open, malformed));
        }

        Assert.Equal(before, await EventsAsync(patient));
        Assert.Equal(["1 active revoked completed"], await AsSuperuserAsync(
            """
            SELECT (SELECT count(*) FROM ehr.observations WHERE patient_id = @patient) || ' ' || (SELECT status FROM ehr.orders WHERE id = @open)
                   || ' ' || (SELECT status FROM ehr.orders WHERE id = @discontinued) || ' ' || (SELECT status FROM ehr.orders WHERE id = @acknowledged)
            """,
            ("patient", patient), ("open", open), ("discontinued", discontinued), ("acknowledged", acknowledged)));
    }

    [Fact]
    public async Task Two_labs_filing_one_order_at_once_file_it_once()
    {
        var (clinician, patient) = await PanelAsync();
        var order = await SignAsync(clinician, patient);
        var collected = DateTimeOffset.UtcNow.AddMinutes(-5);
        await using var first = new NpgsqlConnection(App);
        await first.OpenAsync(Cancel);

        // The second lab's post starts while the first holds the order's lock, waits for it, and then finds the
        // first's results.
        Task<string>? second = null;
        var outcome = await FileAsync(Key, order, Hemoglobin, collected, open: first, beforeCommit: async () =>
        {
            second = FileAsync(Key, order, Hemoglobin, collected);
            await Task.Delay(TimeSpan.FromMilliseconds(500), Cancel);
            Assert.False(second.IsCompleted);
        });

        Assert.Equal("filed", outcome);
        Assert.Equal("already-filed", await second!);
        Assert.Equal(["1 1"], await AsSuperuserAsync(
            "SELECT (SELECT count(*) FROM ehr.observations WHERE order_id = @order) || ' ' || (SELECT count(*) FROM ehr.inbox_items WHERE source_id = @order)",
            ("order", order)));
    }

    [Fact]
    public async Task A_session_that_claims_the_labs_source_for_its_own_writes_is_audited_as_itself()
    {
        var (clinician, patient) = await PanelAsync();
        var order = await SignAsync(clinician, patient);
        var before = await EventsAsync(patient);

        // Filed by hand, unchanged: the clinician is the actor and the source is on-prem, whatever the session says.
        await Accounts.ExecuteAsync(App, clinician,
            $"SELECT set_config('app.write_source', 'lab', true); {ByHand.Replace("@order", $"'{order}'")}",
            ("patient", patient), ("code", "718-7"), ("value", 9.1m));

        Assert.Equal(
            [.. before, $"insert observations {clinician} onprem", $"update orders {clinician} onprem", $"insert inbox_items {clinician} onprem"],
            await EventsAsync(patient));

        // With nobody signed in, claiming the lab's source writes nothing at all: the filing rule finds no order to
        // file against, or no policy lets the row in.
        var refused = await Assert.ThrowsAsync<PostgresException>(() => Accounts.ExecuteAsync(App, null,
            $"SELECT set_config('app.write_source', 'lab', true); {ByHand.Replace("@order", $"'{order}'")}",
            ("patient", patient), ("code", "777-3"), ("value", 250m)));
        Assert.Contains(refused.SqlState, new[] { PostgresErrorCodes.InsufficientPrivilege, PostgresErrorCodes.CheckViolation });
        Assert.Equal(3, (await EventsAsync(patient)).Count - before.Count);
    }

    // Which triggers on the tables a lab filing touches ask who is writing (current_user), and that each holds for the
    // lab's rows; and that the ones that don't ask do what they should for them. The list is pinned: a new trigger
    // there that asks fails this until its behaviour for the lab's door is decided.
    [Fact]
    public async Task The_triggers_that_ask_who_is_writing_hold_for_the_rows_the_lab_writes()
    {
        var asking = await AsSuperuserAsync(
            """
            SELECT t.tgname FROM pg_trigger t JOIN pg_proc p ON p.oid = t.tgfoid
            WHERE NOT t.tgisinternal AND t.tgrelid IN ('ehr.observations'::regclass, 'ehr.orders'::regclass, 'ehr.inbox_items'::regclass, 'ehr.audit_events'::regclass)
              AND (pg_get_triggerdef(t.oid) ~* '\mcurrent_user\M' OR p.prosrc ~* '\mcurrent_user\M')
            ORDER BY t.tgname
            """);
        Assert.Equal(["inbox_items_complete", "observations_file", "observations_mark_in_error", "observations_mark_reading"], asking);

        var (clinician, patient) = await PanelAsync();
        var since = await NowAsync();
        var order = await SignAsync(clinician, patient);
        // The change log has the signing, once: a filing logs nothing.
        Assert.Equal(["1"], await AsSuperuserAsync("SELECT count(*) FROM ehr.change_log WHERE table_name = 'orders' AND row_id = @order", ("order", order)));
        await HandOverAsync(Key, since);
        Assert.Equal("filed", await FileAsync(Key, order, Hemoglobin));
        Assert.Equal(["1"], await AsSuperuserAsync("SELECT count(*) FROM ehr.change_log WHERE row_id = @order OR row_id IN (SELECT id FROM ehr.observations WHERE order_id = @order)", ("order", order)));

        // observations_file ran for the lab's row: the database's name and range, released, and no reading (so the
        // reading checks and observations_mark_reading, keyed on one, never concern it).
        Assert.Equal(["Hemoglobin [Mass/volume] in Blood 12.0000 true true"], await AsSuperuserAsync(
            "SELECT display || ' ' || reference_low || ' ' || (issued_at IS NOT NULL) || ' ' || (reading_id IS NULL) FROM ehr.observations WHERE order_id = @order", ("order", order)));

        // observations_mark_in_error: a clinician on the panel can't mark the lab's result entered in error, as they
        // can't their own: only a vital sign is.
        await RefusedAsync(PostgresErrorCodes.CheckViolation, () => Accounts.ExecuteAsync(App, clinician,
            "UPDATE ehr.observations SET status = 'entered-in-error', entered_in_error_reason = 'Synthetic' WHERE order_id = @order", ("order", order)));

        // inbox_items_complete: the signer's item for the lab's results isn't ticked off; acknowledging clears it.
        await RefusedAsync(PostgresErrorCodes.CheckViolation, () => Accounts.ExecuteAsync(App, clinician,
            "UPDATE ehr.inbox_items SET done_at = now() WHERE source_id = @order", ("order", order)));
        await Accounts.ExecuteAsync(App, clinician, Acknowledge, ("order", order), ("patient", patient), ("by", clinician));
        Assert.Equal(["True"], await AsSuperuserAsync("SELECT done_at IS NOT NULL FROM ehr.inbox_items WHERE source_id = @order", ("order", order)));

        // The write audit, which asks nobody: the lab's writes are the lab's, the clinician's acknowledgment the
        // clinician's. audit_events_guard: nothing the lab did updated an audit event.
        Assert.Equal(
            ["sent_to_lab orders - lab", "insert observations - lab", "update orders - lab", "insert inbox_items - lab",
             $"insert result_acknowledgments {clinician} onprem", $"update inbox_items {clinician} onprem"],
            (await EventsAsync(patient)).Where(e => !e.StartsWith("insert orders", StringComparison.Ordinal) && !e.StartsWith("insert patient", StringComparison.Ordinal)));
    }

    // Step 7's rule, unchanged for the lab: the signer is told; when they can't be, the patient's panel is; with nobody
    // on the panel either, nobody is, and the result is still filed. (For Tahseen: whether admins should be told then.)
    [Fact]
    public async Task A_result_for_a_patient_on_no_panel_whose_signer_left_is_filed_and_reaches_no_inbox()
    {
        var (clinician, patient) = await PanelAsync();
        var order = await SignAsync(clinician, patient);
        await Accounts.ExecuteAsync(database.SuperuserConnectionString, null, "UPDATE ehr.patient_assignments SET ended_at = now() WHERE patient_id = @patient", ("patient", patient));
        await Accounts.LockOutAsync(database, clinician);

        Assert.Equal("filed", await FileAsync(Key, order, Hemoglobin));
        Assert.Equal(["completed 0"], await AsSuperuserAsync(
            "SELECT (SELECT status FROM ehr.orders WHERE id = @order) || ' ' || (SELECT count(*) FROM ehr.inbox_items WHERE source_id = @order)", ("order", order)));
    }
}
