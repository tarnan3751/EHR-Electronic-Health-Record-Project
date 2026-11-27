using Ehr.Data.Accounts;
using Ehr.Migrations;
using Ehr.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;
using static Ehr.Testing.TestRun;

namespace Ehr.Security.Tests;

// The expansion plan's step 16: the AmbientListening migration goes down, on a database in use (policies applied, as
// after any deploy) where no visit has been listened to, and leaves nothing of the step's db/policies behind: no guard,
// trigger or function that names a table or column it drops. So a dictation's review is still written as the app writes
// it, through the on-site app's role, which a guard left behind would refuse (it named the consents' table) whatever
// db/policies is applied next. Up again, the step's rules are all back. A database of its own: it migrates down and up.
public class AmbientListeningMigrationTests(MigratedDatabase database) : IClassFixture<MigratedDatabase>
{
    const string Before = "AiChartProposals";

    // As the migration job does it (EhrDbContextFactory).
    async Task MigrateAsync(string? target)
    {
        await using var context = EhrDbContextFactory.Create(database.OwnerConnectionString, new StringWriter());
        var name = target is null ? null : context.Database.GetMigrations().Single(migration => migration.EndsWith("_" + target, StringComparison.Ordinal));
        await context.GetService<IMigrator>().MigrateAsync(name, Cancel);
    }

    // The step's triggers and functions (db/policies/56-ambient-listening.sql), by name.
    Task<List<string>> StepObjectsAsync() => Accounts.QueryAsync(database.SuperuserConnectionString, null,
        """
        SELECT 'function ' || p.proname FROM pg_proc p
        WHERE p.pronamespace = 'ehr'::regnamespace AND p.proname LIKE '%ambient%'
        UNION ALL
        SELECT 'trigger ' || t.tgname FROM pg_trigger t
        WHERE NOT t.tgisinternal AND (t.tgname LIKE '%ambient%' OR t.tgname LIKE '%transcript%')
        ORDER BY 1
        """);

    [Fact]
    public async Task Down_leaves_none_of_the_steps_rules_behind_so_a_dictations_review_is_still_written()
    {
        var everything = await StepObjectsAsync();
        Assert.Contains("function purge_ambient_transcripts", everything);
        Assert.Contains("trigger ai_note_reviews_ambient", everything);

        await MigrateAsync(Before);
        var clinician = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        var patient = await Accounts.PatientAsync(database, clinician);
        await Accounts.AssignAsync(database, clinician, patient);
        (string, object)[] parameters = [("actor", clinician), ("patient", patient), ("draft", Guid.CreateVersion7()), ("review", Guid.CreateVersion7())];
        await Accounts.ExecuteAsync(database.ConnectionStringFor("ehr_app", MigratedDatabase.AppPassword), clinician,
            """
            INSERT INTO ehr.clinical_drafts
                (id, patient_id, clinician_id, body, version, subjective, objective, assessment, plan, structured_version, has_unreviewed_proposals)
            VALUES (@draft, @patient, @actor, 'Synthetic note', 1, 'Synthetic subjective', 'Synthetic objective', 'Synthetic assessment', 'Synthetic plan', 1, false);
            INSERT INTO ehr.ai_note_reviews (id, patient_id, draft_id, clinician_id, session_id, max_seconds, max_chunk_bytes, state, started_version)
            VALUES (@review, @patient, @draft, @actor, 'synthetic-session-0001', 180, 100000, 'recording', 1);
            """, parameters);
        Assert.Equal(["recording"], await Accounts.QueryAsync(database.SuperuserConnectionString, null,
            "SELECT state FROM ehr.ai_note_reviews WHERE id = @review", parameters));
        Assert.Empty(await StepObjectsAsync());

        await MigrateAsync(null);
        await database.ApplyAsync();
        Assert.Equal(everything, await StepObjectsAsync());
    }
}
