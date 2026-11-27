using Ehr.Data.Accounts;
using Ehr.Testing;
using Npgsql;
using Xunit;
using static Ehr.Testing.TestRun;

namespace Ehr.Security.Tests;

// Step 9c's follow-up, after Tahseen's answers: a vital sign belongs to the patient's visit that was held when it was
// taken or began within fifteen minutes after, decided from the time taken alone, whether it was typed before the
// check-in or after it (db/policies/75-results.sql: ehr.file_result, and ehr.attach_vital_signs, which ties the
// moments already recorded to the visit a check-in starts). A visit the apps insert starts at the database's wall clock,
// whatever they send (ehr.check_in_visit), so no visit dated to suit can take a vital sign. What the check-in may
// change, who can make it change anything, which visit wins, and two transactions at once.
public class VitalSignVisitSecurityTests(MigratedDatabase database) : IClassFixture<MigratedDatabase>
{
    const string Systolic = "8480-6";
    const string Diastolic = "8462-4";
    const string HeartRate = "8867-4";

    string App => database.ConnectionStringFor("ehr_app", MigratedDatabase.AppPassword);

    string Read => database.ConnectionStringFor("ehr_read", MigratedDatabase.ReadPassword);

    string Superuser => database.SuperuserConnectionString;

    // Now by the database's clock, which its rules hold a time to, to the second.
    async Task<DateTime> NowAsync()
    {
        var now = await database.NowAsync();
        return new(now.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, DateTimeKind.Utc);
    }

    const string Insert = """
        INSERT INTO ehr.observations (id, patient_id, encounter_id, category, code, display, status, value, effective_at)
        VALUES (gen_random_uuid(), @patient, @visit, 'vital-signs', @code, 'x', 'final', @value, @at);
        """;

    static (string, object)[] Values(Guid patient, string code, decimal value, DateTime at, Guid? visit = null) =>
        [("patient", patient), ("code", code), ("value", value), ("at", at), ("visit", (object?)visit ?? DBNull.Value)];

    const string Visit = "INSERT INTO ehr.encounters (id, patient_id, status, class_code, period_start) VALUES (@id, @patient, @status, 'AMB', @start)";

    static (string, object)[] VisitValues(Guid id, Guid patient, DateTime start, string status = "in-progress") =>
        [("id", id), ("patient", patient), ("status", status), ("start", start)];

    async Task<(Guid Clinician, Guid Patient)> OnPanelAsync()
    {
        var clinician = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        var patient = await Accounts.PatientAsync(database, clinician);
        await Accounts.AssignAsync(database, clinician, patient);
        return (clinician, patient);
    }

    Task RecordAsync(Guid clinician, Guid patient, string code, decimal value, DateTime at, Guid? visit = null) =>
        Accounts.ExecuteAsync(App, clinician, Insert, Values(patient, code, value, at, visit));

    Task RecordPressureAsync(Guid clinician, Guid patient, decimal systolic, decimal diastolic, DateTime at) =>
        Accounts.ExecuteAsync(App, clinician, Insert + Insert.Replace("@code", "@other_code", StringComparison.Ordinal).Replace("@value", "@other_value", StringComparison.Ordinal),
            [.. Values(patient, Systolic, systolic, at), ("other_code", Diastolic), ("other_value", diastolic)]);

    // A check-in as the app makes one: the visit, by a clinician with the patient on their panel, sending a start the
    // database replaces with its own clock.
    async Task<Guid> CheckInAsync(Guid clinician, Guid patient, DateTime sent, string status = "in-progress")
    {
        var visit = Guid.CreateVersion7();
        await Accounts.ExecuteAsync(App, clinician, Visit, VisitValues(visit, patient, sent, status));
        return visit;
    }

    async Task<DateTime> StartAsync(Guid visit) =>
        DateTime.Parse((await AsSuperuserAsync("SELECT to_char(period_start AT TIME ZONE 'UTC', 'YYYY-MM-DD\"T\"HH24:MI:SS.US') FROM ehr.encounters WHERE id = @id", ("id", visit))).Single(),
            System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);

    Task<List<string>> AsSuperuserAsync(string sql, params (string Name, object Value)[] parameters) =>
        Accounts.QueryAsync(Superuser, null, sql, parameters);

    // "code value visit" for each of the patient's vital signs, by time taken and code: the visit as "visit" when it is
    // `visit`, "earlier" when it is `earlier`.
    Task<List<string>> VisitsAsync(Guid patient, Guid? visit = null, Guid? earlier = null) => AsSuperuserAsync(
        """
        SELECT code || ' ' || trim_scale(value) || ' ' || CASE WHEN encounter_id IS NULL THEN 'none' WHEN encounter_id = @visit THEN 'visit'
            WHEN encounter_id = @earlier THEN 'earlier' ELSE encounter_id::text END
        FROM ehr.observations WHERE patient_id = @patient ORDER BY effective_at, code, value
        """, ("patient", patient), ("visit", visit ?? Guid.Empty), ("earlier", earlier ?? Guid.Empty));

    static async Task RefusedAsync(string state, Func<Task> attempt) =>
        Assert.Equal(state, (await Assert.ThrowsAsync<PostgresException>(attempt)).SqlState);

    // Tahseen's two cases and their mirrors: taken ten minutes before the check-in, the visit's, and twenty minutes
    // before, none, whether typed before the check-in or after it. And around the edge, fifteen minutes before the
    // visit's start (the database's clock at the insert, read back), each value has the visit exactly when it was taken
    // no earlier than that: the check-in and ehr.file_result draw the same line.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_sign_taken_up_to_fifteen_minutes_before_the_check_in_belongs_to_the_visit_whether_typed_before_or_after_it(bool typedBefore)
    {
        var (clinician, patient) = await OnPanelAsync();
        var now = await NowAsync();
        (decimal Value, DateTime At)[] signs =
        [
            (60m, now.AddMinutes(-20)), (61m, now.AddMinutes(-15).AddSeconds(-1)), (62m, now.AddMinutes(-15)), (63m, now.AddMinutes(-15).AddSeconds(1)),
            (64m, now.AddMinutes(-15).AddSeconds(2)), (65m, now.AddMinutes(-10)), (66m, now.AddMinutes(2)),
        ];
        async Task RecordAllAsync()
        {
            foreach (var (value, at) in signs)
            {
                await RecordAsync(clinician, patient, HeartRate, value, at);
            }
        }

        if (typedBefore)
        {
            await RecordAllAsync();
        }

        var visit = await CheckInAsync(clinician, patient, now);
        var start = await StartAsync(visit);
        if (!typedBefore)
        {
            await RecordAllAsync();
        }

        var visits = await VisitsAsync(patient, visit);
        Assert.Equal([.. signs.Select(sign => $"8867-4 {sign.Value} {(sign.At >= start.AddMinutes(-15) ? "visit" : "none")}")], visits);
        Assert.Subset(visits.ToHashSet(), new HashSet<string> { "8867-4 60 none", "8867-4 61 none", "8867-4 65 visit", "8867-4 66 visit" });
    }

    // The check-in moves a moment whole, its values in error too, and changes nothing but their visit: not another
    // patient's, not a result of an order, not a moment outside the window ehr.file_result's rule gives the visit,
    // before it or more than twelve hours after its start; one taken within it, even ahead of the clock (past the apps),
    // is the visit's, as the rule would give it. Each value moved is a change of its visit only, audited as the person
    // checking the patient in; the reading check passes for the moved readings in the check-in's own transaction.
    [Fact]
    public async Task A_check_in_moves_whole_moments_of_its_own_patient_and_changes_nothing_but_their_visit()
    {
        var (clinician, patient) = await OnPanelAsync();
        var (otherClinician, other) = await OnPanelAsync();
        var now = await NowAsync();
        var taken = now.AddMinutes(-5);
        await RecordPressureAsync(clinician, patient, 150m, 95m, taken);
        await RecordAsync(clinician, patient, HeartRate, 70m, taken);
        await Accounts.ExecuteAsync(App, clinician,
            "UPDATE ehr.observations SET status = 'entered-in-error', entered_in_error_reason = 'Synthetic: cuff too small.' WHERE patient_id = @patient AND code = '8462-4'",
            ("patient", patient));
        await RecordPressureAsync(clinician, patient, 140m, 90m, taken);
        await RecordAsync(clinician, patient, HeartRate, 66m, now.AddMinutes(-30));
        // Past the apps: an hour ahead, inside the rule's twelve hours, and thirteen hours ahead, outside them.
        await Accounts.ExecuteAsync(Superuser, null, Insert, Values(patient, HeartRate, 90m, now.AddHours(1)));
        await Accounts.ExecuteAsync(Superuser, null, Insert, Values(patient, HeartRate, 91m, now.AddHours(13)));
        await RecordAsync(otherClinician, other, HeartRate, 80m, taken);
        var order = Guid.CreateVersion7();
        await Accounts.ExecuteAsync(App, clinician,
            "INSERT INTO ehr.orders (id, patient_id, type, catalog_code, details, status) VALUES (@id, @patient, 'lab', 'SYN-LAB-001', '{}', 'active')", ("id", order), ("patient", patient));
        await Accounts.ExecuteAsync(App, clinician,
            """
            INSERT INTO ehr.observations (id, patient_id, order_id, code, display, status, value, effective_at)
            VALUES (gen_random_uuid(), @patient, @order, '718-7', 'x', 'final', 13.5, @at)
            """, ("patient", patient), ("order", order), ("at", taken));
        const string rows = """
            SELECT to_jsonb(o) - 'encounter_id' FROM ehr.observations o WHERE o.patient_id = ANY(@patients) ORDER BY o.id
            """;
        Guid[] both = [patient, other];
        var before = await AsSuperuserAsync(rows, ("patients", both));
        var audited = await AsSuperuserAsync("SELECT count(*)::text FROM ehr.audit_events WHERE table_name = 'observations' AND patient_id = ANY(@patients)", ("patients", both));

        // In one transaction with the check-in: the reading check, moved to the statement, passes.
        var visit = Guid.CreateVersion7();
        await Accounts.ExecuteAsync(App, clinician, Visit + "; SET CONSTRAINTS ALL IMMEDIATE;", VisitValues(visit, patient, now));

        Assert.Equal(
            ["8867-4 66 none", "718-7 13.5 none", "8462-4 90 visit", "8462-4 95 visit", "8480-6 140 visit", "8480-6 150 visit", "8867-4 70 visit", "8867-4 90 visit",
                "8867-4 91 none"],
            await VisitsAsync(patient, visit));
        Assert.Equal(["8867-4 80 none"], await VisitsAsync(other));
        Assert.Equal(before, await AsSuperuserAsync(rows, ("patients", both)));
        Assert.Equal(
            [.. Enumerable.Repeat($"{clinician} encounter_id", 6)],
            await AsSuperuserAsync(
                """
                SELECT e.actor_id || ' ' || (SELECT string_agg(k, ',') FROM jsonb_object_keys(e.details) k)
                FROM ehr.audit_events e
                WHERE e.table_name = 'observations' AND e.action = 'update' AND e.patient_id = ANY(@patients) AND e.details ? 'encounter_id'
                """, ("patients", both)));
        Assert.Equal([$"{int.Parse(audited.Single()) + 6}"], await AsSuperuserAsync(
            "SELECT count(*)::text FROM ehr.audit_events WHERE table_name = 'observations' AND patient_id = ANY(@patients)", ("patients", both)));
    }

    // The rule's latest visit: a moment tied to a visit that began earlier moves to one that begins within fifteen
    // minutes after it was taken; one taken longer before that visit keeps the earlier one. And never back: a moment tied
    // to a visit that begins later than the check-in's (loaded past the apps) stays with it.
    [Fact]
    public async Task A_check_in_takes_a_moment_from_an_earlier_visit_and_never_from_a_later_one()
    {
        var (clinician, patient) = await OnPanelAsync();
        var now = await NowAsync();
        // A visit begun two hours ago, as one checked in then would be: loaded past the apps, which start visits only now.
        var earlier = Guid.CreateVersion7();
        await Accounts.ExecuteAsync(Superuser, null, Visit, VisitValues(earlier, patient, now.AddHours(-2)));
        await RecordAsync(clinician, patient, HeartRate, 70m, now.AddMinutes(-20));
        await RecordPressureAsync(clinician, patient, 120m, 80m, now.AddMinutes(-10));
        Assert.Equal(["8867-4 70 earlier", "8462-4 80 earlier", "8480-6 120 earlier"], await VisitsAsync(patient, earlier: earlier));

        var visit = await CheckInAsync(clinician, patient, now);

        Assert.Equal(["8867-4 70 earlier", "8462-4 80 visit", "8480-6 120 visit"], await VisitsAsync(patient, visit, earlier));

        // A visit starting ten minutes from now, loaded past the apps; a heart rate taken now is its, by the rule; a
        // check-in now begins before it, so it doesn't take the heart rate back.
        var (otherClinician, other) = await OnPanelAsync();
        var later = Guid.CreateVersion7();
        await Accounts.ExecuteAsync(Superuser, null, Visit, VisitValues(later, other, now.AddMinutes(10)));
        await RecordAsync(otherClinician, other, HeartRate, 72m, await NowAsync());
        Assert.Equal(["8867-4 72 earlier"], await VisitsAsync(other, earlier: later));
        await CheckInAsync(otherClinician, other, await NowAsync());
        Assert.Equal(["8867-4 72 earlier"], await VisitsAsync(other, earlier: later));
    }

    // Two visits of one patient starting at the same instant: the one with the smaller ID, as ehr.file_result picks it,
    // whichever is checked in first and whether the set is typed before the check-ins or after them. A visit the apps
    // start takes the wall clock, so two of theirs never share an instant: the rule that sets the start is set aside
    // here, in this test's own database, so the attach's order can be tried at all.
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task Two_visits_starting_at_one_instant_give_a_set_the_same_visit_whatever_the_order(bool smallerFirst, bool typedBefore)
    {
        var (clinician, patient) = await OnPanelAsync();
        var start = await NowAsync();
        var ids = new[] { Guid.CreateVersion7(), Guid.CreateVersion7() }.OrderBy(id => id.ToString(), StringComparer.Ordinal).ToArray();
        await Accounts.ExecuteAsync(Superuser, null, "ALTER TABLE ehr.encounters DISABLE TRIGGER encounters_check_in");
        try
        {
            if (typedBefore)
            {
                await RecordPressureAsync(clinician, patient, 120m, 80m, start.AddMinutes(-5));
            }

            foreach (var id in smallerFirst ? ids : ids.Reverse())
            {
                await Accounts.ExecuteAsync(App, clinician, Visit, VisitValues(id, patient, start));
            }

            if (!typedBefore)
            {
                await RecordPressureAsync(clinician, patient, 120m, 80m, start.AddMinutes(-5));
            }
        }
        finally
        {
            await Accounts.ExecuteAsync(Superuser, null, "ALTER TABLE ehr.encounters ENABLE TRIGGER encounters_check_in");
        }

        Assert.Equal(["8462-4 80 visit", "8480-6 120 visit"], await VisitsAsync(patient, ids[0]));
    }

    // Only a visit the apps start, by someone allowed to: a clinician without the patient on their panel, an admin and
    // the read-only role are refused; so is any visit the apps insert that isn't in progress; a visit inserted past the
    // apps changes nothing; and the app's role can neither change a visit itself nor call the functions.
    [Fact]
    public async Task Only_a_check_in_by_a_clinician_with_the_patient_on_their_panel_ties_vital_signs_to_a_visit()
    {
        var (clinician, patient) = await OnPanelAsync();
        var now = await NowAsync();
        await RecordAsync(clinician, patient, HeartRate, 70m, now.AddMinutes(-5));
        var outsider = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        var admin = await Accounts.StaffAsync(database, StaffRoles.Admin);

        await RefusedAsync(PostgresErrorCodes.InsufficientPrivilege, () => CheckInAsync(outsider, patient, now));
        await RefusedAsync(PostgresErrorCodes.InsufficientPrivilege, () => CheckInAsync(admin, patient, now));
        await RefusedAsync(PostgresErrorCodes.InsufficientPrivilege, () => Accounts.ExecuteAsync(Read, clinician, Visit, VisitValues(Guid.CreateVersion7(), patient, now)));
        foreach (var status in new[] { "planned", "arrived", "finished", "cancelled", "entered-in-error" })
        {
            await RefusedAsync(PostgresErrorCodes.CheckViolation, () => CheckInAsync(clinician, patient, now, status));
        }

        await Accounts.ExecuteAsync(Superuser, null, Visit, VisitValues(Guid.CreateVersion7(), patient, now));
        Assert.Equal(["8867-4 70 none"], await VisitsAsync(patient));

        var visit = Guid.Parse((await AsSuperuserAsync("SELECT id::text FROM ehr.encounters WHERE patient_id = @patient AND status = 'in-progress'", ("patient", patient))).Single());
        await RefusedAsync(PostgresErrorCodes.InsufficientPrivilege, () => Accounts.ExecuteAsync(App, clinician,
            "UPDATE ehr.observations SET encounter_id = @visit WHERE patient_id = @patient", ("visit", visit), ("patient", patient)));
        Assert.Equal(["false false false false"], await AsSuperuserAsync(
            """
            SELECT has_function_privilege('ehr_app', 'ehr.attach_vital_signs()', 'EXECUTE')::text || ' ' || has_function_privilege('ehr_read', 'ehr.attach_vital_signs()', 'EXECUTE')::text
                || ' ' || has_function_privilege('ehr_app', 'ehr.check_in_visit()', 'EXECUTE')::text || ' ' || has_function_privilege('ehr_read', 'ehr.check_in_visit()', 'EXECUTE')::text
            """));
        Assert.Equal(["8867-4 70 none"], await VisitsAsync(patient));
    }

    // The step's reviews: a clinician with the patient on their panel inserting, as the app, a visit dated two years ago
    // to take the patient's two-year-old blood pressure and heart rate. Finished, it is refused; in progress, or dated
    // ahead, it starts now, whatever was sent, and takes only what a check-in now would, so the old values stay put. And
    // once a check-in has tied a moment, a later value of the moment naming no visit or another takes the moment's.
    [Fact]
    public async Task A_visit_dated_to_suit_starts_now_and_takes_no_old_vital_sign()
    {
        var (clinician, patient) = await OnPanelAsync();
        var now = await NowAsync();
        var old = now.AddYears(-2);
        await RecordPressureAsync(clinician, patient, 120m, 80m, old);
        await RecordAsync(clinician, patient, HeartRate, 72m, old);
        await RefusedAsync(PostgresErrorCodes.CheckViolation, () => CheckInAsync(clinician, patient, old.AddMinutes(5), "finished"));

        var backdated = await CheckInAsync(clinician, patient, old.AddMinutes(5));
        var ahead = await CheckInAsync(clinician, patient, now.AddHours(2));
        foreach (var visit in new[] { backdated, ahead })
        {
            var start = await StartAsync(visit);
            Assert.InRange(start, now.AddSeconds(-5), (await NowAsync()).AddSeconds(5));
        }

        Assert.Equal(["8462-4 80 none", "8480-6 120 none", "8867-4 72 none"], await VisitsAsync(patient));
        Assert.Empty(await AsSuperuserAsync(
            "SELECT e.row_id::text FROM ehr.audit_events e WHERE e.table_name = 'observations' AND e.patient_id = @patient AND e.details ? 'encounter_id'",
            ("patient", patient)));

        var taken = now.AddMinutes(-5);
        await RecordAsync(clinician, patient, HeartRate, 70m, taken);
        await RecordAsync(clinician, patient, "8310-5", 37m, taken);
        await RecordAsync(clinician, patient, "9279-1", 16m, taken, backdated);
        Assert.Equal(["8462-4 80 none", "8480-6 120 none", "8867-4 72 none", "8310-5 37 visit", "8867-4 70 visit", "9279-1 16 visit"], await VisitsAsync(patient, ahead));
    }

    // A transaction of the app's, held open, as `user`.
    async Task<(NpgsqlConnection, NpgsqlTransaction)> BeginAsync(Guid user)
    {
        var connection = new NpgsqlConnection(App);
        await connection.OpenAsync(Cancel);
        var transaction = await connection.BeginTransactionAsync(Cancel);
        await using var set = new NpgsqlCommand("SELECT set_config('app.user_id', @user, true)", connection, transaction);
        set.Parameters.AddWithValue("user", user.ToString());
        await set.ExecuteNonQueryAsync(Cancel);
        return (connection, transaction);
    }

    static async Task<int> ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return await command.ExecuteNonQueryAsync(Cancel);
    }

    static async Task WaitsAsync(Task waiting) =>
        Assert.NotSame(waiting, await Task.WhenAny(waiting, Task.Delay(TimeSpan.FromMilliseconds(500), Cancel)));

    // The second review: a transaction held open before the check-in. The visit starts at the wall clock when it is
    // inserted, not when the transaction began, and takes only what a check-in at that moment would: a heart rate taken
    // a second less than fifteen minutes before the transaction began stays without it.
    [Fact]
    public async Task A_check_in_in_a_transaction_held_open_starts_when_it_is_inserted()
    {
        var (clinician, patient) = await OnPanelAsync();
        var now = await NowAsync();
        await RecordAsync(clinician, patient, HeartRate, 70m, now.AddMinutes(-15).AddSeconds(1));
        var visit = Guid.CreateVersion7();
        var (connection, transaction) = await BeginAsync(clinician);
        await using (connection)
        {
            await ExecuteAsync(connection, transaction, "SELECT pg_sleep(5)");
            await ExecuteAsync(connection, transaction, Visit, VisitValues(visit, patient, now.AddMinutes(-1)));
            await using var held = new NpgsqlCommand("SELECT period_start - now() >= interval '5 seconds' FROM ehr.encounters WHERE id = @id", connection, transaction);
            held.Parameters.AddWithValue("id", visit);
            Assert.Equal(true, await held.ExecuteScalarAsync(Cancel));
            await transaction.CommitAsync(Cancel);
        }

        Assert.Equal(["8867-4 70 none"], await VisitsAsync(patient, visit));
    }

    // A value being recorded while the patient is checked in, in either order: the second transaction waits for the
    // first, and the value ends with the visit either way.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_value_recorded_while_the_patient_is_checked_in_ends_with_the_visit_whichever_commits_first(bool valueFirst)
    {
        var (clinician, patient) = await OnPanelAsync();
        var visit = Guid.CreateVersion7();
        var start = await NowAsync();
        var (recording, recordingTransaction) = await BeginAsync(clinician);
        var (checkingIn, checkingInTransaction) = await BeginAsync(clinician);
        await using (recording)
        await using (checkingIn)
        {
            var (first, firstTransaction, firstSql, firstValues) = valueFirst
                ? (recording, recordingTransaction, Insert, Values(patient, HeartRate, 70m, start.AddMinutes(-5)))
                : (checkingIn, checkingInTransaction, Visit, VisitValues(visit, patient, start));
            var (second, secondTransaction, secondSql, secondValues) = valueFirst
                ? (checkingIn, checkingInTransaction, Visit, VisitValues(visit, patient, start))
                : (recording, recordingTransaction, Insert, Values(patient, HeartRate, 70m, start.AddMinutes(-5)));
            Assert.Equal(1, await ExecuteAsync(first, firstTransaction, firstSql, firstValues));
            var waiting = ExecuteAsync(second, secondTransaction, secondSql, secondValues);
            await WaitsAsync(waiting);
            await firstTransaction.CommitAsync(Cancel);
            Assert.Equal(1, await waiting);
            await secondTransaction.CommitAsync(Cancel);
        }

        Assert.Equal(["8867-4 70 visit"], await VisitsAsync(patient, visit));
    }
}
