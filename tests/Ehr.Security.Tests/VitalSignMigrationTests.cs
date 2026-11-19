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
        FROM pg_class c WHERE c.oid IN ('ehr.encounters'::regclass, 'ehr.observations'::regclass) ORDER BY 1
        """;

    [Fact]
    public async Task The_migration_moves_a_visits_vital_signs_both_ways_and_leaves_the_tables_protected_and_unaudited()
    {
        var clinician = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        var patient = await Accounts.PatientAsync(database, clinician);
        await MigrateAsync(BeforeStep9);

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
        string[] protection = ["encounters forced=true audit=O", "observations forced=true audit=O"];
        Assert.Equal(protection, await AsSuperuserAsync(Protection));

        await MigrateAsync(null);

        Assert.Equal(
            ["8462-4 Diastolic blood pressure 92 mm[Hg] 60-84 final true true", "8480-6 Systolic blood pressure 148 mm[Hg] 90-129 final true true",
                "8867-4 Heart rate 64 /min 60-100 final true true", "8867-4 Heart rate 71 /min 60-100 final true true"],
            await AsSuperuserAsync(
                """
                SELECT o.code || ' ' || o.display || ' ' || trim_scale(o.value) || ' ' || o.unit || ' ' || trim_scale(o.reference_low) || '-' || trim_scale(o.reference_high) || ' ' || o.status
                    || ' ' || (o.effective_at = e.period_start AND o.issued_at = e.period_start) || ' ' || (o.patient_id = e.patient_id AND o.category = 'vital-signs')
                FROM ehr.observations o JOIN ehr.encounters e ON e.id = o.encounter_id WHERE o.patient_id = @patient ORDER BY o.code, o.value
                """, ("patient", patient)));
        Assert.Equal(protection, await AsSuperuserAsync(Protection));
        Assert.Equal(audited, await AsSuperuserAsync("SELECT count(*)::text FROM ehr.audit_events"));
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
