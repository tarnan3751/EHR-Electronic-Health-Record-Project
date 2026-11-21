using Ehr.Data;
using Ehr.Data.Accounts;
using Ehr.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Ehr.Security.Tests;

// The expansion plan's step 9: the VitalSigns migration moves patient data by hand. It copies the blood pressure and
// heart rate a visit carried into observations, and to do that as the tables' owner it lifts forced row-level security
// and pauses the write-audit trigger. On a database in use (policies applied, as after any deploy), this proves the
// values arrive, nothing is audited as a clinical write, and the protection is back afterwards; and that rolling the
// migration back returns the values to the visits. A database of its own: it migrates down and up again.
public class VitalSignMigrationTests(MigratedDatabase database) : IClassFixture<MigratedDatabase>
{
    const string BeforeStep9 = "OrderSafety";

    Task<List<string>> AsSuperuserAsync(string sql, params (string Name, object Value)[] parameters) =>
        Accounts.QueryAsync(database.SuperuserConnectionString, null, sql, parameters);

    async Task MigrateAsync(string? target)
    {
        var options = new DbContextOptionsBuilder<EhrDbContext>();
        EhrDbContext.Configure(options, database.OwnerConnectionString);
        await using var context = new EhrDbContext(options.Options);
        var migrator = context.GetService<IMigrator>();
        var name = target is null ? null : context.Database.GetMigrations().Single(migration => migration.EndsWith("_" + target, StringComparison.Ordinal));
        await migrator.MigrateAsync(name, TestContext.Current.CancellationToken);
    }

    const string Protection = """
        SELECT c.relname || ' forced=' || c.relforcerowsecurity || ' audit=' || (SELECT t.tgenabled::text FROM pg_trigger t WHERE t.tgrelid = c.oid AND t.tgname = c.relname || '_audit')
        FROM pg_class c WHERE c.oid IN ('ehr.encounters'::regclass, 'ehr.observations'::regclass, 'ehr.patients'::regclass) ORDER BY 1
        """;

    [Fact]
    public async Task The_migration_moves_a_visits_vital_signs_both_ways_and_leaves_the_tables_protected_and_unaudited()
    {
        var clinician = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        var patient = await Accounts.PatientAsync(database, clinician);
        // An inbox item of the kind step 10b added: rolling back past that step has to take it away, since the older
        // schema's check refuses the kind.
        // Step 13a's kind likewise.
        foreach (var kind in new[] { "addendum", "summary-withdrawn" })
        {
            await Accounts.ExecuteAsync(database.SuperuserConnectionString, null,
                "INSERT INTO ehr.inbox_items (id, recipient_id, kind, patient_id, summary, created_at) VALUES (gen_random_uuid(), @to, @kind, @patient, 'Synthetic', now())",
                ("to", clinician), ("kind", kind), ("patient", patient));
        }

        await MigrateAsync(BeforeStep9);
        Assert.Equal(["0"], await AsSuperuserAsync("SELECT count(*)::text FROM ehr.inbox_items WHERE kind IN ('addendum', 'summary-withdrawn')"));
        // Nor of step 13a: the triggers it put on the notes and addenda, and its functions.
        Assert.Equal(["0"], await AsSuperuserAsync(
            """
            SELECT ((SELECT count(*) FROM pg_trigger WHERE tgname IN ('clinical_notes_follow_summaries', 'note_addenda_follow_summaries', 'note_addenda_signed_follow_summaries'))
                + (SELECT count(*) FROM pg_proc WHERE pronamespace = 'ehr'::regnamespace AND proname IN ('follow_summary_sources', 'guard_visit_summary', 'reject_visit_summary_delete',
                    'deliver_summary_withdrawn', 'standing_visit_summaries', 'withdrawn_visit_summaries', 'withdrawn_summary_was_viewed', 'visit_summary_problems', 'summary_sources_sensitive')))::text
            """));
        // Nor of step 13d: the trigger it put on the medications, and its functions.
        Assert.Equal(["0"], await AsSuperuserAsync(
            """
            SELECT ((SELECT count(*) FROM pg_trigger WHERE tgname = 'medication_statements_follow_refills')
                + (SELECT count(*) FROM pg_proc WHERE pronamespace = 'ehr'::regnamespace AND proname IN ('follow_refill_medication', 'guard_refill_request',
                    'reject_refill_request_delete', 'deliver_refill_request', 'portal_refill_requests', 'renews_same_medication')))::text
            """));
        // Nothing of step 10b is left behind for the older schema to carry.
        Assert.Equal(["0"], await AsSuperuserAsync(
            """
            SELECT ((SELECT count(*) FROM pg_policies WHERE schemaname = 'ehr' AND policyname IN ('clinical_notes_panel_update', 'clinical_notes_owner_select', 'note_addenda_panel_update'))
                + (SELECT count(*) FROM pg_proc WHERE pronamespace = 'ehr'::regnamespace AND proname IN ('change_note_sensitivity', 'change_addendum_sensitivity', 'deliver_addendum', 'log_note_sensitivity_change', 'mark_note_addenda_sensitive', 'clear_inherited_addenda_marks')))::text
            """));

        // As the schema was before step 9: a visit with all three, one with a heart rate only, one with none.
        foreach (var (minutes, systolic, diastolic, rate) in new (int, object, object, object)[] { (60, (short)148, (short)92, (short)71), (120, DBNull.Value, DBNull.Value, (short)64), (180, DBNull.Value, DBNull.Value, DBNull.Value) })
        {
            await Accounts.ExecuteAsync(database.SuperuserConnectionString, null,
                """
                INSERT INTO ehr.encounters (id, patient_id, status, class_code, period_start, systolic_mm_hg, diastolic_mm_hg, heart_rate_per_minute)
                VALUES (gen_random_uuid(), @patient, 'finished', 'AMB', now() - make_interval(mins => @minutes), @systolic, @diastolic, @rate)
                """, ("patient", patient), ("minutes", minutes), ("systolic", systolic), ("diastolic", diastolic), ("rate", rate));
        }

        var audited = await AsSuperuserAsync("SELECT count(*)::text FROM ehr.audit_events");
        string[] protection = ["encounters forced=true audit=O", "observations forced=true audit=O", "patients forced=true audit=O"];

        // A three-year-old, with a heart rate that is ordinary at three and high in an adult (step 9b).
        var child = await Accounts.PatientAsync(database, clinician);
        await Accounts.ExecuteAsync(database.SuperuserConnectionString, null,
            "UPDATE ehr.patients SET birth_date = (now() - interval '3 years 2 months')::date WHERE id = @patient", ("patient", child));
        await Accounts.ExecuteAsync(database.SuperuserConnectionString, null,
            """
            INSERT INTO ehr.encounters (id, patient_id, status, class_code, period_start, systolic_mm_hg, diastolic_mm_hg, heart_rate_per_minute)
            VALUES (gen_random_uuid(), @patient, 'finished', 'AMB', now() - interval '1 hour', 98, 60, 120)
            """, ("patient", child));
        audited = await AsSuperuserAsync("SELECT count(*)::text FROM ehr.audit_events");
        Assert.Equal(protection, await AsSuperuserAsync(Protection));

        await MigrateAsync(null);

        Assert.Equal(
            // The ranges are the adults' for this patient's age, as the later VitalCorrections migration leaves them
            // (step 9b): 60 to 79 for diastolic, where the first migration wrote 60 to 84.
            ["8462-4 Diastolic blood pressure 92 mm[Hg] 60-79 final true true", "8480-6 Systolic blood pressure 148 mm[Hg] 90-129 final true true",
                "8867-4 Heart rate 64 /min 60-100 final true true", "8867-4 Heart rate 71 /min 60-100 final true true"],
            await AsSuperuserAsync(
                """
                SELECT o.code || ' ' || o.display || ' ' || trim_scale(o.value) || ' ' || o.unit || ' ' || trim_scale(o.reference_low) || '-' || trim_scale(o.reference_high) || ' ' || o.status
                    || ' ' || (o.effective_at = e.period_start AND o.issued_at = e.period_start) || ' ' || (o.patient_id = e.patient_id AND o.category = 'vital-signs')
                FROM ehr.observations o JOIN ehr.encounters e ON e.id = o.encounter_id WHERE o.patient_id = @patient ORDER BY o.code, o.value
                """, ("patient", patient)));
        Assert.Equal(protection, await AsSuperuserAsync(Protection));
        Assert.Equal(audited, await AsSuperuserAsync("SELECT count(*)::text FROM ehr.audit_events"));
        // The child's signs came out with a three-year-old's ranges (the VitalCorrections migration, step 9b, and the
        // ChildrenRanges migration, step 9d: Fleming's 3 to 4 years, and Table 6's age 3, the lower of the boys' and
        // girls' values for a patient whose sex is unknown).
        Assert.Equal(["8462-4 60 none-58", "8480-6 98 none-101", "8867-4 120 70-136"], await AsSuperuserAsync(
            """
            SELECT code || ' ' || trim_scale(value) || ' ' || coalesce(trim_scale(reference_low)::text, 'none') || '-' || coalesce(trim_scale(reference_high)::text, 'none')
            FROM ehr.observations WHERE patient_id = @patient ORDER BY code
            """, ("patient", child)));

        // Once a reading has been marked entered in error, VitalCorrections is one way: rolling back is refused with
        // the reason, and nothing is changed.
        var marked = (await AsSuperuserAsync("SELECT id::text FROM ehr.observations WHERE patient_id = @patient AND code = '8867-4'", ("patient", child))).Single();
        await Accounts.ExecuteAsync(database.SuperuserConnectionString, null,
            "UPDATE ehr.observations SET status = 'entered-in-error', entered_in_error_reason = 'Synthetic', entered_in_error_at = now() WHERE id = @id", ("id", Guid.Parse(marked)));
        var refused = await Assert.ThrowsAnyAsync<Exception>(() => MigrateAsync(BeforeStep9));
        Assert.Contains("cannot be rolled back", refused.ToString());
        Assert.Equal(["entered-in-error"], await AsSuperuserAsync("SELECT status FROM ehr.observations WHERE id = @id", ("id", Guid.Parse(marked))));
        Assert.Equal(protection, await AsSuperuserAsync(Protection));
        await Accounts.ExecuteAsync(database.SuperuserConnectionString, null,
            "UPDATE ehr.observations SET status = 'final', entered_in_error_reason = NULL, entered_in_error_at = NULL WHERE id = @id", ("id", Guid.Parse(marked)));
        audited = await AsSuperuserAsync("SELECT count(*)::text FROM ehr.audit_events");
        // With nobody signed in, the app role still sees none of it.
        Assert.Empty(await Accounts.QueryAsync(database.ConnectionStringFor("ehr_app", MigratedDatabase.AppPassword), null, "SELECT id::text FROM ehr.observations"));

        // Back: the visits have their values again, and no vital sign is left to pass as a laboratory result.
        await MigrateAsync(BeforeStep9);
        Assert.Equal(["148/92 71", "none/none 64", "none/none none"], await AsSuperuserAsync(
            """
            SELECT coalesce(systolic_mm_hg::text, 'none') || '/' || coalesce(diastolic_mm_hg::text, 'none') || ' ' || coalesce(heart_rate_per_minute::text, 'none')
            FROM ehr.encounters WHERE patient_id = @patient ORDER BY period_start DESC
            """, ("patient", patient)));
        Assert.Equal(["0"], await AsSuperuserAsync("SELECT count(*)::text FROM ehr.observations WHERE patient_id = @patient", ("patient", patient)));
        Assert.Equal(protection, await AsSuperuserAsync(Protection));
        Assert.Equal(audited, await AsSuperuserAsync("SELECT count(*)::text FROM ehr.audit_events"));

        await MigrateAsync(null);
    }
}
