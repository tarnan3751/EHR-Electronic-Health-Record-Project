using Ehr.Data.Accounts;
using Ehr.Data.Reference;
using Ehr.Testing;
using Npgsql;
using Xunit;

namespace Ehr.Security.Tests;

// The expansion plan's step 9d: children's vital-sign ranges from the primary sources, as the database holds them.
// The rows db/reference/35-vital-ranges.sql loads and the ranges in code (VitalRanges) are each held to a copy of the
// published tables typed here from them, so a slip in either fails. ehr.file_result gives a vital sign the range for
// the patient's age in whole months on the day it was taken, which changes on the birthday and not before, and for a
// child's blood pressure the patient's sex: a patient whose sex is other or unknown gets the lower of the boys' and
// girls' values. A child's blood pressure range is a screening value. The app chooses none of it.
public class VitalRangeSecurityTests(MigratedDatabase database) : IClassFixture<MigratedDatabase>
{
    // Fleming S et al., Lancet 2011, Web Appendix, page 12: each age group's first month and the month it ends before,
    // then Web Table 4's (respiratory rate) and Web Table 5's (heart rate) 1st and 99th centiles, as printed.
    static readonly (int From, int To, int BreathingLow, int BreathingHigh, int HeartLow, int HeartHigh)[] Fleming =
    [
        (0, 3, 25, 66, 107, 181), (3, 6, 24, 64, 104, 175), (6, 9, 23, 61, 98, 168), (9, 12, 22, 58, 93, 161), (12, 18, 21, 53, 88, 156),
        (18, 24, 19, 46, 82, 149), (24, 36, 18, 38, 76, 142), (36, 48, 17, 33, 70, 136), (48, 72, 17, 29, 65, 131), (72, 96, 16, 27, 59, 123),
        (96, 144, 14, 25, 52, 115), (144, 180, 12, 23, 47, 108), (180, 216, 11, 22, 43, 104),
    ];

    // Flynn JT et al., Pediatrics 2017, Table 6, page 15: age in years (13 for "13 and over"), then the boys' systolic and
    // diastolic values and the girls', as printed.
    static readonly (int Age, int BoysSystolic, int BoysDiastolic, int GirlsSystolic, int GirlsDiastolic)[] Table6 =
    [
        (1, 98, 52, 98, 54), (2, 100, 55, 101, 58), (3, 101, 58, 102, 60), (4, 102, 60, 103, 62), (5, 103, 63, 104, 64), (6, 105, 66, 105, 67),
        (7, 106, 68, 106, 68), (8, 107, 69, 107, 69), (9, 107, 70, 108, 71), (10, 108, 72, 109, 72), (11, 110, 74, 111, 74), (12, 113, 75, 114, 75),
        (13, 120, 80, 120, 80),
    ];

    // Every row as "code sex from to low high kind": the published tables, a year of age to each of Table 6's rows (13
    // and over to 18), and the adults' ranges, which step 9d leaves as they were.
    static IEnumerable<string> Published()
    {
        foreach (var group in Fleming)
        {
            yield return $"8867-4 any {group.From} {group.To} {group.HeartLow} {group.HeartHigh} normal";
            yield return $"9279-1 any {group.From} {group.To} {group.BreathingLow} {group.BreathingHigh} normal";
        }

        foreach (var row in Table6)
        {
            var (from, to) = (row.Age * 12, row.Age == 13 ? 216 : (row.Age + 1) * 12);
            yield return $"8480-6 male {from} {to} none {row.BoysSystolic} screening";
            yield return $"8462-4 male {from} {to} none {row.BoysDiastolic} screening";
            yield return $"8480-6 female {from} {to} none {row.GirlsSystolic} screening";
            yield return $"8462-4 female {from} {to} none {row.GirlsDiastolic} screening";
        }

        yield return "8867-4 any 216 none 60 100 normal";
        yield return "9279-1 any 216 none 12 20 normal";
        yield return "8480-6 any 216 none 90 129 normal";
        yield return "8462-4 any 216 none 60 79 normal";
    }

    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    string App => database.ConnectionStringFor("ehr_app", MigratedDatabase.AppPassword);

    Task<List<string>> AsSuperuserAsync(string sql, params (string Name, object Value)[] parameters) =>
        Accounts.QueryAsync(database.SuperuserConnectionString, null, sql, parameters);

    // A patient of this sex and birth date on a new clinician's panel.
    async Task<(Guid Clinician, Guid Patient)> ChildAsync(DateOnly born, string sex)
    {
        var clinician = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        var patient = await Accounts.PatientAsync(database, clinician);
        await Accounts.AssignAsync(database, clinician, patient);
        await Accounts.ExecuteAsync(database.SuperuserConnectionString, null,
            "UPDATE ehr.patients SET birth_date = @born, gender = @sex WHERE id = @patient", ("born", born), ("sex", sex), ("patient", patient));
        return (clinician, patient);
    }

    // A value as the app records one, with a range and kind of the client's own that the database replaces; a blood
    // pressure as its two values.
    Task RecordAsync(Guid clinician, Guid patient, string code, DateTime at, decimal value = 100m)
    {
        const string insert = """
            INSERT INTO ehr.observations (id, patient_id, category, code, display, status, value, reference_low, reference_high, reference_kind, effective_at)
            VALUES (gen_random_uuid(), @patient, 'vital-signs', {0}, 'x', 'final', {1}, 0, 1000, 'normal', @at);
            """;
        var sql = code is "8480-6" or "8462-4"
            ? string.Format(insert, "'8480-6'", "@value") + string.Format(insert, "'8462-4'", "@value - 40")
            : string.Format(insert, "@code", "@value");
        return Accounts.ExecuteAsync(App, clinician, sql, ("patient", patient), ("code", code), ("at", at), ("value", value));
    }

    const string Ranges = """
        SELECT coalesce(trim_scale(reference_low)::text, 'none') || '-' || coalesce(trim_scale(reference_high)::text, 'none') || ' ' || reference_kind
        FROM ehr.observations WHERE patient_id = @patient AND code = @code ORDER BY effective_at
        """;

    [Fact]
    public async Task The_ranges_in_the_database_and_in_code_are_the_published_tables()
    {
        var published = Published().Order(StringComparer.Ordinal).ToList();
        Assert.Equal(82, published.Count);

        Assert.Equal(published, (await AsSuperuserAsync(
            """
            SELECT code || ' ' || sex || ' ' || from_months || ' ' || coalesce(to_months::text, 'none') || ' ' || coalesce(trim_scale(low)::text, 'none') || ' '
                || trim_scale(high) || ' ' || kind
            FROM ehr.vital_reference_ranges
            """)).Order(StringComparer.Ordinal));
        Assert.Equal(published, VitalRanges.All
            .Select(r => $"{r.Code} {r.Sex} {r.FromMonths} {r.ToMonths?.ToString() ?? "none"} {r.Low?.ToString("0") ?? "none"} {r.High!.Value:0} {r.Kind}")
            .Order(StringComparer.Ordinal));

        // For a boy and a girl at every month to 25 years: exactly one row of each sign, except a blood pressure under
        // 1 year, which has none.
        Assert.Empty(await AsSuperuserAsync(
            """
            SELECT c.code || ' ' || s.sex || ' ' || m.months || ' ' || count(r.code)
            FROM (VALUES ('8867-4'), ('9279-1'), ('8480-6'), ('8462-4')) c(code)
            CROSS JOIN (VALUES ('male'), ('female')) s(sex)
            CROSS JOIN generate_series(0, 300) m(months)
            LEFT JOIN ehr.vital_reference_ranges r ON r.code = c.code AND (r.sex = 'any' OR r.sex = s.sex)
                AND m.months >= r.from_months AND (r.to_months IS NULL OR m.months < r.to_months)
            GROUP BY c.code, s.sex, m.months
            HAVING count(r.code) <> CASE WHEN c.code IN ('8480-6', '8462-4') AND m.months < 12 THEN 0 ELSE 1 END
            """));
    }

    // Each kind of boundary: the day before the birthday and the day of it.
    [Theory]
    [InlineData("8867-4", 3, "unknown", "107-181 normal", "104-175 normal")]
    [InlineData("9279-1", 24, "unknown", "19-46 normal", "18-38 normal")]
    [InlineData("9279-1", 180, "unknown", "12-23 normal", "11-22 normal")]
    [InlineData("8867-4", 216, "unknown", "43-104 normal", "60-100 normal")]
    [InlineData("8480-6", 12, "male", "none-none normal", "none-98 screening")]
    [InlineData("8462-4", 24, "female", "none-54 screening", "none-58 screening")]
    [InlineData("8480-6", 156, "male", "none-113 screening", "none-120 screening")]
    [InlineData("8480-6", 216, "female", "none-120 screening", "90-129 normal")]
    [InlineData("8462-4", 216, "male", "none-80 screening", "60-79 normal")]
    public async Task A_range_changes_on_the_birthday_and_not_before(string code, int months, string sex, string before, string on)
    {
        // A birthday on the 15th of a month gone by, so that no month is too short for the day.
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var birthday = new DateOnly(today.Year, today.Month, 15).AddMonths(-2);
        var (clinician, patient) = await ChildAsync(birthday.AddMonths(-months), sex);
        var noon = new TimeOnly(12, 0);

        await RecordAsync(clinician, patient, code, birthday.AddDays(-1).ToDateTime(noon, DateTimeKind.Utc));
        await RecordAsync(clinician, patient, code, birthday.ToDateTime(noon, DateTimeKind.Utc));

        Assert.Equal([before, on], await AsSuperuserAsync(Ranges, ("patient", patient), ("code", code)));
    }

    // Table 6 by sex: at 1 year a boy's diastolic value is 52 and a girl's 54, at 2 a boy's systolic 100 and a girl's
    // 101. A patient whose sex is other or unknown is held to the lower, flagged sooner.
    [Theory]
    [InlineData("male", "98/52 100/55")]
    [InlineData("female", "98/54 101/58")]
    [InlineData("other", "98/52 100/55")]
    [InlineData("unknown", "98/52 100/55")]
    public async Task A_childs_blood_pressure_goes_by_sex_and_other_or_unknown_get_the_lower_value(string sex, string expected)
    {
        var now = await database.NowAsync();
        var (clinician, patient) = await ChildAsync(DateOnly.FromDateTime(now).AddMonths(-30), sex);
        await RecordAsync(clinician, patient, "8480-6", now.AddMonths(-12));
        await RecordAsync(clinician, patient, "8480-6", now.AddMinutes(-10));

        Assert.Equal([expected], await AsSuperuserAsync(
            """
            SELECT string_agg(trim_scale(s.reference_high) || '/' || trim_scale(d.reference_high), ' ' ORDER BY s.effective_at)
            FROM ehr.observations s JOIN ehr.observations d ON d.reading_id = s.reading_id AND d.code = '8462-4'
            WHERE s.patient_id = @patient AND s.code = '8480-6' AND s.reference_kind = 'screening' AND d.reference_kind = 'screening'
            """, ("patient", patient)));
    }

    [Fact]
    public async Task The_range_and_its_kind_are_the_databases_whatever_the_app_sends()
    {
        // A child's blood pressure sent with a normal range of the client's own; an adult's heart rate sent as a
        // screening value; a result of an order sent as a screening value.
        // Five years old on the day the reading is taken, which is the day before today in the first ten minutes of one.
        var taken = (await database.NowAsync()).AddMinutes(-10);
        var (clinician, child) = await ChildAsync(DateOnly.FromDateTime(taken).AddYears(-5), "female");
        await RecordAsync(clinician, child, "8480-6", taken);
        var adult = await Accounts.PatientAsync(database, clinician);
        await Accounts.AssignAsync(database, clinician, adult);
        await Accounts.ExecuteAsync(App, clinician,
            """
            INSERT INTO ehr.observations (id, patient_id, category, code, display, status, value, reference_low, reference_high, reference_kind, effective_at)
            VALUES (gen_random_uuid(), @patient, 'vital-signs', '8867-4', 'x', 'final', 72, null, 75, 'screening', now() - interval '10 minutes')
            """, ("patient", adult));
        var order = Guid.CreateVersion7();
        await Accounts.ExecuteAsync(App, clinician,
            "INSERT INTO ehr.orders (id, patient_id, type, catalog_code, details, status) VALUES (@id, @patient, 'lab', 'SYN-LAB-001', '{}', 'active')", ("id", order), ("patient", adult));
        await Accounts.ExecuteAsync(App, clinician,
            """
            INSERT INTO ehr.observations (id, patient_id, order_id, code, display, status, value, reference_kind, effective_at)
            VALUES (gen_random_uuid(), @patient, @order, '718-7', 'x', 'final', 13.5, 'screening', now() - interval '1 hour')
            """, ("patient", adult), ("order", order));

        Assert.Equal(["8462-4 none-64 screening", "8480-6 none-104 screening"], await AsSuperuserAsync(
            "SELECT code || ' ' || coalesce(trim_scale(reference_low)::text, 'none') || '-' || trim_scale(reference_high) || ' ' || reference_kind FROM ehr.observations WHERE patient_id = @patient ORDER BY code",
            ("patient", child)));
        Assert.Equal(["718-7 normal", "8867-4 60-100 normal"], await AsSuperuserAsync(
            "SELECT code || ' ' || CASE WHEN code = '8867-4' THEN trim_scale(reference_low) || '-' || trim_scale(reference_high) || ' ' ELSE '' END || reference_kind FROM ehr.observations WHERE patient_id = @patient ORDER BY code",
            ("patient", adult)));

        // A kind the table doesn't know, or a screening value with a low, is refused even past the apps; and the app's
        // role can't change the ranges at all.
        async Task RefusedAsync(string state, Func<Task> attempt) => Assert.Equal(state, (await Assert.ThrowsAsync<PostgresException>(attempt)).SqlState);
        await RefusedAsync(PostgresErrorCodes.CheckViolation, () => Accounts.ExecuteAsync(database.SuperuserConnectionString, null,
            "UPDATE ehr.observations SET reference_kind = 'abnormal' WHERE patient_id = @patient AND code = '8867-4'", ("patient", adult)));
        await RefusedAsync(PostgresErrorCodes.CheckViolation, () => Accounts.ExecuteAsync(database.SuperuserConnectionString, null,
            "UPDATE ehr.vital_reference_ranges SET low = 1 WHERE code = '8480-6' AND sex = 'male' AND from_months = 12"));
        await RefusedAsync(PostgresErrorCodes.InsufficientPrivilege, () => Accounts.ExecuteAsync(App, clinician,
            "UPDATE ehr.vital_reference_ranges SET high = 200 WHERE code = '8480-6'"));
        await RefusedAsync(PostgresErrorCodes.InsufficientPrivilege, () => Accounts.ExecuteAsync(App, clinician,
            "UPDATE ehr.observations SET reference_kind = 'screening' WHERE patient_id = @patient", ("patient", adult)));
    }
}
