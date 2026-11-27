using Ehr.Data.Accounts;
using Ehr.Migrations;
using Ehr.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;
using static Ehr.Testing.TestRun;

namespace Ehr.Security.Tests;

// The expansion plan's step 9d: the ChildrenRanges migration gives every heart rate, respiratory rate and blood
// pressure already recorded the range for the patient's age and sex from the primary sources, by hand, as the tables'
// owner. On a database in use, with children's readings ranged as step 9b left them, this proves each kind of change,
// the counts the migration reports (which the migration job prints), that nothing is audited and the protection is back
// afterwards, and that it goes down to the earlier ranges and up again. A database of its own: it migrates down and up.
public class ChildrenRangesMigrationTests(MigratedDatabase database) : IClassFixture<MigratedDatabase>
{
    const string Before = "VitalReadings";

    static readonly DateTime Taken = new(2026, 11, 1, 9, 0, 0, DateTimeKind.Utc);

    // As the migration job does it (EhrDbContextFactory): what this step's migration reports.
    async Task<List<string>> MigrateAsync(string? target)
    {
        var output = new StringWriter();
        await using (var context = EhrDbContextFactory.Create(database.OwnerConnectionString, output))
        {
            var name = target is null ? null : context.Database.GetMigrations().Single(migration => migration.EndsWith("_" + target, StringComparison.Ordinal));
            await context.GetService<IMigrator>().MigrateAsync(name, Cancel);
        }

        return [.. output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Where(line => line.StartsWith("Migration 9d:", StringComparison.Ordinal))];
    }

    Task<List<string>> AsSuperuserAsync(string sql, params (string Name, object Value)[] parameters) =>
        Accounts.QueryAsync(database.SuperuserConnectionString, null, sql, parameters);

    async Task<Guid> PatientAsync(Guid clinician, DateOnly born, string sex)
    {
        var patient = await Accounts.PatientAsync(database, clinician);
        await Accounts.ExecuteAsync(database.SuperuserConnectionString, null,
            "UPDATE ehr.patients SET birth_date = @born, gender = @sex WHERE id = @patient", ("born", born), ("sex", sex), ("patient", patient));
        return patient;
    }

    // A value as step 9b ranged it, before this step: low and high, no kind.
    Task InsertAsync(Guid patient, string code, decimal value, decimal? low, decimal? high) =>
        Accounts.ExecuteAsync(database.SuperuserConnectionString, null,
            """
            INSERT INTO ehr.observations (id, patient_id, category, code, display, status, value, reference_low, reference_high, effective_at, issued_at)
            VALUES (gen_random_uuid(), @patient, 'vital-signs', @code, 'Synthetic', 'final', @value, @low, @high, @taken, @taken)
            """,
            ("patient", patient), ("code", code), ("value", value), ("low", (object?)low ?? DBNull.Value), ("high", (object?)high ?? DBNull.Value), ("taken", Taken));

    // A blood pressure, one reading (step 9c), as step 9b ranged it; standing, or entered in error.
    Task InsertPressureAsync(Guid patient, decimal systolic, decimal? systolicLow, decimal? systolicHigh, decimal diastolic, decimal? diastolicLow, decimal? diastolicHigh,
        string status = "final") =>
        Accounts.ExecuteAsync(database.SuperuserConnectionString, null,
            """
            INSERT INTO ehr.observations (id, patient_id, reading_id, category, code, display, status, value, reference_low, reference_high, effective_at, issued_at)
            VALUES (gen_random_uuid(), @patient, @reading, 'vital-signs', '8480-6', 'Synthetic', @status, @systolic, @systolic_low, @systolic_high, @taken, @taken),
                   (gen_random_uuid(), @patient, @reading, 'vital-signs', '8462-4', 'Synthetic', @status, @diastolic, @diastolic_low, @diastolic_high, @taken, @taken)
            """,
            ("patient", patient), ("reading", Guid.CreateVersion7()), ("taken", Taken), ("status", status), ("systolic", systolic), ("systolic_low", (object?)systolicLow ?? DBNull.Value),
            ("systolic_high", (object?)systolicHigh ?? DBNull.Value), ("diastolic", diastolic), ("diastolic_low", (object?)diastolicLow ?? DBNull.Value),
            ("diastolic_high", (object?)diastolicHigh ?? DBNull.Value));

    // "code value low-high kind" for each of the patient's vital signs, by code.
    Task<List<string>> RangesAsync(Guid patient, bool kind = true) => AsSuperuserAsync(
        $"""
        SELECT code || ' ' || trim_scale(value) || ' ' || coalesce(trim_scale(reference_low)::text, 'none') || '-' || coalesce(trim_scale(reference_high)::text, 'none')
            {(kind ? "|| ' ' || reference_kind" : "")}
        FROM ehr.observations WHERE patient_id = @patient ORDER BY code
        """, ("patient", patient));

    const string Protection = """
        SELECT c.relname || ' forced=' || c.relforcerowsecurity || ' audit=' || (SELECT t.tgenabled::text FROM pg_trigger t WHERE t.tgrelid = c.oid AND t.tgname = c.relname || '_audit')
        FROM pg_class c WHERE c.oid = 'ehr.observations'::regclass
        """;

    static readonly string[] Report =
    [
        "Migration 9d: heart rate (8867-4): 2 standing values re-ranged",
        "Migration 9d: heart rate (8867-4): 1 newly flagged (1 High)",
        "Migration 9d: heart rate (8867-4): 0 no longer flagged",
        "Migration 9d: heart rate (8867-4): 0 changed from one flag to another",
        "Migration 9d: respiratory rate (9279-1): 1 standing values re-ranged",
        "Migration 9d: respiratory rate (9279-1): 1 newly flagged (1 High)",
        "Migration 9d: respiratory rate (9279-1): 0 no longer flagged",
        "Migration 9d: respiratory rate (9279-1): 0 changed from one flag to another",
        "Migration 9d: systolic blood pressure (8480-6): 5 standing values re-ranged",
        "Migration 9d: systolic blood pressure (8480-6): 2 newly flagged (2 needs follow-up)",
        "Migration 9d: systolic blood pressure (8480-6): 1 no longer flagged (1 Low)",
        "Migration 9d: systolic blood pressure (8480-6): 1 changed from one flag to another (1 High to needs follow-up)",
        "Migration 9d: diastolic blood pressure (8462-4): 4 standing values re-ranged",
        "Migration 9d: diastolic blood pressure (8462-4): 2 newly flagged (2 needs follow-up)",
        "Migration 9d: diastolic blood pressure (8462-4): 0 no longer flagged",
        "Migration 9d: diastolic blood pressure (8462-4): 1 changed from one flag to another (1 High to needs follow-up)",
        "Migration 9d: blood pressure: 5 standing readings re-ranged; 2 newly flagged, 1 no longer flagged, 1 changed from one flag to another",
        "Migration 9d: in all, 12 standing values re-ranged; 6 newly flagged, 1 no longer flagged, 2 changed from one flag to another",
        "Migration 9d: 2 values entered in error re-ranged too, which no page flags",
    ];

    [Fact]
    public async Task The_migration_ranges_childrens_readings_from_the_sources_and_reports_how_their_flags_changed()
    {
        var clinician = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        Assert.Empty(await MigrateAsync(Before));

        // As step 9b ranged them, all taken on 1 November 2026. A boy of 3 at 103/59, inside his band then and at or
        // above Table 6's 101/58 now.
        var boy = await PatientAsync(clinician, new DateOnly(2023, 8, 1), "male");
        await InsertPressureAsync(boy, 103, 72, 103, 59, null, 63);
        // A girl of 8 at 115/75, High then (82-110, up to 73) and at or above her 107/69 now.
        var girl = await PatientAsync(clinician, new DateOnly(2018, 5, 1), "female");
        await InsertPressureAsync(girl, 115, 82, 110, 75, null, 73);
        // An infant of 6 months at 65/40: the systolic Low then (the PALS rule's 70), no range now.
        var infant = await PatientAsync(clinician, new DateOnly(2026, 5, 1), "unknown");
        await InsertPressureAsync(infant, 65, 70, null, 40, null, null);
        // A child of 5 with a heart rate of 140 (66-173 then, Fleming's 65-131 now), and one of 10 breathing 30 (13-46
        // then, 14-25 now).
        var five = await PatientAsync(clinician, new DateOnly(2021, 7, 1), "unknown");
        await InsertAsync(five, "8867-4", 140, 66, 173);
        var ten = await PatientAsync(clinician, new DateOnly(2016, 9, 1), "unknown");
        await InsertAsync(ten, "9279-1", 30, 13, 46);
        // A child of 1 whose sex is other, at 99/53: under the boys' 98/52 and the girls' 98/54, the lower counts.
        var other = await PatientAsync(clinician, new DateOnly(2025, 5, 1), "other");
        await InsertPressureAsync(other, 99, 72, 103, 53, null, 63);
        // A boy of 17 at 118/78 and 70: ranged again, with no flag either way.
        var teen = await PatientAsync(clinician, new DateOnly(2009, 2, 1), "male");
        await InsertPressureAsync(teen, 118, 90, 119, 78, null, 79);
        await InsertAsync(teen, "8867-4", 70, 48, 138);
        // A boy of 5 whose 112/70 was marked entered in error: given the new range too, but no page flags it, so it is
        // counted apart.
        var marked = await PatientAsync(clinician, new DateOnly(2021, 6, 1), "male");
        await InsertPressureAsync(marked, 112, 72, 103, 70, null, 63, status: "entered-in-error");
        // An adult, whose ranges don't change.
        var adult = await PatientAsync(clinician, new DateOnly(1980, 1, 1), "female");
        await InsertPressureAsync(adult, 120, 90, 129, 80, 60, 79);
        await InsertAsync(adult, "8867-4", 72, 60, 100);

        var audited = await AsSuperuserAsync("SELECT count(*)::text FROM ehr.audit_events");
        Assert.Equal(Report, await MigrateAsync(null));

        Assert.Equal(["8462-4 59 none-58 screening", "8480-6 103 none-101 screening"], await RangesAsync(boy));
        Assert.Equal(["8462-4 75 none-69 screening", "8480-6 115 none-107 screening"], await RangesAsync(girl));
        Assert.Equal(["8462-4 40 none-none normal", "8480-6 65 none-none normal"], await RangesAsync(infant));
        Assert.Equal(["8867-4 140 65-131 normal"], await RangesAsync(five));
        Assert.Equal(["9279-1 30 14-25 normal"], await RangesAsync(ten));
        Assert.Equal(["8462-4 53 none-52 screening", "8480-6 99 none-98 screening"], await RangesAsync(other));
        Assert.Equal(["8462-4 78 none-80 screening", "8480-6 118 none-120 screening", "8867-4 70 43-104 normal"], await RangesAsync(teen));
        Assert.Equal(["8462-4 70 none-63 screening", "8480-6 112 none-103 screening"], await RangesAsync(marked));
        Assert.Equal(["8462-4 80 60-79 normal", "8480-6 120 90-129 normal", "8867-4 72 60-100 normal"], await RangesAsync(adult));
        // The ranges themselves, as db/reference loads them; no audit event; the protection back.
        Assert.Equal(["82"], await AsSuperuserAsync("SELECT count(*)::text FROM ehr.vital_reference_ranges"));
        Assert.Equal(audited, await AsSuperuserAsync("SELECT count(*)::text FROM ehr.audit_events"));
        Assert.Equal(["observations forced=true audit=O"], await AsSuperuserAsync(Protection));

        // Down: the five bands' ranges come back, the range table is emptied for the earlier release's db/reference,
        // and nothing is left of the kind. Up again: the same changes, counted the same.
        Assert.Empty(await MigrateAsync(Before));
        Assert.Equal(["8462-4 59 none-63", "8480-6 103 72-103"], await RangesAsync(boy, kind: false));
        Assert.Equal(["8462-4 40 none-none", "8480-6 65 70-none"], await RangesAsync(infant, kind: false));
        Assert.Equal(["0", "0"], await AsSuperuserAsync(
            """
            SELECT count(*)::text FROM ehr.vital_reference_ranges
            UNION ALL SELECT count(*)::text FROM information_schema.columns WHERE table_schema = 'ehr' AND table_name = 'observations' AND column_name = 'reference_kind'
            """));
        Assert.Equal(["observations forced=true audit=O"], await AsSuperuserAsync(Protection));
        Assert.Equal(Report, await MigrateAsync(null));

        // And db/policies and db/reference apply over the migrated rows, as the migration job does next.
        await database.ApplyAsync();
        Assert.Equal(["82"], await AsSuperuserAsync("SELECT count(*)::text FROM ehr.vital_reference_ranges"));
        Assert.Equal(audited, await AsSuperuserAsync("SELECT count(*)::text FROM ehr.audit_events"));
    }
}
