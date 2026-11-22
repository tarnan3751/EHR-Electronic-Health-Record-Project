using Ehr.Data.Accounts;
using Ehr.Testing;
using Npgsql;
using Xunit;

namespace Ehr.Security.Tests;

// Runtime-role SQL, independent of the HTTP/domain checks. All rows are synthetic.
public class AiReviewSecurityTests(MigratedDatabase database) : IClassFixture<MigratedDatabase>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;
    string App => database.ConnectionStringFor("ehr_app", MigratedDatabase.AppPassword);
    string Read => database.ConnectionStringFor("ehr_read", MigratedDatabase.ReadPassword);
    sealed record Review(Guid Actor, Guid Patient, Guid Draft, Guid Id, Guid Proposal);
    const string Result = """
        {"sorting":"done","fields":{"subjective":{"status":"pending","value":"Synthetic proposal","sources":[{"start":0,"end":18}],"warnings":[]}}}
        """;
    const string Stage = """
        UPDATE ehr.ai_note_reviews SET state = 'pending', result_json = @result::jsonb WHERE id = @review;
        INSERT INTO ehr.ai_field_proposals (id, review_id, patient_id, field, original_value, state)
        VALUES (@proposal, @review, @patient, 'subjective', 'Synthetic proposal', 'pending');
        """;
    const string Sign = """
        INSERT INTO ehr.clinical_notes (id, draft_id, patient_id, draft_version, subjective, objective, assessment, plan, signer_id, signed_at)
        VALUES (@note, @draft, @patient, 1, 'Synthetic subjective', 'Synthetic objective', 'Synthetic assessment', 'Synthetic plan', @actor, now())
        """;

    async Task<Review> RecordingAsync()
    {
        var actor = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        var patient = await Accounts.PatientAsync(database, actor);
        await Accounts.AssignAsync(database, actor, patient);
        var row = new Review(actor, patient, Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        await Accounts.ExecuteAsync(App, actor, """
            INSERT INTO ehr.clinical_drafts
                (id, patient_id, clinician_id, body, version, subjective, objective, assessment, plan, structured_version, has_unreviewed_proposals)
            VALUES (@draft, @patient, @actor, 'Synthetic note', 1, 'Synthetic subjective', 'Synthetic objective', 'Synthetic assessment', 'Synthetic plan', 1, false);
            INSERT INTO ehr.ai_note_reviews
                (id, patient_id, draft_id, clinician_id, session_id, max_seconds, max_chunk_bytes, state, started_version, created_at)
            VALUES (@review, @patient, @draft, @actor, @session, 900, 100000, 'recording', 1, now());
            """, Parameters(row).Concat(new[] { ("session", (object)Guid.NewGuid().ToString("N")) }).ToArray());
        return row;
    }

    static (string Name, object Value)[] Parameters(Review row) =>
        [("actor", row.Actor), ("patient", row.Patient), ("draft", row.Draft), ("review", row.Id),
            ("proposal", row.Proposal), ("result", Result), ("note", Guid.CreateVersion7())];
    Task Execute(Review row, string sql) => Accounts.ExecuteAsync(App, row.Actor, sql, Parameters(row));
    Task<List<string>> Query(Review row, string sql) => Accounts.QueryAsync(App, row.Actor, sql, Parameters(row));
    async Task<Review> PendingAsync()
    {
        var row = await RecordingAsync();
        await Execute(row, Stage);
        return row;
    }
    async Task Refused(Review row, string sql)
    {
        var error = await Assert.ThrowsAsync<PostgresException>(() => Execute(row, sql));
        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
    }

    // The expansion plan's step 14 (TC-UC10-08): proposals are staged for the note's four fields, Subjective and Objective
    // as Assessment and Plan, and only for them, so a History, the first of the three fields before then, is refused even
    // when the stored result names one. A History proposal staged before then is still decided, as any proposal is.
    [Fact]
    public async Task Only_the_four_fields_are_staged_and_a_history_staged_before_them_is_still_decided()
    {
        // Each of the four, as the app stages a result that proposes them all.
        var four = await RecordingAsync();
        await Accounts.ExecuteAsync(App, four.Actor,
            """
            UPDATE ehr.ai_note_reviews SET state = 'pending', result_json = @result::jsonb WHERE id = @review;
            INSERT INTO ehr.ai_field_proposals (id, review_id, patient_id, field, original_value, state)
            SELECT gen_random_uuid(), @review, @patient, f.key, f.value ->> 'value', 'pending' FROM jsonb_each(@result::jsonb -> 'fields') AS f;
            """,
            [.. Parameters(four).Where(parameter => parameter.Name != "result"), ("result", """
                {"sorting":"done","fields":{
                  "subjective":{"status":"pending","value":"Synthetic subjective proposal","sources":[{"start":0,"end":9}],"warnings":[]},
                  "objective":{"status":"pending","value":"Synthetic objective proposal","sources":[{"start":10,"end":19}],"warnings":[]},
                  "assessment":{"status":"pending","value":"Synthetic assessment proposal","sources":[{"start":20,"end":29}],"warnings":[]},
                  "plan":{"status":"pending","value":"Synthetic plan proposal","sources":[{"start":30,"end":39}],"warnings":[]}}}
                """)]);
        Assert.Equal(["assessment", "objective", "plan", "subjective"],
            await Query(four, "SELECT field FROM ehr.ai_field_proposals WHERE review_id = @review ORDER BY field"));

        // A result and a proposal that name a History, matching each other: only the field's name is wrong.
        var stageHistory = Stage.Replace("'subjective'", "'history'", StringComparison.Ordinal);
        (string Name, object Value)[] WithHistory(Review review) =>
            [.. Parameters(review).Where(parameter => parameter.Name != "result"), ("result", Result.Replace("\"subjective\"", "\"history\"", StringComparison.Ordinal))];
        var row = await RecordingAsync();
        var refused = await Assert.ThrowsAsync<PostgresException>(() => Accounts.ExecuteAsync(App, row.Actor, stageHistory, WithHistory(row)));
        Assert.Equal(PostgresErrorCodes.CheckViolation, refused.SqlState);
        Assert.Equal(["0"], await Query(row, "SELECT count(*) FROM ehr.ai_field_proposals WHERE review_id = @review"));

        var earlier = await RecordingAsync();
        await Accounts.ExecuteAsync(database.SuperuserConnectionString, null, "SET LOCAL session_replication_role = replica;\n" + stageHistory, WithHistory(earlier));
        await Execute(earlier, "UPDATE ehr.ai_field_proposals SET state = 'accepted', approved_value = original_value, decision_key = gen_random_uuid() WHERE id = @proposal");
        Assert.Equal(["history accepted"], await Query(earlier, "SELECT field || ' ' || state FROM ehr.ai_field_proposals WHERE id = @proposal"));
    }

    [Fact]
    public async Task Reviews_and_proposals_are_private_even_to_an_assigned_colleague_and_access_ends_with_the_panel()
    {
        var row = await PendingAsync();
        var other = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        await Accounts.AssignAsync(database, other, row.Patient);
        var admin = await Accounts.StaffAsync(database, StaffRoles.Admin);
        var locked = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        await Accounts.LockOutAsync(database, locked);
        var patientUser = await Accounts.PatientAccountAsync(database, row.Patient);
        foreach (var table in new[] { "ai_note_reviews", "ai_field_proposals" })
        {
            var sql = $"SELECT count(*) FROM ehr.{table} WHERE patient_id = @patient";
            foreach (var connection in new[] { App, Read })
            {
                Assert.Equal(["1"], await Accounts.QueryAsync(connection, row.Actor, sql, Parameters(row)));
                foreach (var denied in new Guid?[] { other, admin, locked, patientUser, null, Guid.NewGuid() })
                    Assert.Equal(["0"], await Accounts.QueryAsync(connection, denied, sql, Parameters(row)));
            }
            Assert.Empty(await Accounts.QueryAsync(App, other,
                $"UPDATE ehr.{table} SET state = 'cancelled' WHERE patient_id = @patient RETURNING id", Parameters(row)));
        }
        await Execute(row, "UPDATE ehr.patient_assignments SET ended_at = now() WHERE patient_id = @patient");
        Assert.Equal(["0"], await Query(row, "SELECT count(*) FROM ehr.ai_note_reviews WHERE id = @review"));
        Assert.Equal(["0"], await Query(row, "SELECT count(*) FROM ehr.ai_field_proposals WHERE id = @proposal"));
    }

    [Fact]
    public async Task Pending_evidence_blocks_direct_signing_even_with_a_false_cached_flag_and_cannot_be_marked_complete()
    {
        var row = await PendingAsync();
        Assert.Equal(["False"], await Query(row, "SELECT has_unreviewed_proposals FROM ehr.clinical_drafts WHERE id = @draft"));
        await Refused(row, Sign);
        await Refused(row, "UPDATE ehr.ai_note_reviews SET state = 'completed', imported_version = 1 WHERE id = @review");
        await Refused(row, "UPDATE ehr.ai_note_reviews SET state = 'cancelled' WHERE id = @review");
        Assert.Equal(["pending"], await Query(row, "SELECT state FROM ehr.ai_field_proposals WHERE id = @proposal"));
        Assert.Equal(["0"], await Query(row, "SELECT count(*) FROM ehr.clinical_notes WHERE draft_id = @draft"));
    }

    [Theory]
    [InlineData("UPDATE ehr.ai_note_reviews SET session_id = 'fabricated-session' WHERE id = @review")]
    [InlineData("UPDATE ehr.ai_note_reviews SET result_json = '{}'::jsonb WHERE id = @review")]
    [InlineData("UPDATE ehr.ai_field_proposals SET original_value = 'Fabricated' WHERE id = @proposal")]
    [InlineData("UPDATE ehr.ai_field_proposals SET state = 'accepted', approved_value = 'Fabricated', decision_key = gen_random_uuid() WHERE id = @proposal")]
    [InlineData("UPDATE ehr.ai_field_proposals SET state = 'edited', approved_value = '  ', decision_key = gen_random_uuid() WHERE id = @proposal")]
    public async Task Originals_bindings_and_invalid_decisions_cannot_be_forged(string sql)
    {
        var row = await PendingAsync();
        await Refused(row, sql);
    }

    [Fact]
    public async Task Review_identity_and_time_are_database_generated_and_terminal_evidence_is_immutable_and_audited()
    {
        var row = await PendingAsync();
        await Execute(row, """
            UPDATE ehr.ai_field_proposals SET state = 'edited', approved_value = 'Synthetic correction', decision_key = gen_random_uuid(),
                reviewer_id = gen_random_uuid(), reviewed_at = '2000-01-01' WHERE id = @proposal;
            UPDATE ehr.ai_note_reviews SET state = 'completed', imported_version = 1 WHERE id = @review;
            """);
        Assert.Equal([row.Actor.ToString()], await Query(row,
            "SELECT reviewer_id FROM ehr.ai_field_proposals WHERE id = @proposal AND reviewed_at > now() - interval '1 minute'"));
        await Refused(row, "UPDATE ehr.ai_field_proposals SET approved_value = 'Changed' WHERE id = @proposal");
        await Refused(row, "UPDATE ehr.ai_note_reviews SET state = 'pending' WHERE id = @review");
        Assert.Equal(["2"], await Accounts.QueryAsync(database.SuperuserConnectionString, null,
            "SELECT count(DISTINCT table_name) FROM ehr.audit_events WHERE table_name IN ('ai_note_reviews', 'ai_field_proposals') AND patient_id = @patient AND actor_id = @actor", Parameters(row)));
        foreach (var table in new[] { "ai_note_reviews", "ai_field_proposals" })
        {
            await Assert.ThrowsAsync<PostgresException>(() => Execute(row, $"DELETE FROM ehr.{table} WHERE patient_id = @patient"));
            await Assert.ThrowsAsync<PostgresException>(() => Accounts.ExecuteAsync(Read, row.Actor,
                $"UPDATE ehr.{table} SET state = 'pending' WHERE patient_id = @patient", Parameters(row)));
        }
    }

    [Fact]
    public async Task A_review_cannot_be_attached_to_another_patient_or_reuse_an_unrelated_field()
    {
        var row = await RecordingAsync();
        var other = await Accounts.PatientAsync(database, row.Actor);
        await Accounts.AssignAsync(database, row.Actor, other);
        var mismatch = row with { Patient = other };
        await Refused(mismatch, "UPDATE ehr.ai_note_reviews SET patient_id = @patient WHERE id = @review");
        await Execute(row, Stage);
        await Refused(mismatch, """
            INSERT INTO ehr.ai_field_proposals (id, review_id, patient_id, field, original_value, state)
            VALUES (gen_random_uuid(), @review, @patient, 'plan', 'Synthetic proposal', 'pending')
            """);
        await Refused(row, """
            INSERT INTO ehr.ai_field_proposals (id, review_id, patient_id, field, original_value, state)
            VALUES (gen_random_uuid(), @review, @patient, 'plan', 'Synthetic proposal', 'pending')
            """);
    }

    [Fact]
    public async Task Concurrent_staging_and_signing_share_the_source_lock_so_signing_rechecks_committed_pending_evidence()
    {
        var row = await RecordingAsync();
        await using var stagingConnection = new NpgsqlConnection(App);
        await using var signingConnection = new NpgsqlConnection(App);
        await stagingConnection.OpenAsync(Cancel);
        await signingConnection.OpenAsync(Cancel);
        await using var staging = await stagingConnection.BeginTransactionAsync(Cancel);
        await using var signing = await signingConnection.BeginTransactionAsync(Cancel);
        foreach (var (connection, transaction) in new[] { (stagingConnection, staging), (signingConnection, signing) })
        {
            await using var set = new NpgsqlCommand("SELECT set_config('app.user_id', @actor, true)", connection, transaction);
            set.Parameters.AddWithValue("actor", row.Actor.ToString());
            await set.ExecuteNonQueryAsync(Cancel);
        }
        await using var stage = new NpgsqlCommand(Stage, stagingConnection, staging);
        foreach (var (name, value) in Parameters(row)) stage.Parameters.AddWithValue(name, value);
        await stage.ExecuteNonQueryAsync(Cancel);
        await using var sign = new NpgsqlCommand(Sign, signingConnection, signing);
        foreach (var (name, value) in Parameters(row)) sign.Parameters.AddWithValue(name, value);
        var waiting = sign.ExecuteNonQueryAsync(Cancel);
        var blocked = false;
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            blocked = (await Accounts.QueryAsync(database.SuperuserConnectionString, null,
                "SELECT cardinality(pg_blocking_pids(@pid))", ("pid", signingConnection.ProcessID)))[0] != "0";
            if (blocked) break;
            await Task.Delay(25, Cancel);
        }
        Assert.True(blocked);
        await staging.CommitAsync(Cancel);
        var error = await Assert.ThrowsAsync<PostgresException>(async () => await waiting);
        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        Assert.Equal(["0"], await Query(row, "SELECT count(*) FROM ehr.clinical_notes WHERE draft_id = @draft"));
    }

    [Fact]
    public async Task An_unstaged_binding_blocks_direct_signing_until_explicit_discard_without_changing_manual_content()
    {
        var row = await RecordingAsync();
        await Refused(row, Sign);
        await Execute(row, "UPDATE ehr.ai_note_reviews SET state = 'cancelled' WHERE id = @review");
        await Execute(row, Sign);
        Assert.Equal(["Synthetic subjective"], await Query(row, "SELECT subjective FROM ehr.clinical_notes WHERE draft_id = @draft"));
    }
}
