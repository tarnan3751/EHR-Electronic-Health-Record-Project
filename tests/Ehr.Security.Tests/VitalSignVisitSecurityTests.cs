using Ehr.Data.Accounts;
using Ehr.Testing;
using Npgsql;
using Xunit;

namespace Ehr.Security.Tests;

// Step 9c's follow-up, after Tahseen's answers: a vital sign belongs to the patient's visit that was held when it was
// taken or began within fifteen minutes after, decided from the time taken alone, whether it was typed before the
// check-in or after it (db/policies/75-results.sql: ehr.file_result, and ehr.attach_vital_signs, which ties the
// moments already recorded to the visit a check-in starts). A visit the apps insert is a check-in starting now
// (ehr.check_in_visit), so no visit dated to suit can take a vital sign. What the check-in may change, who can make it
// change anything, two visits starting at one instant, and two transactions at once.
public class VitalSignVisitSecurityTests(MigratedDatabase database) : IClassFixture<MigratedDatabase>
{
    const string Systolic = "8480-6";
    const string Diastolic = "8462-4";
    const string HeartRate = "8867-4";

    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    string App => database.ConnectionStringFor("ehr_app", MigratedDatabase.AppPassword);

    string Read => database.ConnectionStringFor("ehr_read", MigratedDatabase.ReadPassword);

    string Superuser => database.SuperuserConnectionString;

    // Now, to the second: a check-in's start, which must be within a minute of the database's clock.
    static DateTime Now() => new(DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, DateTimeKind.Utc);

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

    // A check-in as the app makes one: the visit, by a clinician with the patient on their panel.
    async Task<Guid> CheckInAsync(Guid clinician, Guid patient, DateTime start, string status = "in-progress")
    {
        var visit = Guid.CreateVersion7();
        await Accounts.ExecuteAsync(App, clinician, Visit, VisitValues(visit, patient, start, status));
        return visit;
    }

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
    // before, none, whether typed before the check-in or after it; and the edge, fifteen minutes before and a second more.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_sign_taken_up_to_fifteen_minutes_before_the_check_in_belongs_to_the_visit_whether_typed_before_or_after_it(bool typedBefore)
    {
        var (clinician, patient) = await OnPanelAsync();
        var start = Now();
        (string Code, decimal Value, DateTime At)[] signs =
        [
            (HeartRate, 60m, start.AddMinutes(-20)), (HeartRate, 61m, start.AddMinutes(-15).AddSeconds(-1)), (HeartRate, 62m, start.AddMinutes(-15)),
            (HeartRate, 63m, start.AddMinutes(-10)), (HeartRate, 64m, start.AddMinutes(2)),
        ];
        async Task RecordAllAsync()
        {
            foreach (var (code, value, at) in signs)
            {
                await RecordAsync(clinician, patient, code, value, at);
            }
        }

        if (typedBefore)
        {
            await RecordAllAsync();
        }

        var visit = await CheckInAsync(clinician, patient, start);
        if (!typedBefore)
        {
            await RecordAllAsync();
        }

        Assert.Equal(["8867-4 60 none", "8867-4 61 none", "8867-4 62 visit", "8867-4 63 visit", "8867-4 64 visit"], await VisitsAsync(patient, visit));
    }

    // The check-in moves a moment whole, its values in error too, and changes nothing but their visit: not another
    // patient's, not a result of an order, not a moment outside the window, before it or later than the clock allows a
    // time to be typed. Each value moved is a change of its visit only, audited as the person checking the patient in;
    // the reading check passes for the moved readings in the check-in's own transaction.
    [Fact]
    public async Task A_check_in_moves_whole_moments_of_its_own_patient_and_changes_nothing_but_their_visit()
    {
        var (clinician, patient) = await OnPanelAsync();
        var (otherClinician, other) = await OnPanelAsync();
        var start = Now();
        var taken = start.AddMinutes(-5);
        await RecordPressureAsync(clinician, patient, 150m, 95m, taken);
        await RecordAsync(clinician, patient, HeartRate, 70m, taken);
        await Accounts.ExecuteAsync(App, clinician,
            "UPDATE ehr.observations SET status = 'entered-in-error', entered_in_error_reason = 'Synthetic: cuff too small.' WHERE patient_id = @patient AND code = '8462-4'",
            ("patient", patient));
        await RecordPressureAsync(clinician, patient, 140m, 90m, taken);
        await RecordAsync(clinician, patient, HeartRate, 66m, start.AddMinutes(-30));
        // Past the apps, an hour ahead: later than any time a check-in now could take.
        await Accounts.ExecuteAsync(Superuser, null, Insert, Values(patient, HeartRate, 90m, start.AddHours(1)));
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
        await Accounts.ExecuteAsync(App, clinician, Visit + "; SET CONSTRAINTS ALL IMMEDIATE;", VisitValues(visit, patient, start));

        Assert.Equal(
            ["8867-4 66 none", "718-7 13.5 none", "8462-4 90 visit", "8462-4 95 visit", "8480-6 140 visit", "8480-6 150 visit", "8867-4 70 visit", "8867-4 90 none"],
            await VisitsAsync(patient, visit));
        Assert.Equal(["8867-4 80 none"], await VisitsAsync(other));
        Assert.Equal(before, await AsSuperuserAsync(rows, ("patients", both)));
        Assert.Equal(
            [.. Enumerable.Repeat($"{clinician} encounter_id", 5)],
            await AsSuperuserAsync(
                """
                SELECT e.actor_id || ' ' || (SELECT string_agg(k, ',') FROM jsonb_object_keys(e.details) k)
                FROM ehr.audit_events e
                WHERE e.table_name = 'observations' AND e.action = 'update' AND e.patient_id = ANY(@patients) AND e.details ? 'encounter_id'
                """, ("patients", both)));
        Assert.Equal([$"{int.Parse(audited.Single()) + 5}"], await AsSuperuserAsync(
            "SELECT count(*)::text FROM ehr.audit_events WHERE table_name = 'observations' AND patient_id = ANY(@patients)", ("patients", both)));
    }

    // The rule's latest visit: a moment tied to a visit that began earlier the same day moves to one that begins within
    // fifteen minutes after it was taken; one taken longer before that visit keeps the earlier one.
    [Fact]
    public async Task A_moment_on_an_earlier_visit_moves_to_a_visit_that_begins_within_fifteen_minutes_after_it()
    {
        var (clinician, patient) = await OnPanelAsync();
        var start = Now();
        // A visit begun two hours ago, as one checked in then would be: loaded past the apps, which start visits only now.
        var earlier = Guid.CreateVersion7();
        await Accounts.ExecuteAsync(Superuser, null, Visit, VisitValues(earlier, patient, start.AddHours(-2)));
        await RecordAsync(clinician, patient, HeartRate, 70m, start.AddMinutes(-20));
        await RecordPressureAsync(clinician, patient, 120m, 80m, start.AddMinutes(-10));
        Assert.Equal(["8867-4 70 earlier", "8462-4 80 earlier", "8480-6 120 earlier"], await VisitsAsync(patient, earlier: earlier));

        var visit = await CheckInAsync(clinician, patient, start);

        Assert.Equal(["8867-4 70 earlier", "8462-4 80 visit", "8480-6 120 visit"], await VisitsAsync(patient, visit, earlier));
    }

    // Two visits of one patient starting at the same instant: the one with the smaller ID, as ehr.file_result picks it,
    // whichever is checked in first and whether the set is typed before the check-ins or after them.
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task Two_visits_starting_at_one_instant_give_a_set_the_same_visit_whatever_the_order(bool smallerFirst, bool typedBefore)
    {
        var (clinician, patient) = await OnPanelAsync();
        var start = Now();
        var ids = new[] { Guid.CreateVersion7(), Guid.CreateVersion7() }.OrderBy(id => id.ToString(), StringComparer.Ordinal).ToArray();
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

        Assert.Equal(["8462-4 80 visit", "8480-6 120 visit"], await VisitsAsync(patient, ids[0]));
    }

    // Only a visit the apps start, by someone allowed to, as a check-in starting now: a clinician without the patient on
    // their panel, an admin and the read-only role are refused; so is any visit the apps insert that isn't in progress
    // or doesn't start within a minute of now; a visit inserted past the apps changes nothing; and the app's role can
    // neither change a visit itself nor call the functions.
    [Fact]
    public async Task Only_a_check_in_by_a_clinician_with_the_patient_on_their_panel_ties_vital_signs_to_a_visit()
    {
        var (clinician, patient) = await OnPanelAsync();
        var start = Now();
        await RecordAsync(clinician, patient, HeartRate, 70m, start.AddMinutes(-5));
        var outsider = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        var admin = await Accounts.StaffAsync(database, StaffRoles.Admin);

        await RefusedAsync(PostgresErrorCodes.InsufficientPrivilege, () => CheckInAsync(outsider, patient, start));
        await RefusedAsync(PostgresErrorCodes.InsufficientPrivilege, () => CheckInAsync(admin, patient, start));
        await RefusedAsync(PostgresErrorCodes.InsufficientPrivilege, () => Accounts.ExecuteAsync(Read, clinician, Visit, VisitValues(Guid.CreateVersion7(), patient, start)));
        foreach (var status in new[] { "planned", "arrived", "finished", "cancelled", "entered-in-error" })
        {
            await RefusedAsync(PostgresErrorCodes.CheckViolation, () => CheckInAsync(clinician, patient, start, status));
        }

        foreach (var off in new[] { start.AddMinutes(-10), start.AddMinutes(-2), start.AddMinutes(3), start.AddHours(2) })
        {
            await RefusedAsync(PostgresErrorCodes.CheckViolation, () => CheckInAsync(clinician, patient, off));
        }

        await Accounts.ExecuteAsync(Superuser, null, Visit, VisitValues(Guid.CreateVersion7(), patient, start));
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

    // The step's review: a clinician with the patient on their panel inserting, as the app, a visit dated two years ago,
    // finished or in progress, to take the patient's two-year-old blood pressure and heart rate. Refused, and nothing
    // moves. And once a check-in has tied a moment, nothing the apps do moves it but another check-in within fifteen
    // minutes after it was taken: not a value of the moment naming no visit or another, not a visit dated to suit.
    [Fact]
    public async Task A_visit_dated_to_suit_takes_no_vital_sign_and_a_moment_tied_at_check_in_stays()
    {
        var (clinician, patient) = await OnPanelAsync();
        var start = Now();
        var old = start.AddYears(-2);
        await RecordPressureAsync(clinician, patient, 120m, 80m, old);
        await RecordAsync(clinician, patient, HeartRate, 72m, old);
        foreach (var status in new[] { "finished", "in-progress" })
        {
            await RefusedAsync(PostgresErrorCodes.CheckViolation, () => CheckInAsync(clinician, patient, old.AddMinutes(5), status));
        }

        Assert.Equal(["8462-4 80 none", "8480-6 120 none", "8867-4 72 none"], await VisitsAsync(patient));

        var taken = start.AddMinutes(-5);
        await RecordAsync(clinician, patient, HeartRate, 70m, taken);
        var visit = await CheckInAsync(clinician, patient, start);
        var other = Guid.CreateVersion7();
        await Accounts.ExecuteAsync(Superuser, null, Visit, VisitValues(other, patient, start.AddMinutes(-1)));
        await RecordAsync(clinician, patient, "8310-5", 37m, taken);
        await RecordAsync(clinician, patient, "9279-1", 16m, taken, other);
        await RefusedAsync(PostgresErrorCodes.CheckViolation, () => CheckInAsync(clinician, patient, taken.AddMinutes(3)));

        Assert.Equal(["8462-4 80 none", "8480-6 120 none", "8867-4 72 none", "8310-5 37 visit", "8867-4 70 visit", "9279-1 16 visit"], await VisitsAsync(patient, visit));
        Assert.Empty(await AsSuperuserAsync(
            "SELECT e.row_id::text FROM ehr.audit_events e WHERE e.table_name = 'observations' AND e.patient_id = @patient AND e.details ? 'encounter_id' AND e.row_id IN (SELECT o.id FROM ehr.observations o WHERE o.patient_id = @patient AND o.effective_at = @old)",
            ("patient", patient), ("old", old)));
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

    // A value being recorded while the patient is checked in, in either order: the second transaction waits for the
    // first, and the value ends with the visit either way.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_value_recorded_while_the_patient_is_checked_in_ends_with_the_visit_whichever_commits_first(bool valueFirst)
    {
        var (clinician, patient) = await OnPanelAsync();
        var visit = Guid.CreateVersion7();
        var start = Now();
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
