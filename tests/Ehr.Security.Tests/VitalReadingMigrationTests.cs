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
// shaped as steps 9 and 9b could leave them (each set entered in one transaction, as the pages wrote it, or loaded with
// no audit event, as the seed and the VitalSigns migration did), this proves each kind of change and its count in the
// migration's report,
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

    // Values entered together, in one transaction, as the vital signs page entered a set: `minutes` after the moment for
    // each, as above, at the visit if one is given.
    Task InsertTogetherAsync(Guid patient, params (string Code, decimal Value, int Minutes, bool InError)[] values) => InsertTogetherAsync(patient, null, values);

    Task InsertTogetherAsync(Guid patient, Guid? visit, params (string Code, decimal Value, int Minutes, bool InError)[] values) =>
        Accounts.ExecuteAsync(database.SuperuserConnectionString, null,
            string.Concat(values.Select((_, i) =>
                "INSERT INTO ehr.observations (id, patient_id, encounter_id, category, code, display, status, value, effective_at, issued_at, entered_in_error_reason, entered_in_error_at) "
                + $"VALUES (gen_random_uuid(), @patient, @visit, 'vital-signs', @code{i}, 'Synthetic', CASE WHEN @in_error{i} THEN 'entered-in-error' ELSE 'final' END, @value{i}, "
                + $"@taken, @taken + make_interval(mins => @minutes{i}), CASE WHEN @in_error{i} THEN 'Synthetic: typed wrong' END, CASE WHEN @in_error{i} THEN now() END);")),
            [("patient", patient), ("visit", (object?)visit ?? DBNull.Value), ("taken", Taken),
                .. values.SelectMany((value, i) => new (string, object)[] { ($"code{i}", value.Code), ($"value{i}", value.Value), ($"minutes{i}", value.Minutes), ($"in_error{i}", value.InError) })]);

    // A visit's blood pressure loaded with no audit event and one issued_at, as the seed and the VitalSigns migration
    // loaded them: for the visit and with the IDs given, if any.
    Task LoadAsync(Guid patient, decimal systolic, decimal diastolic, Guid? visit = null, Guid? systolicId = null, Guid? diastolicId = null) =>
        Accounts.ExecuteAsync(database.SuperuserConnectionString, null,
            """
            SET LOCAL session_replication_role = replica;
            INSERT INTO ehr.observations (id, patient_id, encounter_id, category, code, display, status, value, effective_at, issued_at)
            VALUES (coalesce(@systolic_id, gen_random_uuid()), @patient, @visit, 'vital-signs', '8480-6', 'Synthetic', 'final', @systolic, @taken, @taken),
                   (coalesce(@diastolic_id, gen_random_uuid()), @patient, @visit, 'vital-signs', '8462-4', 'Synthetic', 'final', @diastolic, @taken, @taken);
            """,
            ("patient", patient), ("systolic", systolic), ("diastolic", diastolic), ("taken", Taken), ("visit", (object?)visit ?? DBNull.Value),
            ("systolic_id", (object?)systolicId ?? DBNull.Value), ("diastolic_id", (object?)diastolicId ?? DBNull.Value));

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
    const string Unpartnered = "not entered together with a standing partner (migration 9c)";
    const string Index = "body mass index of a value marked as a duplicate (migration 9c)";

    [Fact]
    public async Task The_migration_puts_existing_vital_signs_into_readings_marks_what_cannot_stand_audited_and_reports_each_count()
    {
        var clinician = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        async Task<Guid> PatientAsync() => await Accounts.PatientAsync(database, clinician);
        Assert.Empty(await MigrateAsync(Before));
        Assert.Equal(["0"], await AsSuperuserAsync("SELECT count(*)::text FROM information_schema.columns WHERE table_schema = 'ehr' AND table_name = 'observations' AND column_name = 'reading_id'"));

        // Failure A of step 9c: 120/80 with no visit, the diastolic marked in error, and after a check-in 78 typed alone
        // (step 9b's correction of one number). No pair entered together stands: the systolic's partner is in error,
        // and the 78 was entered alone.
        var a = await PatientAsync();
        var aVisit = await VisitAsync(a);
        await InsertTogetherAsync(a, ("8480-6", 120m, 1, false), ("8462-4", 80m, 2, true));
        await InsertAsync(a, "8462-4", 78m, 10, visit: aVisit);
        // Failure B: 120/80 with no visit, and after a check-in 130 typed alone for the same time, beside the diastolic:
        // 120/80, the pair entered together, stands.
        var b = await PatientAsync();
        var bVisit = await VisitAsync(b);
        await InsertTogetherAsync(b, ("8480-6", 120m, 1, false), ("8462-4", 80m, 2, false));
        await InsertAsync(b, "8480-6", 130m, 10, visit: bVisit);
        // Tahseen's example: 120/80, then 130/85 typed whole after a check-in, then the 85 marked in error: 120/80.
        var example = await PatientAsync();
        var exampleVisit = await VisitAsync(example);
        await InsertTogetherAsync(example, ("8480-6", 120m, 1, false), ("8462-4", 80m, 2, false));
        await InsertTogetherAsync(example, exampleVisit, ("8480-6", 130m, 10, false), ("8462-4", 85m, 11, true));
        // Two pairs entered whole, both standing (the second after a check-in, which the older index allowed): the
        // latest stands, the older is a duplicate. And a systolic and a diastolic typed separately, neither with a
        // partner entered with it.
        var twoPairs = await PatientAsync();
        var twoPairsVisit = await VisitAsync(twoPairs);
        await InsertTogetherAsync(twoPairs, ("8480-6", 120m, 1, false), ("8462-4", 80m, 2, false));
        await InsertTogetherAsync(twoPairs, twoPairsVisit, ("8480-6", 130m, 10, false), ("8462-4", 85m, 11, false));
        var separately = await PatientAsync();
        await InsertAsync(separately, "8480-6", 118m, 1);
        await InsertAsync(separately, "8462-4", 76m, 5);
        // A blood pressure loaded with no audit event, then 130 typed alone for the same moment after a check-in.
        var loaded = await PatientAsync();
        var loadedVisit = await VisitAsync(loaded);
        await LoadAsync(loaded, 124m, 78m);
        await InsertAsync(loaded, "8480-6", 130m, 10, visit: loadedVisit);
        // Two visits starting at one instant, each with its blood pressure loaded at that moment with one issued_at: two
        // pairs, told apart by their visits. The one holding the moment's first value (the smaller ID) stands, at its
        // visit; the other is a duplicate, moved to that visit.
        var same = await PatientAsync();
        var (sameFirst, sameSecond) = (await VisitAsync(same), await VisitAsync(same));
        var sameIds = Enumerable.Range(0, 4).Select(_ => Guid.CreateVersion7()).OrderBy(id => id.ToString(), StringComparer.Ordinal).ToArray();
        await LoadAsync(same, 122m, 76m, sameFirst, sameIds[0], sameIds[1]);
        await LoadAsync(same, 128m, 84m, sameSecond, sameIds[2], sameIds[3]);
        // A heart rate recorded twice for one moment, at two visits.
        var twice = await PatientAsync();
        var twiceVisit = await VisitAsync(twice);
        await InsertAsync(twice, "8867-4", 70m, 1, visit: twiceVisit);
        await InsertAsync(twice, "8867-4", 72m, 2);
        // A systolic whose diastolic was marked in error alone (step 9b), and one that never had a diastolic.
        var partner = await PatientAsync();
        await InsertTogetherAsync(partner, ("8480-6", 120m, 1, false), ("8462-4", 80m, 2, true));
        var lone = await PatientAsync();
        await InsertAsync(lone, "8480-6", 120m, 1);
        // A plain set; a blood pressure marked in error whole; and a systolic in error with no diastolic at all.
        var plain = await PatientAsync();
        await InsertTogetherAsync(plain, ("8480-6", 118m, 1, false), ("8462-4", 76m, 2, false), ("8867-4", 64m, 3, false));
        var wrong = await PatientAsync();
        await InsertTogetherAsync(wrong, ("8480-6", 140m, 1, true), ("8462-4", 90m, 2, true));
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
                "Migration 9c: 11 vital signs moved to the visit of the first value recorded at their moment (audited)",
                $"Migration 9c: 6 duplicate values marked entered in error, \"{Duplicate}\" (audited)",
                $"Migration 9c: 1 body mass index values marked entered in error, \"{Index}\" (audited)",
                "Migration 9c: 1 body mass index values worked out again from the weight and height kept (audited)",
                $"Migration 9c: 2 blood pressure values marked entered in error because their partner was, \"{Partner}\" (audited)",
                $"Migration 9c: 1 blood pressure values marked entered in error that never had a partner, \"{Unpaired}\" (audited)",
                $"Migration 9c: 6 blood pressure values marked entered in error that were not entered together with a standing partner, \"{Unpartnered}\" (audited)",
                "Migration 9c: 12 standing blood pressure values made into 6 readings; 21 values in error given readings",
            ],
            report);

        // A: no pair entered together stands, so nothing does; each value its own reading, its own reason.
        Assert.Equal(
            [$"8480-6 120 entered-in-error {Partner} none R1", "8462-4 80 entered-in-error Synthetic: typed wrong none R2", $"8462-4 78 entered-in-error {Unpartnered} none R3"],
            await StateAsync(a));
        Assert.Equal(["8480-6 120 final none none R1", "8462-4 80 final none none R1", $"8480-6 130 entered-in-error {Unpartnered} none R2"], await StateAsync(b));
        Assert.Equal(
            ["8480-6 120 final none none R1", "8462-4 80 final none none R1", $"8480-6 130 entered-in-error {Unpartnered} none R2", "8462-4 85 entered-in-error Synthetic: typed wrong none R3"],
            await StateAsync(example));
        Assert.Equal(
            [$"8480-6 120 entered-in-error {Duplicate} none R1", $"8462-4 80 entered-in-error {Duplicate} none R1", "8480-6 130 final none none R2", "8462-4 85 final none none R2"],
            await StateAsync(twoPairs));
        Assert.Equal([$"8480-6 118 entered-in-error {Unpartnered} none R1", $"8462-4 76 entered-in-error {Unpartnered} none R1"], await StateAsync(separately));
        Assert.Equal(["8462-4 78 final none none R1", "8480-6 124 final none none R1", $"8480-6 130 entered-in-error {Unpartnered} none R2"], await StateAsync(loaded));
        Assert.Equal(
            [$"8462-4 76 final none first {sameIds[0]}", $"8462-4 84 entered-in-error {Duplicate} first {sameIds[2]}", $"8480-6 122 final none first {sameIds[0]}",
                $"8480-6 128 entered-in-error {Duplicate} first {sameIds[2]}"],
            await AsSuperuserAsync(
                """
                SELECT o.code || ' ' || trim_scale(o.value) || ' ' || o.status || ' ' || coalesce(o.entered_in_error_reason, 'none') || ' '
                    || CASE o.encounter_id WHEN @first THEN 'first' WHEN @second THEN 'second' ELSE 'none' END || ' '
                    || (SELECT min(r.id::text) FROM ehr.observations r WHERE r.reading_id = o.reading_id)
                FROM ehr.observations o WHERE o.patient_id = @patient ORDER BY o.code, o.value
                """, ("patient", same), ("first", sameFirst), ("second", sameSecond)));
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
                $"8462-4 78 visit {a}", $"8480-6 130 visit {b}", $"8867-4 72 visit {twice}", $"29463-7 79 visit {weighed}", $"8480-6 130 visit {example}",
                $"8462-4 85 visit {example}", $"8480-6 130 visit {twoPairs}", $"8462-4 85 visit {twoPairs}", $"8480-6 130 visit {loaded}",
                $"8480-6 120 marked {a} {Partner}", $"8462-4 78 marked {a} {Unpartnered}", $"8480-6 130 marked {b} {Unpartnered}", $"8867-4 70 marked {twice} {Duplicate}",
                $"8480-6 120 marked {partner} {Partner}", $"8480-6 120 marked {lone} {Unpaired}", $"29463-7 81 marked {weighed} {Duplicate}", $"39156-5 25 marked {weighed} {Index}",
                $"8480-6 130 marked {example} {Unpartnered}", $"8480-6 120 marked {twoPairs} {Duplicate}", $"8462-4 80 marked {twoPairs} {Duplicate}",
                $"8480-6 118 marked {separately} {Unpartnered}", $"8462-4 76 marked {separately} {Unpartnered}", $"8480-6 130 marked {loaded} {Unpartnered}",
                $"8480-6 128 visit {same}", $"8462-4 84 visit {same}", $"8480-6 128 marked {same} {Duplicate}", $"8462-4 84 marked {same} {Duplicate}",
            }.Order(StringComparer.Ordinal),
            (await AsSuperuserAsync(
                """
                SELECT coalesce(o.code, 'none') || ' ' || trim_scale(o.value) || ' ' || CASE WHEN e.details ? 'encounter_id' THEN 'visit ' || e.patient_id ELSE 'marked ' || e.patient_id || ' ' || (e.details -> 'entered_in_error_reason' ->> 'new') END
                FROM ehr.audit_events e JOIN ehr.observations o ON o.id = e.row_id
                WHERE e.table_name = 'observations' AND e.action = 'update' AND e.actor_id IS NULL AND e.occurred_at > now() - interval '10 minutes'
                  AND NOT e.details ? 'reading_id' AND e.patient_id = ANY(@patients)
                """, ("patients", new[] { a, b, example, twoPairs, separately, loaded, same, twice, partner, lone, plain, wrong, half, apart, weighed, uncoded }))).Order(StringComparer.Ordinal));
        // The body mass index worked out again is an insert by nobody signed in, with its values.
        Assert.Equal(["insert none 24.4 final"], await AsSuperuserAsync(
            """
            SELECT e.action || ' ' || coalesce(e.actor_id::text, 'none') || ' ' || (e.details -> 'inserted' ->> 'value')::numeric::float8 || ' ' || (e.details -> 'inserted' ->> 'status')
            FROM ehr.audit_events e JOIN ehr.observations o ON o.id = e.row_id
            WHERE e.table_name = 'observations' AND e.patient_id = @patient AND e.action = 'insert' AND o.code = '39156-5' AND o.status = 'final'
            """, ("patient", weighed)));
        Assert.Equal(audited + 28, int.Parse((await AsSuperuserAsync("SELECT count(*)::text FROM ehr.audit_events")).Single()));
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
                $"Migration 9c: 0 blood pressure values marked entered in error that were not entered together with a standing partner, \"{Unpartnered}\" (audited)",
                "Migration 9c: 12 standing blood pressure values made into 6 readings; 21 values in error given readings",
            ],
            await MigrateAsync(null));
        Assert.Equal(audited + 28, int.Parse((await AsSuperuserAsync("SELECT count(*)::text FROM ehr.audit_events")).Single()));
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
