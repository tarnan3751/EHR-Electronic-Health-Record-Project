using Ehr.Data.Accounts;
using Ehr.Migrations;
using Ehr.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;
using static Ehr.Testing.TestRun;

namespace Ehr.Security.Tests;

// The expansion plan's step 18: the EmergencyAccessReviews migration goes down, on a database in use (policies applied,
// as after any deploy) where nothing has been reviewed, and leaves nothing of the step's db/policies behind: no trigger or
// function that names the table it drops, so a clinician's emergency access is still granted. Up again, the step's
// rules are all back. Down is refused once an access has been reviewed: each review is a record. A database of its own:
// it migrates down and up.
public class EmergencyAccessReviewsMigrationTests(MigratedDatabase database) : IClassFixture<MigratedDatabase>
{
    const string Before = "AmbientListening";

    async Task MigrateAsync(string? target)
    {
        await using var context = EhrDbContextFactory.Create(database.OwnerConnectionString, new StringWriter());
        var name = target is null ? null : context.Database.GetMigrations().Single(migration => migration.EndsWith("_" + target, StringComparison.Ordinal));
        await context.GetService<IMigrator>().MigrateAsync(name, Cancel);
    }

    Task<List<string>> StepObjectsAsync() => Accounts.QueryAsync(database.SuperuserConnectionString, null,
        """
        SELECT 'function ' || p.proname FROM pg_proc p
        WHERE p.pronamespace = 'ehr'::regnamespace AND p.proname LIKE '%emergency_access%'
        UNION ALL
        SELECT 'trigger ' || t.tgname FROM pg_trigger t
        WHERE NOT t.tgisinternal AND (t.tgname LIKE '%emergency_access%' OR t.tgname LIKE '%review_items%')
        ORDER BY 1
        """);

    async Task GrantAsync(Guid clinician)
    {
        var patient = await Accounts.PatientAsync(database, clinician);
        await Accounts.ExecuteAsync(database.ConnectionStringFor("ehr_app", MigratedDatabase.AppPassword), clinician,
            """
            INSERT INTO ehr.break_glass_accesses (id, patient_id, clinician_id, reason, granted_at, expires_at)
            VALUES (gen_random_uuid(), @patient, @clinician, 'Synthetic: covering the ward', now() - interval '5 hours', now() - interval '1 hour')
            """,
            ("patient", patient), ("clinician", clinician));
    }

    [Fact]
    public async Task Down_leaves_none_of_the_steps_rules_behind_and_is_refused_once_anything_is_reviewed()
    {
        var everything = await StepObjectsAsync();
        Assert.Contains("trigger break_glass_accesses_review_items", everything);
        Assert.Contains("function emergency_access_for_review", everything);

        var clinician = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        await Accounts.StaffAsync(database, StaffRoles.Admin);
        await MigrateAsync(Before);
        Assert.Empty(await StepObjectsAsync());
        await GrantAsync(clinician);

        await MigrateAsync(null);
        await database.ApplyAsync();
        Assert.Equal(everything, await StepObjectsAsync());

        // Reviewed once, the migration stays.
        var admin = await Accounts.StaffAsync(database, StaffRoles.Admin);
        var access = Guid.Parse((await Accounts.QueryAsync(database.SuperuserConnectionString, null,
            "SELECT id::text FROM ehr.break_glass_accesses ORDER BY granted_at LIMIT 1")).Single());
        await Accounts.ExecuteAsync(database.ConnectionStringFor("ehr_app", MigratedDatabase.AppPassword), admin,
            "INSERT INTO ehr.emergency_access_reviews (id, reviewer_id, reviewed_at, outcome, note) VALUES (@id, @admin, now(), 'appropriate', 'Synthetic: checked with the ward.')",
            ("id", access), ("admin", admin));
        var refused = await Assert.ThrowsAnyAsync<Exception>(() => MigrateAsync(Before));
        var cause = refused;
        while (cause is not PostgresException && cause.InnerException is { } inner)
        {
            cause = inner;
        }

        Assert.Equal("55000", Assert.IsType<PostgresException>(cause).SqlState);
        Assert.Equal(everything, await StepObjectsAsync());
    }
}
