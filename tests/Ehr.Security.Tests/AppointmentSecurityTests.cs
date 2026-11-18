using Ehr.Data.Accounts;
using Ehr.Testing;
using Npgsql;
using Xunit;

namespace Ehr.Security.Tests;

// FR-17 (the expansion plan's step 4): what the database lets each account do with appointments, as the runtime
// roles, in SQL of its own (db/policies/10-grants.sql, 30-rls.sql and 80-appointments.sql). A clinician books and
// changes them only for a patient on their panel; an appointment makes one of four moves and then never changes; a
// clinician is never double-booked, even by two bookings at once; a patient reads only their own; nothing is deleted.
public class AppointmentSecurityTests(MigratedDatabase database) : IClassFixture<MigratedDatabase>
{
    string App => database.ConnectionStringFor("ehr_app", MigratedDatabase.AppPassword);

    string Read => database.ConnectionStringFor("ehr_read", MigratedDatabase.ReadPassword);

    const string Book = """
        INSERT INTO ehr.appointments (id, patient_id, clinician_id, starts_at, minutes, reason, status, booked_at)
        VALUES (@id, @patient, @clinician, @start, 20, 'Synthetic: check-up', 'booked', '2000-01-01')
        """;

    static readonly DateTimeOffset Morning = new(2027, 3, 1, 9, 0, 0, TimeSpan.Zero);

    async Task<(Guid Clinician, Guid Patient)> OnPanelAsync()
    {
        var clinician = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        var patient = await Accounts.PatientAsync(database, clinician);
        await Accounts.AssignAsync(database, clinician, patient);
        return (clinician, patient);
    }

    async Task<Guid> BookAsync(Guid? user, Guid patient, Guid clinician, DateTimeOffset start)
    {
        var id = Guid.CreateVersion7();
        await Accounts.ExecuteAsync(App, user, Book, ("id", id), ("patient", patient), ("clinician", clinician), ("start", start));
        return id;
    }

    Task<List<string>> AsSuperuserAsync(string sql, params (string Name, object Value)[] parameters) =>
        Accounts.QueryAsync(database.SuperuserConnectionString, null, sql, parameters);

    [Fact]
    public async Task A_clinician_books_for_a_patient_on_their_panel_and_the_database_sets_who_and_when_and_audits_it()
    {
        var (clinician, patient) = await OnPanelAsync();
        var colleague = await Accounts.StaffAsync(database, StaffRoles.Clinician);

        // With a colleague, who needn't have the patient on their panel.
        var id = await BookAsync(clinician, patient, colleague, Morning);

        Assert.Equal([$"{clinician} true {colleague}"], await AsSuperuserAsync(
            "SELECT booked_by_id || ' ' || (booked_at > now() - interval '1 minute') || ' ' || clinician_id FROM ehr.appointments WHERE id = @id", ("id", id)));
        Assert.Equal([$"insert {clinician} {patient}"], await AsSuperuserAsync(
            "SELECT action || ' ' || actor_id || ' ' || patient_id FROM ehr.audit_events WHERE table_name = 'appointments' AND row_id = @id", ("id", id)));
    }

    [Fact]
    public async Task Nobody_but_a_clinician_with_the_patient_on_their_panel_books_or_changes_an_appointment()
    {
        var (clinician, patient) = await OnPanelAsync();
        var id = await BookAsync(clinician, patient, clinician, Morning);
        var offPanel = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        var admin = await Accounts.StaffAsync(database, StaffRoles.Admin);
        var patientAccount = await Accounts.PatientAccountAsync(database, patient);
        var disabled = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        await Accounts.AssignAsync(database, disabled, patient);
        await Accounts.LockOutAsync(database, disabled);

        foreach (var refused in new Guid?[] { offPanel, admin, patientAccount, disabled, null, Guid.NewGuid() })
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => BookAsync(refused, patient, clinician, Morning.AddDays(1)));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
            // Row-level security shows them no appointment to update.
            await Accounts.ExecuteAsync(App, refused, "UPDATE ehr.appointments SET status = 'cancelled', cancel_reason = 'Synthetic' WHERE id = @id", ("id", id));
        }

        foreach (var (connection, sql) in new[]
        {
            (Read, Book),
            (Read, "UPDATE ehr.appointments SET status = 'noshow' WHERE id = @id"),
            (App, "DELETE FROM ehr.appointments WHERE id = @id"),
            (App, "UPDATE ehr.appointments SET patient_id = patient_id WHERE id = @id"),
            (App, "UPDATE ehr.appointments SET booked_by_id = booked_by_id WHERE id = @id"),
        })
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => Accounts.ExecuteAsync(connection, clinician, sql,
                ("id", id), ("patient", patient), ("clinician", clinician), ("start", Morning.AddDays(2))));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
        }

        Assert.Equal(["booked 1"], await AsSuperuserAsync(
            "SELECT status || ' ' || (SELECT count(*) FROM ehr.appointments WHERE patient_id = @patient) FROM ehr.appointments WHERE id = @id", ("id", id), ("patient", patient)));
    }

    [Fact]
    public async Task A_patient_reads_their_own_appointments_and_clinicians_read_all()
    {
        var (clinician, patient) = await OnPanelAsync();
        var id = await BookAsync(clinician, patient, clinician, Morning);
        var colleague = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        var own = await Accounts.PatientAccountAsync(database, patient);
        var otherPatient = await Accounts.PatientAsync(database, clinician);
        var other = await Accounts.PatientAccountAsync(database, otherPatient);
        var admin = await Accounts.StaffAsync(database, StaffRoles.Admin);
        const string read = "SELECT count(*) FROM ehr.appointments WHERE id = @id";

        foreach (var connection in new[] { App, Read })
        {
            foreach (var allowed in new[] { clinician, colleague, own })
            {
                Assert.Equal(["1"], await Accounts.QueryAsync(connection, allowed, read, ("id", id)));
            }

            foreach (var refused in new Guid?[] { other, admin, null, Guid.NewGuid() })
            {
                Assert.Equal(["0"], await Accounts.QueryAsync(connection, refused, read, ("id", id)));
            }
        }
    }

    [Fact]
    public async Task An_appointment_makes_one_of_four_moves_and_then_never_changes()
    {
        var (clinician, patient) = await OnPanelAsync();
        var appointment = await BookAsync(clinician, patient, clinician, Morning);

        // Reschedule: still booked.
        await Accounts.ExecuteAsync(App, clinician, "UPDATE ehr.appointments SET starts_at = @start, minutes = 30 WHERE id = @id",
            ("id", appointment), ("start", Morning.AddHours(2)));
        // Not: a status that isn't one, a cancellation without a reason, a check-in without a visit or with another
        // patient's, a change of time while cancelling, a change of clinician.
        var otherPatient = await Accounts.PatientAsync(database, clinician);
        var theirVisit = await Accounts.ChartRowAsync(database, "encounters", otherPatient);
        foreach (var (sql, state) in new[]
        {
            ("UPDATE ehr.appointments SET status = 'pending' WHERE id = @id", PostgresErrorCodes.CheckViolation),
            ("UPDATE ehr.appointments SET status = 'cancelled' WHERE id = @id", PostgresErrorCodes.CheckViolation),
            ("UPDATE ehr.appointments SET status = 'cancelled', cancel_reason = '  ' WHERE id = @id", PostgresErrorCodes.CheckViolation),
            ("UPDATE ehr.appointments SET status = 'fulfilled' WHERE id = @id", PostgresErrorCodes.CheckViolation),
            ("UPDATE ehr.appointments SET status = 'fulfilled', encounter_id = @visit WHERE id = @id", PostgresErrorCodes.CheckViolation),
            ("UPDATE ehr.appointments SET status = 'noshow', minutes = 60 WHERE id = @id", PostgresErrorCodes.CheckViolation),
        })
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => Accounts.ExecuteAsync(App, clinician, sql, ("id", appointment), ("visit", theirVisit)));
            Assert.Equal(state, error.SqlState);
        }

        // Check in: the visit, then the appointment naming it, as the app does.
        var visit = Guid.CreateVersion7();
        await Accounts.ExecuteAsync(App, clinician,
            """
            INSERT INTO ehr.encounters (id, patient_id, status, class_code, period_start) VALUES (@visit, @patient, 'in-progress', 'AMB', now());
            UPDATE ehr.appointments SET status = 'fulfilled', encounter_id = @visit WHERE id = @id
            """,
            ("id", appointment), ("visit", visit), ("patient", patient));
        Assert.Equal([$"fulfilled {visit} 30"], await AsSuperuserAsync("SELECT status || ' ' || encounter_id || ' ' || minutes FROM ehr.appointments WHERE id = @id", ("id", appointment)));

        // Done: it never changes again.
        foreach (var sql in new[]
        {
            "UPDATE ehr.appointments SET status = 'booked', encounter_id = NULL WHERE id = @id",
            "UPDATE ehr.appointments SET status = 'cancelled', cancel_reason = 'Synthetic', encounter_id = NULL WHERE id = @id",
            "UPDATE ehr.appointments SET reason = 'Synthetic: changed' WHERE id = @id",
        })
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => Accounts.ExecuteAsync(App, clinician, sql, ("id", appointment)));
            Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        }
    }

    // A visit is created only for a patient on the clinician's panel, and never changed or deleted.
    [Fact]
    public async Task A_visit_is_created_only_for_a_patient_on_the_clinicians_panel()
    {
        var (clinician, patient) = await OnPanelAsync();
        var offPanel = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        const string insert = "INSERT INTO ehr.encounters (id, patient_id, status, class_code, period_start) VALUES (gen_random_uuid(), @patient, 'in-progress', 'AMB', now())";

        await Accounts.ExecuteAsync(App, clinician, insert, ("patient", patient));
        foreach (var refused in new Guid?[] { offPanel, null })
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => Accounts.ExecuteAsync(App, refused, insert, ("patient", patient)));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
        }

        foreach (var sql in new[] { "UPDATE ehr.encounters SET reason = 'changed' WHERE patient_id = @patient", "DELETE FROM ehr.encounters WHERE patient_id = @patient" })
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => Accounts.ExecuteAsync(App, clinician, sql, ("patient", patient)));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
        }
    }

    [Fact]
    public async Task A_clinician_is_never_double_booked_and_an_appointment_is_with_a_clinician()
    {
        var (clinician, patient) = await OnPanelAsync();
        var first = await BookAsync(clinician, patient, clinician, Morning);

        // Overlapping the first: the same start, starting inside it, ending inside it.
        foreach (var start in new[] { Morning, Morning.AddMinutes(10), Morning.AddMinutes(-10) })
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => BookAsync(clinician, patient, clinician, start));
            Assert.Equal(PostgresErrorCodes.ExclusionViolation, error.SqlState);
        }

        // Straight after it, and at the same time with another clinician: fine.
        var after = await BookAsync(clinician, patient, clinician, Morning.AddMinutes(20));
        var colleague = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        await BookAsync(clinician, patient, colleague, Morning);
        // Rescheduling onto another is refused; a cancelled one frees its time.
        var moved = await Assert.ThrowsAsync<PostgresException>(() => Accounts.ExecuteAsync(App, clinician,
            "UPDATE ehr.appointments SET starts_at = @start WHERE id = @id", ("id", after), ("start", Morning.AddMinutes(5))));
        Assert.Equal(PostgresErrorCodes.ExclusionViolation, moved.SqlState);
        await Accounts.ExecuteAsync(App, clinician, "UPDATE ehr.appointments SET status = 'cancelled', cancel_reason = 'Synthetic' WHERE id = @id", ("id", first));
        await BookAsync(clinician, patient, clinician, Morning);

        // Not with an admin, a disabled clinician, or nobody.
        var admin = await Accounts.StaffAsync(database, StaffRoles.Admin);
        var disabled = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        await Accounts.LockOutAsync(database, disabled);
        foreach (var with in new[] { admin, disabled })
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => BookAsync(clinician, patient, with, Morning.AddDays(3)));
            Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        }
    }

    // A clinician locked out for a few minutes after wrong passwords still has a schedule: only a disabled account,
    // locked out for good, can't be booked with.
    [Fact]
    public async Task A_clinician_locked_out_for_minutes_can_still_be_booked_with()
    {
        var (clinician, patient) = await OnPanelAsync();
        var colleague = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        await Accounts.ExecuteAsync(database.SuperuserConnectionString, null,
            "UPDATE ehr.staff_users SET lockout_end = now() + interval '15 minutes' WHERE id = @id", ("id", colleague));

        await BookAsync(clinician, patient, colleague, Morning.AddDays(9));
    }

    // Each change is one more version, whatever the client sends, so an update that names an old version finds no row.
    [Fact]
    public async Task Each_change_is_one_more_version()
    {
        var (clinician, patient) = await OnPanelAsync();
        var id = await BookAsync(clinician, patient, clinician, Morning.AddDays(10));

        await Accounts.ExecuteAsync(App, clinician, "UPDATE ehr.appointments SET minutes = 30 WHERE id = @id AND version = 1", ("id", id));
        await Accounts.ExecuteAsync(App, clinician, "UPDATE ehr.appointments SET minutes = 45 WHERE id = @id AND version = 1", ("id", id));

        Assert.Equal(["2 30"], await AsSuperuserAsync("SELECT version || ' ' || minutes FROM ehr.appointments WHERE id = @id", ("id", id)));
        var error = await Assert.ThrowsAsync<PostgresException>(() => Accounts.ExecuteAsync(App, clinician,
            "UPDATE ehr.appointments SET version = 1 WHERE id = @id", ("id", id)));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
    }

    // Under a stricter isolation level the overlap check would read an old snapshot and miss the booking it waited
    // for: refused outright, as signing an order is.
    [Theory]
    [InlineData(System.Data.IsolationLevel.RepeatableRead)]
    [InlineData(System.Data.IsolationLevel.Serializable)]
    public async Task A_booking_outside_a_read_committed_transaction_is_refused(System.Data.IsolationLevel level)
    {
        var (clinician, patient) = await OnPanelAsync();
        await using var connection = new NpgsqlConnection(App);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(level, TestContext.Current.CancellationToken);
        await using (var user = new NpgsqlCommand("SELECT set_config('app.user_id', @id, true)", connection, transaction))
        {
            user.Parameters.AddWithValue("id", clinician.ToString());
            await user.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        await using var book = new NpgsqlCommand(Book, connection, transaction);
        book.Parameters.AddWithValue("id", Guid.CreateVersion7());
        book.Parameters.AddWithValue("patient", patient);
        book.Parameters.AddWithValue("clinician", clinician);
        book.Parameters.AddWithValue("start", Morning.AddDays(11));
        var error = await Assert.ThrowsAsync<PostgresException>(() => book.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));

        Assert.Equal(PostgresErrorCodes.FeatureNotSupported, error.SqlState);
    }

    [Fact]
    public async Task Two_bookings_of_the_same_time_at_once_cannot_both_get_through()
    {
        var (clinician, patient) = await OnPanelAsync();

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(async _ =>
        {
            try
            {
                await BookAsync(clinician, patient, clinician, Morning.AddDays(7));
                return true;
            }
            catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.ExclusionViolation)
            {
                return false;
            }
        }));

        Assert.Single(results, booked => booked);
    }
}
