using Ehr.Data.Accounts;
using Ehr.Testing;
using Npgsql;
using Xunit;

namespace Ehr.Security.Tests;

// Direct SQL as runtime roles: these invariants must hold even if an application check is missed.
public class ClinicalNoteSecurityTests(MigratedDatabase database) : IClassFixture<MigratedDatabase>
{
    string App => database.ConnectionStringFor("ehr_app", MigratedDatabase.AppPassword);
    string Read => database.ConnectionStringFor("ehr_read", MigratedDatabase.ReadPassword);

    const string InsertNote = """
        INSERT INTO ehr.clinical_notes (id, draft_id, patient_id, draft_version, subjective, objective, assessment, plan, signer_id, signed_at)
        VALUES (@note, @id, @patient, @version, @subjective, 'Synthetic objective', 'Synthetic assessment', 'Synthetic plan', @spoofed, '2000-01-01')
        """;

    async Task<(Guid Clinician, Guid Patient, Guid Draft)> DraftAsync()
    {
        var clinician = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        var patient = await Accounts.PatientAsync(database, clinician);
        await Accounts.AssignAsync(database, clinician, patient);
        var draft = Guid.CreateVersion7();
        await Accounts.ExecuteAsync(App, clinician,
            """
            INSERT INTO ehr.clinical_drafts
                (id, patient_id, clinician_id, body, version, subjective, objective, assessment, plan, structured_version, has_unreviewed_proposals)
            VALUES (@id, @patient, @owner, 'Synthetic plain text', 1, 'Synthetic subjective', 'Synthetic objective', 'Synthetic assessment', 'Synthetic plan', 1, false)
            """, ("id", draft), ("patient", patient), ("owner", clinician));
        return (clinician, patient, draft);
    }

    Task SignAsync(Guid clinician, Guid patient, Guid draft, long version = 1, string subjective = "Synthetic subjective", Guid? spoofed = null) =>
        Accounts.ExecuteAsync(App, clinician, InsertNote, ("note", Guid.CreateVersion7()), ("id", draft), ("patient", patient),
            ("version", version), ("subjective", subjective), ("spoofed", spoofed ?? clinician));

    [Fact]
    public async Task Drafts_are_private_in_both_runtime_roles_and_cannot_be_reassigned()
    {
        var (clinician, patient, draft) = await DraftAsync();
        var colleague = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        var admin = await Accounts.StaffAsync(database, StaffRoles.Admin);
        var patientAccount = await Accounts.PatientAccountAsync(database, patient);
        await Accounts.AssignAsync(database, colleague, patient);
        const string read = "SELECT count(*) FROM ehr.clinical_drafts WHERE id = @id";
        foreach (var connection in new[] { App, Read })
        {
            Assert.Equal(["1"], await Accounts.QueryAsync(connection, clinician, read, ("id", draft)));
            foreach (var denied in new Guid?[] { colleague, admin, patientAccount, null, Guid.NewGuid() })
                Assert.Equal(["0"], await Accounts.QueryAsync(connection, denied, read, ("id", draft)));
        }
        Assert.Empty(await Accounts.QueryAsync(App, colleague,
            "UPDATE ehr.clinical_drafts SET body = 'Refused', version = version + 1 WHERE id = @id RETURNING id", ("id", draft)));
        await Assert.ThrowsAsync<PostgresException>(() => SignAsync(colleague, patient, draft));
        await Assert.ThrowsAsync<PostgresException>(() => Accounts.ExecuteAsync(App, clinician,
            "UPDATE ehr.clinical_drafts SET clinician_id = @other, version = version + 1 WHERE id = @id", ("id", draft), ("other", colleague)));
        await Accounts.ExecuteAsync(App, clinician, "UPDATE ehr.patient_assignments SET ended_at = now() WHERE patient_id = @id", ("id", patient));
        Assert.Equal(["0"], await Accounts.QueryAsync(App, clinician, read, ("id", draft)));
        await Assert.ThrowsAsync<PostgresException>(() => SignAsync(clinician, patient, draft));
    }

    [Fact]
    public async Task Signed_records_have_server_identity_time_audit_and_no_runtime_mutation_path()
    {
        var (clinician, patient, draft) = await DraftAsync();
        var spoofed = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        await SignAsync(clinician, patient, draft, spoofed: spoofed);
        Assert.Equal([clinician.ToString()], await Accounts.QueryAsync(App, clinician,
            "SELECT signer_id FROM ehr.clinical_notes WHERE draft_id = @id AND signed_at > now() - interval '1 minute'", ("id", draft)));
        Assert.Equal(["1"], await Accounts.QueryAsync(database.SuperuserConnectionString, null,
            "SELECT count(*) FROM ehr.audit_events WHERE action = 'insert' AND table_name = 'clinical_notes' AND patient_id = @id AND actor_id = @actor",
            ("id", patient), ("actor", clinician)));
        await Assert.ThrowsAsync<PostgresException>(() => Accounts.ExecuteAsync(App, clinician,
            "UPDATE ehr.clinical_drafts SET body = 'Refused', version = version + 1 WHERE id = @id", ("id", draft)));
        await Assert.ThrowsAsync<PostgresException>(() => Accounts.ExecuteAsync(App, clinician,
            "UPDATE ehr.clinical_notes SET subjective = 'Refused' WHERE draft_id = @id", ("id", draft)));
        await Assert.ThrowsAsync<PostgresException>(() => Accounts.ExecuteAsync(App, clinician,
            "DELETE FROM ehr.clinical_notes WHERE draft_id = @id", ("id", draft)));
        // Even a privileged maintenance write cannot silently alter a signed clinical record.
        await Assert.ThrowsAsync<PostgresException>(() => Accounts.ExecuteAsync(database.SuperuserConnectionString, null,
            "UPDATE ehr.clinical_notes SET subjective = 'Refused' WHERE draft_id = @id", ("id", draft)));
        await Assert.ThrowsAsync<PostgresException>(() => SignAsync(clinician, patient, draft, version: 2));
        Assert.Equal(["1"], await Accounts.QueryAsync(App, clinician,
            "SELECT count(*) FROM ehr.clinical_notes WHERE draft_id = @id", ("id", draft)));
    }

    [Fact]
    public async Task An_update_waiting_on_a_signature_lock_cannot_modify_the_finalized_draft()
    {
        var (clinician, patient, draft) = await DraftAsync();
        await using var signer = new NpgsqlConnection(App);
        await using var editor = new NpgsqlConnection(App);
        await signer.OpenAsync(TestContext.Current.CancellationToken);
        await editor.OpenAsync(TestContext.Current.CancellationToken);
        await using var signing = await signer.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await using var editing = await editor.BeginTransactionAsync(TestContext.Current.CancellationToken);
        foreach (var (connection, transaction) in new[] { (signer, signing), (editor, editing) })
        {
            await using var set = new NpgsqlCommand("SELECT set_config('app.user_id', @actor, true)", connection, transaction);
            set.Parameters.AddWithValue("actor", clinician.ToString());
            await set.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        await using var sign = new NpgsqlCommand(InsertNote, signer, signing);
        sign.Parameters.AddWithValue("note", Guid.CreateVersion7());
        sign.Parameters.AddWithValue("id", draft);
        sign.Parameters.AddWithValue("patient", patient);
        sign.Parameters.AddWithValue("version", 1L);
        sign.Parameters.AddWithValue("subjective", "Synthetic subjective");
        sign.Parameters.AddWithValue("spoofed", clinician);
        await sign.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        await using var update = new NpgsqlCommand(
            "UPDATE ehr.clinical_drafts SET body = 'Must not overwrite', version = version + 1 WHERE id = @id", editor, editing);
        update.Parameters.AddWithValue("id", draft);
        var waiting = update.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        // Observe the competing writer actually waiting, rather than relying on request scheduling or sleeps.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            var blocked = await Accounts.QueryAsync(database.SuperuserConnectionString, null,
                "SELECT cardinality(pg_blocking_pids(@pid))", ("pid", editor.ProcessID));
            if (blocked[0] != "0") break;
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }
        Assert.False(waiting.IsCompleted);
        await signing.CommitAsync(TestContext.Current.CancellationToken);
        var error = await Assert.ThrowsAsync<PostgresException>(async () => await waiting);
        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        Assert.Equal(["Synthetic plain text"], await Accounts.QueryAsync(App, clinician,
            "SELECT body FROM ehr.clinical_drafts WHERE id = @id", ("id", draft)));
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("stale")]
    [InlineData("incomplete")]
    [InlineData("tampered")]
    [InlineData("wrong patient")]
    public async Task Database_refuses_unreviewed_stale_incomplete_and_fabricated_signed_content(string kind)
    {
        var (clinician, patient, draft) = await DraftAsync();
        if (kind == "pending")
            await Accounts.ExecuteAsync(App, clinician,
                "UPDATE ehr.clinical_drafts SET has_unreviewed_proposals = true, version = 2, structured_version = 2 WHERE id = @id", ("id", draft));
        if (kind == "incomplete")
            await Accounts.ExecuteAsync(App, clinician,
                "UPDATE ehr.clinical_drafts SET plan = '  ', version = 2, structured_version = 2 WHERE id = @id", ("id", draft));
        var targetPatient = kind == "wrong patient" ? await Accounts.PatientAsync(database, clinician) : patient;
        var error = await Assert.ThrowsAsync<PostgresException>(() => SignAsync(clinician, targetPatient, draft,
            version: kind is "pending" or "incomplete" or "stale" ? 2 : 1,
            subjective: kind == "tampered" ? "Fabricated" : "Synthetic subjective"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        Assert.Equal(["0"], await Accounts.QueryAsync(App, clinician,
            "SELECT count(*) FROM ehr.clinical_notes WHERE draft_id = @id", ("id", draft)));
    }

    // The expansion plan's step 14: a note is signed in Subjective, Objective, Assessment and Plan, each as the draft saved
    // it; a note is never sent with a History, nor signed from a draft that still has one; and no note or draft holds both
    // shapes, or a note neither, whoever writes it.
    [Fact]
    public async Task Notes_are_signed_only_in_the_four_sections_the_draft_saved()
    {
        var (clinician, patient, draft) = await DraftAsync();
        var tampered = await Assert.ThrowsAsync<PostgresException>(() => Accounts.ExecuteAsync(App, clinician,
            InsertNote.Replace("'Synthetic objective'", "'Fabricated objective'", StringComparison.Ordinal), ("note", Guid.CreateVersion7()), ("id", draft),
            ("patient", patient), ("version", 1L), ("subjective", "Synthetic subjective"), ("spoofed", clinician)));
        Assert.Equal(PostgresErrorCodes.CheckViolation, tampered.SqlState);
        var withHistory = await Assert.ThrowsAsync<PostgresException>(() => Accounts.ExecuteAsync(App, clinician,
            """
            INSERT INTO ehr.clinical_notes (id, draft_id, patient_id, draft_version, history, assessment, plan)
            VALUES (@note, @id, @patient, 1, 'Synthetic history', 'Synthetic assessment', 'Synthetic plan')
            """, ("note", Guid.CreateVersion7()), ("id", draft), ("patient", patient)));
        Assert.Equal(PostgresErrorCodes.CheckViolation, withHistory.SqlState);
        await Accounts.ExecuteAsync(App, clinician,
            "UPDATE ehr.clinical_drafts SET objective = ' ', version = 2, structured_version = 2 WHERE id = @id", ("id", draft));
        var blank = await Assert.ThrowsAsync<PostgresException>(() => Accounts.ExecuteAsync(App, clinician,
            InsertNote.Replace("'Synthetic objective'", "' '", StringComparison.Ordinal), ("note", Guid.CreateVersion7()), ("id", draft),
            ("patient", patient), ("version", 2L), ("subjective", "Synthetic subjective"), ("spoofed", clinician)));
        Assert.Contains("Subjective, Objective, Assessment and Plan", blank.MessageText, StringComparison.Ordinal);
        // A draft that holds a History and the new sections: refused, as the app or the superuser.
        var both = await Assert.ThrowsAsync<PostgresException>(() => Accounts.ExecuteAsync(App, clinician,
            "UPDATE ehr.clinical_drafts SET history = 'Synthetic history', version = 3, structured_version = 3 WHERE id = @id", ("id", draft)));
        Assert.Equal("ck_clinical_drafts_sections", both.ConstraintName);
        Assert.Equal(["0"], await Accounts.QueryAsync(App, clinician, "SELECT count(*) FROM ehr.clinical_notes WHERE draft_id = @id", ("id", draft)));

        // Written before step 14: its History stays until its author saves the four sections, and it isn't signed until then.
        var earlier = await EarlierNotes.DraftAsync(database, clinician, patient, "Synthetic history: written before four sections.");
        var unsorted = await Assert.ThrowsAsync<PostgresException>(() => Accounts.ExecuteAsync(App, clinician,
            """
            INSERT INTO ehr.clinical_notes (id, draft_id, patient_id, draft_version, subjective, objective, assessment, plan)
            VALUES (@note, @id, @patient, 1, 'Synthetic history: written before four sections.', 'x', 'Synthetic assessment: earlier.', 'Synthetic plan: earlier.')
            """, ("note", Guid.CreateVersion7()), ("id", earlier), ("patient", patient)));
        Assert.Contains("Save the draft in Subjective, Objective, Assessment and Plan", unsorted.MessageText, StringComparison.Ordinal);

        // No note in both shapes or neither, even with every trigger held.
        foreach (var (columns, values) in new[]
        {
            ("history, subjective, objective", "'H', 'S', 'O'"),
            ("subjective", "'S'"),
            ("history, objective", "'H', 'O'"),
        })
        {
            var shape = await Assert.ThrowsAsync<PostgresException>(() => Accounts.ExecuteAsync(database.SuperuserConnectionString, null,
                $"""
                SET LOCAL session_replication_role = replica;
                INSERT INTO ehr.clinical_notes (id, draft_id, patient_id, draft_version, {columns}, assessment, plan, signer_id, signed_at)
                VALUES (gen_random_uuid(), @id, @patient, 1, {values}, 'A', 'P', @clinician, now())
                """, ("id", earlier), ("patient", patient), ("clinician", clinician)));
            Assert.Equal("ck_clinical_notes_sections", shape.ConstraintName);
        }
    }

    // A section that shows nothing isn't signed (step 14): whitespace of every kind and the characters step 13a's summary
    // rule strips, alone or together, in any of the four sections. One with anything visible among them is signed, and
    // kept exactly as written. As the app role signs, copying the draft as saved.
    [Theory]
    [InlineData("subjective", "\u200B")]
    [InlineData("objective", "\u00A0")]
    [InlineData("assessment", "\u2060\uFEFF\u3000\n")]
    [InlineData("plan", "\t\u00AD\u202F\U000E0041\u1680\u180E")]
    public async Task A_section_that_shows_nothing_is_not_signed(string section, string blank)
    {
        var (clinician, patient, draft) = await DraftAsync();
        const string sign = """
            INSERT INTO ehr.clinical_notes (id, draft_id, patient_id, draft_version, subjective, objective, assessment, plan)
            SELECT gen_random_uuid(), d.id, d.patient_id, d.version, d.subjective, d.objective, d.assessment, d.plan FROM ehr.clinical_drafts d WHERE d.id = @id
            """;
        Task SaveAsync(string text, long version) => Accounts.ExecuteAsync(App, clinician,
            $"UPDATE ehr.clinical_drafts SET {section} = @text, version = @version, structured_version = @version WHERE id = @id",
            ("id", draft), ("text", text), ("version", version));

        await SaveAsync(blank, 2);
        var refused = await Assert.ThrowsAsync<PostgresException>(() => Accounts.ExecuteAsync(App, clinician, sign, ("id", draft)));
        Assert.Equal(PostgresErrorCodes.CheckViolation, refused.SqlState);
        Assert.Contains("Complete Subjective, Objective, Assessment and Plan", refused.MessageText, StringComparison.Ordinal);

        await SaveAsync(blank + "x" + blank, 3);
        await Accounts.ExecuteAsync(App, clinician, sign, ("id", draft));
        Assert.Equal([blank + "x" + blank], await Accounts.QueryAsync(App, clinician, $"SELECT {section} FROM ehr.clinical_notes WHERE draft_id = @id", ("id", draft)));
    }

    // What shows nothing in a section is what the summary rule strips (52-visit-summaries.sql): the signing rule's list
    // of those characters is the summary rule's, character for character, so neither changes without the other.
    [Fact]
    public async Task The_signing_rule_takes_out_what_the_summary_rule_strips()
    {
        const string list = """SELECT substring(pg_get_functiondef(@name::regprocedure) FROM '[[][\\]u00AD[^]]*[]]')""";
        var summary = await Accounts.QueryAsync(database.SuperuserConnectionString, null, list, ("name", "ehr.visit_summary_problems(uuid, uuid, text[])"));
        Assert.StartsWith("[\u00AD", Assert.Single(summary));
        Assert.Equal(summary, await Accounts.QueryAsync(database.SuperuserConnectionString, null, list, ("name", "ehr.note_section_is_blank(text)")));
    }

    // A note signed before step 14 keeps its History, Assessment and Plan as signed: it is still marked sensitive and
    // unmarked, and nothing turns it into a note of four sections, the superuser included.
    [Fact]
    public async Task A_note_signed_before_four_sections_keeps_its_history_and_still_changes_only_its_sensitivity()
    {
        var clinician = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        var patient = await Accounts.PatientAsync(database, clinician);
        await Accounts.AssignAsync(database, clinician, patient);
        var (_, note) = await EarlierNotes.SignedAsync(database, clinician, patient, "Synthetic history: signed before four sections.");
        const string read = "SELECT coalesce(history, '-') || '|' || coalesce(subjective, '-') || '|' || coalesce(objective, '-') || '|' || sensitive FROM ehr.clinical_notes WHERE id = @id";
        Assert.Equal(["Synthetic history: signed before four sections.|-|-|false"], await Accounts.QueryAsync(App, clinician, read, ("id", note)));

        await Accounts.ExecuteAsync(App, clinician, "UPDATE ehr.clinical_notes SET sensitive = true, sensitive_reason = 'Synthetic reason' WHERE id = @id", ("id", note));
        await Accounts.ExecuteAsync(App, clinician, "UPDATE ehr.clinical_notes SET sensitive = false, sensitive_reason = 'Synthetic reason again' WHERE id = @id", ("id", note));
        foreach (var change in new[] { "subjective = history, objective = 'O', history = NULL", "history = 'Rewritten'", "objective = 'O'" })
        {
            await Assert.ThrowsAsync<PostgresException>(() => Accounts.ExecuteAsync(App, clinician, $"UPDATE ehr.clinical_notes SET {change} WHERE id = @id", ("id", note)));
            await Assert.ThrowsAsync<PostgresException>(() => Accounts.ExecuteAsync(database.SuperuserConnectionString, null, $"UPDATE ehr.clinical_notes SET {change} WHERE id = @id", ("id", note)));
        }

        Assert.Equal(["Synthetic history: signed before four sections.|-|-|false"], await Accounts.QueryAsync(App, clinician, read, ("id", note)));
    }

    [Fact]
    public async Task Signed_notes_follow_clinician_chart_access_and_never_expose_drafts_to_admins_or_patients()
    {
        var (clinician, patient, draft) = await DraftAsync();
        await SignAsync(clinician, patient, draft);
        var colleague = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        var admin = await Accounts.StaffAsync(database, StaffRoles.Admin);
        var patientAccount = await Accounts.PatientAccountAsync(database, patient);
        var locked = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        await Accounts.LockOutAsync(database, locked);
        const string read = "SELECT count(*) FROM ehr.clinical_notes WHERE draft_id = @id";
        foreach (var connection in new[] { App, Read })
        {
            Assert.Equal(["1"], await Accounts.QueryAsync(connection, colleague, read, ("id", draft)));
            foreach (var denied in new Guid?[] { admin, patientAccount, locked, null, Guid.NewGuid() })
                Assert.Equal(["0"], await Accounts.QueryAsync(connection, denied, read, ("id", draft)));
        }
    }

    [Fact]
    public async Task Read_only_database_connections_cannot_sign_and_leave_no_signed_record()
    {
        var (clinician, patient, draft) = await DraftAsync();
        var readOnly = new NpgsqlConnectionStringBuilder(App) { Options = "-c default_transaction_read_only=on" }.ConnectionString;
        var error = await Assert.ThrowsAsync<PostgresException>(() => Accounts.ExecuteAsync(readOnly, clinician, InsertNote,
            ("note", Guid.CreateVersion7()), ("id", draft), ("patient", patient), ("version", 1L), ("subjective", "Synthetic subjective"), ("spoofed", clinician)));
        Assert.Equal(PostgresErrorCodes.ReadOnlySqlTransaction, error.SqlState);
        Assert.Equal(["0"], await Accounts.QueryAsync(App, clinician,
            "SELECT count(*) FROM ehr.clinical_notes WHERE draft_id = @id", ("id", draft)));
        await Assert.ThrowsAsync<PostgresException>(() => Accounts.ExecuteAsync(Read, clinician, InsertNote,
            ("note", Guid.CreateVersion7()), ("id", draft), ("patient", patient), ("version", 1L), ("subjective", "Synthetic subjective"), ("spoofed", clinician)));
    }
}
