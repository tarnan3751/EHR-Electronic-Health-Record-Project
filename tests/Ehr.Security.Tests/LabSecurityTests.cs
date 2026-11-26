using System.Diagnostics;
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
public class LabSecurityTests(MigratedDatabase database) : IClassFixture<MigratedDatabase>, IAsyncLifetime
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    // When the test started, by the database's clock, which its rules hold a collection time to.
    DateTimeOffset Started { get; set; }

    public async ValueTask InitializeAsync() => Started = await database.NowAsync();

    public ValueTask DisposeAsync() => default;

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
        await SettledAsync(patient);
        return order;
    }

    // Until the lab's watermark has passed the transactions that signed the patient's lab orders. Any transaction still
    // running from before them, such as autovacuum's analyze, holds it back for a moment, as it would a lab's; a test
    // that asks at once would be handed nothing yet. Past 30 seconds it fails, rather than let a later assertion fail
    // for another reason, or pass on nothing handed over.
    async Task SettledAsync(Guid patient)
    {
        for (var tries = 0; tries < 600; tries++)
        {
            if ((await AsSuperuserAsync(
                    """
                    SELECT coalesce(pg_snapshot_xmin(pg_current_snapshot()) > (SELECT max(l.transaction_id) FROM ehr.change_log l
                        JOIN ehr.orders o ON o.id = l.row_id WHERE l.table_name = 'orders' AND o.patient_id = @patient), true)::text
                    """, ("patient", patient))).Single() == "true")
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), Cancel);
        }

        Assert.Fail("The lab's watermark did not pass the transaction that signed the patient's lab order within 30 seconds: a transaction still running holds it back.");
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
    async Task<(string Watermark, bool More, JsonElement Orders)> HandOverAsync(
        string? key, string since, Guid? user = null, string? connectionString = null, string? timeZone = null)
    {
        await using var connection = new NpgsqlConnection(connectionString ?? App);
        await connection.OpenAsync(Cancel);
        await using var transaction = await connection.BeginTransactionAsync(Cancel);
        await SetUserAsync(connection, transaction, user);
        if (timeZone is not null)
        {
            await using var zone = new NpgsqlCommand("SELECT set_config('TimeZone', @zone, true)", connection, transaction);
            zone.Parameters.AddWithValue("zone", timeZone);
            await zone.ExecuteNonQueryAsync(Cancel);
        }

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
        command.Parameters.AddWithValue("collected", collectedAt ?? Started.AddMinutes(-30));
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

    // A query, with nobody signed in, after app.lab_door is set to 'open' by hand in the same transaction.
    static async Task<List<string>> WithDoorOpenAsync(string connectionString, string sql, Guid patient)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Cancel);
        await using var transaction = await connection.BeginTransactionAsync(Cancel);
        await SetUserAsync(connection, transaction, null);
        await using (var open = new NpgsqlCommand("SELECT set_config('app.lab_door', 'open', true)", connection, transaction))
        {
            await open.ExecuteNonQueryAsync(Cancel);
        }

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("patient", patient);
        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Cancel);
        while (await reader.ReadAsync(Cancel))
        {
            rows.Add(reader.GetValue(0).ToString()!);
        }

        return rows;
    }

    static async Task RefusedAsync(string state, Func<Task> attempt) =>
        Assert.Equal(state, (await Assert.ThrowsAsync<PostgresException>(attempt)).SqlState);

    // A statement in a session of the owner's own, with nobody signed in and app.lab_door set as asked, in a
    // transaction that is rolled back: how many rows it wrote, and the order's status after it, as the session sees it.
    async Task<(int Written, string? Status)> AsOwnerAsync(bool doorOpen, string sql, Guid patient, Guid? order = null)
    {
        await using var connection = new NpgsqlConnection(database.OwnerConnectionString);
        await connection.OpenAsync(Cancel);
        await using var transaction = await connection.BeginTransactionAsync(Cancel);
        await SetUserAsync(connection, transaction, null);
        await using (var door = new NpgsqlCommand("SELECT set_config('app.lab_door', @door, true)", connection, transaction))
        {
            door.Parameters.AddWithValue("door", doorOpen ? "open" : "");
            await door.ExecuteNonQueryAsync(Cancel);
        }

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("patient", patient);
        command.Parameters.Add(new NpgsqlParameter("order", NpgsqlDbType.Uuid) { Value = (object?)order ?? DBNull.Value });
        var written = await command.ExecuteNonQueryAsync(Cancel);
        await using var status = new NpgsqlCommand("SELECT status FROM ehr.orders WHERE id = @order", connection, transaction);
        status.Parameters.Add(new NpgsqlParameter("order", NpgsqlDbType.Uuid) { Value = (object?)order ?? DBNull.Value });
        var after = (string?)await status.ExecuteScalarAsync(Cancel);
        await transaction.RollbackAsync(Cancel);
        return (written, after);
    }

    // The functions that run as the owner and name a table whose owner policies ask for app.lab_door (30-rls.sql),
    // trigger functions too. A function with a SQL-standard body (RETURN, BEGIN ATOMIC) keeps no source text, but the
    // tables it uses are recorded as what it depends on; any other is matched by its whole definition, in any case and
    // spacing, quoted or not, and by bare names too where its search_path holds ehr. Observations count where a
    // function inserts into them, since reading and updating them are the owner's already; a SQL-standard body that
    // uses them at all counts, since what it does with them isn't recorded.
    const string NamesTheDoorsTables = """
        SELECT p.proname FROM pg_proc p
        WHERE p.prosecdef AND p.proowner = 'ehr_owner'::regrole
          AND CASE WHEN p.prosqlbody IS NOT NULL THEN EXISTS (
                  SELECT FROM pg_depend d
                  WHERE d.classid = 'pg_proc'::regclass AND d.objid = p.oid AND d.refclassid = 'pg_class'::regclass
                    AND d.refobjid IN ('ehr.orders'::regclass, 'ehr.conditions'::regclass, 'ehr.result_acknowledgments'::regclass, 'ehr.observations'::regclass))
              ELSE pg_get_functiondef(p.oid) ~* '"?ehr"?\s*\.\s*"?(orders|conditions|result_acknowledgments)\M|\minsert\s+into\s+"?ehr"?\s*\.\s*"?observations\M'
                OR (EXISTS (SELECT FROM unnest(p.proconfig) AS c WHERE c ~* '^search_path=.*\mehr\M')
                    AND pg_get_functiondef(p.oid) ~* '\m"?(orders|conditions|result_acknowledgments)\M|\minsert\s+into\s+"?observations\M')
              END
        ORDER BY p.proname
        """;

    // The door's own refusal of a caller: insufficient_privilege, named so the app tells it from any other.
    static async Task KeyRefusedAsync(Func<Task> attempt)
    {
        var refused = await Assert.ThrowsAsync<PostgresException>(attempt);
        Assert.Equal((PostgresErrorCodes.InsufficientPrivilege, "lab_key_refused"), (refused.SqlState, refused.ConstraintName));
    }

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
            await KeyRefusedAsync(() => HandOverAsync(key, since));
            await KeyRefusedAsync(() => FileAsync(key, order, Hemoglobin));
        }

        // The right hash, but an account signed in: a clinician on the panel, a patient's portal account.
        foreach (var user in new[] { clinician, account })
        {
            await KeyRefusedAsync(() => HandOverAsync(Key, since, user));
            await KeyRefusedAsync(() => FileAsync(Key, order, Hemoglobin, user: user));
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

    // Step 21: a session of the on-prem app's role left idle in a transaction that has taken a transaction ID, as its
    // first write does, holds back the hand-over, which stops short of the oldest transaction still running. The role's
    // idle limit (db/policies/00-roles.sql) ends that session after a minute, rolling its transaction back, and the lab
    // is then handed what was held back. This test waits out the real minute; it holds nothing back outside this class,
    // whose database is its own and whose tests run one at a time.
    [Fact]
    public async Task A_session_left_idle_in_a_transaction_is_ended_after_a_minute_and_no_longer_holds_back_the_lab()
    {
        var (clinician, patient) = await PanelAsync();
        var since = await NowAsync();

        await using var idle = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(App) { Pooling = false }.ConnectionString);
        await idle.OpenAsync(Cancel);
        var pid = idle.ProcessID;
        await using var transaction = await idle.BeginTransactionAsync(Cancel);
        await using (var write = new NpgsqlCommand("SELECT pg_current_xact_id()", idle, transaction))
        {
            await write.ExecuteScalarAsync(Cancel);
        }

        var idleFor = Stopwatch.StartNew();

        // Signed after the idle session's transaction began, so held back: not handed over while it lasts.
        var order = Guid.CreateVersion7();
        await Accounts.ExecuteAsync(App, clinician, Order, ("id", order), ("patient", patient), ("type", "lab"), ("code", "SYN-LAB-005"));
        var (_, _, held) = await HandOverAsync(Key, since);
        Assert.DoesNotContain(order, held.EnumerateArray().Select(o => o.GetProperty("id").GetGuid()));

        while ((await AsSuperuserAsync("SELECT count(*)::text FROM pg_stat_activity WHERE pid = @pid", ("pid", pid))).Single() != "0"
            && idleFor.Elapsed < TimeSpan.FromSeconds(90))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250), Cancel);
        }

        // Ended by the server at the minute, not before, and its next command is told why.
        Assert.InRange(idleFor.Elapsed, TimeSpan.FromSeconds(59), TimeSpan.FromSeconds(90));
        await using (var next = new NpgsqlCommand("SELECT 1", idle, transaction))
        {
            var ended = await Assert.ThrowsAsync<PostgresException>(() => next.ExecuteScalarAsync(Cancel));
            // idle_in_transaction_session_timeout, which Npgsql's PostgresErrorCodes doesn't name.
            Assert.Equal("25P03", ended.SqlState);
        }

        await SettledAsync(patient);
        var (_, _, handed) = await HandOverAsync(Key, since);
        Assert.Contains(order, handed.EnumerateArray().Select(o => o.GetProperty("id").GetGuid()));
    }

    [Fact]
    public async Task A_lab_filing_follows_the_clinicians_rules_completes_the_order_tells_its_signer_and_names_the_lab()
    {
        var (clinician, patient) = await PanelAsync();
        var order = await SignAsync(clinician, patient);
        var collected = Started.AddMinutes(-20);
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
        Assert.Equal("in-the-future", await FileAsync(Key, open, Hemoglobin, Started.AddMinutes(30)));
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
        var collected = Started.AddMinutes(-5);
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

    // Step 7's rule for the lab: the signer is told; when they can't be, the patient's panel is; and since step 18a, with
    // nobody on the panel either, every administrator whose account isn't disabled is, once, by an item that names the
    // patient by ID and nothing of the test, which the apps never tick off and a clinician's acknowledgment closes. The
    // result is filed all the same.
    [Fact]
    public async Task A_result_for_a_patient_on_no_panel_whose_signer_left_reaches_every_administrator_once()
    {
        var (clinician, patient) = await PanelAsync();
        var admin = await Accounts.StaffAsync(database, StaffRoles.Admin);
        var disabled = await Accounts.StaffAsync(database, StaffRoles.Admin);
        await Accounts.LockOutAsync(database, disabled);
        var order = await SignAsync(clinician, patient);
        await Accounts.ExecuteAsync(database.SuperuserConnectionString, null, "UPDATE ehr.patient_assignments SET ended_at = now() WHERE patient_id = @patient", ("patient", patient));
        await Accounts.LockOutAsync(database, clinician);

        Assert.Equal("filed", await FileAsync(Key, order, Hemoglobin));
        // A second part of the results: the order was completed by the first, so nobody is told again.
        Assert.Equal("filed", await FileAsync(Key, order, [("777-3", 250m, "10*3/uL")]));
        Assert.Equal(["completed"], await AsSuperuserAsync("SELECT status FROM ehr.orders WHERE id = @order", ("order", order)));
        var items = await AsSuperuserAsync(
            "SELECT recipient_id || ' ' || kind || ' ' || patient_id || ' ' || source_table || ' ' || summary || ' ' || sensitive FROM ehr.inbox_items WHERE source_id = @order",
            ("order", order));
        // Each administrator in the database whose account isn't disabled, the fixture's others too, and nobody else.
        var expected = await AsSuperuserAsync(
            """
            SELECT id || ' unrouted-result ' || @patient || ' orders Results are in, and no clinician can receive them false'
            FROM ehr.staff_users WHERE roles @> ARRAY['Admin'] AND (lockout_end IS NULL OR lockout_end < now() + interval '10 years')
            """,
            ("patient", patient));
        Assert.Equal(expected.Order(), items.Order());
        Assert.Contains(items, item => item.StartsWith(admin.ToString(), StringComparison.Ordinal));
        Assert.DoesNotContain(items, item => item.StartsWith(disabled.ToString(), StringComparison.Ordinal) || item.StartsWith(clinician.ToString(), StringComparison.Ordinal));

        // The apps never tick one off, the administrator's own included.
        await RefusedAsync(PostgresErrorCodes.CheckViolation, () => Accounts.ExecuteAsync(App, admin,
            "UPDATE ehr.inbox_items SET done_at = now() WHERE source_id = @order AND recipient_id = @me", ("order", order), ("me", admin)));

        // A clinician takes the patient onto their panel and acknowledges the results: every administrator's item is done.
        var colleague = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        await Accounts.AssignAsync(database, colleague, patient);
        await Accounts.ExecuteAsync(App, colleague, Acknowledge, ("order", order), ("patient", patient), ("by", colleague));
        Assert.Equal(["0"], await AsSuperuserAsync("SELECT count(*) FROM ehr.inbox_items WHERE source_id = @order AND done_at IS NULL", ("order", order)));
    }

    [Fact]
    public async Task Times_in_the_hand_over_are_utc_with_a_z_whatever_the_sessions_time_zone()
    {
        var (clinician, patient) = await PanelAsync();
        var since = await NowAsync();
        var order = await SignAsync(clinician, patient);

        var (_, _, orders) = await HandOverAsync(Key, since, timeZone: "America/Chicago");

        var signedAt = Assert.Single(orders.EnumerateArray()).GetProperty("signedAt").GetString()!;
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{6}Z$", signedAt);
        Assert.Equal(["True"], await AsSuperuserAsync("SELECT signed_at = @at::timestamptz FROM ehr.orders WHERE id = @order", ("at", signedAt), ("order", order)));
    }

    // An answer holds whole signing transactions, as many as come to 100 lab orders, or one alone that signed more: no
    // order is split from its transaction, skipped or handed over twice.
    [Fact]
    public async Task An_answer_is_cut_at_a_hundred_orders_between_transactions_and_a_bigger_transaction_comes_whole()
    {
        var (clinician, patient) = await PanelAsync();
        const string Many = """
            INSERT INTO ehr.orders (id, patient_id, type, catalog_code, details, status, duplicate_reason)
            SELECT uuidv7(), @patient, 'lab', 'SYN-LAB-001', '{}', 'active', 'Synthetic repeat' FROM generate_series(1, @count)
            """;

        var since = await NowAsync();
        foreach (var count in new[] { 60, 60, 150, 1 })
        {
            await Accounts.ExecuteAsync(App, clinician, Many, ("patient", patient), ("count", count));
        }

        await SettledAsync(patient);

        var sizes = new List<int>();
        var handed = new List<Guid>();
        var from = since;
        bool more;
        do
        {
            (from, more, var page) = await HandOverAsync(Key, from);
            sizes.Add(page.GetArrayLength());
            handed.AddRange(page.EnumerateArray().Select(o => o.GetProperty("id").GetGuid()));
        }
        while (more);

        Assert.Equal([60, 60, 150, 1], sizes);
        Assert.Equal(271, handed.Distinct().Count());
        Assert.Equal(handed.Order(), (await AsSuperuserAsync("SELECT id::text FROM ehr.orders WHERE patient_id = @patient", ("patient", patient))).Select(Guid.Parse).Order());
    }

    // The owner's policies for the door hold only while the lab's own two functions run (30-rls.sql). An app session that
    // sets app.lab_door itself sees nothing more with its own statements; another function that runs as the owner sees
    // nothing through them; and while a lab files, ehr.file_result still finds an order's acknowledgment.
    [Fact]
    public async Task The_doors_owner_policies_hold_only_inside_the_labs_own_functions()
    {
        var (clinician, patient) = await PanelAsync();
        await ConditionAsync(patient, "E11.9");
        var open = await SignAsync(clinician, patient);
        var acknowledged = await SignAsync(clinician, patient);
        await Accounts.ExecuteAsync(App, clinician, ByHand, ("patient", patient), ("order", acknowledged), ("code", "718-7"), ("value", 13.1m));
        await Accounts.ExecuteAsync(App, clinician, Acknowledge, ("order", acknowledged), ("patient", patient), ("by", clinician));
        const string Counts = """
            SELECT (SELECT count(*) FROM ehr.orders WHERE patient_id = @patient) || ' ' || (SELECT count(*) FROM ehr.result_acknowledgments WHERE patient_id = @patient)
                   || ' ' || (SELECT count(*) FROM ehr.conditions WHERE patient_id = @patient)
            """;

        // The app's own statements, with nobody signed in, the setting open by hand: nothing.
        Assert.Equal(["0 0 0"], await WithDoorOpenAsync(App, Counts, patient));

        // Functions that run as the owner, made here for the test: one that only counts, one that inserts a result for
        // the acknowledged order as the lab's door would, and the same with the door open.
        await MigratedDatabase.ExecuteAsync(database.OwnerConnectionString, $"""
            CREATE FUNCTION ehr.test_probe_counts(patient uuid) RETURNS text LANGUAGE sql SECURITY DEFINER SET search_path = pg_catalog, pg_temp
            AS $$ {Counts.Replace("@patient", "patient")} $$;
            CREATE FUNCTION ehr.test_probe_file(patient uuid, lab_order uuid) RETURNS void LANGUAGE sql SECURITY DEFINER SET search_path = pg_catalog, pg_temp
            AS $$ INSERT INTO ehr.observations (id, patient_id, order_id, category, code, display, status, value, effective_at)
                  VALUES (uuidv7(), patient, lab_order, 'laboratory', '777-3', 'x', 'final', 250, now()) $$;
            CREATE FUNCTION ehr.test_probe_file_open(patient uuid, lab_order uuid) RETURNS void LANGUAGE sql SECURITY DEFINER
                SET search_path = pg_catalog, pg_temp
            AS $$ SELECT set_config('app.lab_door', 'open', true);
                  INSERT INTO ehr.observations (id, patient_id, order_id, category, code, display, status, value, effective_at)
                  VALUES (uuidv7(), patient, lab_order, 'laboratory', '777-3', 'x', 'final', 250, now()) $$;
            GRANT EXECUTE ON FUNCTION ehr.test_probe_counts(uuid), ehr.test_probe_file(uuid, uuid), ehr.test_probe_file_open(uuid, uuid) TO ehr_app;
            """);
        try
        {
            Assert.Equal(["0 0 0"], await Accounts.QueryAsync(App, null, "SELECT ehr.test_probe_counts(@patient)", ("patient", patient)));
            // Without it the owner can't see the order, and the filing rule refuses before the insert's policy is asked.
            var unseen = await Assert.ThrowsAsync<PostgresException>(() =>
                Accounts.ExecuteAsync(App, null, "SELECT ehr.test_probe_file(@patient, @order)", ("patient", patient), ("order", open)));
            Assert.Equal((PostgresErrorCodes.CheckViolation, "Results are filed against an active lab order of the same patient"), (unseen.SqlState, unseen.MessageText));
            // With the door open the insert reaches ehr.file_result, which finds the acknowledgment and refuses.
            var refused = await Assert.ThrowsAsync<PostgresException>(() =>
                Accounts.ExecuteAsync(App, null, "SELECT ehr.test_probe_file_open(@patient, @order)", ("patient", patient), ("order", acknowledged)));
            Assert.Equal(PostgresErrorCodes.CheckViolation, refused.SqlState);
            Assert.Contains("acknowledged", refused.MessageText, StringComparison.Ordinal);
        }
        finally
        {
            await MigratedDatabase.ExecuteAsync(database.OwnerConnectionString,
                "DROP FUNCTION ehr.test_probe_counts(uuid); DROP FUNCTION ehr.test_probe_file(uuid, uuid); DROP FUNCTION ehr.test_probe_file_open(uuid, uuid)");
        }

        // The lab's own function still sees what it must, and files.
        Assert.Equal("filed", await FileAsync(Key, open, Hemoglobin));
        Assert.Equal("closed", await FileAsync(Key, acknowledged, [("777-3", 250m, "10*3/uL")]));

        // No function that runs as its owner names the door's tables but the lab's two: a new one is a decision to make
        // about app.lab_door (30-rls.sql).
        Assert.Equal(["file_lab_results", "lab_orders"], await AsSuperuserAsync(NamesTheDoorsTables));
    }

    // The list above finds each kind of function its first version missed: one that runs as the owner with a
    // SQL-standard body, which keeps no source text (the review's probe was one); a trigger function that names a table
    // in capitals with spaces around the dot; and one that names it bare, with ehr on its search_path. Each is made
    // here and dropped after.
    [Fact]
    public async Task The_list_of_functions_that_name_the_doors_tables_misses_none()
    {
        await MigratedDatabase.ExecuteAsync(database.OwnerConnectionString,
            """
            CREATE FUNCTION ehr.test_guard_standard(patient uuid) RETURNS bigint LANGUAGE sql SECURITY DEFINER SET search_path = pg_catalog, pg_temp
            RETURN (SELECT count(*) FROM ehr.orders WHERE patient_id = patient) + (SELECT count(*) FROM ehr.conditions WHERE patient_id = patient);
            CREATE FUNCTION ehr.test_guard_trigger() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp
            AS $$ BEGIN PERFORM 1 FROM EHR . ORDERS WHERE id = NEW.id; RETURN NEW; END $$;
            CREATE FUNCTION ehr.test_guard_path(patient uuid) RETURNS bigint LANGUAGE plpgsql SECURITY DEFINER SET search_path = ehr, pg_temp
            AS $$ BEGIN RETURN (SELECT count(*) FROM result_acknowledgments WHERE patient_id = patient); END $$;
            """);
        try
        {
            Assert.Equal(["file_lab_results", "lab_orders", "test_guard_path", "test_guard_standard", "test_guard_trigger"],
                await AsSuperuserAsync(NamesTheDoorsTables));
        }
        finally
        {
            await MigratedDatabase.ExecuteAsync(database.OwnerConnectionString,
                "DROP FUNCTION ehr.test_guard_standard(uuid); DROP FUNCTION ehr.test_guard_trigger(); DROP FUNCTION ehr.test_guard_path(uuid)");
        }

        Assert.Equal(["file_lab_results", "lab_orders"], await AsSuperuserAsync(NamesTheDoorsTables));
    }

    // The door's two writing policies, each on its own, in a session of the owner's own, which ehr.file_result leaves
    // alone (75-results.sql), so only the policy stands in the way. Closed, an update reaches no order (it has no
    // WHERE, so only the update policy is asked, not the select policy a WHERE brings in) and a result is refused.
    // Open, a result of the lab order is inserted and completes the order, as the door's filing does, through the
    // update policy; an order of another kind isn't reached; and a result of no order is refused.
    [Fact]
    public async Task Each_of_the_doors_writing_policies_holds_only_while_the_door_is_open()
    {
        var (clinician, patient) = await PanelAsync();
        var lab = await SignAsync(clinician, patient);
        var medication = await SignAsync(clinician, patient, "SYN-MED-001", "medication");
        const string Complete = "UPDATE ehr.orders SET status = 'completed'";
        const string Result = """
            INSERT INTO ehr.observations (id, patient_id, order_id, category, code, display, status, value, effective_at)
            VALUES (uuidv7(), @patient, @order, 'laboratory', '718-7', 'x', 'final', 9.1, now())
            """;

        Assert.Equal(0, (await AsOwnerAsync(doorOpen: false, Complete, patient)).Written);
        await RefusedAsync(PostgresErrorCodes.InsufficientPrivilege, () => AsOwnerAsync(doorOpen: false, Result, patient, lab));

        Assert.Equal((1, "completed"), await AsOwnerAsync(doorOpen: true, Result, patient, lab));
        Assert.Equal((0, null), await AsOwnerAsync(doorOpen: true, Complete + " WHERE id = @order", patient, medication));
        await RefusedAsync(PostgresErrorCodes.InsufficientPrivilege, () => AsOwnerAsync(doorOpen: true, Result, patient));
    }

    // Each layer that withholds a sensitive diagnosis from the lab, on its own (step 12): the owner's policy on
    // conditions, as the door's functions see the table; and the hand-over's own filter, with the policy out of the
    // picture, as a superuser can arrange inside a transaction that is rolled back.
    [Fact]
    public async Task Each_layer_withholds_a_sensitive_diagnosis_on_its_own()
    {
        var (clinician, patient) = await PanelAsync();
        await ConditionAsync(patient, "E11.9");
        await ConditionAsync(patient, "F32.A");
        var since = await NowAsync();
        var order = await SignAsync(clinician, patient);

        // The policy: as the owner, with the door open as the functions open it.
        Assert.Contains("NOT sensitive", (await AsSuperuserAsync(
            "SELECT pg_get_expr(polqual, polrelid) FROM pg_policy WHERE polname = 'conditions_owner_select'")).Single(), StringComparison.Ordinal);
        Assert.Equal(["E11.9"], await WithDoorOpenAsync(database.OwnerConnectionString, "SELECT code FROM ehr.conditions WHERE patient_id = @patient ORDER BY code", patient));
        Assert.Equal(["0"], await Accounts.QueryAsync(database.OwnerConnectionString, null, "SELECT count(*)::text FROM ehr.conditions WHERE patient_id = @patient", ("patient", patient)));

        // The function: forced row-level security lifted on conditions, so the owner reads every row, and only the
        // hand-over's own filter is left.
        await using var connection = new NpgsqlConnection(database.SuperuserConnectionString);
        await connection.OpenAsync(Cancel);
        await using var transaction = await connection.BeginTransactionAsync(Cancel);
        async Task<string> OneAsync(string sql)
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue("patient", patient);
            command.Parameters.AddWithValue("key", Key);
            command.Parameters.AddWithValue("since", since);
            return (await command.ExecuteScalarAsync(Cancel))?.ToString() ?? "";
        }

        await OneAsync("ALTER TABLE ehr.conditions NO FORCE ROW LEVEL SECURITY");
        await OneAsync("SET LOCAL ROLE ehr_owner");
        Assert.Equal("E11.9 F32.A", await OneAsync("SELECT string_agg(code, ' ' ORDER BY code) FROM ehr.conditions WHERE patient_id = @patient"));
        await OneAsync("RESET ROLE");
        await OneAsync("SELECT set_config('app.user_id', '', true)");
        var orders = JsonDocument.Parse(await OneAsync("SELECT orders::text FROM ehr.lab_orders(@key, @since::xid8)")).RootElement;
        var handed = Assert.Single(orders.EnumerateArray(), o => o.GetProperty("id").GetGuid() == order);
        Assert.Equal(["E11.9"], handed.GetProperty("conditions").EnumerateArray().Select(c => c.GetString()));
        await transaction.RollbackAsync(Cancel);

        Assert.Equal(["True"], await AsSuperuserAsync("SELECT relforcerowsecurity FROM pg_class WHERE oid = 'ehr.conditions'::regclass"));
    }
}
