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

    Task<List<string>> InheritedAsync(Guid addendum) =>
        AsSuperuserAsync("SELECT sensitive_inherited::text FROM ehr.note_addenda WHERE id = @id", ("id", addendum));

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
        // Its addendum is sensitive with it, with the same reason: inherited from the note, marked by nobody itself.
        Assert.Equal(["true none Patient asked for it. true"], await StateAsync("note_addenda", addendum));
        Assert.Equal(["true"], await InheritedAsync(addendum));
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
        // Marked on its own, before the note is: the mark is the addendum's, and the marker's.
        await ChangeAsync(marker, "note_addenda", addendum, sensitive: true);
        await ChangeAsync(marker, "clinical_notes", note, sensitive: true);
        Assert.Equal(["false"], await InheritedAsync(addendum));

        // A third clinician on the panel: refused, for the note and for the addendum they wrote themselves, which the
        // marker made sensitive.
        // The policy gives them no row to update: nothing changes, whatever the statement does later.
        await ChangeAsync(other, "clinical_notes", note, sensitive: false);
        Assert.StartsWith($"true {marker} ", (await StateAsync("clinical_notes", note)).Single());
        // While the note is sensitive, so is its addendum, whoever asks.
        await RefusedAsync(PostgresErrorCodes.CheckViolation, () => ChangeAsync(marker, "note_addenda", addendum, sensitive: false));
        await RefusedAsync(PostgresErrorCodes.CheckViolation, () => ChangeAsync(marker, "clinical_notes", note, sensitive: false, reason: "  "));

        // The person who marked it unmarks it; the addendum, marked on its own, stays sensitive until it is unmarked itself.
        await ChangeAsync(marker, "clinical_notes", note, sensitive: false, reason: "Marked the wrong note.");
        Assert.Equal(["false none Marked the wrong note. true"], await StateAsync("clinical_notes", note));
        Assert.StartsWith($"true {marker} ", (await StateAsync("note_addenda", addendum)).Single());
        // Its own author didn't make it sensitive, the marker did: the author can't unmark it.
        await ChangeAsync(other, "note_addenda", addendum, sensitive: false);
        Assert.StartsWith($"true {marker} ", (await StateAsync("note_addenda", addendum)).Single());
        // Nor by naming the marker part-way through the statement: the policy has already decided on the row as it
        // stood and the person the statement began as.
        await Accounts.ExecuteAsync(App, other,
            "UPDATE ehr.note_addenda SET sensitive = false, sensitive_reason = 'x' || left(set_config('app.user_id', @marker, true), 0) WHERE id = @id",
            ("id", addendum), ("marker", marker.ToString()));
        Assert.StartsWith($"true {marker} ", (await StateAsync("note_addenda", addendum)).Single());
        // The note's signer may unmark what someone else marked: the addendum here, and a note marked again.
        await ChangeAsync(signer, "note_addenda", addendum, sensitive: false, reason: "Not sensitive.");
        Assert.Equal(["false none Not sensitive. true"], await StateAsync("note_addenda", addendum));
        await ChangeAsync(other, "clinical_notes", note, sensitive: true);
        await ChangeAsync(signer, "clinical_notes", note, sensitive: false, reason: "Reviewed with the patient.");
        Assert.Equal(["false none Reviewed with the patient. true"], await StateAsync("clinical_notes", note));
    }

    // An addendum signed at the very moment its note is being marked sensitive ends sensitive: the two wait for each
    // other instead of passing. Two connections, the first holding its transaction open.
    [Fact]
    public async Task An_addendum_signed_while_its_note_is_being_marked_ends_sensitive()
    {
        var (signer, patient, note, _) = await SignedAsync();
        var colleague = await ColleagueAsync(patient);
        var cancel = TestContext.Current.CancellationToken;
        async Task<(NpgsqlConnection, NpgsqlTransaction)> BeginAsync(Guid user)
        {
            var connection = new NpgsqlConnection(App);
            await connection.OpenAsync(cancel);
            var transaction = await connection.BeginTransactionAsync(cancel);
            await using var set = new NpgsqlCommand("SELECT set_config('app.user_id', @user, true)", connection, transaction);
            set.Parameters.AddWithValue("user", user.ToString());
            await set.ExecuteNonQueryAsync(cancel);
            return (connection, transaction);
        }

        var (writing, writingTransaction) = await BeginAsync(colleague);
        var (marking, markingTransaction) = await BeginAsync(signer);
        await using (writing)
        await using (marking)
        {
            await using (var insert = new NpgsqlCommand(
                "INSERT INTO ehr.note_addenda (id, note_id, patient_id, author_id, text) VALUES (gen_random_uuid(), @note, @patient, @author, 'Synthetic addendum, signed during the mark.')",
                writing, writingTransaction))
            {
                insert.Parameters.AddWithValue("note", note);
                insert.Parameters.AddWithValue("patient", patient);
                insert.Parameters.AddWithValue("author", colleague);
                await insert.ExecuteNonQueryAsync(cancel);
            }

            await using var mark = new NpgsqlCommand("UPDATE ehr.clinical_notes SET sensitive = true, sensitive_reason = 'Synthetic' WHERE id = @note", marking, markingTransaction);
            mark.Parameters.AddWithValue("note", note);
            var waiting = mark.ExecuteNonQueryAsync(cancel);
            // The mark waits for the addendum's transaction.
            Assert.NotSame(waiting, await Task.WhenAny(waiting, Task.Delay(TimeSpan.FromMilliseconds(500), cancel)));
            await writingTransaction.CommitAsync(cancel);
            Assert.Equal(1, await waiting);
            await markingTransaction.CommitAsync(cancel);
        }

        Assert.Equal(["true true"], await AsSuperuserAsync(
            "SELECT n.sensitive || ' ' || a.sensitive FROM ehr.clinical_notes n JOIN ehr.note_addenda a ON a.note_id = n.id WHERE n.id = @note", ("note", note)));
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
            foreach (var column in new[] { table == "clinical_notes" ? "plan = 'Changed'" : "text = 'Changed'", table == "clinical_notes" ? "signer_id = gen_random_uuid()" : "sensitive_inherited = true", "sensitive_marked_by = gen_random_uuid()", "sensitive_changed_at = '2000-01-01'", "patient_id = gen_random_uuid()" })
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

    // Tahseen's decision of 19 November 2026 (step 10e): marking a note marks its addenda as inherited from it,
    // unmarking it clears only the inherited marks, and an addendum marked on its own stays marked.
    [Fact]
    public async Task Marking_a_note_marks_its_addenda_as_inherited_and_unmarking_it_clears_only_those()
    {
        var (signer, patient, note, _) = await SignedAsync();
        var colleague = await ColleagueAsync(patient);
        async Task<Guid> SignedSensitiveAsync(string text)
        {
            var id = Guid.CreateVersion7();
            await Accounts.ExecuteAsync(App, colleague, "INSERT INTO ehr.note_addenda (id, note_id, patient_id, author_id, text, sensitive) VALUES (@id, @note, @patient, @author, @text, true)",
                ("id", id), ("note", note), ("patient", patient), ("author", colleague), ("text", text));
            return id;
        }

        Task<List<string>> AllAsync() => AsSuperuserAsync(
            "SELECT text || ': ' || sensitive || ' ' || sensitive_inherited || ' ' || coalesce(sensitive_marked_by::text, 'none') || ' ' || coalesce(sensitive_reason, 'none') FROM ehr.note_addenda WHERE note_id = @note ORDER BY text",
            ("note", note));

        // Three addenda before the note is marked: one plain, one marked on its own afterwards, one signed sensitive.
        var plain = await AddendumAsync(signer, patient, note, "a plain");
        var marked = await AddendumAsync(signer, patient, note, "b marked on its own");
        await ChangeAsync(colleague, "note_addenda", marked, sensitive: true, reason: "Names a relative.");
        await SignedSensitiveAsync("c signed sensitive");

        // 1. Marking the note marks each addendum that wasn't, as inherited, by nobody of its own; the two with a
        //    mark of their own keep it, with who and why.
        await ChangeAsync(signer, "clinical_notes", note, sensitive: true);
        Assert.Equal(
            ["a plain: true true none Patient asked for it.", $"b marked on its own: true false {colleague} Names a relative.", $"c signed sensitive: true false {colleague} none"],
            await AllAsync());

        // Signed while the note is sensitive: inherited, unless its author ticked it sensitive themselves.
        var later = await AddendumAsync(colleague, patient, note, "d signed to a sensitive note");
        await SignedSensitiveAsync("e signed sensitive to a sensitive note");
        Assert.Equal(
            ["d signed to a sensitive note: true true none none", $"e signed sensitive to a sensitive note: true false {colleague} none"],
            (await AllAsync())[3..]);

        // No addendum of a sensitive note is anything but sensitive, and none can be made so: unmarking one is
        // refused whoever asks and whichever kind of mark it has, and "inherited" isn't a column anyone may set.
        foreach (var user in new[] { signer, colleague })
        {
            foreach (var addendum in new[] { plain, marked, later })
            {
                await RefusedAsync(PostgresErrorCodes.CheckViolation, () => ChangeAsync(user, "note_addenda", addendum, sensitive: false));
            }
        }

        await RefusedAsync(PostgresErrorCodes.InsufficientPrivilege, () => Accounts.ExecuteAsync(App, signer, "UPDATE ehr.note_addenda SET sensitive_inherited = false WHERE id = @id", ("id", plain)));
        await RefusedAsync(PostgresErrorCodes.CheckViolation, () => Accounts.ExecuteAsync(database.SuperuserConnectionString, null, "UPDATE ehr.note_addenda SET sensitive_inherited = false WHERE id = @id", ("id", plain)));
        Assert.Equal(["0"], await AsSuperuserAsync("SELECT count(*)::text FROM ehr.note_addenda WHERE note_id = @note AND NOT sensitive", ("note", note)));

        // 2 and 3. Unmarking the note clears the inherited marks, with the note's reason, and only those.
        await ChangeAsync(signer, "clinical_notes", note, sensitive: false, reason: "Marked the wrong note.");
        Assert.Equal(
            [
                "a plain: false false none Marked the wrong note.",
                $"b marked on its own: true false {colleague} Names a relative.",
                $"c signed sensitive: true false {colleague} none",
                "d signed to a sensitive note: false false none Marked the wrong note.",
                $"e signed sensitive to a sensitive note: true false {colleague} none",
            ],
            await AllAsync());
        // Each cleared mark is in the audit log, as a change to the addendum.
        // (the first was marked and cleared: two changes; the one signed to the sensitive note was cleared: one).
        Assert.Equal(["3"], await AsSuperuserAsync(
            "SELECT count(*)::text FROM ehr.audit_events WHERE table_name = 'note_addenda' AND action = 'update' AND row_id = ANY(@ids)",
            ("ids", new[] { plain, later })));

        // An addendum marked on its own stays marked until it is unmarked itself, by whoever marked it.
        await ChangeAsync(colleague, "note_addenda", marked, sensitive: false, reason: "Not sensitive after all.");
        Assert.Equal("b marked on its own: false false none Not sensitive after all.", (await AllAsync())[1]);

        // Marked again: the one just unmarked is inherited this time, and goes with the note's mark.
        await ChangeAsync(colleague, "clinical_notes", note, sensitive: true);
        Assert.Equal("b marked on its own: true true none Patient asked for it.", (await AllAsync())[1]);
        await ChangeAsync(colleague, "clinical_notes", note, sensitive: false, reason: "Reviewed.");
        Assert.Equal("b marked on its own: false false none Reviewed.", (await AllAsync())[1]);
    }

    // An addendum's own mark changed at the very moment its note's is: the two wait for each other. Unmarked while the
    // note is being marked, it ends sensitive with the note; marked while the note is being unmarked, it ends with a
    // mark of its own, never an inherited one with no sensitive note behind it. Two connections each time.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_addendum_changed_while_its_note_is_being_changed_waits_for_it(bool noteIsBeingMarked)
    {
        var (signer, patient, note, _) = await SignedAsync();
        var colleague = await ColleagueAsync(patient);
        var addendum = await AddendumAsync(colleague, patient, note);
        if (noteIsBeingMarked)
        {
            await ChangeAsync(colleague, "note_addenda", addendum, sensitive: true);
        }
        else
        {
            // The note is sensitive, then unmarked while the addendum (unmarked with it at once below) is marked again.
            await ChangeAsync(signer, "clinical_notes", note, sensitive: true);
        }

        var cancel = TestContext.Current.CancellationToken;
        async Task<(NpgsqlConnection, NpgsqlTransaction)> BeginAsync(Guid user)
        {
            var connection = new NpgsqlConnection(App);
            await connection.OpenAsync(cancel);
            var transaction = await connection.BeginTransactionAsync(cancel);
            await using var set = new NpgsqlCommand("SELECT set_config('app.user_id', @user, true)", connection, transaction);
            set.Parameters.AddWithValue("user", user.ToString());
            await set.ExecuteNonQueryAsync(cancel);
            return (connection, transaction);
        }

        NpgsqlCommand Command(string table, Guid id, bool sensitive, NpgsqlConnection connection, NpgsqlTransaction transaction)
        {
            var command = new NpgsqlCommand($"UPDATE ehr.{table} SET sensitive = @sensitive, sensitive_reason = 'Synthetic' WHERE id = @id", connection, transaction);
            command.Parameters.AddWithValue("sensitive", sensitive);
            command.Parameters.AddWithValue("id", id);
            return command;
        }

        var (first, firstTransaction) = await BeginAsync(noteIsBeingMarked ? colleague : signer);
        var (second, secondTransaction) = await BeginAsync(noteIsBeingMarked ? signer : colleague);
        await using (first)
        await using (second)
        {
            if (noteIsBeingMarked)
            {
                // The addendum's marker unmarks it and hasn't committed; the note's mark waits, then marks it again.
                await using var unmark = Command("note_addenda", addendum, sensitive: false, first, firstTransaction);
                Assert.Equal(1, await unmark.ExecuteNonQueryAsync(cancel));
                await using var mark = Command("clinical_notes", note, sensitive: true, second, secondTransaction);
                var waiting = mark.ExecuteNonQueryAsync(cancel);
                Assert.NotSame(waiting, await Task.WhenAny(waiting, Task.Delay(TimeSpan.FromMilliseconds(500), cancel)));
                await firstTransaction.CommitAsync(cancel);
                Assert.Equal(1, await waiting);
                await secondTransaction.CommitAsync(cancel);
                Assert.Equal(["true true true"], await AsSuperuserAsync(
                    "SELECT n.sensitive || ' ' || a.sensitive || ' ' || a.sensitive_inherited FROM ehr.note_addenda a JOIN ehr.clinical_notes n ON n.id = a.note_id WHERE a.id = @id", ("id", addendum)));
            }
            else
            {
                // The note's unmark has cleared the addendum's inherited mark and hasn't committed; marking the
                // addendum waits for it, then finds the note not sensitive: a mark of its own, or nothing to change.
                await using var unmark = Command("clinical_notes", note, sensitive: false, first, firstTransaction);
                Assert.Equal(1, await unmark.ExecuteNonQueryAsync(cancel));
                await using var mark = Command("note_addenda", addendum, sensitive: true, second, secondTransaction);
                var waiting = mark.ExecuteNonQueryAsync(cancel);
                Assert.NotSame(waiting, await Task.WhenAny(waiting, Task.Delay(TimeSpan.FromMilliseconds(500), cancel)));
                await firstTransaction.CommitAsync(cancel);
                Assert.Equal(1, await waiting);
                await secondTransaction.CommitAsync(cancel);
                Assert.Equal([$"false true false {colleague}"], await AsSuperuserAsync(
                    "SELECT n.sensitive || ' ' || a.sensitive || ' ' || a.sensitive_inherited || ' ' || a.sensitive_marked_by FROM ehr.note_addenda a JOIN ehr.clinical_notes n ON n.id = a.note_id WHERE a.id = @id", ("id", addendum)));
            }
        }

        // Whatever the order: no addendum is left with an inherited mark and no sensitive note behind it.
        Assert.Equal(["0"], await AsSuperuserAsync(
            "SELECT count(*)::text FROM ehr.note_addenda a JOIN ehr.clinical_notes n ON n.id = a.note_id WHERE a.note_id = @note AND (a.sensitive_inherited AND NOT n.sensitive OR n.sensitive AND NOT a.sensitive)", ("note", note)));
    }

    // Tahseen's question (2026-11-19): if someone viewed it before it was marked, the audit log already shows that.
    // A view recorded before the mark is still there after it, unchanged, with its time before the mark's: a
    // colleague's view of the note, and the views proxies made of the portal's visit summaries (step 13a), which is how
    // anything written from a note reaches a proxy. And his note of 19 November: a proxy never sees a sensitive note or
    // addendum. The database gives no portal session a signed note or addendum at all (not the patient, not a proxy
    // with or without sensitive items in the grant, before the mark, after it, or after it is taken off), only the
    // note's visit summary: to the patient and a proxy allowed sensitive items always, to a proxy without them only while
    // the note isn't sensitive, and to a proxy whose access was removed never.
    [Fact]
    public async Task Views_made_before_a_note_was_marked_stay_in_the_audit_log_and_a_proxy_sees_no_note_or_addendum_and_its_summary_only_as_the_grant_allows()
    {
        var (signer, patient, note, _) = await SignedAsync();
        var colleague = await ColleagueAsync(patient);
        var addendum = await AddendumAsync(signer, patient, note);
        // A visit summary written from the note by the colleague, released to the portal (step 13a).
        var summary = Guid.CreateVersion7();
        await Accounts.ExecuteAsync(App, colleague,
            "INSERT INTO ehr.visit_summaries (id, note_id, patient_id, talked_about, found, next_steps) VALUES (@id, @note, @patient, 'We talked about your cough.', 'Your chest was clear.', 'Rest and drink fluids.')",
            ("id", summary), ("note", note), ("patient", patient));
        // A colleague's view of the note, recorded the way the app records one (AuditRecorder writes this row).
        await Accounts.ExecuteAsync(App, colleague,
            "INSERT INTO ehr.audit_events (id, occurred_at, actor_id, action, table_name, row_id, patient_id, source) VALUES (uuidv7(), now(), @actor, 'read', 'clinical_notes', @note, @patient, 'onprem')",
            ("actor", colleague), ("note", note), ("patient", patient));
        const string views = "SELECT id || ' ' || actor_id || ' ' || action || ' ' || occurred_at FROM ehr.audit_events WHERE patient_id = @patient AND action = 'read' AND row_id = @note";
        var before = await AsSuperuserAsync(views, ("patient", patient), ("note", note));
        Assert.Single(before);

        // A proxy acting for the patient, allowed sensitive items.
        var proxyPatient = await Accounts.PatientAsync(database, signer);
        var proxy = await Accounts.PatientAccountAsync(database, proxyPatient);
        // Given by the clinician, as the clinic gives one (ProxyAccessSecurityTests), and the patient then allows sensitive items.
        async Task<Guid> GiveAsync(Guid account)
        {
            var id = Guid.CreateVersion7();
            await Accounts.ExecuteAsync(App, signer,
                """
                INSERT INTO ehr.proxy_grants (id, patient_id, proxy_email, scope, include_sensitive, expires_at, granted_by, granted_at, granted_by_kind)
                VALUES (@id, @patient, @email, 'record', false, now() + interval '30 days', gen_random_uuid(), '2000-01-01', 'staff')
                """, ("id", id), ("patient", patient), ("email", $"{account:N}@example.com"));
            return id;
        }

        var grant = await GiveAsync(proxy);
        // Only the patient allows a proxy sensitive items: they do.
        var own = await Accounts.PatientAccountAsync(database, patient);
        await Accounts.ExecuteAsync(App, own, "UPDATE ehr.proxy_grants SET include_sensitive = true WHERE id = @id", ("id", grant));
        Assert.Equal(["true"], await AsSuperuserAsync("SELECT include_sensitive::text FROM ehr.proxy_grants WHERE id = @id", ("id", grant)));
        // A second proxy, whose access doesn't include sensitive items, and a third, whose access the patient removed.
        var without = await Accounts.PatientAccountAsync(database, await Accounts.PatientAsync(database, signer));
        var withoutGrant = await GiveAsync(without);
        var removed = await Accounts.PatientAccountAsync(database, await Accounts.PatientAsync(database, signer));
        var removedGrant = await GiveAsync(removed);
        await Accounts.ExecuteAsync(App, own, "UPDATE ehr.proxy_grants SET revoked_at = now() WHERE id = @id", ("id", removedGrant));

        // What each portal session is given, in either role: patients, signed notes, addenda, visit summaries as the
        // portal is given them (ehr.standing_visit_summaries), and rows of the summaries' table, which is none.
        async Task<List<string>> AsPortalAsync(string connection, Guid account, Guid? acting)
        {
            using (acting is { } chosen ? Accounts.ActingUnder(chosen) : null)
            {
                return await Accounts.QueryAsync(connection, account,
                    "SELECT (SELECT count(*) FROM ehr.patients) || ' ' || (SELECT count(*) FROM ehr.clinical_notes) || ' ' || (SELECT count(*) FROM ehr.note_addenda) || ' ' || (SELECT count(*) FROM ehr.standing_visit_summaries(100)) || ' ' || (SELECT count(*) FROM ehr.visit_summaries)");
            }
        }

        async Task GivenAsync(string patientSees, string allowedSees, string withoutSees, string removedSees)
        {
            foreach (var connection in new[] { App, Read })
            {
                Assert.Equal([patientSees], await AsPortalAsync(connection, own, null));
                Assert.Equal([allowedSees], await AsPortalAsync(connection, proxy, grant));
                Assert.Equal([withoutSees], await AsPortalAsync(connection, without, withoutGrant));
                Assert.Equal([removedSees], await AsPortalAsync(connection, removed, removedGrant));
            }
        }

        // Before the mark: the record and the summary, never the note or its addendum.
        await GivenAsync("1 0 0 1 0", "1 0 0 1 0", "1 0 0 1 0", "0 0 0 0 0");

        // The two proxies' views of the summaries page, recorded the way the portal records one: a read of the patient,
        // and a read of each summary it showed, under the proxy's own account, while acting for the patient.
        foreach (var (viewer, acting) in new[] { (proxy, grant), (without, withoutGrant) })
        {
            using (Accounts.ActingUnder(acting))
            {
                await Accounts.ExecuteAsync(App, viewer,
                    """
                    INSERT INTO ehr.audit_events (id, occurred_at, actor_id, action, table_name, row_id, patient_id, source)
                    VALUES (uuidv7(), now(), @actor, 'read', 'patients', @patient, @patient, 'onprem'), (uuidv7(), now(), @actor, 'read', 'visit_summaries', @summary, @patient, 'onprem')
                    """, ("actor", viewer), ("patient", patient), ("summary", summary));
            }
        }

        const string proxyViews = "SELECT id || ' ' || actor_id || ' ' || action || ' ' || table_name || ' ' || occurred_at FROM ehr.audit_events WHERE patient_id = @patient AND action = 'read' AND actor_id = ANY(@viewers) ORDER BY id";
        Guid[] viewers = [proxy, without];
        var proxiesBefore = await AsSuperuserAsync(proxyViews, ("patient", patient), ("viewers", viewers));
        Assert.Equal(4, proxiesBefore.Count);

        await ChangeAsync(colleague, "clinical_notes", note, sensitive: true);

        // The earlier views: the same rows, the same actors and times, and before the mark.
        Assert.Equal(before, await AsSuperuserAsync(views, ("patient", patient), ("note", note)));
        Assert.Equal(proxiesBefore, await AsSuperuserAsync(proxyViews, ("patient", patient), ("viewers", viewers)));
        Assert.Equal(["true"], await AsSuperuserAsync(
            """
            SELECT (e.occurred_at < n.sensitive_changed_at)::text FROM ehr.audit_events e JOIN ehr.clinical_notes n ON n.id = e.row_id
            WHERE e.patient_id = @patient AND e.action = 'read' AND e.row_id = @note
            """, ("patient", patient), ("note", note)));
        Assert.Equal(["true"], await AsSuperuserAsync(
            """
            SELECT bool_and(e.occurred_at < n.sensitive_changed_at)::text FROM ehr.audit_events e, ehr.clinical_notes n
            WHERE n.id = @note AND e.patient_id = @patient AND e.action = 'read' AND e.actor_id = ANY(@viewers)
            """, ("patient", patient), ("note", note), ("viewers", viewers)));
        // An administrator still sees that the views happened, by whom and when (they have no values to hide).
        var admin = await Accounts.StaffAsync(database, StaffRoles.Admin);
        Assert.Equal([colleague.ToString()], await Accounts.QueryAsync(App, admin,
            "SELECT actor_id::text FROM ehr.audit_events WHERE patient_id = @patient AND action = 'read' AND row_id = @note", ("patient", patient), ("note", note)));
        Assert.Equal([.. viewers.Select(viewer => viewer.ToString()).Order(StringComparer.Ordinal)], await Accounts.QueryAsync(App, admin,
            "SELECT DISTINCT actor_id::text FROM ehr.audit_events WHERE patient_id = @patient AND action = 'read' AND actor_id = ANY(@viewers) ORDER BY actor_id::text",
            ("patient", patient), ("viewers", viewers)));

        // Marked: the summary is sensitive with the note, inherited (Tahseen's decision of 21 November 2026). The
        // patient and the proxy allowed sensitive items still see it; the proxy without them doesn't; nobody in the
        // portal sees the note or its addendum.
        Assert.Equal(["true true"], await AsSuperuserAsync("SELECT sensitive || ' ' || sensitive_inherited FROM ehr.visit_summaries WHERE id = @id", ("id", summary)));
        await GivenAsync("1 0 0 1 0", "1 0 0 1 0", "1 0 0 0 0", "0 0 0 0 0");
        // Nor an addendum signed to the note now that it is sensitive, nor any after the note is unmarked again.
        await AddendumAsync(colleague, patient, note, "Synthetic addendum to a sensitive note.");
        await GivenAsync("1 0 0 1 0", "1 0 0 1 0", "1 0 0 0 0", "0 0 0 0 0");

        // Unmarked: nothing of the note is sensitive any more, so the summary's inherited mark goes and it is shown to the
        // proxy without sensitive items again; the note and its addenda still to nobody in the portal.
        await ChangeAsync(colleague, "clinical_notes", note, sensitive: false, reason: "Marked the wrong note.");
        await GivenAsync("1 0 0 1 0", "1 0 0 1 0", "1 0 0 1 0", "0 0 0 0 0");

        Assert.NotEqual(Guid.Empty, addendum);
    }
}
