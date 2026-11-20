using Ehr.Data.Accounts;
using Ehr.Testing;
using Npgsql;
using Xunit;

namespace Ehr.Security.Tests;

// The expansion plan's step 9: what the database lets each account do with a vital sign, as the runtime roles, in SQL
// of its own (db/policies/10-grants.sql, 30-rls.sql and 75-results.sql). A vital sign is an observation with no order:
// a clinician with the patient on their panel records one, only of the vital-sign codes, with the database's own
// name, unit and reference range, final and released at once; a body mass index is the database's arithmetic, never
// the client's number; a result of an order is still filed only against its order; nothing is edited or deleted.
public class VitalSignSecurityTests(MigratedDatabase database) : IClassFixture<MigratedDatabase>
{
    const string HeartRate = "8867-4";
    const string Weight = "29463-7";
    const string Height = "8302-2";
    const string BodyMassIndex = "39156-5";

    string App => database.ConnectionStringFor("ehr_app", MigratedDatabase.AppPassword);

    string Read => database.ConnectionStringFor("ehr_read", MigratedDatabase.ReadPassword);

    // Everything a client could try to choose for itself is sent wrong.
    const string Vital = """
        INSERT INTO ehr.observations (id, patient_id, encounter_id, category, code, display, status, value, unit, reference_low, reference_high, effective_at, issued_at, note)
        VALUES (@id, @patient, @visit, @category, @code, 'Forged name', 'preliminary', @value, 'forged', 0, 1000, @at, '2000-01-01', 'Forged note')
        """;

    static readonly DateTime Taken = DateTime.UtcNow.AddMinutes(-10);

    async Task<(Guid Clinician, Guid Patient)> OnPanelAsync()
    {
        var clinician = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        var patient = await Accounts.PatientAsync(database, clinician);
        await Accounts.AssignAsync(database, clinician, patient);
        return (clinician, patient);
    }

    Task RecordAsync(
        Guid? user, Guid patient, string code = HeartRate, decimal value = 142m, string category = "vital-signs", Guid? visit = null, DateTime? at = null,
        string? connection = null) =>
        Accounts.ExecuteAsync(connection ?? App, user, Vital, ("id", Guid.CreateVersion7()), ("patient", patient), ("visit", (object?)visit ?? DBNull.Value),
            ("category", category), ("code", code), ("value", value), ("at", at ?? Taken));

    Task<List<string>> AsSuperuserAsync(string sql, params (string Name, object Value)[] parameters) =>
        Accounts.QueryAsync(database.SuperuserConnectionString, null, sql, parameters);

    async Task RefusedAsync(string state, Func<Task> attempt) =>
        Assert.Equal(state, (await Assert.ThrowsAsync<PostgresException>(attempt)).SqlState);

    async Task<Guid> VisitAsync(Guid patient)
    {
        var visit = Guid.CreateVersion7();
        await Accounts.ExecuteAsync(database.SuperuserConnectionString, null,
            "INSERT INTO ehr.encounters (id, patient_id, status, class_code, period_start) VALUES (@id, @patient, 'finished', 'AMB', now() - interval '1 hour')",
            ("id", visit), ("patient", patient));
        return visit;
    }

    [Fact]
    public async Task A_recorded_vital_sign_has_the_databases_name_unit_and_range_is_released_and_is_audited()
    {
        var (clinician, patient) = await OnPanelAsync();
        var visit = await VisitAsync(patient);

        await RecordAsync(clinician, patient, visit: visit);

        Assert.Equal(["Heart rate|/min|60.0000|100.0000|final|true|142.0000|vital-signs|none|none"], await AsSuperuserAsync(
            """
            SELECT display || '|' || unit || '|' || reference_low || '|' || reference_high || '|' || status || '|' || (issued_at > now() - interval '1 minute') || '|' || value
                || '|' || category || '|' || coalesce(note, 'none') || '|' || coalesce(order_id::text, 'none')
            FROM ehr.observations WHERE patient_id = @patient
            """, ("patient", patient)));
        Assert.Equal([clinician.ToString()], await AsSuperuserAsync(
            "SELECT actor_id::text FROM ehr.audit_events WHERE table_name = 'observations' AND action = 'insert' AND patient_id = @patient", ("patient", patient)));

        // The patient sees it at once, as a released observation; another patient never does.
        var account = await Accounts.PatientAccountAsync(database, patient);
        Assert.Equal(["142.0000"], await Accounts.QueryAsync(Read, account, "SELECT value::text FROM ehr.observations WHERE patient_id = @patient", ("patient", patient)));
        var stranger = await Accounts.PatientAccountAsync(database, await Accounts.PatientAsync(database, clinician));
        Assert.Empty(await Accounts.QueryAsync(Read, stranger, "SELECT value::text FROM ehr.observations WHERE patient_id = @patient", ("patient", patient)));
    }

    [Fact]
    public async Task Only_a_clinician_with_the_patient_on_their_panel_records_a_vital_sign()
    {
        var (clinician, patient) = await OnPanelAsync();
        var colleague = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        var admin = await Accounts.StaffAsync(database, StaffRoles.Admin);
        var account = await Accounts.PatientAccountAsync(database, patient);

        foreach (var user in new Guid?[] { colleague, admin, account, null })
        {
            await RefusedAsync(PostgresErrorCodes.InsufficientPrivilege, () => RecordAsync(user, patient));
        }

        // The read-only role has no INSERT at all, whoever is signed in.
        await RefusedAsync(PostgresErrorCodes.InsufficientPrivilege, () => RecordAsync(clinician, patient, connection: Read));
        Assert.Equal(["0"], await AsSuperuserAsync("SELECT count(*)::text FROM ehr.observations WHERE patient_id = @patient", ("patient", patient)));
    }

    [Fact]
    public async Task Without_an_order_only_a_vital_sign_is_recorded_and_only_a_sound_one()
    {
        var (clinician, patient) = await OnPanelAsync();
        var (_, someoneElse) = await OnPanelAsync();
        var theirVisit = await VisitAsync(someoneElse);

        // A laboratory code, or the laboratory category, with no order: still refused, as before step 9.
        await RefusedAsync(PostgresErrorCodes.CheckViolation, () => RecordAsync(clinician, patient, code: "718-7"));
        await RefusedAsync(PostgresErrorCodes.CheckViolation, () => RecordAsync(clinician, patient, category: "laboratory"));
        // A code the reference data doesn't have, a value of zero or less, a time in the future, another patient's visit.
        await RefusedAsync(PostgresErrorCodes.CheckViolation, () => RecordAsync(clinician, patient, code: "0000-0"));
        await RefusedAsync(PostgresErrorCodes.CheckViolation, () => RecordAsync(clinician, patient, value: 0m));
        await RefusedAsync(PostgresErrorCodes.CheckViolation, () => RecordAsync(clinician, patient, value: -5m));
        await RefusedAsync(PostgresErrorCodes.CheckViolation, () => RecordAsync(clinician, patient, at: DateTime.UtcNow.AddHours(2)));
        await RefusedAsync(PostgresErrorCodes.CheckViolation, () => RecordAsync(clinician, patient, visit: theirVisit));

        Assert.Equal(["0"], await AsSuperuserAsync("SELECT count(*)::text FROM ehr.observations WHERE patient_id = @patient", ("patient", patient)));
    }

    [Fact]
    public async Task A_body_mass_index_is_worked_out_by_the_database_from_the_weight_and_height_taken_with_it()
    {
        var (clinician, patient) = await OnPanelAsync();

        // Without both, refused; with a weight and height from another moment, refused.
        await RefusedAsync(PostgresErrorCodes.CheckViolation, () => RecordAsync(clinician, patient, BodyMassIndex, 19m));
        await RecordAsync(clinician, patient, Weight, 81m);
        await RefusedAsync(PostgresErrorCodes.CheckViolation, () => RecordAsync(clinician, patient, BodyMassIndex, 19m));
        await RecordAsync(clinician, patient, Height, 180m, at: Taken.AddMinutes(-30));
        await RefusedAsync(PostgresErrorCodes.CheckViolation, () => RecordAsync(clinician, patient, BodyMassIndex, 19m));

        // 81 kg at 1.80 m is 25.0, whatever number the client sent.
        await RecordAsync(clinician, patient, Height, 180m);
        await RecordAsync(clinician, patient, BodyMassIndex, 19m);
        Assert.Equal(["25.0000|18.5000|24.9000|kg/m2"], await AsSuperuserAsync(
            "SELECT value || '|' || reference_low || '|' || reference_high || '|' || unit FROM ehr.observations WHERE patient_id = @patient AND code = @code",
            ("patient", patient), ("code", BodyMassIndex)));
    }

    [Fact]
    public async Task A_vital_sign_is_tied_to_the_visit_it_was_taken_at_and_never_to_one_that_was_not_held()
    {
        var (clinician, patient) = await OnPanelAsync();
        var visit = await VisitAsync(patient);
        var cancelled = Guid.CreateVersion7();
        await Accounts.ExecuteAsync(database.SuperuserConnectionString, null,
            "INSERT INTO ehr.encounters (id, patient_id, status, class_code, period_start) VALUES (@id, @patient, 'cancelled', 'AMB', now() - interval '20 minutes')",
            ("id", cancelled), ("patient", patient));

        // Naming no visit: the one held in the hours before. A day earlier: none. A cancelled visit can't be named (at a
        // moment of its own: a value at a moment that has one takes that one's visit, step 9c).
        await RecordAsync(clinician, patient);
        await RecordAsync(clinician, patient, at: Taken.AddDays(-1));
        await RefusedAsync(PostgresErrorCodes.CheckViolation, () => RecordAsync(clinician, patient, code: "8310-5", value: 37m, visit: cancelled, at: Taken.AddMinutes(-5)));

        Assert.Equal([$"none", $"{visit}"], await AsSuperuserAsync(
            "SELECT coalesce(encounter_id::text, 'none') FROM ehr.observations WHERE patient_id = @patient ORDER BY effective_at", ("patient", patient)));
    }

    [Fact]
    public async Task A_value_no_person_has_a_time_before_any_lifetime_and_the_same_sign_twice_are_refused()
    {
        var (clinician, patient) = await OnPanelAsync();

        await RefusedAsync(PostgresErrorCodes.CheckViolation, () => RecordAsync(clinician, patient, code: "59408-5", value: 999m));
        await RefusedAsync(PostgresErrorCodes.CheckViolation, () => RecordAsync(clinician, patient, code: "8310-5", value: 60m));
        await RefusedAsync(PostgresErrorCodes.CheckViolation, () => RecordAsync(clinician, patient, at: new DateTime(1850, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        // The heaviest and shortest the bounds allow: the body mass index is refused, not stored or overflowed.
        await RecordAsync(clinician, patient, Weight, 700m);
        await RecordAsync(clinician, patient, Height, 20m);
        await RefusedAsync(PostgresErrorCodes.CheckViolation, () => RecordAsync(clinician, patient, BodyMassIndex, 19m));

        // A form sent twice records once.
        await RecordAsync(clinician, patient);
        await RefusedAsync(PostgresErrorCodes.UniqueViolation, () => RecordAsync(clinician, patient, value: 150m));
        Assert.Equal(["29463-7 700", "8302-2 20", "8867-4 142"], await AsSuperuserAsync(
            "SELECT code || ' ' || trim_scale(value) FROM ehr.observations WHERE patient_id = @patient ORDER BY code", ("patient", patient)));
    }

    [Fact]
    public async Task A_result_of_an_order_cannot_pass_as_a_vital_sign_or_claim_a_visit()
    {
        var (clinician, patient) = await OnPanelAsync();
        var visit = await VisitAsync(patient);
        var order = Guid.CreateVersion7();
        await Accounts.ExecuteAsync(App, clinician,
            "INSERT INTO ehr.orders (id, patient_id, type, catalog_code, details, status) VALUES (@id, @patient, 'lab', 'SYN-LAB-001', '{}', 'active')",
            ("id", order), ("patient", patient));
        const string result = """
            INSERT INTO ehr.observations (id, patient_id, order_id, encounter_id, category, code, display, status, value, effective_at)
            VALUES (gen_random_uuid(), @patient, @order, @visit, @category, '718-7', 'x', 'final', 13.5, now() - interval '1 hour')
            """;

        await RefusedAsync(PostgresErrorCodes.CheckViolation, () => Accounts.ExecuteAsync(App, clinician, result,
            ("patient", patient), ("order", order), ("visit", DBNull.Value), ("category", "vital-signs")));
        await RefusedAsync(PostgresErrorCodes.CheckViolation, () => Accounts.ExecuteAsync(App, clinician, result,
            ("patient", patient), ("order", order), ("visit", visit), ("category", "laboratory")));

        await Accounts.ExecuteAsync(App, clinician, result, ("patient", patient), ("order", order), ("visit", DBNull.Value), ("category", "laboratory"));
        Assert.Equal(["laboratory"], await AsSuperuserAsync("SELECT category FROM ehr.observations WHERE order_id = @order", ("order", order)));
    }

    [Fact]
    public async Task A_vital_sign_is_never_edited_or_deleted_by_the_apps()
    {
        var (clinician, patient) = await OnPanelAsync();
        await RecordAsync(clinician, patient);

        foreach (var connection in new[] { App, Read })
        {
            await RefusedAsync(PostgresErrorCodes.InsufficientPrivilege, () => Accounts.ExecuteAsync(connection, clinician,
                "UPDATE ehr.observations SET value = 120 WHERE patient_id = @patient", ("patient", patient)));
            await RefusedAsync(PostgresErrorCodes.InsufficientPrivilege, () => Accounts.ExecuteAsync(connection, clinician,
                "DELETE FROM ehr.observations WHERE patient_id = @patient", ("patient", patient)));
        }

        Assert.Equal(["142.0000"], await AsSuperuserAsync("SELECT value::text FROM ehr.observations WHERE patient_id = @patient", ("patient", patient)));
    }
}
