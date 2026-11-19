using Ehr.Data.Accounts;
using Ehr.Testing;
using Npgsql;
using Xunit;

namespace Ehr.Security.Tests;

// The expansion plan's step 10b: what the database lets each account do to a signed note's or addendum's sensitivity
// after signing, and who is told of an addendum, as the runtime roles, in SQL of its own (db/policies/10-grants.sql,
// 30-rls.sql, 40-audit.sql and 50-clinical-notes.sql). A clinician with the patient on their panel marks one
// sensitive, with a reason; only the person who made it sensitive, or the note's signer, unmarks it; the text never
// changes; the audit log keeps the views made before it was marked, and stops giving an administrator its text.
public class NoteSensitivitySecurityTests(MigratedDatabase database) : IClassFixture<MigratedDatabase>
{
    string App => database.ConnectionStringFor("ehr_app", MigratedDatabase.AppPassword);

    string Read => database.ConnectionStringFor("ehr_read", MigratedDatabase.ReadPassword);

    const string Change = "UPDATE ehr.{0} SET sensitive = @sensitive, sensitive_reason = @reason WHERE id = @id";

    async Task<(Guid Signer, Guid Patient, Guid Note, Guid Draft)> SignedAsync()
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
            "INSERT INTO ehr.clinical_notes (id, draft_id, patient_id, draft_version, history, assessment, plan) VALUES (@note, @draft, @patient, 1, 'Synthetic history', 'Synthetic assessment', 'Synthetic plan')",
            ("note", note), ("draft", draft), ("patient", patient));
        return (clinician, patient, note, draft);
    }

    async Task<Guid> ColleagueAsync(Guid patient)
    {
        var colleague = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        await Accounts.AssignAsync(database, colleague, patient);
        return colleague;
    }

    async Task<Guid> AddendumAsync(Guid author, Guid patient, Guid note, string text = "Synthetic addendum.")
    {
        var id = Guid.CreateVersion7();
        await Accounts.ExecuteAsync(App, author, "INSERT INTO ehr.note_addenda (id, note_id, patient_id, author_id, text) VALUES (@id, @note, @patient, @author, @text)",
            ("id", id), ("note", note), ("patient", patient), ("author", author), ("text", text));
        return id;
    }

    Task ChangeAsync(Guid? user, string table, Guid id, bool sensitive, string reason = "  Patient asked for it.  ", string? connection = null) =>
        Accounts.ExecuteAsync(connection ?? App, user, string.Format(System.Globalization.CultureInfo.InvariantCulture, Change, table), ("id", id), ("sensitive", sensitive), ("reason", reason));

    Task<List<string>> AsSuperuserAsync(string sql, params (string Name, object Value)[] parameters) =>
        Accounts.QueryAsync(database.SuperuserConnectionString, null, sql, parameters);

    async Task RefusedAsync(string state, Func<Task> attempt) =>
        Assert.Equal(state, (await Assert.ThrowsAsync<PostgresException>(attempt)).SqlState);

    Task<List<string>> StateAsync(string table, Guid id) => AsSuperuserAsync(
        $"SELECT sensitive || ' ' || coalesce(sensitive_marked_by::text, 'none') || ' ' || coalesce(sensitive_reason, 'none') || ' ' || coalesce((sensitive_changed_at > now() - interval '1 minute')::text, 'none') FROM ehr.{table} WHERE id = @id",
        ("id", id));

    [Fact]
    public async Task A_signed_note_is_marked_sensitive_with_a_reason_and_its_addenda_its_audit_trail_and_the_desktop_follow()
    {
        var (signer, patient, note, draft) = await SignedAsync();
        var colleague = await ColleagueAsync(patient);
        var admin = await Accounts.StaffAsync(database, StaffRoles.Admin);
        var addendum = await AddendumAsync(signer, patient, note);
        var text = await AsSuperuserAsync("SELECT history || assessment || plan || signer_id || signed_at FROM ehr.clinical_notes WHERE id = @id", ("id", note));
        var logged = await AsSuperuserAsync("SELECT count(*)::text FROM ehr.change_log WHERE table_name = 'clinical_drafts' AND row_id = @id", ("id", draft));
        const string shown = """
            SELECT e.table_name || ' ' || e.action || ' ' || e.sensitive || ' ' || CASE WHEN (SELECT d.details FROM ehr.shown_audit_details(ARRAY[e.id]) d) IS NULL THEN 'hidden' ELSE 'shown' END
            FROM ehr.audit_events e WHERE e.patient_id = @patient AND e.table_name IN ('clinical_drafts', 'clinical_notes', 'note_addenda') ORDER BY e.occurred_at, e.table_name
            """;
        Assert.All(await Accounts.QueryAsync(App, admin, shown, ("patient", patient)), line => Assert.EndsWith(" false shown", line));

        // A colleague on the panel marks it: not only its signer may.
        await ChangeAsync(colleague, "clinical_notes", note, sensitive: true);

        Assert.Equal([$"true {colleague} Patient asked for it. true"], await StateAsync("clinical_notes", note));
        Assert.Equal(text, await AsSuperuserAsync("SELECT history || assessment || plan || signer_id || signed_at FROM ehr.clinical_notes WHERE id = @id", ("id", note)));
        // Its addendum is sensitive with it, with the same reason.
        Assert.Equal([$"true {colleague} Patient asked for it. true"], await StateAsync("note_addenda", addendum));
        // The administrator's audit page: every earlier event of the note, its draft and its addendum is hidden now,
        // and the marking itself is there, with no values.
        var after = await Accounts.QueryAsync(App, admin, shown, ("patient", patient));
        Assert.All(after, line => Assert.EndsWith(" true hidden", line));
        Assert.Contains("clinical_notes update true hidden", after);
        Assert.Contains("note_addenda update true hidden", after);
        // The desktop's draft sync hears of it: the draft is in the change log again.
        Assert.NotEqual(logged, await AsSuperuserAsync("SELECT count(*)::text FROM ehr.change_log WHERE table_name = 'clinical_drafts' AND row_id = @id", ("id", draft)));
    }

    [Fact]
    public async Task Only_the_person_who_marked_it_or_the_notes_signer_unmarks_it_with_a_reason()
    {
        var (signer, patient, note, _) = await SignedAsync();
        var marker = await ColleagueAsync(patient);
        var other = await ColleagueAsync(patient);
        var addendum = await AddendumAsync(other, patient, note);
        await ChangeAsync(marker, "clinical_notes", note, sensitive: true);

        // A third clinician on the panel: refused, for the note and for the addendum they wrote themselves, which the
        // marker made sensitive.
        await RefusedAsync(PostgresErrorCodes.InsufficientPrivilege, () => ChangeAsync(other, "clinical_notes", note, sensitive: false));
        // While the note is sensitive, so is its addendum, whoever asks.
        await RefusedAsync(PostgresErrorCodes.CheckViolation, () => ChangeAsync(marker, "note_addenda", addendum, sensitive: false));
        await RefusedAsync(PostgresErrorCodes.CheckViolation, () => ChangeAsync(marker, "clinical_notes", note, sensitive: false, reason: "  "));

        // The person who marked it unmarks it; the addendum stays sensitive until it is unmarked itself.
        await ChangeAsync(marker, "clinical_notes", note, sensitive: false, reason: "Marked the wrong note.");
        Assert.Equal(["false none Marked the wrong note. true"], await StateAsync("clinical_notes", note));
        Assert.StartsWith($"true {marker} ", (await StateAsync("note_addenda", addendum)).Single());
        await RefusedAsync(PostgresErrorCodes.InsufficientPrivilege, () => ChangeAsync(other, "note_addenda", addendum, sensitive: false));
        // The note's signer may unmark what someone else marked: the addendum here, and a note marked again.
        await ChangeAsync(signer, "note_addenda", addendum, sensitive: false, reason: "Not sensitive.");
        Assert.Equal(["false none Not sensitive. true"], await StateAsync("note_addenda", addendum));
        await ChangeAsync(other, "clinical_notes", note, sensitive: true);
        await ChangeAsync(signer, "clinical_notes", note, sensitive: false, reason: "Reviewed with the patient.");
        Assert.Equal(["false none Reviewed with the patient. true"], await StateAsync("clinical_notes", note));
    }

    [Fact]
    public async Task Nothing_but_the_sensitivity_changes_and_only_for_a_clinician_with_the_patient_on_their_panel()
    {
        var (signer, patient, note, _) = await SignedAsync();
        var addendum = await AddendumAsync(signer, patient, note);
        var outsider = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        var admin = await Accounts.StaffAsync(database, StaffRoles.Admin);
        var account = await Accounts.PatientAccountAsync(database, patient);

        foreach (var (table, id) in new[] { ("clinical_notes", note), ("note_addenda", addendum) })
        {
            // No row to update for anyone but a panel clinician; no privilege at all on the read-only role.
            foreach (var user in new Guid?[] { outsider, admin, account, null })
            {
                await ChangeAsync(user, table, id, sensitive: true);
            }

            await RefusedAsync(PostgresErrorCodes.InsufficientPrivilege, () => ChangeAsync(signer, table, id, sensitive: true, connection: Read));
            Assert.Equal(["false none none none"], await StateAsync(table, id));

            // The text, who and when: no privilege. As the tables' owner, who has every privilege: the trigger.
            foreach (var column in new[] { table == "clinical_notes" ? "plan = 'Changed'" : "text = 'Changed'", "sensitive_marked_by = gen_random_uuid()", "sensitive_changed_at = '2000-01-01'", "patient_id = gen_random_uuid()" })
            {
                await RefusedAsync(PostgresErrorCodes.InsufficientPrivilege, () => Accounts.ExecuteAsync(App, signer, $"UPDATE ehr.{table} SET {column} WHERE id = @id", ("id", id)));
            }

            await RefusedAsync(PostgresErrorCodes.CheckViolation, () => Accounts.ExecuteAsync(database.SuperuserConnectionString, null,
                $"UPDATE ehr.{table} SET {(table == "clinical_notes" ? "plan" : "text")} = 'Changed', sensitive = true, sensitive_reason = 'x' WHERE id = @id", ("id", id)));
            // It must actually change, and say why.
            await RefusedAsync(PostgresErrorCodes.CheckViolation, () => ChangeAsync(signer, table, id, sensitive: false));
            await RefusedAsync(PostgresErrorCodes.CheckViolation, () => Accounts.ExecuteAsync(App, signer, $"UPDATE ehr.{table} SET sensitive = true WHERE id = @id", ("id", id)));
            await RefusedAsync(PostgresErrorCodes.InsufficientPrivilege, () => Accounts.ExecuteAsync(App, signer, $"DELETE FROM ehr.{table} WHERE id = @id", ("id", id)));
            await RefusedAsync(PostgresErrorCodes.CheckViolation, () => Accounts.ExecuteAsync(database.SuperuserConnectionString, null, $"DELETE FROM ehr.{table} WHERE id = @id", ("id", id)));
            Assert.Equal(["false none none none"], await StateAsync(table, id));
        }
    }

    [Fact]
    public async Task An_addendum_by_someone_else_goes_to_the_signers_inbox_with_no_text()
    {
        var (signer, patient, note, _) = await SignedAsync();
        var colleague = await ColleagueAsync(patient);
        const string items = "SELECT recipient_id || ' ' || kind || ' ' || source_table || ' ' || source_id || ' ' || summary || ' ' || patient_id FROM ehr.inbox_items WHERE patient_id = @patient ORDER BY created_at";

        // The signer's own addendum tells nobody; a colleague's tells the signer, and only the signer.
        await AddendumAsync(signer, patient, note, "Synthetic addendum by the signer.");
        Assert.Empty(await AsSuperuserAsync(items, ("patient", patient)));
        await AddendumAsync(colleague, patient, note, "Synthetic addendum by a colleague.");
        Assert.Equal([$"{signer} addendum clinical_notes {note} An addendum was added to a note you signed {patient}"], await AsSuperuserAsync(items, ("patient", patient)));
        Assert.Single(await Accounts.QueryAsync(App, signer, "SELECT id::text FROM ehr.inbox_items WHERE kind = 'addendum'"));
        Assert.Empty(await Accounts.QueryAsync(App, colleague, "SELECT id::text FROM ehr.inbox_items WHERE kind = 'addendum'"));
        // The apps can't put one there themselves; the signer marks theirs done.
        await RefusedAsync(PostgresErrorCodes.InsufficientPrivilege, () => Accounts.ExecuteAsync(App, colleague,
            "INSERT INTO ehr.inbox_items (id, recipient_id, kind, summary) VALUES (gen_random_uuid(), @to, 'addendum', 'Forged')", ("to", signer)));
        await Accounts.ExecuteAsync(App, signer, "UPDATE ehr.inbox_items SET done_at = now() WHERE kind = 'addendum' AND recipient_id = @me", ("me", signer));

        // A signer who can no longer sign in is not told.
        var (gone, theirs, old, _) = await SignedAsync();
        var covering = await ColleagueAsync(theirs);
        await Accounts.ExecuteAsync(database.SuperuserConnectionString, null, "UPDATE ehr.staff_users SET lockout_end = now() + interval '10 years' WHERE id = @id", ("id", gone));
        await AddendumAsync(covering, theirs, old);
        Assert.Empty(await AsSuperuserAsync(items, ("patient", theirs)));
    }

    // Tahseen's question (2026-11-19): if someone viewed it before it was marked, the audit log already shows that.
    // A view recorded before the mark is still there after it, unchanged, with its time before the mark's. A proxy is
    // the case he named: today the database gives a proxy no signed note or addendum at all, with or without sensitive
    // items in their grant, so a proxy has no view of one to show; that is proved here too, so the day notes reach the
    // portal this test has to be revisited.
    [Fact]
    public async Task Views_made_before_a_note_was_marked_stay_in_the_audit_log_and_a_proxy_is_given_no_note_to_view()
    {
        var (signer, patient, note, _) = await SignedAsync();
        var colleague = await ColleagueAsync(patient);
        var addendum = await AddendumAsync(signer, patient, note);
        // A colleague's view of the note, recorded the way the app records one (AuditRecorder writes this row).
        await Accounts.ExecuteAsync(App, colleague,
            "INSERT INTO ehr.audit_events (id, occurred_at, actor_id, action, table_name, row_id, patient_id, source) VALUES (uuidv7(), now(), @actor, 'read', 'clinical_notes', @note, @patient, 'onprem')",
            ("actor", colleague), ("note", note), ("patient", patient));
        const string views = "SELECT id || ' ' || actor_id || ' ' || action || ' ' || occurred_at FROM ehr.audit_events WHERE patient_id = @patient AND action = 'read' AND row_id = @note";
        var before = await AsSuperuserAsync(views, ("patient", patient), ("note", note));
        Assert.Single(before);

        // A proxy acting for the patient, allowed sensitive items: no note and no addendum, in either role.
        var proxyPatient = await Accounts.PatientAsync(database, signer);
        var proxy = await Accounts.PatientAccountAsync(database, proxyPatient);
        var grant = Guid.CreateVersion7();
        // Given by the clinician, as the clinic gives one (ProxyAccessSecurityTests), and the patient then allows sensitive items.
        await Accounts.ExecuteAsync(App, signer,
            """
            INSERT INTO ehr.proxy_grants (id, patient_id, proxy_email, scope, include_sensitive, expires_at, granted_by, granted_at, granted_by_kind)
            VALUES (@id, @patient, @email, 'record', false, now() + interval '30 days', gen_random_uuid(), '2000-01-01', 'staff')
            """, ("id", grant), ("patient", patient), ("email", $"{proxy:N}@example.com"));
        // Only the patient allows a proxy sensitive items: they do.
        var own = await Accounts.PatientAccountAsync(database, patient);
        await Accounts.ExecuteAsync(App, own, "UPDATE ehr.proxy_grants SET include_sensitive = true WHERE id = @id", ("id", grant));
        Assert.Equal(["true"], await AsSuperuserAsync("SELECT include_sensitive::text FROM ehr.proxy_grants WHERE id = @id", ("id", grant)));
        async Task<List<string>> AsProxyAsync(string connection)
        {
            using (Accounts.ActingUnder(grant))
            {
                return await Accounts.QueryAsync(connection, proxy,
                    "SELECT (SELECT count(*) FROM ehr.patients) || ' ' || (SELECT count(*) FROM ehr.clinical_notes) || ' ' || (SELECT count(*) FROM ehr.note_addenda)");
            }
        }

        foreach (var connection in new[] { App, Read })
        {
            Assert.Equal(["1 0 0"], await AsProxyAsync(connection));
        }

        await ChangeAsync(colleague, "clinical_notes", note, sensitive: true);

        // The earlier view: the same row, the same actor and time, and before the mark.
        Assert.Equal(before, await AsSuperuserAsync(views, ("patient", patient), ("note", note)));
        Assert.Equal(["true"], await AsSuperuserAsync(
            """
            SELECT (e.occurred_at < n.sensitive_changed_at)::text FROM ehr.audit_events e JOIN ehr.clinical_notes n ON n.id = e.row_id
            WHERE e.patient_id = @patient AND e.action = 'read' AND e.row_id = @note
            """, ("patient", patient), ("note", note)));
        // An administrator still sees that the view happened, by whom and when (it has no values to hide).
        var admin = await Accounts.StaffAsync(database, StaffRoles.Admin);
        Assert.Equal([colleague.ToString()], await Accounts.QueryAsync(App, admin,
            "SELECT actor_id::text FROM ehr.audit_events WHERE patient_id = @patient AND action = 'read' AND row_id = @note", ("patient", patient), ("note", note)));
        foreach (var connection in new[] { App, Read })
        {
            Assert.Equal(["1 0 0"], await AsProxyAsync(connection));
        }

        Assert.NotEqual(Guid.Empty, addendum);
    }
}
