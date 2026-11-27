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

// The expansion plan's step 14: the SoapNotes migration gives notes and drafts Subjective and Objective, and moves
// nothing already written. On a database in use (policies applied, as after any deploy), with notes and drafts as
// steps 10 to 13 left them, ordinary and awkward (a History at the 16,000-character limit, with CRLF line ends, the
// word "Objective" and characters from other scripts, a sensitive note with addenda, a draft with a dictation review
// waiting on a History proposal, a draft last saved by the desktop app), this proves that no row changes, nothing is
// audited and no draft is put in the desktop's sync again; that the migration says what it kept; that it goes down and
// up again while nothing is written in four sections; and that once something is, it refuses to go down and changes
// nothing. Gone down or refused, it leaves the drafts' row-level security enabled and forced. A database of its own: it
// migrates down and up.
public class SoapNotesMigrationTests(MigratedDatabase database) : IClassFixture<MigratedDatabase>
{
    const string Before = "AuditEventParts";

    string App => database.ConnectionStringFor("ehr_app", MigratedDatabase.AppPassword);

    // As the migration job does it (EhrDbContextFactory): what this step's migration reports comes back.
    async Task<List<string>> MigrateAsync(string? target)
    {
        var output = new StringWriter();
        await using (var context = EhrDbContextFactory.Create(database.OwnerConnectionString, output))
        {
            var name = target is null ? null : context.Database.GetMigrations().Single(migration => migration.EndsWith("_" + target, StringComparison.Ordinal));
            await context.GetService<IMigrator>().MigrateAsync(name, Cancel);
        }

        return [.. output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Where(line => line.StartsWith("Migration 14:", StringComparison.Ordinal))];
    }

    Task<List<string>> AsSuperuserAsync(string sql, params (string Name, object Value)[] parameters) =>
        Accounts.QueryAsync(database.SuperuserConnectionString, null, sql, parameters);

    // Rows as an earlier release left them, with every trigger held: no audit event, no change in the sync's log.
    Task PlantAsync(string sql, params (string Name, object Value)[] parameters) =>
        Accounts.ExecuteAsync(database.SuperuserConnectionString, null, "SET LOCAL session_replication_role = replica;\n" + sql, parameters);

    // Every row of the tables a note is kept in, as JSON, without the columns this step added, nor those a later step
    // added to a dictation's review and its proposals (step 16: a review's mode, consent and sensitivity, a proposal's
    // sensitivity), which a migration after this one gives every row.
    async Task<List<string>> RowsAsync() => await AsSuperuserAsync(
        """
        SELECT t || ' ' || (r - 'subjective' - 'objective' - 'note_template_code')::text
        FROM (SELECT 'clinical_notes' AS t, to_jsonb(n) AS r, n.id FROM ehr.clinical_notes n
              UNION ALL SELECT 'clinical_drafts', to_jsonb(d), d.id FROM ehr.clinical_drafts d
              UNION ALL SELECT 'note_addenda', to_jsonb(a), a.id FROM ehr.note_addenda a
              UNION ALL SELECT 'ai_note_reviews', to_jsonb(v) - 'mode' - 'consent_id' - 'sensitive', v.id FROM ehr.ai_note_reviews v
              UNION ALL SELECT 'ai_field_proposals', to_jsonb(p) - 'sensitive', p.id FROM ehr.ai_field_proposals p) AS rows
        ORDER BY t, id
        """);

    const string Logged = "SELECT (SELECT count(*) FROM ehr.audit_events)::text || ' ' || (SELECT count(*) FROM ehr.change_log)::text";

    const string Protection = "SELECT relrowsecurity::text || ' ' || relforcerowsecurity::text FROM pg_class WHERE oid = 'ehr.clinical_drafts'::regclass";

    Task<List<string>> ColumnsAsync() => AsSuperuserAsync(
        """
        SELECT table_name || '.' || column_name || ' ' || is_nullable FROM information_schema.columns
        WHERE table_schema = 'ehr' AND table_name IN ('clinical_notes', 'clinical_drafts')
          AND column_name IN ('history', 'subjective', 'objective', 'note_template_code')
        ORDER BY 1
        """);

    [Fact]
    public async Task Nothing_written_moves_the_migration_says_so_and_it_goes_down_only_while_nothing_is_written_in_four_sections()
    {
        var clinician = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        var patient = await Accounts.PatientAsync(database, clinician);
        await Accounts.AssignAsync(database, clinician, patient);
        Assert.Empty(await MigrateAsync(Before));
        Assert.Equal(["clinical_drafts.history YES", "clinical_notes.history NO"], await ColumnsAsync());
        Assert.Equal(["true true"], await AsSuperuserAsync(Protection));

        // Signed before step 14: an ordinary note; one at the limit with CRLF line ends, the word "Objective", combining
        // marks, an emoji, right-to-left text and a zero-width space; and a sensitive one with two addenda.
        await EarlierNotes.SignedAsync(database, clinician, patient, "Synthetic history: cough for two days.");
        var awkward = "Synthetic history: Objective findings were dictated here.\r\nTemperature 37.9.\r\nCafe\u0301 \U0001FA7A \u05E2\u05D1\u05E8\u05D9\u05EA \u200B end.\r\n";
        await EarlierNotes.SignedAsync(database, clinician, patient, awkward + new string('x', 16_000 - awkward.EnumerateRunes().Count()));
        var (_, sensitive) = await EarlierNotes.SignedAsync(database, clinician, patient, "Synthetic history: low mood.", sensitive: true);
        await PlantAsync(
            """
            INSERT INTO ehr.note_addenda (id, note_id, patient_id, author_id, text, signed_at, sensitive, sensitive_inherited)
            VALUES (gen_random_uuid(), @note, @patient, @clinician, 'Synthetic addendum one.', now(), true, true),
                   (gen_random_uuid(), @note, @patient, @clinician, 'Synthetic addendum two.', now(), true, true)
            """, ("note", sensitive), ("patient", patient), ("clinician", clinician));
        // Unsigned: a draft saved by the editor, with a dictation review waiting on its History proposal; and one last
        // saved by the desktop app, with no sections.
        var waiting = await EarlierNotes.DraftAsync(database, clinician, patient, "");
        await PlantAsync(
            """
            UPDATE ehr.clinical_drafts SET version = 2, structured_version = 2, has_unreviewed_proposals = true WHERE id = @draft;
            INSERT INTO ehr.ai_note_reviews (id, patient_id, draft_id, clinician_id, session_id, max_seconds, max_chunk_bytes, state, started_version, result_json, created_at)
            VALUES (@review, @patient, @draft, @clinician, 'synthetic-session-0001', 180, 2097152, 'pending', 1,
                    '{"sorting":"done","fields":{"history":{"status":"pending","value":"Cough for three days.","sources":[{"start":0,"end":21}],"warnings":[]}}}', now());
            INSERT INTO ehr.ai_field_proposals (id, review_id, patient_id, field, original_value, state)
            VALUES (gen_random_uuid(), @review, @patient, 'history', 'Cough for three days.', 'pending');
            INSERT INTO ehr.clinical_drafts (id, patient_id, clinician_id, body, version, has_unreviewed_proposals)
            VALUES (gen_random_uuid(), @patient, @clinician, 'Synthetic text typed offline.', 3, false);
            """, ("draft", waiting), ("review", Guid.CreateVersion7()), ("patient", patient), ("clinician", clinician));

        var rows = await RowsAsync();
        var logged = await AsSuperuserAsync(Logged);
        Assert.Equal(
            [
                "Migration 14: 3 signed notes keep their History, Assessment and Plan as signed; no row was changed",
                "Migration 14: unsigned drafts were not read; each opens in the editor with its History in Subjective, and changes only when its author saves it",
            ],
            await MigrateAsync(null));
        await database.ApplyAsync();
        Assert.Equal(rows, await RowsAsync());
        Assert.Equal(logged, await AsSuperuserAsync(Logged));
        Assert.Equal(["0"], await AsSuperuserAsync(
            "SELECT (SELECT count(*) FROM ehr.clinical_notes WHERE subjective IS NOT NULL OR objective IS NOT NULL) + (SELECT count(*) FROM ehr.clinical_drafts WHERE subjective IS NOT NULL OR objective IS NOT NULL OR note_template_code IS NOT NULL)"));
        Assert.Equal(
            ["clinical_drafts.history YES", "clinical_drafts.note_template_code YES", "clinical_drafts.objective YES", "clinical_drafts.subjective YES",
             "clinical_notes.history YES", "clinical_notes.objective YES", "clinical_notes.subjective YES"],
            await ColumnsAsync());
        Assert.Equal(["5"], await AsSuperuserAsync("SELECT count(*) FROM ehr.note_templates"));

        // Nothing is written in four sections yet: it goes down, the notes' History is required again, and up again.
        Assert.Empty(await MigrateAsync(Before));
        Assert.Equal(["clinical_drafts.history YES", "clinical_notes.history NO"], await ColumnsAsync());
        Assert.Equal(["true true"], await AsSuperuserAsync(Protection));
        Assert.Equal(rows, await RowsAsync());
        Assert.Equal(2, (await MigrateAsync(null)).Count);
        await database.ApplyAsync();
        Assert.Equal(rows, await RowsAsync());

        // A draft written in four sections: down is refused, nothing changes, and the drafts' row-level security, lifted
        // for the check, is enabled and forced again.
        var draft = Guid.CreateVersion7();
        await Accounts.ExecuteAsync(App, clinician,
            """
            INSERT INTO ehr.clinical_drafts (id, patient_id, clinician_id, body, version, subjective, objective, assessment, plan, structured_version, has_unreviewed_proposals)
            VALUES (@id, @patient, @owner, 'Synthetic plain text', 1, 'Synthetic subjective', 'Synthetic objective', 'Synthetic assessment', 'Synthetic plan', 1, false)
            """, ("id", draft), ("patient", patient), ("owner", clinician));
        await AssertNotUndoneAsync("drafts have been written in Subjective, Objective, Assessment and Plan");

        // And a note signed in them.
        await Accounts.ExecuteAsync(App, clinician,
            """
            INSERT INTO ehr.clinical_notes (id, draft_id, patient_id, draft_version, subjective, objective, assessment, plan)
            VALUES (@note, @id, @patient, 1, 'Synthetic subjective', 'Synthetic objective', 'Synthetic assessment', 'Synthetic plan')
            """, ("note", Guid.CreateVersion7()), ("id", draft), ("patient", patient));
        await AssertNotUndoneAsync("notes have been signed in Subjective, Objective, Assessment and Plan");
    }

    async Task AssertNotUndoneAsync(string why)
    {
        var columns = await ColumnsAsync();
        var rows = await AsSuperuserAsync("SELECT to_jsonb(d)::text FROM ehr.clinical_drafts d ORDER BY id");
        // EF's execution strategy wraps what the database said.
        var refused = Assert.IsType<PostgresException>((await Assert.ThrowsAsync<InvalidOperationException>(() => MigrateAsync(Before))).InnerException);
        Assert.Equal("55000", refused.SqlState);
        Assert.Contains(why, refused.MessageText, StringComparison.Ordinal);
        Assert.Equal(columns, await ColumnsAsync());
        Assert.Equal(rows, await AsSuperuserAsync("SELECT to_jsonb(d)::text FROM ehr.clinical_drafts d ORDER BY id"));
        Assert.Equal(["true true"], await AsSuperuserAsync(Protection));
        Assert.Equal(["0"], await Accounts.QueryAsync(database.OwnerConnectionString, null, "SELECT count(*) FROM ehr.clinical_drafts"));
    }
}
