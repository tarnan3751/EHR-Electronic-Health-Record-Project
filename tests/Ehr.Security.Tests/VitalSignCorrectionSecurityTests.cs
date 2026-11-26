using Ehr.Data.Accounts;
using Ehr.Data.Reference;
using Ehr.Testing;
using Npgsql;
using Xunit;

namespace Ehr.Security.Tests;

// The expansion plan's step 9b: what the database lets each account do to correct a vital sign, and which reference
// range it gives one, as the runtime roles, in SQL of its own (db/policies/10-grants.sql, 30-rls.sql and
// 75-results.sql). A vital sign is never deleted or edited: a clinician with the patient on their panel marks it
// entered in error, once, with a reason, and the database records who and when; the patient stops seeing it; the
// correct value is a new reading. Heart rate, respiratory rate and blood pressure get the range for the patient's
// age on the day they were taken.
public class VitalSignCorrectionSecurityTests(MigratedDatabase database) : IClassFixture<MigratedDatabase>, IAsyncLifetime
{
    const string Systolic = "8480-6";
    const string Diastolic = "8462-4";
    const string HeartRate = "8867-4";
    const string Temperature = "8310-5";
    const string Weight = "29463-7";
    const string Height = "8302-2";
    const string BodyMassIndex = "39156-5";

    string App => database.ConnectionStringFor("ehr_app", MigratedDatabase.AppPassword);

    string Read => database.ConnectionStringFor("ehr_read", MigratedDatabase.ReadPassword);

    // When the test started, by the database's clock, which its rules hold a time to; and ten minutes before.
    DateTime Started { get; set; }

    DateTime Taken => Started.AddMinutes(-10);

    public async ValueTask InitializeAsync() => Started = await database.NowAsync();

    public ValueTask DisposeAsync() => default;

    const string Mark = "UPDATE ehr.observations SET status = 'entered-in-error', entered_in_error_reason = @reason WHERE patient_id = @patient AND code = @code AND status = 'final'";

    async Task<(Guid Clinician, Guid Patient)> OnPanelAsync()
    {
        var clinician = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        var patient = await Accounts.PatientAsync(database, clinician);
        await Accounts.AssignAsync(database, clinician, patient);
        return (clinician, patient);
    }

    // Heart rate unless another sign is named: a blood pressure value is recorded with its partner (RecordPressureAsync).
    Task RecordAsync(Guid clinician, Guid patient, string code = HeartRate, decimal value = 180m, DateTime? at = null) =>
        Accounts.ExecuteAsync(App, clinician,
            "INSERT INTO ehr.observations (id, patient_id, category, code, display, status, value, effective_at) VALUES (gen_random_uuid(), @patient, 'vital-signs', @code, 'x', 'final', @value, @at)",
            ("patient", patient), ("code", code), ("value", value), ("at", at ?? Taken));

    // A blood pressure, its two values in one transaction, as one reading (step 9c).
    Task RecordPressureAsync(Guid clinician, Guid patient, decimal systolic, decimal diastolic) =>
        Accounts.ExecuteAsync(App, clinician,
            """
            INSERT INTO ehr.observations (id, patient_id, category, code, display, status, value, effective_at) VALUES (gen_random_uuid(), @patient, 'vital-signs', '8480-6', 'x', 'final', @systolic, @at);
            INSERT INTO ehr.observations (id, patient_id, category, code, display, status, value, effective_at) VALUES (gen_random_uuid(), @patient, 'vital-signs', '8462-4', 'x', 'final', @diastolic, @at);
            """,
            ("patient", patient), ("systolic", systolic), ("diastolic", diastolic), ("at", Taken));

    Task MarkAsync(Guid? user, Guid patient, string code = HeartRate, string reason = "  Typed 180 for 108.  ", string? connection = null) =>
        Accounts.ExecuteAsync(connection ?? App, user, Mark, ("patient", patient), ("code", code), ("reason", reason));

    Task<List<string>> AsSuperuserAsync(string sql, params (string Name, object Value)[] parameters) =>
        Accounts.QueryAsync(database.SuperuserConnectionString, null, sql, parameters);

    async Task RefusedAsync(string state, Func<Task> attempt) =>
        Assert.Equal(state, (await Assert.ThrowsAsync<PostgresException>(attempt)).SqlState);

    Task<List<string>> StateAsync(Guid patient) => AsSuperuserAsync(
        """
        SELECT code || ' ' || trim_scale(value) || ' ' || status || ' ' || coalesce(entered_in_error_reason, 'none') || ' ' || coalesce(entered_in_error_by::text, 'none')
            || ' ' || coalesce((entered_in_error_at > now() - interval '1 minute')::text, 'none')
        FROM ehr.observations WHERE patient_id = @patient ORDER BY code, issued_at
        """, ("patient", patient));

    [Fact]
    public async Task A_vital_sign_is_marked_entered_in_error_once_with_a_reason_by_the_database_and_the_correct_value_is_a_new_reading()
    {
        var (clinician, patient) = await OnPanelAsync();
        var account = await Accounts.PatientAccountAsync(database, patient);
        await RecordAsync(clinician, patient);
        Assert.Single(await Accounts.QueryAsync(Read, account, "SELECT id::text FROM ehr.observations WHERE patient_id = @patient", ("patient", patient)));

        await MarkAsync(clinician, patient);

        Assert.Equal([$"{HeartRate} 180 entered-in-error Typed 180 for 108. {clinician} true"], await StateAsync(patient));
        // Audited as a change, with the reason; and the patient no longer sees the reading.
        Assert.Single(await AsSuperuserAsync(
            "SELECT id::text FROM ehr.audit_events WHERE table_name = 'observations' AND action = 'update' AND patient_id = @patient AND actor_id = @actor AND details::text LIKE '%Typed 180 for 108.%'",
            ("patient", patient), ("actor", clinician)));
        Assert.Empty(await Accounts.QueryAsync(Read, account, "SELECT id::text FROM ehr.observations WHERE patient_id = @patient", ("patient", patient)));

        // The correct value, for the same moment: a new reading, which the once-only rule lets through.
        await RecordAsync(clinician, patient, value: 108m);
        Assert.Equal(2, (await StateAsync(patient)).Count);
        Assert.Equal(["108.0000"], await Accounts.QueryAsync(Read, account, "SELECT value::text FROM ehr.observations WHERE patient_id = @patient", ("patient", patient)));

        // Nothing unmarks an entry, changes its reason, or marks it twice.
        const string entry = "WHERE patient_id = @patient AND status = 'entered-in-error'";
        await RefusedAsync(PostgresErrorCodes.CheckViolation, () => Accounts.ExecuteAsync(App, clinician, $"UPDATE ehr.observations SET status = 'final' {entry}", ("patient", patient)));
        await RefusedAsync(PostgresErrorCodes.CheckViolation, () => Accounts.ExecuteAsync(App, clinician, $"UPDATE ehr.observations SET entered_in_error_reason = 'Another' {entry}", ("patient", patient)));
        await RefusedAsync(PostgresErrorCodes.CheckViolation, () => Accounts.ExecuteAsync(App, clinician, $"UPDATE ehr.observations SET status = 'entered-in-error', entered_in_error_reason = 'Again' {entry}", ("patient", patient)));
    }

    [Fact]
    public async Task The_one_change_is_to_entered_in_error_with_a_reason_and_no_other_column_moves()
    {
        var (clinician, patient) = await OnPanelAsync();
        await RecordAsync(clinician, patient);
        const string mine = "WHERE patient_id = @patient";

        // Any other status, or no reason, or one too long.
        await RefusedAsync(PostgresErrorCodes.CheckViolation, () => Accounts.ExecuteAsync(App, clinician, $"UPDATE ehr.observations SET status = 'cancelled' {mine}", ("patient", patient)));
        await RefusedAsync(PostgresErrorCodes.CheckViolation, () => Accounts.ExecuteAsync(App, clinician, $"UPDATE ehr.observations SET status = 'entered-in-error' {mine}", ("patient", patient)));
        await RefusedAsync(PostgresErrorCodes.CheckViolation, () => MarkAsync(clinician, patient, reason: "   "));
        await RefusedAsync(PostgresErrorCodes.StringDataRightTruncation, () => MarkAsync(clinician, patient, reason: new string('x', 501)));
        // Who, when, the value and everything else: no privilege at all.
        foreach (var column in new[] { "value = 108", "entered_in_error_by = gen_random_uuid()", "entered_in_error_at = '2000-01-01'", "effective_at = now()", "reference_high = 500", "patient_id = gen_random_uuid()" })
        {
            await RefusedAsync(PostgresErrorCodes.InsufficientPrivilege, () => Accounts.ExecuteAsync(App, clinician, $"UPDATE ehr.observations SET {column} {mine}", ("patient", patient)));
        }

        await RefusedAsync(PostgresErrorCodes.InsufficientPrivilege, () => Accounts.ExecuteAsync(App, clinician, $"DELETE FROM ehr.observations {mine}", ("patient", patient)));
        Assert.Equal([$"{HeartRate} 180 final none none none"], await StateAsync(patient));
    }

    [Fact]
    public async Task Only_a_clinician_with_the_patient_on_their_panel_marks_one_and_never_a_result_of_an_order()
    {
        var (clinician, patient) = await OnPanelAsync();
        await RecordAsync(clinician, patient);
        var outsider = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        var admin = await Accounts.StaffAsync(database, StaffRoles.Admin);
        var account = await Accounts.PatientAccountAsync(database, patient);

        // Row-level security shows the update no row to change: nothing happens, and nothing is refused aloud.
        foreach (var user in new Guid?[] { outsider, admin, account, null })
        {
            await MarkAsync(user, patient);
        }

        await RefusedAsync(PostgresErrorCodes.InsufficientPrivilege, () => MarkAsync(clinician, patient, connection: Read));
        Assert.Equal([$"{HeartRate} 180 final none none none"], await StateAsync(patient));

        // A laboratory result is not corrected this way.
        var order = Guid.CreateVersion7();
        await Accounts.ExecuteAsync(App, clinician,
            "INSERT INTO ehr.orders (id, patient_id, type, catalog_code, details, status) VALUES (@id, @patient, 'lab', 'SYN-LAB-001', '{}', 'active')", ("id", order), ("patient", patient));
        await Accounts.ExecuteAsync(App, clinician,
            "INSERT INTO ehr.observations (id, patient_id, order_id, code, display, status, value, effective_at) VALUES (gen_random_uuid(), @patient, @order, '718-7', 'x', 'final', 13.5, now() - interval '1 hour')",
            ("patient", patient), ("order", order));
        await RefusedAsync(PostgresErrorCodes.CheckViolation, () => MarkAsync(clinician, patient, "718-7"));
    }

    // One statement can't record someone else as the person who marked a reading by changing who is signed in between
    // its rows: the policy holds the row to the person the statement began as.
    [Fact]
    public async Task A_statement_cannot_name_someone_else_as_the_person_who_marked_a_reading()
    {
        var (clinician, patient) = await OnPanelAsync();
        var admin = await Accounts.StaffAsync(database, StaffRoles.Admin);
        await RecordAsync(clinician, patient);
        await RecordAsync(clinician, patient, Temperature, 37m);

        await RefusedAsync(PostgresErrorCodes.InsufficientPrivilege, () => Accounts.ExecuteAsync(App, clinician,
            """
            UPDATE ehr.observations
            SET status = 'entered-in-error',
                entered_in_error_reason = 'Synthetic' || CASE WHEN code = '8310-5' THEN left(set_config('app.user_id', @other, true), 0) ELSE '' END
            WHERE patient_id = @patient
            """, ("patient", patient), ("other", admin.ToString())));
        Assert.All(await StateAsync(patient), state => Assert.EndsWith("final none none none", state));
    }

    [Fact]
    public async Task Marking_a_weight_or_height_marks_the_body_mass_index_worked_out_from_it_and_none_is_worked_out_from_an_entry_in_error()
    {
        var (clinician, patient) = await OnPanelAsync();
        await RecordAsync(clinician, patient, Weight, 81m);
        await RecordAsync(clinician, patient, Height, 180m);
        await RecordAsync(clinician, patient, BodyMassIndex, 1m);

        await MarkAsync(clinician, patient, Weight, "Weighed with a coat on.");

        Assert.Equal(
            [$"{Weight} 81 entered-in-error Weighed with a coat on. {clinician} true", $"{BodyMassIndex} 25 entered-in-error Weighed with a coat on. {clinician} true", $"{Height} 180 final none none none"],
            await StateAsync(patient));
        // With the weight in error, there is nothing to work a new one out from; with the correct weight, there is.
        await RefusedAsync(PostgresErrorCodes.CheckViolation, () => RecordAsync(clinician, patient, BodyMassIndex, 1m));
        await RecordAsync(clinician, patient, Weight, 79m);
        await RecordAsync(clinician, patient, BodyMassIndex, 1m);
        Assert.Contains($"{BodyMassIndex} 24.4 final none none none", await StateAsync(patient));
    }

    // Steps 9b and 9d: Fleming's centiles for heart rate, the AAP's Table 6 for a child's blood pressure (a screening
    // value; none under 1 year), the adults' ranges from 18. The patient's sex is unknown, so a child's blood pressure
    // is held to the lower of the boys' and girls' values.
    [Theory]
    [InlineData(4, AgeBand.Infant, "104-175 normal", "none-none normal", "none-none normal")]
    [InlineData(40, AgeBand.YoungChild, "70-136 normal", "none-101 screening", "none-58 screening")]
    [InlineData(100, AgeBand.OlderChild, "52-115 normal", "none-107 screening", "none-69 screening")]
    [InlineData(180, AgeBand.Adolescent, "43-104 normal", "none-120 screening", "none-80 screening")]
    [InlineData(480, AgeBand.Adult, "60-100 normal", "90-129 normal", "60-79 normal")]
    public async Task A_vital_sign_gets_the_reference_range_for_the_patients_age_on_the_day_it_was_taken(int months, string band, string heartRate, string systolic, string diastolic)
    {
        Assert.Equal(band, AgeBand.Of(months));
        var (clinician, patient) = await OnPanelAsync();
        await Accounts.ExecuteAsync(database.SuperuserConnectionString, null,
            "UPDATE ehr.patients SET birth_date = (now() - make_interval(months => @months, days => 3))::date WHERE id = @patient", ("months", months), ("patient", patient));

        await RecordAsync(clinician, patient, HeartRate, 90m);
        await RecordPressureAsync(clinician, patient, 100m, 60m);
        // Temperature has one range at every age.
        await RecordAsync(clinician, patient, "8310-5", 37m);

        Assert.Equal([$"8310-5 36.1-37.5 normal", $"{Diastolic} {diastolic}", $"{Systolic} {systolic}", $"{HeartRate} {heartRate}"], await AsSuperuserAsync(
            """
            SELECT code || ' ' || coalesce(trim_scale(reference_low)::text, 'none') || '-' || coalesce(trim_scale(reference_high)::text, 'none') || ' ' || reference_kind
            FROM ehr.observations WHERE patient_id = @patient ORDER BY code
            """, ("patient", patient)));
    }

    // The age is the one on the day the sign was taken, not today's: a reading from before a birthday keeps the
    // younger age's range.
    [Fact]
    public async Task The_age_is_the_one_on_the_day_the_sign_was_taken()
    {
        var (clinician, patient) = await OnPanelAsync();
        // Six years and a month old today, in Fleming's 6 to 8 years; two months ago in its 4 to 6 years.
        await Accounts.ExecuteAsync(database.SuperuserConnectionString, null,
            "UPDATE ehr.patients SET birth_date = (now() - interval '6 years 1 month')::date WHERE id = @patient", ("patient", patient));

        await RecordAsync(clinician, patient, HeartRate, 90m, Started.AddDays(-62));
        await RecordAsync(clinician, patient, HeartRate, 90m);

        Assert.Equal(["65-131", "59-123"], await AsSuperuserAsync(
            "SELECT trim_scale(reference_low) || '-' || trim_scale(reference_high) FROM ehr.observations WHERE patient_id = @patient ORDER BY effective_at", ("patient", patient)));
    }

    // db/reference/35-vital-ranges.sql and Ehr.Data.Reference.VitalRanges (which the seed uses) are the same rows.
    // VitalRangeSecurityTests holds both to the published tables.
    [Fact]
    public async Task The_reference_file_and_the_ranges_in_code_are_the_same()
    {
        var expected = VitalRanges.All
            .Select(r => $"{r.Code} {r.Sex} {r.FromMonths} {r.ToMonths?.ToString() ?? "none"} {r.Low?.ToString("0") ?? "none"} {r.High?.ToString("0") ?? "none"} {r.Kind} {r.Source}")
            .Order(StringComparer.Ordinal);

        Assert.Equal(expected, (await AsSuperuserAsync(
            """
            SELECT code || ' ' || sex || ' ' || from_months || ' ' || coalesce(to_months::text, 'none') || ' ' || coalesce(trim_scale(low)::text, 'none') || ' '
                || coalesce(trim_scale(high)::text, 'none') || ' ' || kind || ' ' || source
            FROM ehr.vital_reference_ranges
            """)).Order(StringComparer.Ordinal));
    }
}
