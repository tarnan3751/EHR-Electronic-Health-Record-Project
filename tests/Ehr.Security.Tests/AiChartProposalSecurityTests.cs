using Ehr.Data.Accounts;
using Ehr.Testing;
using Npgsql;
using Xunit;
using static Ehr.Testing.TestRun;

namespace Ehr.Security.Tests;

// The expansion plan's step 15 (FR-14 as reworded): what the database lets anyone do with the orders and allergies to add
// a dictation proposes, as the runtime roles, in SQL of its own (db/policies/10-grants.sql, 30-rls.sql and
// 55-ai-review.sql). A proposal is staged only as its review's stored result holds it, is seen only by the review's
// clinician, and is decided once: ordered only by an order signed for it just then by whoever decides it, recorded only by
// a coded allergy added for it just then by them, or dismissed. Nothing about it moves otherwise, nobody deletes one, and
// nothing turns one into an order or an allergy. Every row is synthetic.
public class AiChartProposalSecurityTests(MigratedDatabase database) : IClassFixture<MigratedDatabase>
{
    string App => database.ConnectionStringFor("ehr_app", MigratedDatabase.AppPassword);

    string Read => database.ConnectionStringFor("ehr_read", MigratedDatabase.ReadPassword);

    // What the review stores, as the app stages it: an order at place 0, an allergy to add at place 1, and no field.
    const string Result = """
        {"sorting":"done","transcript":{"text":"Go ahead and order a CBC. Add penicillin to her allergy list.","final":true,"segments":[]},
         "fields":{"subjective":{"status":"empty","reasons":["not_dictated"]}},"notCarriedOver":[],
         "proposals":[{"kind":"order","start":0,"end":24,"warnings":[]},{"kind":"allergy_addition","start":26,"end":61,"warnings":[]}]}
        """;

    // Staged as NoteReviews stages a result with no field to decide: the review completed with the result, then a
    // proposal for each of the result's.
    const string Stage = """
        UPDATE ehr.ai_note_reviews SET state = 'completed', imported_version = 1, result_json = @result::jsonb WHERE id = @review;
        INSERT INTO ehr.ai_chart_proposals (id, review_id, patient_id, ordinal, kind, state)
        VALUES (@order_proposal, @review, @patient, 0, 'order', 'pending'), (@allergy_proposal, @review, @patient, 1, 'allergy_addition', 'pending');
        """;

    const string Insert = "INSERT INTO ehr.ai_chart_proposals (id, review_id, patient_id, ordinal, kind, state) VALUES (gen_random_uuid(), @review, @patient, @ordinal, @kind, 'pending')";

    // Signed as the order form signs one: the database's signing trigger sets the signer and the time.
    const string Sign = "INSERT INTO ehr.orders (id, patient_id, type, catalog_code, details, status) VALUES (@order, @patient, 'lab', @code, '{}', 'active');";

    const string Ordered = "UPDATE ehr.ai_chart_proposals SET state = 'ordered', order_id = @order WHERE id = @proposal;";

    // Added as the allergy form adds one: the database's history trigger records who and when.
    const string Add = """
        INSERT INTO ehr.allergy_intolerances (id, patient_id, code, code_system, display, clinical_status, recorded_at)
        VALUES (@allergy, @patient, @allergen, @system, 'Synthetic allergen', @status, now());
        """;

    const string Recorded = "UPDATE ehr.ai_chart_proposals SET state = 'recorded', allergy_id = @allergy WHERE id = @proposal;";

    // The user the statements that follow run as, inside the transaction, as the interceptor sets it.
    static string As(string parameter) => $"SELECT set_config('app.user_id', @{parameter}::text, true);";

    // A staged review of a clinician's draft for a patient on their panel, with its two proposals.
    sealed record Staged(Guid Clinician, Guid Patient, Guid Draft, Guid Review, Guid OrderProposal, Guid AllergyProposal);

    Task<(Guid Clinician, Guid Patient)> OnPanelAsync() => Accounts.OnPanelAsync(database);

    // A draft and its dictation review, still recording, as the app begins one.
    async Task<(Guid Draft, Guid Review)> RecordingAsync(Guid clinician, Guid patient)
    {
        var (draft, review) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        await Accounts.ExecuteAsync(App, clinician, """
            INSERT INTO ehr.clinical_drafts
                (id, patient_id, clinician_id, body, version, subjective, objective, assessment, plan, structured_version, has_unreviewed_proposals)
            VALUES (@draft, @patient, @clinician, 'Synthetic note', 1, 'Synthetic subjective', 'Synthetic objective', 'Synthetic assessment', 'Synthetic plan', 1, false);
            INSERT INTO ehr.ai_note_reviews (id, patient_id, draft_id, clinician_id, session_id, max_seconds, max_chunk_bytes, state, started_version, created_at)
            VALUES (@review, @patient, @draft, @clinician, @session, 900, 100000, 'recording', 1, now());
            """, ("draft", draft), ("review", review), ("patient", patient), ("clinician", clinician), ("session", Guid.NewGuid().ToString("N")));
        return (draft, review);
    }

    async Task<Staged> StagedAsync((Guid Clinician, Guid Patient)? onPanel = null)
    {
        var (clinician, patient) = onPanel ?? await OnPanelAsync();
        var (draft, review) = await RecordingAsync(clinician, patient);
        var row = new Staged(clinician, patient, draft, review, Guid.CreateVersion7(), Guid.CreateVersion7());
        await Accounts.ExecuteAsync(App, clinician, Stage, ("review", review), ("patient", patient), ("result", Result),
            ("order_proposal", row.OrderProposal), ("allergy_proposal", row.AllergyProposal));
        return row;
    }

    // The statements as the staged row's clinician, in one transaction, with the row's patient and proposal unless given.
    Task Run(Staged row, string sql, params (string Name, object Value)[] parameters)
    {
        var all = new Dictionary<string, object>
        {
            ["patient"] = row.Patient,
            ["proposal"] = row.OrderProposal,
            ["review"] = row.Review,
            ["clinician"] = row.Clinician,
            ["order"] = Guid.CreateVersion7(),
            ["allergy"] = Guid.CreateVersion7(),
            ["code"] = "SYN-LAB-002",
            ["allergen"] = "1191",
            ["system"] = "rxnorm",
            ["status"] = "active",
        };
        foreach (var (name, value) in parameters)
        {
            all[name] = value;
        }

        return Accounts.ExecuteAsync(App, row.Clinician, sql, [.. all.Select(pair => (pair.Key, pair.Value))]);
    }

    static async Task<PostgresException> Refused(Func<Task> act, string state = PostgresErrorCodes.CheckViolation, string? constraint = null)
    {
        var error = await Assert.ThrowsAsync<PostgresException>(act);
        Assert.Equal(state, error.SqlState);
        if (constraint is not null)
        {
            Assert.Equal(constraint, error.ConstraintName);
        }

        return error;
    }

    async Task<string> StateOf(Staged row, Guid proposal) =>
        string.Join(" ", await Accounts.QueryAsync(App, row.Clinician,
            "SELECT state || ' ' || coalesce(order_id::text, '-') || ' ' || coalesce(allergy_id::text, '-') FROM ehr.ai_chart_proposals WHERE id = @id",
            ("id", proposal)));

    // TC-UC10-11, in the database: a proposal is staged only as the review's stored result holds it, at its place there and
    // of its kind, waiting, once, before the note is signed, for the review's own patient.
    [Fact]
    public async Task Only_a_waiting_proposal_the_stored_result_holds_is_staged()
    {
        var row = await StagedAsync();
        Assert.Equal(["allergy_addition pending", "order pending"], await Accounts.QueryAsync(App, row.Clinician,
            "SELECT kind || ' ' || state FROM ehr.ai_chart_proposals WHERE review_id = @review ORDER BY kind", ("review", row.Review)));

        // Another kind than the result gives at a place, or a place the result has no proposal at.
        await Refused(() => Run(row, Insert, ("ordinal", (short)0), ("kind", "allergy_addition")), constraint: "ai_chart_proposals_staged");
        await Refused(() => Run(row, Insert, ("ordinal", (short)2), ("kind", "order")), constraint: "ai_chart_proposals_staged");
        // A second proposal at a place already staged.
        await Refused(() => Run(row, Insert, ("ordinal", (short)0), ("kind", "order")), PostgresErrorCodes.UniqueViolation);
        // Already decided, with all a decision has, as a forger would send it.
        await Refused(() => Run(row, """
            INSERT INTO ehr.ai_chart_proposals (id, review_id, patient_id, ordinal, kind, state, decided_by, decided_at)
            VALUES (gen_random_uuid(), @review, @patient, 1, 'allergy_addition', 'dismissed', @clinician, now())
            """), constraint: "ai_chart_proposals_staged");
        // Named with another patient than its review's.
        var other = await OnPanelAsync();
        await Refused(() => Run(row, Insert, ("ordinal", (short)3), ("kind", "order"), ("patient", other.Patient)),
            constraint: "ai_chart_proposals_review");

        // A review with no stored result yet: still recording.
        var (_, recording) = await RecordingAsync(row.Clinician, row.Patient);
        await Refused(() => Run(row, Insert, ("review", recording), ("ordinal", (short)0), ("kind", "order")), constraint: "ai_chart_proposals_staged");

        // Not once the review's note is signed.
        var signed = await OnPanelAsync();
        var (draft, review) = await RecordingAsync(signed.Clinician, signed.Patient);
        await Accounts.ExecuteAsync(App, signed.Clinician,
            "UPDATE ehr.ai_note_reviews SET state = 'completed', imported_version = 1, result_json = @result::jsonb WHERE id = @review",
            ("review", review), ("result", Result));
        await SignNoteAsync(signed.Clinician, signed.Patient, draft);
        await Refused(() => Accounts.ExecuteAsync(App, signed.Clinician, Insert,
            ("review", review), ("patient", signed.Patient), ("ordinal", (short)0), ("kind", "order")), constraint: "ai_chart_proposals_staged");
    }

    Task SignNoteAsync(Guid clinician, Guid patient, Guid draft) => Accounts.ExecuteAsync(App, clinician, """
        INSERT INTO ehr.clinical_notes (id, draft_id, patient_id, draft_version, subjective, objective, assessment, plan, signer_id, signed_at)
        VALUES (gen_random_uuid(), @draft, @patient, 1, 'Synthetic subjective', 'Synthetic objective', 'Synthetic assessment', 'Synthetic plan', @clinician, now())
        """, ("draft", draft), ("patient", patient), ("clinician", clinician));

    // Only the review's clinician sees or decides its proposals: not a colleague with the patient on their panel, an
    // admin, the patient or nobody; on either runtime role. ehr_read writes none, and nobody deletes one.
    [Fact]
    public async Task Proposals_are_their_clinicians_alone_and_never_deleted()
    {
        var row = await StagedAsync();
        var colleague = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        await Accounts.AssignAsync(database, colleague, row.Patient);
        var admin = await Accounts.StaffAsync(database, StaffRoles.Admin);
        var patientUser = await Accounts.PatientAccountAsync(database, row.Patient);
        const string count = "SELECT count(*) FROM ehr.ai_chart_proposals WHERE patient_id = @patient";
        foreach (var connection in new[] { App, Read })
        {
            Assert.Equal(["2"], await Accounts.QueryAsync(connection, row.Clinician, count, ("patient", row.Patient)));
            foreach (var denied in new Guid?[] { colleague, admin, patientUser, null, Guid.NewGuid() })
            {
                Assert.Equal(["0"], await Accounts.QueryAsync(connection, denied, count, ("patient", row.Patient)));
            }
        }

        Assert.Empty(await Accounts.QueryAsync(App, colleague,
            "UPDATE ehr.ai_chart_proposals SET state = 'dismissed' WHERE patient_id = @patient RETURNING id", ("patient", row.Patient)));
        await Refused(() => Accounts.ExecuteAsync(Read, row.Clinician, "UPDATE ehr.ai_chart_proposals SET state = 'dismissed' WHERE id = @id",
            ("id", row.OrderProposal)), PostgresErrorCodes.InsufficientPrivilege);
        await Refused(() => Accounts.ExecuteAsync(Read, row.Clinician, Insert,
            ("review", row.Review), ("patient", row.Patient), ("ordinal", (short)3), ("kind", "order")), PostgresErrorCodes.InsufficientPrivilege);
        await Refused(() => Run(row, "DELETE FROM ehr.ai_chart_proposals WHERE id = @proposal"), PostgresErrorCodes.InsufficientPrivilege);
        Assert.Equal("pending - -", await StateOf(row, row.OrderProposal));
    }

    // D12 for orders (TC-UC1-23 and 24, in the database): an order decides a proposed order only when it is an active
    // order of the patient, signed just then by the clinician deciding, and not a change of another order; and it decides
    // one proposal. Step 13d's review's cases: an order signed in an earlier transaction, by another clinician, for another
    // patient, or deciding another proposal already.
    [Fact]
    public async Task An_order_decides_a_proposed_order_only_when_its_clinician_signed_it_for_it_now()
    {
        var row = await StagedAsync();
        const string order = "ai_chart_proposals_order";

        // Signed in an earlier transaction, by the same clinician, for the same patient; and named while another is signed now.
        var earlier = Guid.CreateVersion7();
        await Run(row, Sign, ("order", earlier), ("code", "SYN-LAB-001"));
        await Refused(() => Run(row, Ordered, ("order", earlier)), constraint: order);
        await Refused(() => Run(row, Sign + Ordered.Replace("@order", "@earlier", StringComparison.Ordinal), ("earlier", earlier)), constraint: order);

        // Signed now, but by a colleague with the patient on their panel.
        var colleague = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        await Accounts.AssignAsync(database, colleague, row.Patient);
        await Refused(() => Run(row, As("colleague") + Sign + As("clinician") + Ordered, ("colleague", colleague)), constraint: order);

        // Signed now, by the clinician, for another patient on their panel.
        var otherPatient = await Accounts.PatientAsync(database, row.Clinician);
        await Accounts.AssignAsync(database, row.Clinician, otherPatient);
        await Refused(() => Run(row,
            Sign.Replace("@patient", "@other", StringComparison.Ordinal) + Ordered, ("other", otherPatient)), constraint: order);

        // Signed now, then discontinued before it decides it.
        await Refused(() => Run(row, Sign + "UPDATE ehr.orders SET status = 'revoked', discontinue_reason = 'Synthetic' WHERE id = @order;" + Ordered),
            constraint: order);

        // Signed now as a change of the order signed earlier.
        await Refused(() => Run(row, """
            INSERT INTO ehr.orders (id, patient_id, type, catalog_code, details, status, replaces_order_id)
            VALUES (@order, @patient, 'lab', 'SYN-LAB-001', '{}', 'active', @earlier);
            UPDATE ehr.orders SET status = 'revoked', discontinue_reason = 'Replaced by a changed order', replaced_by_order_id = @order WHERE id = @earlier;
            """ + Ordered, ("earlier", earlier)), constraint: order);

        // An allergy's proposal is never ordered, whatever is signed for it.
        await Refused(() => Run(row, Sign + Ordered, ("proposal", row.AllergyProposal)), constraint: "ck_ai_chart_proposals_decision");

        // One order decides one proposal: another dictation's of the same patient, both named in one transaction.
        var second = await StagedAsync((row.Clinician, row.Patient));
        await Refused(() => Run(row, Sign + Ordered + Ordered.Replace("@proposal", "@second", StringComparison.Ordinal),
            ("second", second.OrderProposal)), PostgresErrorCodes.UniqueViolation);
        Assert.Equal("pending - -", await StateOf(row, row.OrderProposal));
        Assert.Equal("pending - -", await StateOf(row, second.OrderProposal));

        // As the order form does it: signed now, by the clinician, then the proposal decided with it. The database
        // records who decided it, and when.
        var signed = Guid.CreateVersion7();
        await Run(row, Sign + Ordered, ("order", signed));
        Assert.Equal($"ordered {signed} -", await StateOf(row, row.OrderProposal));
        Assert.Equal(["True"], await Accounts.QueryAsync(App, row.Clinician,
            "SELECT decided_by = @clinician AND decided_at <= clock_timestamp() FROM ehr.ai_chart_proposals WHERE id = @id",
            ("clinician", row.Clinician), ("id", row.OrderProposal)));
    }

    // D12 for allergies (TC-UC4-33 and 34, in the database): an allergy decides a proposed allergy only when it is an active,
    // coded allergy of the patient, not "No known allergies", added just then by the clinician deciding; and it decides one
    // proposal. One that was there before and is only changed now doesn't, nor one a colleague adds.
    [Fact]
    public async Task An_allergy_decides_a_proposed_allergy_only_when_its_clinician_added_it_for_it_now()
    {
        var row = await StagedAsync();
        const string allergy = "ai_chart_proposals_allergy";
        (string, object) proposal = ("proposal", row.AllergyProposal);

        // Added in an earlier transaction, then changed now; or named while another is added now.
        var earlier = Guid.CreateVersion7();
        await Run(row, Add, ("allergy", earlier));
        await Refused(() => Run(row, "UPDATE ehr.allergy_intolerances SET reaction = 'Hives' WHERE id = @allergy;" + Recorded,
            ("allergy", earlier), proposal), constraint: allergy);
        await Refused(() => Run(row, Add + Recorded.Replace("@allergy", "@earlier", StringComparison.Ordinal),
            ("allergen", "7980"), ("earlier", earlier), proposal), constraint: allergy);

        // Added now, but with no code, with a code and no system, or inactive.
        await Refused(() => Run(row, Add + Recorded, ("allergen", DBNull.Value), proposal), constraint: allergy);
        await Refused(() => Run(row, Add + Recorded, ("allergen", "2670"), ("system", DBNull.Value), proposal), constraint: allergy);
        await Refused(() => Run(row, Add + Recorded, ("allergen", "2670"), ("status", "inactive"), proposal), constraint: allergy);

        // Added now, but by a colleague with the patient on their panel.
        var colleague = await Accounts.StaffAsync(database, StaffRoles.Clinician);
        await Accounts.AssignAsync(database, colleague, row.Patient);
        await Refused(() => Run(row, As("colleague") + Add + As("clinician") + Recorded, ("allergen", "2670"), ("colleague", colleague), proposal),
            constraint: allergy);

        // Added now, by the clinician, for another patient on their panel.
        var otherPatient = await Accounts.PatientAsync(database, row.Clinician);
        await Accounts.AssignAsync(database, row.Clinician, otherPatient);
        await Refused(() => Run(row, Add.Replace("@patient", "@other", StringComparison.Ordinal) + Recorded,
            ("other", otherPatient), proposal), constraint: allergy);

        // "No known allergies", for a patient with nothing recorded, coded as no other is.
        var none = await StagedAsync();
        await Refused(() => Run(none, Add + Recorded, ("allergen", "no-known-allergy"), ("proposal", none.AllergyProposal)), constraint: allergy);

        // An order's proposal is never recorded, whatever is added for it.
        await Refused(() => Run(row, Add + Recorded, ("allergen", "2670")), constraint: "ck_ai_chart_proposals_decision");

        // One allergy decides one proposal.
        var second = await StagedAsync((row.Clinician, row.Patient));
        await Refused(() => Run(row, Add + Recorded + Recorded.Replace("@proposal", "@second", StringComparison.Ordinal),
            ("allergen", "2670"), ("second", second.AllergyProposal), proposal), PostgresErrorCodes.UniqueViolation);
        Assert.Equal("pending - -", await StateOf(row, row.AllergyProposal));

        // As the allergy form does it.
        var added = Guid.CreateVersion7();
        await Run(row, Add + Recorded, ("allergy", added), ("allergen", "2670"), proposal);
        Assert.Equal($"recorded - {added}", await StateOf(row, row.AllergyProposal));
    }

    // Decided once (TC-UC13-11, in the database): dismissed, then nothing more; who and when are the database's; the apps
    // may write only the decision's columns, and the tables' owner may not change what the dictation gave either.
    [Fact]
    public async Task A_proposal_is_decided_once_and_nothing_else_about_it_moves()
    {
        var row = await StagedAsync();
        await Refused(() => Run(row, "UPDATE ehr.ai_chart_proposals SET state = 'pending' WHERE id = @proposal"),
            constraint: "ck_ai_chart_proposals_decision");
        await Run(row, "UPDATE ehr.ai_chart_proposals SET state = 'dismissed' WHERE id = @proposal");
        Assert.Equal("dismissed - -", await StateOf(row, row.OrderProposal));
        await Refused(() => Run(row, "UPDATE ehr.ai_chart_proposals SET state = 'dismissed' WHERE id = @proposal"),
            constraint: "ai_chart_proposals_decided");
        await Refused(() => Run(row, Sign + Ordered), constraint: "ai_chart_proposals_decided");

        foreach (var column in new[] { "decided_by = @clinician", "decided_at = now()", "kind = 'allergy_addition'", "ordinal = 5", "review_id = gen_random_uuid()" })
        {
            await Refused(() => Run(row, $"UPDATE ehr.ai_chart_proposals SET {column} WHERE id = @proposal"), PostgresErrorCodes.InsufficientPrivilege);
        }

        var other = await StagedAsync((row.Clinician, row.Patient));
        foreach (var column in new[] { "id = gen_random_uuid()", $"review_id = '{other.Review}'", "ordinal = 4", "kind = 'order'" })
        {
            await Refused(() => Accounts.ExecuteAsync(database.SuperuserConnectionString, null,
                $"{As("clinician")} UPDATE ehr.ai_chart_proposals SET {column} WHERE id = @proposal",
                ("clinician", row.Clinician), ("proposal", row.AllergyProposal)), PostgresErrorCodes.InsufficientPrivilege);
        }

        Assert.Equal("pending - -", await StateOf(row, row.AllergyProposal));
    }

    // D8 (TC-UC14-35, in the database): signing the note neither waits for its proposals nor ends them.
    [Fact]
    public async Task Signing_the_note_neither_waits_for_its_proposals_nor_ends_them()
    {
        var row = await StagedAsync();
        await SignNoteAsync(row.Clinician, row.Patient, row.Draft);
        await Run(row, Sign + Ordered);
        Assert.StartsWith("ordered", await StateOf(row, row.OrderProposal), StringComparison.Ordinal);
        await Run(row, "UPDATE ehr.ai_chart_proposals SET state = 'dismissed' WHERE id = @proposal", ("proposal", row.AllergyProposal));
        Assert.Equal("dismissed - -", await StateOf(row, row.AllergyProposal));
    }

    // Two tabs deciding one proposal at once: the second waits for the first, finds it decided, and goes back to its
    // savepoint as the order form and the allergy form do, so it leaves no order or allergy behind. One decision, the
    // first's, and only its order or allergy.
    [Theory]
    [InlineData("order")]
    [InlineData("allergy")]
    public async Task Two_tabs_deciding_one_proposal_leave_one_decision_and_nothing_else(string kind)
    {
        var row = await StagedAsync();
        var (write, link, proposal, table) = kind == "order"
            ? (Sign, Ordered, row.OrderProposal, "orders")
            : (Add, Recorded, row.AllergyProposal, "allergy_intolerances");
        var (first, second) = (Guid.CreateVersion7(), Guid.CreateVersion7());

        await using var a = await BeginAsync(row.Clinician);
        await RunOnAsync(a, "SAVEPOINT decide;" + write + link + "RELEASE SAVEPOINT decide;", row.Patient, first, proposal, "SYN-LAB-001", "1191");

        await using var b = await BeginAsync(row.Clinician);
        var waiting = Task.Run(async () =>
        {
            await RunOnAsync(b, "SAVEPOINT decide;", row.Patient, second, proposal, "SYN-LAB-002", "2670");
            var refused = await Assert.ThrowsAsync<PostgresException>(() => RunOnAsync(b, write + link, row.Patient, second, proposal, "SYN-LAB-002", "2670"));
            await RunOnAsync(b, "ROLLBACK TO SAVEPOINT decide;", row.Patient, second, proposal, "SYN-LAB-002", "2670");
            await b.Transaction.CommitAsync(Cancel);
            return refused;
        }, Cancel);

        // The second tab is held behind the first, on the patient's row both writes lock, until the first commits.
        await WaitForLockAsync(b.Pid);
        Assert.False(waiting.IsCompleted);
        await a.Transaction.CommitAsync(Cancel);
        var error = await waiting;
        Assert.Equal("ai_chart_proposals_decided", error.ConstraintName);

        var expected = kind == "order" ? $"ordered {first} -" : $"recorded - {first}";
        Assert.Equal(expected, await StateOf(row, proposal));
        Assert.Equal([first.ToString()], await Accounts.QueryAsync(App, row.Clinician,
            $"SELECT id FROM ehr.{table} WHERE patient_id = @patient", ("patient", row.Patient)));
    }

    // A medication order for lisinopril 10 mg, as the order form signs one, and a refill request approved with it.
    const string SignLisinopril = "INSERT INTO ehr.orders (id, patient_id, type, catalog_code, details, status) VALUES (@order, @patient, 'medication', '314076', '{}', 'active');";

    const string Approve = "UPDATE ehr.refill_requests SET order_id = @order WHERE id = @request;";

    // The patient's portal account asks to renew a lisinopril 10 mg on their chart, as the portal asks (step 13d).
    async Task<Guid> RefillRequestAsync(Staged row)
    {
        var (medication, request) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        await Accounts.ExecuteAsync(database.SuperuserConnectionString, null,
            "INSERT INTO ehr.medication_statements (id, patient_id, code, display, dosage, status, sensitive) VALUES (@id, @patient, '314076', 'lisinopril 10 MG Oral Tablet', '1 tablet by mouth daily', 'active', false)",
            ("id", medication), ("patient", row.Patient));
        await Accounts.ExecuteAsync(App, await Accounts.PatientAccountAsync(database, row.Patient), """
            INSERT INTO ehr.refill_requests (id, patient_id, medication_statement_id, medication_display, note, requested_by, requested_at, status, sensitive)
            VALUES (@id, @patient, @medication, 'Synthetic', NULL, gen_random_uuid(), now(), 'requested', false)
            """, ("id", request), ("patient", row.Patient), ("medication", medication));
        return request;
    }

    // D7 in the database (the expansion plan's step 15): one order answers a refill request or decides a proposal, never
    // both, whichever is linked first, in one transaction or in two at once: the second waits on the order's lock until
    // the first commits, then finds the first's link. Only in READ COMMITTED, where it can.
    [Fact]
    public async Task An_order_answers_a_refill_request_or_decides_a_proposal_never_both()
    {
        var row = await StagedAsync();
        var request = await RefillRequestAsync(row);

        await Refused(() => Run(row, SignLisinopril + Approve + Ordered, ("request", request)), constraint: "ai_chart_proposals_order_refill");
        await Refused(() => Run(row, SignLisinopril + Ordered + Approve, ("request", request)), constraint: "refill_requests_order_proposal");
        Assert.Equal("pending - -", await StateOf(row, row.OrderProposal));
        Assert.Equal(["requested"], await Accounts.QueryAsync(App, row.Clinician, "SELECT status FROM ehr.refill_requests WHERE id = @id", ("id", request)));

        await using var repeatable = await BeginAsync(row.Clinician, System.Data.IsolationLevel.RepeatableRead);
        var order = Guid.CreateVersion7();
        await Run(row, SignLisinopril, ("order", order));
        await Refused(() => RunOnAsync(repeatable, Ordered, row.Patient, order, row.OrderProposal, "314076", "1191"), PostgresErrorCodes.FeatureNotSupported);
    }

    [Theory]
    [InlineData("proposal first")]
    [InlineData("refill first")]
    public async Task Two_transactions_linking_one_order_to_a_refill_request_and_a_proposal_leave_one_link(string first)
    {
        var row = await StagedAsync();
        var request = await RefillRequestAsync(row);
        await using var deciding = await BeginAsync(row.Clinician);
        await using var approving = await BeginAsync(row.Clinician);

        // Signed by the clinician in a transaction of theirs committed after both began: "now" to both.
        var order = Guid.CreateVersion7();
        await Run(row, SignLisinopril, ("order", order));

        async Task Link(Tab tab)
        {
            await using var command = new NpgsqlCommand(tab == deciding ? Ordered : Approve, tab.Connection, tab.Transaction);
            command.Parameters.AddWithValue("order", order);
            command.Parameters.AddWithValue("proposal", row.OrderProposal);
            command.Parameters.AddWithValue("request", request);
            await command.ExecuteNonQueryAsync(Cancel);
        }

        var (ahead, behind) = first == "proposal first" ? (deciding, approving) : (approving, deciding);
        await Link(ahead);
        var waiting = Task.Run(() => Assert.ThrowsAsync<PostgresException>(() => Link(behind)), Cancel);
        await WaitForLockAsync(behind.Pid);
        await ahead.Transaction.CommitAsync(Cancel);
        var error = await waiting;

        Assert.Equal(first == "proposal first" ? "refill_requests_order_proposal" : "ai_chart_proposals_order_refill", error.ConstraintName);
        Assert.Equal(first == "proposal first" ? $"ordered {order} -" : "pending - -", await StateOf(row, row.OrderProposal));
        Assert.Equal([first == "proposal first" ? "requested" : "completed"], await Accounts.QueryAsync(App, row.Clinician,
            "SELECT status FROM ehr.refill_requests WHERE id = @id", ("id", request)));
    }

    sealed record Tab(NpgsqlConnection Connection, NpgsqlTransaction Transaction, int Pid) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Transaction.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }

    async Task<Tab> BeginAsync(Guid user, System.Data.IsolationLevel isolation = System.Data.IsolationLevel.ReadCommitted)
    {
        var connection = new NpgsqlConnection(App);
        await connection.OpenAsync(Cancel);
        var transaction = await connection.BeginTransactionAsync(isolation, Cancel);
        await using var set = new NpgsqlCommand("SELECT set_config('app.user_id', @user, true), pg_backend_pid()", connection, transaction);
        set.Parameters.AddWithValue("user", user.ToString());
        await using var reader = await set.ExecuteReaderAsync(Cancel);
        await reader.ReadAsync(Cancel);
        var pid = reader.GetInt32(1);
        return new Tab(connection, transaction, pid);
    }

    static async Task RunOnAsync(Tab tab, string sql, Guid patient, Guid written, Guid proposal, string code, string allergen)
    {
        await using var command = new NpgsqlCommand(sql, tab.Connection, tab.Transaction);
        command.Parameters.AddWithValue("order", written);
        command.Parameters.AddWithValue("allergy", written);
        command.Parameters.AddWithValue("proposal", proposal);
        command.Parameters.AddWithValue("code", code);
        command.Parameters.AddWithValue("allergen", allergen);
        command.Parameters.AddWithValue("system", "rxnorm");
        command.Parameters.AddWithValue("status", "active");
        command.Parameters.AddWithValue("patient", patient);
        await command.ExecuteNonQueryAsync(Cancel);
    }

    // Until the backend waits on a lock another transaction holds.
    async Task WaitForLockAsync(int pid)
    {
        for (var tries = 0; tries < 200; tries++)
        {
            var waiting = await Accounts.QueryAsync(database.SuperuserConnectionString, null,
                "SELECT count(*) FROM pg_stat_activity WHERE pid = @pid AND wait_event_type = 'Lock'", ("pid", pid));
            if (waiting is ["1"])
            {
                return;
            }

            await Task.Delay(50, Cancel);
        }

        Assert.Fail("The second tab never waited for the first.");
    }
}
