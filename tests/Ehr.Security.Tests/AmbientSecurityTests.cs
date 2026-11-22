using Ehr.Data.Accounts;
using Ehr.Testing;
using Npgsql;
using Xunit;

namespace Ehr.Security.Tests;

// Listening to a visit (the expansion plan's step 16; UC 23): the database's own rules, as the runtime roles meet them
// (db/policies/56-ambient-listening.sql, with the policies in 30-rls.sql, the grants in 10-grants.sql and the write
// audit in 40-audit.sql). A consent is the signed-in clinician's, for a patient on their panel, at this database's time,
// and never changes; a review of a conversation needs a fresh one of its own; its transcript is kept exactly while the
// review is open, never in the audit log, and goes 7 days after it was written at the latest, as the system's act, after
// which no proposal of that review is accepted as offered. Times are this database's clock. All rows are synthetic.
public class AmbientSecurityTests(MigratedDatabase database) : IClassFixture<MigratedDatabase>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    string App => database.ConnectionStringFor("ehr_app", MigratedDatabase.AppPassword);

    string Read => database.ConnectionStringFor("ehr_read", MigratedDatabase.ReadPassword);

    sealed record Visit(Guid Actor, Guid Patient, Guid Draft, Guid Consent, Guid Review, Guid Proposal);

    const string Words = "Synthetic words about a cough for three days.";

    static readonly string Transcript =
        $$"""{"text":"{{Words}}","final":true,"segments":[{"start":0,"end":{{Words.Length}},"startMs":0,"endMs":3000,"final":true,"unclear":null,"speaker":"patient"}]}""";

    const string Result = """
        {"transcript":{"text":"","final":true,"segments":[]},"sorting":"done",
         "fields":{"subjective":{"status":"pending","value":"Synthetic proposal","sources":[{"start":0,"end":9,"speaker":"patient"}],"warnings":[]}}}
        """;

    const string Consent = """
        INSERT INTO ehr.ambient_consents (id, patient_id, clinician_id, speakers, given_by, confirmed_at)
        VALUES (@consent, @patient, @actor, 2, 'patient', '2000-01-01T00:00:00Z');
        """;

    const string Draft = """
        INSERT INTO ehr.clinical_drafts
            (id, patient_id, clinician_id, body, version, subjective, objective, assessment, plan, structured_version, has_unreviewed_proposals)
        VALUES (@draft, @patient, @actor, 'Synthetic note', 1, 'Synthetic subjective', 'Synthetic objective', 'Synthetic assessment', 'Synthetic plan', 1, false);
        """;

    const string Begin = """
        INSERT INTO ehr.ai_note_reviews
            (id, patient_id, draft_id, clinician_id, session_id, max_seconds, max_chunk_bytes, state, started_version, mode, consent_id, sensitive)
        VALUES (@review, @patient, @draft, @actor, @session, 1200, 100000, 'recording', 1, 'ambient', @consent, true);
        """;

    const string Stage = """
        UPDATE ehr.ai_note_reviews SET state = 'pending', result_json = @result::jsonb WHERE id = @review;
        INSERT INTO ehr.ai_field_proposals (id, review_id, patient_id, field, original_value, state)
        VALUES (@proposal, @review, @patient, 'subjective', 'Synthetic proposal', 'pending');
        INSERT INTO ehr.ambient_transcripts (id, patient_id, transcript) VALUES (@review, @patient, @transcript::jsonb);
        """;

    const string Finish = """
        UPDATE ehr.ai_field_proposals SET state = 'rejected', decision_key = gen_random_uuid() WHERE id = @proposal;
        UPDATE ehr.ai_note_reviews SET state = 'completed', imported_version = 1 WHERE id = @review;
        """;

    async Task<Visit> NewVisitAsync()
    {
        var actor = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        var patient = await Accounts.PatientAsync(database, actor);
        await Accounts.AssignAsync(database, actor, patient);
        return new(actor, patient, Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
    }

    // A visit being listened to: its consent, a draft, and its review, recording.
    async Task<Visit> RecordingAsync()
    {
        var visit = await NewVisitAsync();
        await Execute(visit, Consent + Draft + Begin);
        return visit;
    }

    async Task<Visit> PendingAsync()
    {
        var visit = await RecordingAsync();
        await Execute(visit, Stage);
        return visit;
    }

    static (string Name, object Value)[] Parameters(Visit visit) =>
    [
        ("actor", visit.Actor), ("patient", visit.Patient), ("draft", visit.Draft), ("consent", visit.Consent),
        ("review", visit.Review), ("proposal", visit.Proposal), ("session", Guid.NewGuid().ToString("N")),
        ("result", Result), ("transcript", Transcript),
    ];

    Task Execute(Visit visit, string sql, string? connection = null, Guid? user = null) =>
        Accounts.ExecuteAsync(connection ?? App, user ?? visit.Actor, sql, Parameters(visit));

    Task<List<string>> Query(Visit visit, string sql, string? connection = null, Guid? user = null) =>
        Accounts.QueryAsync(connection ?? App, user ?? visit.Actor, sql, Parameters(visit));

    async Task Refused(string code, Func<Task> action) =>
        Assert.Equal(code, (await Assert.ThrowsAsync<PostgresException>(action)).SqlState);

    async Task<DateTimeOffset> ClockAsync() =>
        DateTimeOffset.Parse((await Accounts.QueryAsync(database.SuperuserConnectionString, null,
            "SELECT to_char(clock_timestamp() AT TIME ZONE 'UTC', 'YYYY-MM-DD\"T\"HH24:MI:SS.US\"Z\"')"))[0], System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public async Task A_consent_is_the_signed_in_clinicians_for_a_patient_on_their_panel_at_the_databases_time_and_never_changes()
    {
        var visit = await NewVisitAsync();
        var before = await ClockAsync();
        await Execute(visit, Consent);
        var after = await ClockAsync();
        var confirmed = DateTimeOffset.Parse((await Query(visit,
            "SELECT to_char(confirmed_at AT TIME ZONE 'UTC', 'YYYY-MM-DD\"T\"HH24:MI:SS.US\"Z\"') FROM ehr.ambient_consents WHERE id = @consent"))[0],
            System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(confirmed, before, after);

        // Seen by its clinician through both roles, and by nobody else.
        var other = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        await Accounts.AssignAsync(database, other, visit.Patient);
        var admin = await Accounts.StaffAsync(database, StaffRoles.Admin);
        var patientUser = await Accounts.PatientAccountAsync(database, visit.Patient);
        foreach (var connection in new[] { App, Read })
        {
            Assert.Equal(["1"], await Query(visit, "SELECT count(*) FROM ehr.ambient_consents WHERE patient_id = @patient", connection));
            foreach (var denied in new Guid?[] { other, admin, patientUser, null })
                Assert.Equal(["0"], await Accounts.QueryAsync(connection, denied, "SELECT count(*) FROM ehr.ambient_consents WHERE patient_id = @patient", Parameters(visit)));
        }

        // Not in another clinician's name, not for a patient off the panel, not by the cloud app's role, and never changed.
        var named = visit with { Consent = Guid.CreateVersion7(), Actor = other };
        await Refused("42501", () => Execute(named, Consent, user: visit.Actor));
        var offPanel = visit with { Consent = Guid.CreateVersion7(), Patient = await Accounts.PatientAsync(database, other) };
        await Refused("42501", () => Execute(offPanel, Consent));
        await Refused("42501", () => Execute(visit with { Consent = Guid.CreateVersion7() }, Consent, Read));
        await Refused("42501", () => Execute(visit, "UPDATE ehr.ambient_consents SET speakers = 3 WHERE id = @consent"));
        await Refused("42501", () => Execute(visit, "DELETE FROM ehr.ambient_consents WHERE id = @consent"));
        await Refused("23514", () => Execute(visit with { Consent = Guid.CreateVersion7() },
            Consent.Replace("2, 'patient'", "4, 'patient'", StringComparison.Ordinal)));
        await Refused("23514", () => Execute(visit with { Consent = Guid.CreateVersion7() },
            Consent.Replace("'patient',", "'neighbour',", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_review_of_a_conversation_needs_a_fresh_consent_of_its_own_given_by_its_clinician_for_its_patient()
    {
        // None at all, or a dictation that names one.
        var visit = await NewVisitAsync();
        await Execute(visit, Consent + Draft);
        await Refused("23514", () => Execute(visit, Begin.Replace("@consent", "NULL", StringComparison.Ordinal)));
        await Refused("23514", () => Execute(visit, Begin.Replace("'ambient'", "'dictation'", StringComparison.Ordinal).Replace(", true)", ", false)", StringComparison.Ordinal)));

        // One for another of the clinician's patients, another clinician's, or one more than 10 minutes old on this
        // database's clock.
        var second = await Accounts.PatientAsync(database, visit.Actor);
        await Accounts.AssignAsync(database, visit.Actor, second);
        var elsewhere = visit with { Patient = second, Consent = Guid.CreateVersion7() };
        await Execute(elsewhere, Consent);
        await Refused("23514", () => Execute(visit with { Consent = elsewhere.Consent }, Begin));
        var colleague = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        await Accounts.AssignAsync(database, colleague, visit.Patient);
        var theirs = visit with { Actor = colleague, Consent = Guid.CreateVersion7() };
        await Execute(theirs, Consent);
        await Refused("23514", () => Execute(visit with { Consent = theirs.Consent }, Begin));
        await Accounts.ExecuteAsync(database.SuperuserConnectionString, null,
            "SET LOCAL session_replication_role = replica; UPDATE ehr.ambient_consents SET confirmed_at = clock_timestamp() - interval '11 minutes' WHERE id = @consent",
            Parameters(visit));
        await Refused("23514", () => Execute(visit, Begin));

        // A fresh one binds one review, which is a sensitive item, and whose mode, consent and sensitivity never change.
        var fresh = visit with { Consent = Guid.CreateVersion7() };
        await Execute(fresh, Consent + Begin.Replace(", true)", ", false)", StringComparison.Ordinal));
        Assert.Equal(["True"], await Query(fresh, "SELECT sensitive FROM ehr.ai_note_reviews WHERE id = @review"));
        await Refused("23505", () => Execute(fresh with { Review = Guid.CreateVersion7(), Draft = Guid.CreateVersion7() }, Draft + Begin));
        await Refused("23514", () => Execute(fresh, "UPDATE ehr.ai_note_reviews SET mode = 'dictation', consent_id = NULL, sensitive = false WHERE id = @review"));
        await Refused("23514", () => Execute(fresh, "UPDATE ehr.ai_note_reviews SET sensitive = false WHERE id = @review"));
    }

    [Fact]
    public async Task A_conversations_transcript_is_kept_exactly_while_its_review_is_open_and_the_cloud_app_never_reads_it()
    {
        // Staged without it: refused at commit. Written for a review still recording, or for a dictation: refused.
        var recording = await RecordingAsync();
        await Refused("23514", () => Execute(recording, Stage.Replace(
            "INSERT INTO ehr.ambient_transcripts (id, patient_id, transcript) VALUES (@review, @patient, @transcript::jsonb);", "", StringComparison.Ordinal)));
        await Refused("23514", () => Execute(recording, "INSERT INTO ehr.ambient_transcripts (id, patient_id, transcript) VALUES (@review, @patient, @transcript::jsonb)"));

        // Staged in savepoints, as EF Core saves: the transcript is still written in the staging's transaction.
        var saved = await RecordingAsync();
        await Execute(saved, "SAVEPOINT review;\n" + Stage.Replace("INSERT INTO ehr.ai_field_proposals", "RELEASE SAVEPOINT review;\nSAVEPOINT rest;\nINSERT INTO ehr.ai_field_proposals", StringComparison.Ordinal)
            + "RELEASE SAVEPOINT rest;");
        Assert.Equal(["1"], await Query(saved, "SELECT count(*) FROM ehr.ambient_transcripts WHERE id = @review"));

        var visit = await PendingAsync();
        Assert.Equal(["True"], await Query(visit, "SELECT sensitive FROM ehr.ambient_transcripts WHERE id = @review"));
        Assert.Equal(["True"], await Query(visit, "SELECT sensitive FROM ehr.ai_field_proposals WHERE id = @proposal"));
        await Refused("23514", () => Execute(visit, "UPDATE ehr.ai_field_proposals SET sensitive = false WHERE id = @proposal"));

        // Its clinician reads it on-site; the cloud app's role sees only that it exists; nobody else sees even that.
        Assert.Equal([Words], await Query(visit, "SELECT transcript ->> 'text' FROM ehr.ambient_transcripts WHERE id = @review"));
        Assert.Equal(["1"], await Query(visit, "SELECT count(*) FROM ehr.ambient_transcripts WHERE id = @review", Read));
        await Refused("42501", () => Query(visit, "SELECT transcript FROM ehr.ambient_transcripts WHERE id = @review", Read));
        var colleague = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        await Accounts.AssignAsync(database, colleague, visit.Patient);
        var admin = await Accounts.StaffAsync(database, StaffRoles.Admin);
        foreach (var connection in new[] { App, Read })
            foreach (var denied in new Guid?[] { colleague, admin, null })
                Assert.Equal(["0"], await Accounts.QueryAsync(connection, denied, "SELECT count(*) FROM ehr.ambient_transcripts WHERE id = @review", Parameters(visit)));

        // Never changed; not deleted while the review is open, by its clinician or the cloud app's role; and a review
        // finished with it still there is refused at commit.
        await Refused("42501", () => Execute(visit, "UPDATE ehr.ambient_transcripts SET transcript = '{\"text\":\"\",\"segments\":[]}' WHERE id = @review"));
        await Refused("23514", () => Accounts.ExecuteAsync(database.SuperuserConnectionString, null,
            "UPDATE ehr.ambient_transcripts SET transcript = '{\"text\":\"\",\"segments\":[]}' WHERE id = @review", Parameters(visit)));
        await Refused("23514", () => Execute(visit, "DELETE FROM ehr.ambient_transcripts WHERE id = @review"));
        await Refused("42501", () => Execute(visit, "DELETE FROM ehr.ambient_transcripts WHERE id = @review", Read));
        await Refused("23514", () => Execute(visit, Finish));
        // Nor passed at commit because its clinician is no longer signed in by then: what can't be seen is refused.
        await Refused("42501", () => Execute(visit, Finish + "SELECT set_config('app.user_id', '', true);"));
        Assert.Equal(["pending 1"], await Query(visit,
            "SELECT r.state || ' ' || (SELECT count(*) FROM ehr.ambient_transcripts t WHERE t.id = r.id) FROM ehr.ai_note_reviews r WHERE r.id = @review"));

        await Execute(visit, Finish + "DELETE FROM ehr.ambient_transcripts WHERE id = @review;");
        Assert.Equal(["completed 0"], await Query(visit,
            "SELECT r.state || ' ' || (SELECT count(*) FROM ehr.ambient_transcripts t WHERE t.id = r.id) FROM ehr.ai_note_reviews r WHERE r.id = @review"));
    }

    [Fact]
    public async Task A_conversations_review_keeps_no_word_of_it_in_its_stored_result()
    {
        // Every place words of the conversation could sit beside where each passage was and who said it: the transcript's
        // text, a segment, a field's source or warning, an instruction not carried over, an empty field's reasons, a part
        // of the result the contract doesn't have, or a code that isn't one. Staged, or finished straight from recording.
        var visit = await RecordingAsync();
        const string Said = "Synthetic words about a cough";
        const string Source = """{"start":0,"end":9,"speaker":"patient"}""";
        const string Segment = """{"start":0,"end":9,"startMs":0,"endMs":900,"final":true""";
        string Put(string what, string with) => Result.Replace(what, with, StringComparison.Ordinal);
        string[] variants =
        [
            Put("\"text\":\"\"", $"\"text\":\"{Said}\""),
            Put("\"segments\":[]", $$"""
                "segments":[{{Segment}},"speaker":"patient","text":"{{Said}}"}]
                """),
            Put("\"segments\":[]", $$"""
                "segments":[{{Segment}},"speaker":"{{Said}}"}]
                """),
            Put("\"segments\":[]", $$"""
                "segments":[{{Segment}},"unclear":"{{Said}}"}]
                """),
            Put("\"segments\":[]", $$"""
                "segments":["{{Said}}"]
                """),
            Put(Source, $$"""
                {"start":0,"end":9,"speaker":"patient","said":"{{Said}}"}
                """),
            Put("\"warnings\":[]", $$"""
                "warnings":[{"code":"speaker_unsure","start":0,"end":9,"said":"{{Said}}"}]
                """),
            Put("\"warnings\":[]", $$"""
                "warnings":[{"code":"{{Said}}","start":0,"end":9}]
                """),
            Put("\"sorting\":\"done\",", $$"""
                "sorting":"done","notCarriedOver":[{"reason":"order","start":0,"end":9,"text":"{{Said}}"}],
                """),
            Put("\"sorting\":\"done\",", $$"""
                "sorting":"done","transcriptText":"{{Said}}",
                """),
            Put("\"fields\":{", $$"""
                "fields":{"objective":{"status":"empty","reasons":["{{Said}}"]},
                """),
            Put("\"fields\":{", $$"""
                "fields":{"objective":{"status":"empty","reasons":[],"value":"{{Said}}"},
                """),
        ];
        var staged = Stage.Replace("@result", "@variant", StringComparison.Ordinal);
        foreach (var variant in variants)
        {
            Assert.NotEqual(Result, variant);
            await Refused("23514", () => Accounts.ExecuteAsync(App, visit.Actor, staged, [.. Parameters(visit), ("variant", variant)]));
        }

        // Finished straight from recording, with nothing proposed.
        await Refused("23514", () => Accounts.ExecuteAsync(App, visit.Actor,
            "UPDATE ehr.ai_note_reviews SET state = 'completed', imported_version = 1, result_json = @variant::jsonb WHERE id = @review",
            [.. Parameters(visit), ("variant", $$"""
                {"transcript":{"text":"{{Said}}","final":true,"segments":[]},"sorting":"done","fields":null,"notCarriedOver":[]}
                """)]));

        // As the app stores one: places, codes, an empty field's reasons, provenance.
        var stored = Put("\"segments\":[]", $$"""
                "segments":[{{Segment}},"unclear":null,"speakerGroup":2,"speaker":"patient"}]
                """)
            .Replace("\"warnings\":[]", """
                "warnings":[{"code":"patients_own_words","start":0,"end":9}]
                """, StringComparison.Ordinal)
            .Replace("\"fields\":{", """
                "fields":{"objective":{"status":"empty","reasons":["not_dictated"]},
                """, StringComparison.Ordinal)
            .Replace("\"sorting\":\"done\",", """
                "sorting":"done","notCarriedOver":[{"reason":"order","start":0,"end":9}],"provenance":{"speech":{"repository":"synthetic","commit":"0","sha256":"0"},"language":null},
                """, StringComparison.Ordinal);
        await Accounts.ExecuteAsync(App, visit.Actor, staged, [.. Parameters(visit), ("variant", stored)]);
        Assert.Equal(["pending"], await Query(visit, "SELECT state FROM ehr.ai_note_reviews WHERE id = @review"));
    }

    // Two rules step 16 widened for a visit's review (db/policies/55-ai-review.sql) and no further: a review is staged
    // pending with nothing proposed only if it is a visit's, so its transcript can be read; and a visit's review holds no
    // proposal beyond its note, even with one planted in its stored result (as by the database's owner).
    [Fact]
    public async Task Only_a_visits_review_is_staged_with_nothing_proposed_and_none_holds_a_proposed_order_or_allergy()
    {
        var dictation = await NewVisitAsync();
        await Execute(dictation, Draft + Begin.Replace("'ambient', @consent, true", "'dictation', NULL, false", StringComparison.Ordinal));
        const string Nothing = """
            {"transcript":{"text":"Synthetic words","final":true,"segments":[]},"sorting":"done",
             "fields":{"subjective":{"status":"empty","reasons":["not_dictated"]}},"notCarriedOver":[]}
            """;
        await Refused("23514", () => Accounts.ExecuteAsync(App, dictation.Actor,
            "UPDATE ehr.ai_note_reviews SET state = 'pending', result_json = @nothing::jsonb WHERE id = @review", [.. Parameters(dictation), ("nothing", Nothing)]));
        await Accounts.ExecuteAsync(App, dictation.Actor,
            "UPDATE ehr.ai_note_reviews SET state = 'completed', imported_version = 1, result_json = @nothing::jsonb WHERE id = @review",
            [.. Parameters(dictation), ("nothing", Nothing)]);
        Assert.Equal(["completed"], await Query(dictation, "SELECT state FROM ehr.ai_note_reviews WHERE id = @review"));

        var visit = await PendingAsync();
        await Accounts.ExecuteAsync(database.SuperuserConnectionString, null, """
            SET LOCAL session_replication_role = replica;
            UPDATE ehr.ai_note_reviews SET result_json = result_json || '{"proposals":[{"kind":"order","start":0,"end":9,"warnings":[]}]}' WHERE id = @review
            """, Parameters(visit));
        await Refused("23514", () => Execute(visit,
            "INSERT INTO ehr.ai_chart_proposals (id, review_id, patient_id, ordinal, kind, state) VALUES (gen_random_uuid(), @review, @patient, 0, 'order', 'pending')"));
        Assert.Equal(["0"], await Query(visit, "SELECT count(*) FROM ehr.ai_chart_proposals WHERE review_id = @review"));
    }

    // The writes of listening to a visit, among a patient's events: the consent, the review, its proposals, the transcript.
    const string Ours = "action IN ('insert', 'update', 'delete') AND table_name IN ('ambient_consents', 'ai_note_reviews', 'ai_field_proposals', 'ambient_transcripts')";

    [Fact]
    public async Task The_audit_log_keeps_a_transcripts_hash_and_length_never_its_text_and_shows_administrators_nothing_of_the_conversation()
    {
        var visit = await PendingAsync();
        var hash = (await Query(visit, "SELECT encode(sha256(convert_to(transcript::text, 'UTF8')), 'hex') FROM ehr.ambient_transcripts WHERE id = @review"))[0];
        await Execute(visit, Finish + "DELETE FROM ehr.ambient_transcripts WHERE id = @review;");

        var events = await Accounts.QueryAsync(database.SuperuserConnectionString, null, """
            SELECT action || ' ' || sensitive::text || ' ' || (jsonb_path_query_first(details, '$.*.transcript.sha256') #>> '{}')
                   || ' ' || (jsonb_path_query_first(details, '$.*.transcript.characters') #>> '{}')
            FROM ehr.audit_events WHERE table_name = 'ambient_transcripts' AND row_id = @review ORDER BY occurred_at, action DESC
            """, Parameters(visit));
        var length = (await Accounts.QueryAsync(database.SuperuserConnectionString, null, "SELECT char_length(@transcript::jsonb::text)", Parameters(visit)))[0];
        Assert.Equal([$"insert true {hash} {length}", $"delete true {hash} {length}"], events);
        Assert.Equal(["0"], await Accounts.QueryAsync(database.SuperuserConnectionString, null,
            "SELECT count(*) FROM ehr.audit_events WHERE patient_id = @patient AND details::text LIKE '%' || @words || '%'",
            [.. Parameters(visit), ("words", "cough for three days")]));

        // The review's and its proposal's events are sensitive, so the audit page shows none of their values; the consent's
        // holds no text and is shown.
        Assert.Equal(["ai_field_proposals true", "ai_note_reviews true", "ambient_consents false", "ambient_transcripts true"],
            await Accounts.QueryAsync(database.SuperuserConnectionString, null,
                $"SELECT table_name || ' ' || bool_and(sensitive)::text FROM ehr.audit_events WHERE patient_id = @patient AND {Ours} GROUP BY table_name ORDER BY 1",
                Parameters(visit)));
        var admin = await Accounts.StaffAsync(database, StaffRoles.Admin);
        foreach (var connection in new[] { App, Read })
            Assert.Equal(["ambient_consents"], await Accounts.QueryAsync(connection, admin, $$"""
                SELECT e.table_name FROM ehr.shown_audit_details(ARRAY(
                    SELECT id FROM ehr.audit_events WHERE patient_id = @patient AND {{Ours}})) s
                JOIN ehr.audit_events e ON e.id = s.id ORDER BY 1
                """, Parameters(visit)));
    }

    [Fact]
    public async Task Transcripts_of_reviews_left_open_go_after_7_days_at_most_as_the_systems_act_and_their_proposals_cant_be_accepted_as_offered()
    {
        var visit = await PendingAsync();
        const string Purge = "SELECT deleted::text || ' ' || to_char(older_than AT TIME ZONE 'UTC', 'YYYY-MM-DD\"T\"HH24:MI:SS.US\"Z\"') FROM ehr.purge_ambient_transcripts(@keep::interval)";
        // As the on-site app's timer runs it: the on-site app's role, nobody signed in, committed.
        async Task<(int Deleted, DateTimeOffset OlderThan)> PurgeAsync(string keep)
        {
            await using var connection = new NpgsqlConnection(App);
            await connection.OpenAsync(Cancel);
            await using var transaction = await connection.BeginTransactionAsync(Cancel);
            await using (var nobody = new NpgsqlCommand("SELECT set_config('app.user_id', '', true), set_config('app.acting_grant', '', true)", connection, transaction))
            {
                await nobody.ExecuteNonQueryAsync(Cancel);
            }

            await using var command = new NpgsqlCommand(Purge, connection, transaction);
            command.Parameters.AddWithValue("keep", keep);
            var row = ((string)(await command.ExecuteScalarAsync(Cancel))!).Split(' ');
            await transaction.CommitAsync(Cancel);
            return (int.Parse(row[0], System.Globalization.CultureInfo.InvariantCulture),
                DateTimeOffset.Parse(row[1], System.Globalization.CultureInfo.InvariantCulture));
        }

        // Only the on-site app's role may run it, and only with nobody signed in.
        await Refused("42501", () => Accounts.QueryAsync(Read, null, Purge, [.. Parameters(visit), ("keep", "7 days")]));
        await Refused("42501", () => Accounts.QueryAsync(App, visit.Actor, Purge, [.. Parameters(visit), ("keep", "7 days")]));

        // Its owner reads and deletes transcripts only inside it: outside, it sees none and deletes none, and a runtime
        // role that opens the purge's switch itself gains nothing.
        Assert.Equal(["0"], await Accounts.QueryAsync(database.OwnerConnectionString, null, "SELECT count(*) FROM ehr.ambient_transcripts WHERE id = @review", Parameters(visit)));
        await Accounts.ExecuteAsync(database.OwnerConnectionString, null, "DELETE FROM ehr.ambient_transcripts WHERE id = @review", Parameters(visit));
        await using (var connection = new NpgsqlConnection(App))
        {
            await connection.OpenAsync(Cancel);
            await using var transaction = await connection.BeginTransactionAsync(Cancel);
            await using var command = new NpgsqlCommand(
                "SELECT set_config('app.user_id', '', true), set_config('app.ambient_purge', 'open', true)", connection, transaction);
            await command.ExecuteNonQueryAsync(Cancel);
            command.CommandText = "SELECT count(*) FROM ehr.ambient_transcripts WHERE id = @review";
            command.Parameters.AddWithValue("review", visit.Review);
            Assert.Equal(0L, (long)(await command.ExecuteScalarAsync(Cancel))!);
        }

        Assert.Equal(["1"], await Query(visit, "SELECT count(*) FROM ehr.ambient_transcripts WHERE id = @review"));

        // 7 days keeps a transcript written just now; asking for longer still means 7 days, and for less than a day (none,
        // or less than none) means a day, on this database's clock.
        foreach (var (keep, days) in new[] { ("7 days", 7), ("30 days", 7), ("1 year", 7), ("0", 1), ("-1 day", 1), ("1 hour", 1) })
        {
            var before = await ClockAsync();
            var (deleted, olderThan) = await PurgeAsync(keep);
            var after = await ClockAsync();
            Assert.Equal(0, deleted);
            Assert.InRange(olderThan, before.AddDays(-days), after.AddDays(-days));
        }

        Assert.Equal(["1"], await Query(visit, "SELECT count(*) FROM ehr.ambient_transcripts WHERE id = @review"));

        // Written two days ago (as the database's owner ages it, holding the triggers): 7 days keeps it, and 1 day, asked
        // for, deletes it, with nobody's name on the delete and only the hash in the log.
        await Accounts.ExecuteAsync(database.SuperuserConnectionString, null,
            "SET LOCAL session_replication_role = replica; UPDATE ehr.ambient_transcripts SET created_at = created_at - interval '2 days' WHERE id = @review",
            Parameters(visit));
        Assert.Equal(0, (await PurgeAsync("7 days")).Deleted);
        var (gone, _) = await PurgeAsync("1 day");
        Assert.True(gone >= 1);
        Assert.Equal(["0"], await Query(visit, "SELECT count(*) FROM ehr.ambient_transcripts WHERE id = @review"));
        Assert.Equal(["delete  true"], await Accounts.QueryAsync(database.SuperuserConnectionString, null,
            "SELECT action || ' ' || coalesce(actor_id::text, '') || ' ' || (jsonb_path_query_first(details, '$.*.transcript.sha256') IS NOT NULL)::text FROM ehr.audit_events WHERE table_name = 'ambient_transcripts' AND row_id = @review AND action = 'delete'",
            Parameters(visit)));

        // Nor written again, so that they could be: a transcript is written once, as its review is staged.
        await Refused("23514", () => Execute(visit, "INSERT INTO ehr.ambient_transcripts (id, patient_id, transcript) VALUES (@review, @patient, @transcript::jsonb)"));

        // Its proposals can't go into the note as offered any more: not accepted, nor edited back to the words offered
        // (trimmed, every run of whitespace one space); rejected, or edited into the clinician's own words.
        await Refused("23514", () => Execute(visit,
            "UPDATE ehr.ai_field_proposals SET state = 'accepted', approved_value = original_value, decision_key = gen_random_uuid() WHERE id = @proposal"));
        await Refused("23514", () => Execute(visit,
            "UPDATE ehr.ai_field_proposals SET state = 'edited', approved_value = original_value, decision_key = gen_random_uuid() WHERE id = @proposal"));
        await Refused("23514", () => Execute(visit,
            "UPDATE ehr.ai_field_proposals SET state = 'edited', approved_value = E' Synthetic \\n\\t  proposal  ', decision_key = gen_random_uuid() WHERE id = @proposal"));
        await Execute(visit,
            "UPDATE ehr.ai_field_proposals SET state = 'edited', approved_value = 'Synthetic words of the clinician', decision_key = gen_random_uuid() WHERE id = @proposal");
        await Execute(visit, "UPDATE ehr.ai_note_reviews SET state = 'completed', imported_version = 1 WHERE id = @review");
        Assert.Equal(["completed edited"], await Query(visit,
            "SELECT r.state || ' ' || p.state FROM ehr.ai_note_reviews r JOIN ehr.ai_field_proposals p ON p.review_id = r.id WHERE r.id = @review"));
    }
}
