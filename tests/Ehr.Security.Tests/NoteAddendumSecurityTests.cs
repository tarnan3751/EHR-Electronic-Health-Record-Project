using Ehr.Data.Accounts;
using Ehr.Testing;
using Npgsql;
using Xunit;

namespace Ehr.Security.Tests;

// The expansion plan's step 10: what the database lets each account do with an addendum to a signed note, and with a
// note marked sensitive, as the runtime roles, in SQL of its own (db/policies/10-grants.sql, 30-rls.sql, 40-audit.sql
// and 50-clinical-notes.sql). A clinician with the patient on their panel signs an addendum, as themselves, at the
// database's time; the note it is attached to never changes; an addendum is never edited or deleted; an addendum to a
// sensitive note is sensitive whatever was sent; and an administrator reading the audit log never sees a sensitive
// note's or addendum's text.
public class NoteAddendumSecurityTests(MigratedDatabase database) : IClassFixture<MigratedDatabase>
{
    string App => database.ConnectionStringFor("ehr_app", MigratedDatabase.AppPassword);

    string Read => database.ConnectionStringFor("ehr_read", MigratedDatabase.ReadPassword);

    // Who signed and when are sent wrong.
    const string Addendum = """
        INSERT INTO ehr.note_addenda (id, note_id, patient_id, author_id, text, signed_at, sensitive)
        VALUES (gen_random_uuid(), @note, @patient, @author, @text, '2000-01-01', @sensitive)
        """;

    async Task<(Guid Clinician, Guid Patient, Guid Note)> SignedAsync(bool sensitive = false)
    {
        var clinician = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        var patient = await Accounts.PatientAsync(database, clinician);
        await Accounts.AssignAsync(database, clinician, patient);
        var (draft, note) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        await Accounts.ExecuteAsync(App, clinician,
            """
            INSERT INTO ehr.clinical_drafts
                (id, patient_id, clinician_id, body, version, history, assessment, plan, structured_version, has_unreviewed_proposals)
            VALUES (@id, @patient, @owner, 'Synthetic plain text', 1, 'Synthetic history', 'Synthetic assessment', 'Synthetic plan', 1, false)
            """, ("id", draft), ("patient", patient), ("owner", clinician));
        await Accounts.ExecuteAsync(App, clinician,
            """
            INSERT INTO ehr.clinical_notes (id, draft_id, patient_id, draft_version, history, assessment, plan, sensitive)
            VALUES (@note, @draft, @patient, 1, 'Synthetic history', 'Synthetic assessment', 'Synthetic plan', @sensitive)
            """, ("note", note), ("draft", draft), ("patient", patient), ("sensitive", sensitive));
        return (clinician, patient, note);
    }

    Task AddAsync(Guid? user, Guid patient, Guid note, string text = "Synthetic addendum: culture negative.", bool sensitive = false, Guid? author = null, string? connection = null) =>
        Accounts.ExecuteAsync(connection ?? App, user, Addendum, ("note", note), ("patient", patient), ("author", author ?? user ?? Guid.NewGuid()), ("text", text), ("sensitive", sensitive));

    Task<List<string>> AsSuperuserAsync(string sql, params (string Name, object Value)[] parameters) =>
        Accounts.QueryAsync(database.SuperuserConnectionString, null, sql, parameters);

    async Task RefusedAsync(string state, Func<Task> attempt) =>
        Assert.Equal(state, (await Assert.ThrowsAsync<PostgresException>(attempt)).SqlState);

    [Fact]
    public async Task An_addendum_is_signed_as_the_clinician_at_the_databases_time_is_audited_and_leaves_the_note_as_it_was()
    {
        var (clinician, patient, note) = await SignedAsync();
        var colleague = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        await Accounts.AssignAsync(database, colleague, patient);
        var before = await AsSuperuserAsync("SELECT to_jsonb(n)::text FROM ehr.clinical_notes n WHERE id = @note", ("note", note));

        // A colleague on the panel may add one too: it is theirs, not the note's signer's.
        await AddAsync(clinician, patient, note);
        await AddAsync(colleague, patient, note, "Synthetic addendum: seen in follow-up.");

        Assert.Equal([$"{clinician} true false Synthetic addendum: culture negative.", $"{colleague} true false Synthetic addendum: seen in follow-up."],
            await AsSuperuserAsync(
                "SELECT author_id || ' ' || (signed_at > now() - interval '1 minute') || ' ' || sensitive || ' ' || text FROM ehr.note_addenda WHERE note_id = @note ORDER BY signed_at",
                ("note", note)));
        Assert.Equal(before, await AsSuperuserAsync("SELECT to_jsonb(n)::text FROM ehr.clinical_notes n WHERE id = @note", ("note", note)));
        Assert.Equal([clinician.ToString(), colleague.ToString()], await AsSuperuserAsync(
            "SELECT actor_id::text FROM ehr.audit_events WHERE table_name = 'note_addenda' AND action = 'insert' AND patient_id = @patient ORDER BY occurred_at", ("patient", patient)));
        // Signing as someone else: the trigger makes it the caller's, whoever was named.
        await AddAsync(clinician, patient, note, "Synthetic addendum: named another author.", author: colleague);
        Assert.Equal([clinician.ToString()], await AsSuperuserAsync(
            "SELECT author_id::text FROM ehr.note_addenda WHERE note_id = @note AND text LIKE '%named another author.'", ("note", note)));
    }

    [Fact]
    public async Task Only_a_clinician_with_the_patient_on_their_panel_signs_an_addendum_and_only_clinicians_read_one()
    {
        var (clinician, patient, note) = await SignedAsync();
        await AddAsync(clinician, patient, note);
        var outsider = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        var admin = await Accounts.StaffAsync(database, StaffRoles.Admin);
        var account = await Accounts.PatientAccountAsync(database, patient);

        // A clinician without the patient on their panel can read the note, and is refused by the policy. Anyone else
        // can't read the note at all, so the signing trigger refuses first: the note is unavailable to them.
        await RefusedAsync(PostgresErrorCodes.InsufficientPrivilege, () => AddAsync(outsider, patient, note));
        foreach (var user in new Guid?[] { admin, account, null })
        {
            await RefusedAsync(PostgresErrorCodes.CheckViolation, () => AddAsync(user, patient, note));
        }

        await RefusedAsync(PostgresErrorCodes.InsufficientPrivilege, () => AddAsync(clinician, patient, note, connection: Read));

        const string read = "SELECT count(*)::text FROM ehr.note_addenda WHERE note_id = @note";
        foreach (var connection in new[] { App, Read })
        {
            // Read on the chart's model: any clinician, as the note is. Nobody else.
            Assert.Equal(["1"], await Accounts.QueryAsync(connection, clinician, read, ("note", note)));
            Assert.Equal(["1"], await Accounts.QueryAsync(connection, outsider, read, ("note", note)));
            foreach (var user in new Guid?[] { admin, account, null })
            {
                Assert.Equal(["0"], await Accounts.QueryAsync(connection, user, read, ("note", note)));
            }
        }
    }

    [Fact]
    public async Task An_addendum_names_a_note_of_the_same_patient_has_text_within_the_limit_and_is_never_changed()
    {
        var (clinician, patient, note) = await SignedAsync();
        var elsewhere = await Accounts.PatientAsync(database, clinician);
        await Accounts.AssignAsync(database, clinician, elsewhere);

        // Another patient's chart can't take this note's addendum; a note that doesn't exist takes none.
        await RefusedAsync(PostgresErrorCodes.CheckViolation, () => AddAsync(clinician, elsewhere, note));
        await RefusedAsync(PostgresErrorCodes.CheckViolation, () => AddAsync(clinician, patient, Guid.NewGuid()));
        await RefusedAsync(PostgresErrorCodes.CheckViolation, () => AddAsync(clinician, patient, note, "   "));
        await RefusedAsync(PostgresErrorCodes.CheckViolation, () => AddAsync(clinician, patient, note, new string('x', 16001)));

        await AddAsync(clinician, patient, note);
        foreach (var connection in new[] { App, Read })
        {
            await RefusedAsync(PostgresErrorCodes.InsufficientPrivilege, () => Accounts.ExecuteAsync(connection, clinician,
                "UPDATE ehr.note_addenda SET text = 'Changed' WHERE note_id = @note", ("note", note)));
            await RefusedAsync(PostgresErrorCodes.InsufficientPrivilege, () => Accounts.ExecuteAsync(connection, clinician,
                "DELETE FROM ehr.note_addenda WHERE note_id = @note", ("note", note)));
        }

        // Even the tables' owner, who has every privilege, is stopped by the trigger; and the note itself still is.
        await RefusedAsync(PostgresErrorCodes.CheckViolation, () => Accounts.ExecuteAsync(database.SuperuserConnectionString, null,
            "UPDATE ehr.note_addenda SET text = 'Changed' WHERE note_id = @note", ("note", note)));
        await RefusedAsync(PostgresErrorCodes.CheckViolation, () => Accounts.ExecuteAsync(database.SuperuserConnectionString, null,
            "UPDATE ehr.clinical_notes SET sensitive = true WHERE id = @note", ("note", note)));
        Assert.Equal(["Synthetic addendum: culture negative."], await AsSuperuserAsync("SELECT text FROM ehr.note_addenda WHERE note_id = @note", ("note", note)));
    }

    [Fact]
    public async Task A_sensitive_notes_addenda_are_sensitive_and_an_administrator_never_sees_sensitive_text_in_the_audit_log()
    {
        var (clinician, patient, sensitiveNote) = await SignedAsync(sensitive: true);
        var (author, other, ordinaryNote) = await SignedAsync();
        var admin = await Accounts.StaffAsync(database, StaffRoles.Admin);

        // Sent as not sensitive, to a sensitive note: sensitive all the same. To an ordinary note: as its author says.
        await AddAsync(clinician, patient, sensitiveNote, "Synthetic addendum to a sensitive note.");
        await AddAsync(author, other, ordinaryNote, "Synthetic sensitive addendum.", sensitive: true);
        await AddAsync(author, other, ordinaryNote, "Synthetic ordinary addendum.");
        Assert.Equal(["true"], await AsSuperuserAsync("SELECT sensitive::text FROM ehr.note_addenda WHERE note_id = @note", ("note", sensitiveNote)));
        Assert.Equal(["false", "true"], await AsSuperuserAsync("SELECT sensitive::text FROM ehr.note_addenda WHERE note_id = @note ORDER BY sensitive", ("note", ordinaryNote)));

        // What an administrator's audit page is given: that the sensitive rows were written, never what they say.
        const string shown = """
            SELECT e.table_name || ' ' || e.sensitive || ' ' || coalesce((SELECT d.details::text FROM ehr.shown_audit_details(ARRAY[e.id]) d), 'hidden')
            FROM ehr.audit_events e WHERE e.patient_id = @patient AND e.table_name IN ('clinical_notes', 'note_addenda') ORDER BY e.table_name, e.sensitive
            """;
        var first = await Accounts.QueryAsync(App, admin, shown, ("patient", patient));
        Assert.Equal(["clinical_notes true hidden", "note_addenda true hidden"], first);

        // The sensitive note was a draft first, and the draft's events hold the same text: marked with the note, so an
        // administrator gets none of it. An ordinary note's draft events are shown as any write is.
        const string drafts = """
            SELECT e.sensitive || ' ' || coalesce((SELECT d.details::text FROM ehr.shown_audit_details(ARRAY[e.id]) d), 'hidden')
            FROM ehr.audit_events e WHERE e.patient_id = @patient AND e.table_name = 'clinical_drafts' ORDER BY e.occurred_at
            """;
        var sensitiveDraft = await Accounts.QueryAsync(App, admin, drafts, ("patient", patient));
        Assert.NotEmpty(sensitiveDraft);
        Assert.All(sensitiveDraft, line => Assert.Equal("true hidden", line));
        var ordinaryDraft = await Accounts.QueryAsync(App, admin, drafts, ("patient", other));
        Assert.Contains(ordinaryDraft, line => line.StartsWith("false ", StringComparison.Ordinal) && line.Contains("Synthetic history", StringComparison.Ordinal));
        var second = await Accounts.QueryAsync(App, admin, shown, ("patient", other));
        Assert.Equal(3, second.Count);
        Assert.Contains("Synthetic history", second[0]);
        Assert.StartsWith("clinical_notes false ", second[0]);
        Assert.Contains("Synthetic ordinary addendum.", second[1]);
        Assert.Equal("note_addenda true hidden", second[2]);
        Assert.DoesNotContain(second, line => line.Contains("Synthetic sensitive addendum.", StringComparison.Ordinal));
    }
}
