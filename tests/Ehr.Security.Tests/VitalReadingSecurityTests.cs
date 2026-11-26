using Ehr.Data.Accounts;
using Ehr.Testing;
using Npgsql;
using Xunit;

namespace Ehr.Security.Tests;

// The expansion plan's step 9c: what the database holds vital signs to, whatever the app does, in SQL of its own
// (db/policies/75-results.sql and 30-rls.sql, the indexes and check of the VitalReadings migration). A sign has one
// standing value for a patient at a moment, whatever the visit; a moment's visit is decided by its first value, from
// the time it was taken (VitalSignVisitSecurityTests has the check-in's side of it); a blood pressure is one reading of a systolic and a diastolic, which the database gives
// its ID, which stands whole or not at all when a transaction commits, and which is marked entered in error whole, by
// one person. Each rule for the app's role and, where it is the rule for everyone, for a superuser; and with two
// connections where timing matters.
public class VitalReadingSecurityTests(MigratedDatabase database) : IClassFixture<MigratedDatabase>, IAsyncLifetime
{
    const string Systolic = "8480-6";
    const string Diastolic = "8462-4";
    const string HeartRate = "8867-4";
    const string Temperature = "8310-5";

    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    string App => database.ConnectionStringFor("ehr_app", MigratedDatabase.AppPassword);

    string Read => database.ConnectionStringFor("ehr_read", MigratedDatabase.ReadPassword);

    string Superuser => database.SuperuserConnectionString;

    // Ten minutes before the test started, by the database's clock, which its rules hold a time to, to the second.
    DateTime Taken { get; set; }

    public async ValueTask InitializeAsync()
    {
        var now = await database.NowAsync();
        Taken = new(now.AddMinutes(-10).Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, DateTimeKind.Utc);
    }

    public ValueTask DisposeAsync() => default;

    // One value, everything a client could choose for itself sent too; as the app, the database decides the rest.
    const string Insert = """
        INSERT INTO ehr.observations (id, patient_id, encounter_id, reading_id, category, code, display, status, value, effective_at)
        VALUES (gen_random_uuid(), @patient, @visit, @reading, 'vital-signs', @code, 'x', @status, @value, @at);
        """;

    (string, object)[] Values(Guid patient, string code, decimal value, DateTime? at = null, Guid? visit = null, Guid? reading = null, string status = "final") =>
        [("patient", patient), ("code", code), ("value", value), ("at", at ?? Taken), ("visit", (object?)visit ?? DBNull.Value), ("reading", (object?)reading ?? DBNull.Value),
            ("status", status)];

    async Task<(Guid Clinician, Guid Patient)> OnPanelAsync()
    {
        var clinician = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        var patient = await Accounts.PatientAsync(database, clinician);
        await Accounts.AssignAsync(database, clinician, patient);
        return (clinician, patient);
    }

    Task RecordAsync(Guid clinician, Guid patient, string code, decimal value, DateTime? at = null, Guid? visit = null, Guid? reading = null) =>
        Accounts.ExecuteAsync(App, clinician, Insert, Values(patient, code, value, at, visit, reading));

    // A blood pressure as the app records one: both values in one transaction, the systolic first.
    Task RecordPressureAsync(Guid clinician, Guid patient, decimal systolic, decimal diastolic, DateTime? at = null, Guid? reading = null) =>
        Accounts.ExecuteAsync(App, clinician,
            """
            INSERT INTO ehr.observations (id, patient_id, reading_id, category, code, display, status, value, effective_at)
            VALUES (gen_random_uuid(), @patient, @reading, 'vital-signs', '8480-6', 'x', 'final', @systolic, @at);
            INSERT INTO ehr.observations (id, patient_id, reading_id, category, code, display, status, value, effective_at)
            VALUES (gen_random_uuid(), @patient, @reading, 'vital-signs', '8462-4', 'x', 'final', @diastolic, @at);
            """,
            ("patient", patient), ("systolic", systolic), ("diastolic", diastolic), ("at", at ?? Taken), ("reading", (object?)reading ?? DBNull.Value));

    // As the vital signs page marks one: the patient's standing values of a sign.
    Task MarkAsync(Guid? user, Guid patient, string code, string reason = "Synthetic: cuff too small.", string? connection = null) =>
        Accounts.ExecuteAsync(connection ?? App, user,
            "UPDATE ehr.observations SET status = 'entered-in-error', entered_in_error_reason = @reason WHERE patient_id = @patient AND code = @code AND status = 'final'",
            ("patient", patient), ("code", code), ("reason", reason));

    // A visit begun now, as checking the patient in starts one: as the app, by the clinician (step 9c's follow-up ties
    // the moments already recorded to it, ehr.attach_vital_signs).
    async Task<Guid> CheckInAsync(Guid clinician, Guid patient)
    {
        var visit = Guid.CreateVersion7();
        await Accounts.ExecuteAsync(App, clinician,
            "INSERT INTO ehr.encounters (id, patient_id, status, class_code, period_start) VALUES (@id, @patient, 'in-progress', 'AMB', now())",
            ("id", visit), ("patient", patient));
        return visit;
    }

    Task<List<string>> AsSuperuserAsync(string sql, params (string Name, object Value)[] parameters) =>
        Accounts.QueryAsync(Superuser, null, sql, parameters);

    static async Task RefusedAsync(string state, Func<Task> attempt) =>
        Assert.Equal(state, (await Assert.ThrowsAsync<PostgresException>(attempt)).SqlState);

    // "code value status visit reading" for each of the patient's vital signs in the order recorded: the visit as
    // "visit" when it is `visit`, each reading as R1, R2... in the order readings first appear.
    async Task<List<string>> StateAsync(Guid patient, Guid? visit = null)
    {
        var rows = await AsSuperuserAsync(
            """
            SELECT code || ' ' || trim_scale(value) || ' ' || status || ' ' || coalesce(encounter_id::text, 'none') || ' ' || coalesce(reading_id::text, 'none')
            FROM ehr.observations WHERE patient_id = @patient ORDER BY issued_at, code, value
            """, ("patient", patient));
        var readings = new List<string>();
        return [.. rows.Select(row =>
        {
            var parts = row.Split(' ');
            parts[3] = parts[3] == visit?.ToString() ? "visit" : parts[3];
            if (parts[4] != "none")
            {
                if (!readings.Contains(parts[4]))
                {
                    readings.Add(parts[4]);
                }

                parts[4] = $"R{readings.IndexOf(parts[4]) + 1}";
            }

            return string.Join(' ', parts);
        })];
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

    // Whether `waiting` is still waiting after half a second.
    static async Task WaitsAsync(Task waiting) =>
        Assert.NotSame(waiting, await Task.WhenAny(waiting, Task.Delay(TimeSpan.FromMilliseconds(500), Cancel)));

    [Fact]
    public async Task A_sign_has_one_standing_value_at_a_moment_whatever_the_visit_for_every_role()
    {
        var (clinician, patient) = await OnPanelAsync();
        await RecordAsync(clinician, patient, HeartRate, 72m, at: Taken.AddMinutes(-10));
        // The patient checked in since: a visit begun twenty minutes after the sign was taken, so not its visit.
        var visit = await CheckInAsync(clinician, patient);

        await RefusedAsync(PostgresErrorCodes.UniqueViolation, () => RecordAsync(clinician, patient, HeartRate, 80m, at: Taken.AddMinutes(-10)));
        await RefusedAsync(PostgresErrorCodes.UniqueViolation, () => RecordAsync(clinician, patient, HeartRate, 80m, at: Taken.AddMinutes(-10), visit: visit));
        // Past every trigger the apps go through, as a superuser naming the visit: the index is for everyone.
        await RefusedAsync(PostgresErrorCodes.UniqueViolation, () => Accounts.ExecuteAsync(Superuser, null, Insert, Values(patient, HeartRate, 80m, Taken.AddMinutes(-10), visit: visit)));
        Assert.Equal(["8867-4 72 final none none"], await StateAsync(patient));

        // Once the value standing is marked entered in error, the right one is recorded for the same moment.
        await MarkAsync(clinician, patient, HeartRate);
        await RecordAsync(clinician, patient, HeartRate, 80m, at: Taken.AddMinutes(-10));
        Assert.Equal(["8867-4 72 entered-in-error none none", "8867-4 80 final none none"], await StateAsync(patient));
    }

    // Failure A of step 9c, in the database: 120/80 recorded with no visit, the patient checked in ten minutes later,
    // the diastolic marked in error and 120/78 typed for the same time. Before step 9c, the systolic stood with no
    // visit and the new diastolic had the visit; now the check-in ties the moment to its visit whole, since it was taken
    // within fifteen minutes before it (step 9c's follow-up), and every later value of the moment takes the same.
    [Fact]
    public async Task A_moments_visit_is_decided_from_the_time_taken_so_a_check_in_between_cannot_split_a_reading()
    {
        var (clinician, patient) = await OnPanelAsync();
        await RecordPressureAsync(clinician, patient, 120m, 80m);
        var visit = await CheckInAsync(clinician, patient);

        await RecordAsync(clinician, patient, HeartRate, 70m);
        await RecordAsync(clinician, patient, Temperature, 37m, visit: visit);
        await MarkAsync(clinician, patient, Diastolic);
        await RecordPressureAsync(clinician, patient, 120m, 78m);

        Assert.Equal(
            ["8480-6 120 entered-in-error visit R1", "8462-4 80 entered-in-error visit R1", "8867-4 70 final visit none", "8310-5 37 final visit none",
                "8480-6 120 final visit R2", "8462-4 78 final visit R2"],
            await StateAsync(patient, visit));

        // From the time taken alone: a moment taken twenty minutes before the check-in and first recorded after it has no
        // visit, and naming the visit for it is refused; a later value of that moment naming the visit takes the
        // moment's. A moment taken after the check-in has the visit, and the rest of that moment follows its first value.
        var before = Taken.AddMinutes(-10);
        await RecordAsync(clinician, patient, HeartRate, 74m, at: before);
        await RefusedAsync(PostgresErrorCodes.CheckViolation, () => RecordAsync(clinician, patient, HeartRate, 75m, at: before.AddMinutes(-1), visit: visit));
        await RecordAsync(clinician, patient, Temperature, 36.8m, at: before, visit: visit);
        var after = new DateTime(((await database.NowAsync()).Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond) + (2 * TimeSpan.TicksPerSecond), DateTimeKind.Utc);
        await RecordAsync(clinician, patient, HeartRate, 76m, at: after);
        await RecordPressureAsync(clinician, patient, 118m, 76m, at: after);
        const string visits = "SELECT coalesce(encounter_id::text, 'none') FROM ehr.observations WHERE patient_id = @patient AND effective_at = @at";
        Assert.Equal(["none", "none"], await AsSuperuserAsync(visits, ("patient", patient), ("at", before)));
        Assert.Equal([$"{visit}", $"{visit}", $"{visit}"], await AsSuperuserAsync(visits, ("patient", patient), ("at", after)));
    }

    [Fact]
    public async Task The_database_gives_a_reading_its_ID_whatever_the_app_sends()
    {
        var (clinician, patient) = await OnPanelAsync();
        var (other, theirs) = await OnPanelAsync();
        await RecordPressureAsync(other, theirs, 130m, 85m);
        var their = Guid.Parse((await AsSuperuserAsync("SELECT DISTINCT reading_id::text FROM ehr.observations WHERE patient_id = @patient", ("patient", theirs))).Single());

        // Another patient's reading named for both values, and a reading named for a heart rate: the database's own.
        await RecordPressureAsync(clinician, patient, 120m, 80m, reading: their);
        await RecordAsync(clinician, patient, HeartRate, 70m, reading: their);
        Assert.Equal(["8480-6 120 final none R1", "8462-4 80 final none R1", "8867-4 70 final none none"], await StateAsync(patient));
        Assert.Equal(["2"], await AsSuperuserAsync("SELECT count(*)::text FROM ehr.observations WHERE reading_id = @reading", ("reading", their)));

        // As a superuser: a reading on anything but a blood pressure value, or a blood pressure value without one.
        await RefusedAsync(PostgresErrorCodes.CheckViolation, () => Accounts.ExecuteAsync(Superuser, null, Insert, Values(patient, HeartRate, 71m, Taken.AddMinutes(-1), reading: Guid.CreateVersion7())));
        await RefusedAsync(PostgresErrorCodes.CheckViolation, () => Accounts.ExecuteAsync(Superuser, null, Insert + Insert.Replace("@code", "'8462-4'", StringComparison.Ordinal),
            Values(patient, Systolic, 121m, Taken.AddMinutes(-1))));
    }

    [Fact]
    public async Task A_reading_stands_whole_or_not_at_all_when_the_transaction_commits_for_the_app_and_a_superuser()
    {
        var (clinician, patient) = await OnPanelAsync();
        var (_, someoneElse) = await OnPanelAsync();
        var visit = await CheckInAsync(clinician, patient);

        // A lone value, as the app: refused when the transaction commits.
        await RefusedAsync(PostgresErrorCodes.CheckViolation, () => RecordAsync(clinician, patient, Systolic, 120m));
        // The check can be moved to the statement, not skipped; and the app's role can't turn it off.
        await RefusedAsync(PostgresErrorCodes.CheckViolation, () => Accounts.ExecuteAsync(App, clinician, "SET CONSTRAINTS ALL IMMEDIATE; " + Insert, Values(patient, Systolic, 120m)));
        await RefusedAsync(PostgresErrorCodes.CheckViolation, () => Accounts.ExecuteAsync(App, clinician,
            "SET CONSTRAINTS ehr.observations_reading_inserted DEFERRED; " + Insert, Values(patient, Diastolic, 80m)));
        await RefusedAsync(PostgresErrorCodes.InsufficientPrivilege, () => Accounts.ExecuteAsync(App, clinician, "SET session_replication_role = replica"));
        await RefusedAsync(PostgresErrorCodes.InsufficientPrivilege, () => Accounts.ExecuteAsync(App, clinician,
            "ALTER TABLE ehr.observations DISABLE TRIGGER observations_reading_inserted"));

        // As a superuser, past the apps' trigger: a lone value; two systolics in one reading (an index, at once); the
        // two values of two patients, of two times, or of two visits in one reading.
        var reading = Guid.CreateVersion7();
        const string other = "INSERT INTO ehr.observations (id, patient_id, encounter_id, reading_id, category, code, display, status, value, effective_at) VALUES (gen_random_uuid(), @other_patient, @other_visit, @reading, 'vital-signs', @other_code, 'x', 'final', 80, @other_at);";
        (string, object)[] Pair(string otherCode = Diastolic, Guid? otherPatient = null, DateTime? otherAt = null, Guid? otherVisit = null) =>
            [.. Values(patient, Systolic, 120m, reading: reading), ("other_patient", otherPatient ?? patient), ("other_code", otherCode), ("other_at", otherAt ?? Taken),
                ("other_visit", (object?)otherVisit ?? DBNull.Value)];
        await RefusedAsync(PostgresErrorCodes.CheckViolation, () => Accounts.ExecuteAsync(Superuser, null, Insert, Values(patient, Systolic, 120m, reading: reading)));
        await RefusedAsync(PostgresErrorCodes.UniqueViolation, () => Accounts.ExecuteAsync(Superuser, null, Insert + other, Pair(otherCode: Systolic, otherAt: Taken.AddMinutes(-1))));
        await RefusedAsync(PostgresErrorCodes.CheckViolation, () => Accounts.ExecuteAsync(Superuser, null, Insert + other, Pair(otherPatient: someoneElse)));
        await RefusedAsync(PostgresErrorCodes.CheckViolation, () => Accounts.ExecuteAsync(Superuser, null, Insert + other, Pair(otherAt: Taken.AddMinutes(-1))));
        await RefusedAsync(PostgresErrorCodes.CheckViolation, () => Accounts.ExecuteAsync(Superuser, null, Insert + other, Pair(otherVisit: visit)));
        Assert.Empty(await StateAsync(patient));

        // A whole reading goes in; taking one of its values away again, which nothing but a superuser may do, is refused.
        await Accounts.ExecuteAsync(Superuser, null, Insert + other, Pair());
        await RefusedAsync(PostgresErrorCodes.CheckViolation, () => Accounts.ExecuteAsync(Superuser, null,
            "DELETE FROM ehr.observations WHERE reading_id = @reading AND code = '8480-6'", ("reading", reading)));
        Assert.Equal(["8462-4 80 final none R1", "8480-6 120 final none R1"], await StateAsync(patient));
        Assert.Empty(await StateAsync(someoneElse));
    }

    [Fact]
    public async Task Marking_either_value_marks_the_whole_reading_by_the_same_person_and_each_value_is_audited()
    {
        var (clinician, patient) = await OnPanelAsync();
        await RecordPressureAsync(clinician, patient, 150m, 95m);
        await RecordAsync(clinician, patient, HeartRate, 70m);

        // From the diastolic.
        await MarkAsync(clinician, patient, Diastolic, "Synthetic: cuff too small.");
        const string marks = """
            SELECT code || ' ' || trim_scale(value) || ' ' || status || ' ' || coalesce(entered_in_error_reason, 'none') || ' ' || coalesce(entered_in_error_by::text, 'none')
            FROM ehr.observations WHERE patient_id = @patient ORDER BY effective_at DESC, issued_at
            """;
        Assert.Equal(
            [$"8480-6 150 entered-in-error Synthetic: cuff too small. {clinician}", $"8462-4 95 entered-in-error Synthetic: cuff too small. {clinician}", "8867-4 70 final none none"],
            await AsSuperuserAsync(marks, ("patient", patient)));
        // Each value its own audited change, by the clinician, with the reason.
        Assert.Equal([Diastolic, Systolic], await AsSuperuserAsync(
            """
            SELECT o.code FROM ehr.audit_events e JOIN ehr.observations o ON o.id = e.row_id
            WHERE e.table_name = 'observations' AND e.action = 'update' AND e.patient_id = @patient AND e.actor_id = @actor AND e.details::text LIKE '%cuff too small%'
            ORDER BY o.code
            """, ("patient", patient), ("actor", clinician)));

        // From the systolic, likewise.
        var earlier = Taken.AddMinutes(-5);
        await RecordPressureAsync(clinician, patient, 140m, 90m, at: earlier);
        await MarkAsync(clinician, patient, Systolic, "Synthetic: wrong arm.");
        Assert.Equal(["Synthetic: wrong arm.", "Synthetic: wrong arm."], await AsSuperuserAsync(
            "SELECT entered_in_error_reason FROM ehr.observations WHERE patient_id = @patient AND effective_at = @at", ("patient", patient), ("at", earlier)));

        // A correction is a new reading, with an ID of its own. A value can't be put into a reading whose other value is
        // in error, even by a superuser: a reading with nothing standing may be there, one half standing may not.
        await RecordPressureAsync(clinician, patient, 130m, 85m);
        Assert.Equal(["3"], await AsSuperuserAsync("SELECT count(DISTINCT reading_id)::text FROM ehr.observations WHERE patient_id = @patient", ("patient", patient)));
        var halfInError = Guid.CreateVersion7();
        var older = Taken.AddMinutes(-30);
        await Accounts.ExecuteAsync(Superuser, null, Insert, Values(patient, Systolic, 160m, older, reading: halfInError, status: "entered-in-error"));
        await RefusedAsync(PostgresErrorCodes.CheckViolation, () => Accounts.ExecuteAsync(Superuser, null, Insert, Values(patient, Diastolic, 85m, older, reading: halfInError)));
    }

    [Fact]
    public async Task Nobody_without_the_patient_on_their_panel_marks_a_reading()
    {
        var (clinician, patient) = await OnPanelAsync();
        await RecordPressureAsync(clinician, patient, 150m, 95m);
        var outsider = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        var admin = await Accounts.StaffAsync(database, StaffRoles.Admin);
        var account = await Accounts.PatientAccountAsync(database, patient);

        // Row-level security shows the update no row to change.
        foreach (var user in new Guid?[] { outsider, admin, account, null })
        {
            await MarkAsync(user, patient, Diastolic);
        }

        await RefusedAsync(PostgresErrorCodes.InsufficientPrivilege, () => MarkAsync(clinician, patient, Diastolic, connection: Read));
        Assert.Equal(["8480-6 150 final none R1", "8462-4 95 final none R1"], await StateAsync(patient));
    }

    // Step 9b's rule, kept for the reading's other value: one statement can't name someone else as the person who
    // marked it by changing who is signed in after the first value is marked, here to a colleague who also has the
    // patient on their panel and so could mark it.
    [Fact]
    public async Task A_statement_cannot_name_someone_else_for_the_other_value_of_a_reading()
    {
        var (clinician, patient) = await OnPanelAsync();
        var colleague = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        await Accounts.AssignAsync(database, colleague, patient);
        await RecordPressureAsync(clinician, patient, 150m, 95m);

        await RefusedAsync(PostgresErrorCodes.InsufficientPrivilege, () => Accounts.ExecuteAsync(App, clinician,
            """
            UPDATE ehr.observations SET status = 'entered-in-error', entered_in_error_reason = 'Synthetic'
            WHERE patient_id = @patient AND code = '8480-6'
            RETURNING left(set_config('app.user_id', @other, true), 0)
            """, ("patient", patient), ("other", colleague.ToString())));
        Assert.Equal(["8480-6 150 final none R1", "8462-4 95 final none R1"], await StateAsync(patient));
    }

    // A later migration that gives readings a new reference range (step 9d) changes nothing the check reads: it leaves no
    // check waiting for the commit, so it can lift and put back the table's protection in the same transaction. A change
    // to what a reading is made of still waits for the check, and is refused when it would leave a reading half.
    [Fact]
    public async Task Only_a_change_to_what_a_reading_is_made_of_waits_for_the_check()
    {
        var (clinician, patient) = await OnPanelAsync();
        var visit = Guid.CreateVersion7();
        await Accounts.ExecuteAsync(Superuser, null,
            "INSERT INTO ehr.encounters (id, patient_id, status, class_code, period_start) VALUES (@id, @patient, 'finished', 'AMB', now() - interval '1 day')",
            ("id", visit), ("patient", patient));
        await RecordPressureAsync(clinician, patient, 150m, 95m);
        const string reranged = "UPDATE ehr.observations SET reference_high = reference_high + 1 WHERE patient_id = @patient AND code IN ('8480-6', '8462-4'); ";
        const string altered = "ALTER TABLE ehr.observations FORCE ROW LEVEL SECURITY;";

        await Accounts.ExecuteAsync(Superuser, null, reranged + altered, ("patient", patient));
        Assert.Equal(["130", "80"], await AsSuperuserAsync(
            "SELECT trim_scale(reference_high)::text FROM ehr.observations WHERE patient_id = @patient ORDER BY code DESC", ("patient", patient)));

        await RefusedAsync(PostgresErrorCodes.ObjectInUse, () => Accounts.ExecuteAsync(Superuser, null,
            "UPDATE ehr.observations SET status = 'entered-in-error' WHERE patient_id = @patient AND code = '8480-6'; " + altered, ("patient", patient)));
        await RefusedAsync(PostgresErrorCodes.CheckViolation, () => Accounts.ExecuteAsync(Superuser, null,
            "UPDATE ehr.observations SET encounter_id = @visit WHERE patient_id = @patient AND code = '8480-6'", ("patient", patient), ("visit", visit)));
        await RefusedAsync(PostgresErrorCodes.CheckViolation, () => Accounts.ExecuteAsync(Superuser, null,
            "UPDATE ehr.observations SET effective_at = effective_at - interval '1 minute' WHERE patient_id = @patient AND code = '8462-4'", ("patient", patient)));
        Assert.Equal(["8480-6 150 final none R1", "8462-4 95 final none R1"], await StateAsync(patient));
    }

    // Two transactions recording a blood pressure for one patient and time: the second waits for the first, then finds
    // its reading and is refused; nothing is left half.
    [Fact]
    public async Task A_second_blood_pressure_for_the_same_moment_waits_for_the_first_and_is_refused()
    {
        var (clinician, patient) = await OnPanelAsync();
        var (first, firstTransaction) = await BeginAsync(clinician);
        var (second, secondTransaction) = await BeginAsync(clinician);
        await using (first)
        await using (second)
        {
            await ExecuteAsync(first, firstTransaction, Insert, Values(patient, Systolic, 120m));
            var waiting = ExecuteAsync(second, secondTransaction, Insert, Values(patient, Diastolic, 85m));
            await WaitsAsync(waiting);
            await ExecuteAsync(first, firstTransaction, Insert, Values(patient, Diastolic, 80m));
            await firstTransaction.CommitAsync(Cancel);
            Assert.Equal(PostgresErrorCodes.UniqueViolation, (await Assert.ThrowsAsync<PostgresException>(() => waiting)).SqlState);
        }

        Assert.Equal(["8480-6 120 final none R1", "8462-4 80 final none R1"], await StateAsync(patient));
    }

    // A check-in while one transaction holds a moment's first value, and a second transaction records another value of
    // that moment: the check-in and the second value both wait for the first; whichever goes next, the other waits for
    // it in turn, and the moment ends whole, with the visit (taken within fifteen minutes before it, step 9c's
    // follow-up), not split by the check-in between.
    [Fact]
    public async Task A_check_in_between_two_transactions_of_one_moment_cannot_split_it()
    {
        var (clinician, patient) = await OnPanelAsync();
        var visit = Guid.CreateVersion7();
        var (first, firstTransaction) = await BeginAsync(clinician);
        var (second, secondTransaction) = await BeginAsync(clinician);
        var (checkingIn, checkInTransaction) = await BeginAsync(clinician);
        await using (first)
        await using (second)
        await using (checkingIn)
        {
            await ExecuteAsync(first, firstTransaction, Insert, Values(patient, HeartRate, 70m));
            var checkIn = ExecuteAsync(checkingIn, checkInTransaction,
                "INSERT INTO ehr.encounters (id, patient_id, status, class_code, period_start) VALUES (@id, @patient, 'in-progress', 'AMB', now())",
                ("id", visit), ("patient", patient));
            await WaitsAsync(checkIn);
            var waiting = ExecuteAsync(second, secondTransaction, Insert, Values(patient, Temperature, 37m));
            await WaitsAsync(waiting);
            await firstTransaction.CommitAsync(Cancel);
            var next = await Task.WhenAny(checkIn, waiting);
            await (next == checkIn ? checkInTransaction : secondTransaction).CommitAsync(Cancel);
            Assert.Equal(1, await checkIn);
            Assert.Equal(1, await waiting);
            await (next == checkIn ? secondTransaction : checkInTransaction).CommitAsync(Cancel);
        }

        Assert.Equal(["8867-4 70 final visit none", "8310-5 37 final visit none"], await StateAsync(patient, visit));
    }

    // Two clinicians marking one reading at once, one from each value: the second waits, then finds it marked and is
    // refused; the reading is marked whole, by the first, with the first's reason.
    [Fact]
    public async Task Two_clinicians_marking_one_reading_at_once_leave_it_marked_whole_by_one_of_them()
    {
        var (clinician, patient) = await OnPanelAsync();
        var colleague = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        await Accounts.AssignAsync(database, colleague, patient);
        await RecordPressureAsync(clinician, patient, 150m, 95m);
        var ids = (await AsSuperuserAsync("SELECT code || ' ' || id FROM ehr.observations WHERE patient_id = @patient", ("patient", patient)))
            .ToDictionary(row => row.Split(' ')[0], row => Guid.Parse(row.Split(' ')[1]));
        const string mark = "UPDATE ehr.observations SET status = 'entered-in-error', entered_in_error_reason = @reason WHERE id = @id";

        var (first, firstTransaction) = await BeginAsync(clinician);
        var (second, secondTransaction) = await BeginAsync(colleague);
        await using (first)
        await using (second)
        {
            Assert.Equal(1, await ExecuteAsync(first, firstTransaction, mark, ("id", ids[Systolic]), ("reason", "Synthetic: first")));
            var waiting = ExecuteAsync(second, secondTransaction, mark, ("id", ids[Diastolic]), ("reason", "Synthetic: second"));
            await WaitsAsync(waiting);
            await firstTransaction.CommitAsync(Cancel);
            Assert.Equal(PostgresErrorCodes.CheckViolation, (await Assert.ThrowsAsync<PostgresException>(() => waiting)).SqlState);
        }

        Assert.Equal([$"entered-in-error Synthetic: first {clinician}", $"entered-in-error Synthetic: first {clinician}"], await AsSuperuserAsync(
            "SELECT status || ' ' || entered_in_error_reason || ' ' || entered_in_error_by FROM ehr.observations WHERE patient_id = @patient", ("patient", patient)));
    }
}
