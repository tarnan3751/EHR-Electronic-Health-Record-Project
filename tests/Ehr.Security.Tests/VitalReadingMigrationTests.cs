using Ehr.Data.Accounts;
using Ehr.Migrations;
using Ehr.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Ehr.Security.Tests;

// The expansion plan's step 9c: the VitalReadings migration puts the vital signs already recorded in the shape the new
// rules need, by hand, as the tables' owner. On a database in use (policies applied, as after any deploy), with rows
// shaped as steps 9 and 9b could leave them, this proves each kind of change and its count in the migration's report,
// that each change to patient data is an audit event and the reading IDs are not, that the protection is back
// afterwards, and that the migration goes down and up again. A database of its own: it migrates down and up.
public class VitalReadingMigrationTests(MigratedDatabase database) : IClassFixture<MigratedDatabase>
{
    const string Before = "InheritedSensitivity";

    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    // As the migration job does it (EhrDbContextFactory): what this step's migration reports comes back, without what
    // the database says about anything else on the way (EF's history table, for one).
    async Task<List<string>> MigrateAsync(string? target)
    {
        var output = new StringWriter();
        await using (var context = EhrDbContextFactory.Create(database.OwnerConnectionString, output))
        {
            var name = target is null ? null : context.Database.GetMigrations().Single(migration => migration.EndsWith("_" + target, StringComparison.Ordinal));
            await context.GetService<IMigrator>().MigrateAsync(name, Cancel);
        }

        return [.. output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Where(line => line.StartsWith("Migration 9c:", StringComparison.Ordinal))];
    }

    Task<List<string>> AsSuperuserAsync(string sql, params (string Name, object Value)[] parameters) =>
        Accounts.QueryAsync(database.SuperuserConnectionString, null, sql, parameters);

    // A vital sign as steps 9 and 9b stored one, before readings: recorded `minutes` after the moment it was taken; in
    // error, `markedAgo` minutes ago, with the reason given.
    Task InsertAsync(Guid patient, string? code, decimal value, int minutes, Guid? visit = null, bool inError = false, string reason = "Synthetic: typed wrong", int markedAgo = 0) =>
        Accounts.ExecuteAsync(database.SuperuserConnectionString, null,
            """
            INSERT INTO ehr.observations (id, patient_id, encounter_id, category, code, display, status, value, effective_at, issued_at, entered_in_error_reason, entered_in_error_at)
            VALUES (gen_random_uuid(), @patient, @visit, 'vital-signs', @code, 'Synthetic', CASE WHEN @in_error THEN 'entered-in-error' ELSE 'final' END, @value,
                @taken, @taken + make_interval(mins => @minutes), CASE WHEN @in_error THEN @reason END, CASE WHEN @in_error THEN now() - make_interval(mins => @marked_ago) END)
            """,
            ("patient", patient), ("visit", (object?)visit ?? DBNull.Value), ("code", (object?)code ?? DBNull.Value), ("value", value), ("minutes", minutes),
            ("in_error", inError), ("reason", reason), ("marked_ago", markedAgo), ("taken", Taken));

    static readonly DateTime Taken = new(2026, 11, 1, 9, 0, 0, DateTimeKind.Utc);

    async Task<Guid> VisitAsync(Guid patient)
    {
        var visit = Guid.CreateVersion7();
        await Accounts.ExecuteAsync(database.SuperuserConnectionString, null,
            "INSERT INTO ehr.encounters (id, patient_id, status, class_code, period_start) VALUES (@id, @patient, 'finished', 'AMB', @start)",
            ("id", visit), ("patient", patient), ("start", Taken.AddMinutes(5)));
        return visit;
    }

    // "code value status reason visit reading" for each of the patient's vital signs, in the order recorded: the visit
    // as "visit" or "none", each reading as R1, R2... in the order readings first appear.
    async Task<List<string>> StateAsync(Guid patient)
    {
        var rows = await AsSuperuserAsync(
            """
            SELECT code || '|' || trim_scale(value) || '|' || status || '|' || coalesce(entered_in_error_reason, 'none') || '|' || CASE WHEN encounter_id IS NULL THEN 'none' ELSE 'visit' END
                || '|' || coalesce(reading_id::text, 'none')
            FROM ehr.observations WHERE patient_id = @patient ORDER BY issued_at, code
            """, ("patient", patient));
        var readings = new List<string>();
        return [.. rows.Select(row =>
        {
            var parts = row.Split('|');
            if (parts[5] != "none")
            {
                if (!readings.Contains(parts[5]))
                {
                    readings.Add(parts[5]);
                }

                parts[5] = $"R{readings.IndexOf(parts[5]) + 1}";
            }

            return string.Join(' ', parts);
        })];
    }

    const string Protection = """
        SELECT c.relname || ' forced=' || c.relforcerowsecurity
            || ' audit=' || (SELECT t.tgenabled::text FROM pg_trigger t WHERE t.tgrelid = c.oid AND t.tgname = c.relname || '_audit')
            || ' body_mass_index=' || (SELECT t.tgenabled::text FROM pg_trigger t WHERE t.tgrelid = c.oid AND t.tgname = 'observations_mark_body_mass_index')
        FROM pg_class c WHERE c.oid = 'ehr.observations'::regclass
        """;

    const string Duplicate = "duplicate resolved by migration 9c";
    const string Partner = "partner value was marked in error before readings existed (migration 9c)";
    const string Unpaired = "blood pressure value without its partner (migration 9c)";
    const string Index = "body mass index of a value marked as a duplicate (migration 9c)";

    [Fact]
    public async Task The_migration_puts_existing_vital_signs_into_readings_marks_what_cannot_stand_audited_and_reports_each_count()
    {
        var clinician = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        async Task<Guid> PatientAsync() => await Accounts.PatientAsync(database, clinician);
        Assert.Empty(await MigrateAsync(Before));
        Assert.Equal(["0"], await AsSuperuserAsync("SELECT count(*)::text FROM information_schema.columns WHERE table_schema = 'ehr' AND table_name = 'observations' AND column_name = 'reading_id'"));

        // Failure A of step 9c: 120/80 with no visit, the diastolic marked in error, and after a check-in 120/78.
        var a = await PatientAsync();
        var aVisit = await VisitAsync(a);
        await InsertAsync(a, "8480-6", 120m, 1);
        await InsertAsync(a, "8462-4", 80m, 2, inError: true);
        await InsertAsync(a, "8462-4", 78m, 10, visit: aVisit);
        // Failure B: 120/80 with no visit, and after a check-in 130 for the same time, beside the diastolic.
        var b = await PatientAsync();
        var bVisit = await VisitAsync(b);
        await InsertAsync(b, "8480-6", 120m, 1);
        await InsertAsync(b, "8462-4", 80m, 2);
        await InsertAsync(b, "8480-6", 130m, 10, visit: bVisit);
        // A heart rate recorded twice for one moment, at two visits.
        var twice = await PatientAsync();
        var twiceVisit = await VisitAsync(twice);
        await InsertAsync(twice, "8867-4", 70m, 1, visit: twiceVisit);
        await InsertAsync(twice, "8867-4", 72m, 2);
        // A systolic whose diastolic was marked in error alone (step 9b), and one that never had a diastolic.
        var partner = await PatientAsync();
        await InsertAsync(partner, "8480-6", 120m, 1);
        await InsertAsync(partner, "8462-4", 80m, 2, inError: true);
        var lone = await PatientAsync();
        await InsertAsync(lone, "8480-6", 120m, 1);
        // A plain set; a blood pressure marked in error whole; and a systolic in error with no diastolic at all.
        var plain = await PatientAsync();
        await InsertAsync(plain, "8480-6", 118m, 1);
        await InsertAsync(plain, "8462-4", 76m, 2);
        await InsertAsync(plain, "8867-4", 64m, 3);
        var wrong = await PatientAsync();
        await InsertAsync(wrong, "8480-6", 140m, 1, inError: true);
        await InsertAsync(wrong, "8462-4", 90m, 2, inError: true);
        var half = await PatientAsync();
        await InsertAsync(half, "8480-6", 150m, 1, inError: true);
        // The two halves of a blood pressure marked in error an hour apart, as two corrections: two readings, so that
        // neither mark hides the other.
        var apart = await PatientAsync();
        await InsertAsync(apart, "8480-6", 120m, 1, inError: true, markedAgo: 60);
        await InsertAsync(apart, "8462-4", 80m, 2, inError: true);
        // 81 kg, 180 cm and the body mass index worked out from them, then 79 kg typed after a check-in: the body mass
        // index of the weight marked as a duplicate is marked with it, and one is worked out from 79 kg and 180 cm.
        var weighed = await PatientAsync();
        var weighedVisit = await VisitAsync(weighed);
        await InsertAsync(weighed, "29463-7", 81m, 1);
        await InsertAsync(weighed, "8302-2", 180m, 2);
        await InsertAsync(weighed, "39156-5", 25m, 3);
        await InsertAsync(weighed, "29463-7", 79m, 10, visit: weighedVisit);
        // Two vital signs with no code at one moment, which only an insert past the apps could leave (and then only at
        // two visits, which the older index held apart): no sign, so neither marked nor moved.
        var uncoded = await PatientAsync();
        var uncodedVisit = await VisitAsync(uncoded);
        await InsertAsync(uncoded, null, 1m, 1);
        await InsertAsync(uncoded, null, 2m, 2, visit: uncodedVisit);

        var audited = int.Parse((await AsSuperuserAsync("SELECT count(*)::text FROM ehr.audit_events")).Single());
        string[] protection = ["observations forced=true audit=O body_mass_index=O"];
        Assert.Equal(protection, await AsSuperuserAsync(Protection));

        var report = await MigrateAsync(null);

        Assert.Equal(
            [
                "Migration 9c: 4 vital signs moved to the visit of the first value recorded at their moment (audited)",
                $"Migration 9c: 3 duplicate values marked entered in error, \"{Duplicate}\" (audited)",
                $"Migration 9c: 1 body mass index values marked entered in error, \"{Index}\" (audited)",
                "Migration 9c: 1 body mass index values worked out again from the weight and height kept (audited)",
                $"Migration 9c: 1 blood pressure values marked entered in error because their partner was, \"{Partner}\" (audited)",
                $"Migration 9c: 1 blood pressure values marked entered in error that never had a partner, \"{Unpaired}\" (audited)",
                "Migration 9c: 6 standing blood pressure values made into 3 readings; 10 values in error given readings",
            ],
            report);

        Assert.Equal(["8480-6 120 final none none R1", "8462-4 80 entered-in-error Synthetic: typed wrong none R2", "8462-4 78 final none none R1"], await StateAsync(a));
        Assert.Equal([$"8480-6 120 entered-in-error {Duplicate} none R1", "8462-4 80 final none none R2", "8480-6 130 final none none R2"], await StateAsync(b));
        Assert.Equal([$"8867-4 70 entered-in-error {Duplicate} visit none", "8867-4 72 final none visit none"], await StateAsync(twice));
        // Marked apart (the diastolic under step 9b, the systolic by the migration): a reading each, each reason kept.
        Assert.Equal([$"8480-6 120 entered-in-error {Partner} none R1", "8462-4 80 entered-in-error Synthetic: typed wrong none R2"], await StateAsync(partner));
        Assert.Equal(["8480-6 120 entered-in-error Synthetic: typed wrong none R1", "8462-4 80 entered-in-error Synthetic: typed wrong none R2"], await StateAsync(apart));
        Assert.Equal(
            [$"29463-7 81 entered-in-error {Duplicate} none none", "8302-2 180 final none none none", $"39156-5 25 entered-in-error {Index} none none",
                "29463-7 79 final none none none", "39156-5 24.4 final none none none"],
            await StateAsync(weighed));
        Assert.Equal(["1 final none", "2 final visit"], await AsSuperuserAsync(
            "SELECT trim_scale(value) || ' ' || status || ' ' || CASE WHEN encounter_id IS NULL THEN 'none' ELSE 'visit' END FROM ehr.observations WHERE patient_id = @patient ORDER BY value",
            ("patient", uncoded)));
        Assert.Equal([$"8480-6 120 entered-in-error {Unpaired} none R1"], await StateAsync(lone));
        Assert.Equal(["8480-6 118 final none none R1", "8462-4 76 final none none R1", "8867-4 64 final none none none"], await StateAsync(plain));
        Assert.Equal(["8480-6 140 entered-in-error Synthetic: typed wrong none R1", "8462-4 90 entered-in-error Synthetic: typed wrong none R1"], await StateAsync(wrong));
        Assert.Equal(["8480-6 150 entered-in-error Synthetic: typed wrong none R1"], await StateAsync(half));

        // Each change to patient data is an audit event of its own, with nobody signed in ("System" on the audit page)
        // and the reason in the change; the reading IDs are not.
        Assert.Equal(
            new[]
            {
                $"8462-4 78 visit {a}", $"8480-6 130 visit {b}", $"8867-4 72 visit {twice}", $"29463-7 79 visit {weighed}",
                $"8480-6 120 marked {b} {Duplicate}", $"8867-4 70 marked {twice} {Duplicate}", $"8480-6 120 marked {partner} {Partner}", $"8480-6 120 marked {lone} {Unpaired}",
                $"29463-7 81 marked {weighed} {Duplicate}", $"39156-5 25 marked {weighed} {Index}",
            }.Order(StringComparer.Ordinal),
            (await AsSuperuserAsync(
                """
                SELECT coalesce(o.code, 'none') || ' ' || trim_scale(o.value) || ' ' || CASE WHEN e.details ? 'encounter_id' THEN 'visit ' || e.patient_id ELSE 'marked ' || e.patient_id || ' ' || (e.details -> 'entered_in_error_reason' ->> 'new') END
                FROM ehr.audit_events e JOIN ehr.observations o ON o.id = e.row_id
                WHERE e.table_name = 'observations' AND e.action = 'update' AND e.actor_id IS NULL AND e.occurred_at > now() - interval '10 minutes'
                  AND NOT e.details ? 'reading_id' AND e.patient_id = ANY(@patients)
                """, ("patients", new[] { a, b, twice, partner, lone, plain, wrong, half, apart, weighed, uncoded }))).Order(StringComparer.Ordinal));
        // The body mass index worked out again is an insert by nobody signed in, with its values.
        Assert.Equal(["insert none 24.4 final"], await AsSuperuserAsync(
            """
            SELECT e.action || ' ' || coalesce(e.actor_id::text, 'none') || ' ' || (e.details -> 'inserted' ->> 'value')::numeric::float8 || ' ' || (e.details -> 'inserted' ->> 'status')
            FROM ehr.audit_events e JOIN ehr.observations o ON o.id = e.row_id
            WHERE e.table_name = 'observations' AND e.patient_id = @patient AND e.action = 'insert' AND o.code = '39156-5' AND o.status = 'final'
            """, ("patient", weighed)));
        Assert.Equal(audited + 11, int.Parse((await AsSuperuserAsync("SELECT count(*)::text FROM ehr.audit_events")).Single()));
        Assert.Equal(protection, await AsSuperuserAsync(Protection));

        // Down: the readings go, the marks and the visits stay. Up again: nothing more to mark or move.
        Assert.Empty(await MigrateAsync(Before));
        Assert.Equal(["0"], await AsSuperuserAsync("SELECT count(*)::text FROM information_schema.columns WHERE table_schema = 'ehr' AND table_name = 'observations' AND column_name = 'reading_id'"));
        Assert.Equal(["8867-4 70 entered-in-error", "8867-4 72 final"], await AsSuperuserAsync(
            "SELECT code || ' ' || trim_scale(value) || ' ' || status FROM ehr.observations WHERE patient_id = @patient AND encounter_id IS NOT NULL ORDER BY value", ("patient", twice)));
        Assert.Equal(
            [
                "Migration 9c: 0 vital signs moved to the visit of the first value recorded at their moment (audited)",
                $"Migration 9c: 0 duplicate values marked entered in error, \"{Duplicate}\" (audited)",
                $"Migration 9c: 0 body mass index values marked entered in error, \"{Index}\" (audited)",
                "Migration 9c: 0 body mass index values worked out again from the weight and height kept (audited)",
                $"Migration 9c: 0 blood pressure values marked entered in error because their partner was, \"{Partner}\" (audited)",
                $"Migration 9c: 0 blood pressure values marked entered in error that never had a partner, \"{Unpaired}\" (audited)",
                "Migration 9c: 6 standing blood pressure values made into 3 readings; 10 values in error given readings",
            ],
            await MigrateAsync(null));
        Assert.Equal(audited + 11, int.Parse((await AsSuperuserAsync("SELECT count(*)::text FROM ehr.audit_events")).Single()));
        Assert.Equal(protection, await AsSuperuserAsync(Protection));

        // And db/policies applies over the migrated rows, as the migration job does next.
        await database.ApplyAsync();
        var app = database.ConnectionStringFor("ehr_app", MigratedDatabase.AppPassword);
        Assert.Empty(await Accounts.QueryAsync(app, null, "SELECT id::text FROM ehr.observations"));

        // The body mass index the migration worked out is the one ehr.file_result works out from the same weight and
        // height: recorded through the app's role for another patient, the two agree in name, unit, value, range and
        // status, so in what the pages flag too.
        var other = await PatientAsync();
        await Accounts.AssignAsync(database, clinician, other);
        await Accounts.ExecuteAsync(app, clinician,
            """
            INSERT INTO ehr.observations (id, patient_id, category, code, display, status, value, effective_at) VALUES (gen_random_uuid(), @patient, 'vital-signs', '29463-7', 'x', 'final', 79, now() - interval '1 hour');
            INSERT INTO ehr.observations (id, patient_id, category, code, display, status, value, effective_at) VALUES (gen_random_uuid(), @patient, 'vital-signs', '8302-2', 'x', 'final', 180, now() - interval '1 hour');
            INSERT INTO ehr.observations (id, patient_id, category, code, display, status, value, effective_at) VALUES (gen_random_uuid(), @patient, 'vital-signs', '39156-5', 'x', 'final', 1, now() - interval '1 hour');
            """, ("patient", other));
        const string worked = """
            SELECT display || '|' || unit || '|' || trim_scale(value) || '|' || trim_scale(reference_low) || '|' || trim_scale(reference_high) || '|' || status || '|' || category
            FROM ehr.observations WHERE patient_id = @patient AND code = '39156-5' AND status = 'final'
            """;
        Assert.Equal(["Body mass index (BMI) [Ratio]|kg/m2|24.4|18.5|24.9|final|vital-signs"], await AsSuperuserAsync(worked, ("patient", weighed)));
        Assert.Equal(await AsSuperuserAsync(worked, ("patient", other)), await AsSuperuserAsync(worked, ("patient", weighed)));
    }
}
