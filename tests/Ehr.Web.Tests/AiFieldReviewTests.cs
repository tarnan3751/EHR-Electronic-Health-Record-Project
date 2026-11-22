using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ehr.Data.Accounts;
using Ehr.Data.Audit;
using Ehr.Domain.Notes;
using Ehr.Web.Api.Dictation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Xunit;
using static Ehr.Web.Tests.Browser;

namespace Ehr.Web.Tests;

public class AiFieldReviewTests(WebApps apps) : IClassFixture<WebApps>
{
    readonly DictationSidecarStub sidecar = apps.Dictation.Reset();
    static CancellationToken Cancel => TestContext.Current.CancellationToken;
    const string Api = DictationApi.Path;

    sealed record Flow(Session Session, Guid Patient, Guid Review, Guid Draft, string SessionId, string Token, string Editor, string ReviewUrl);

    async Task<Flow> BeginAsync(Guid? patient = null, Guid? draft = null, long? version = null, Session? session = null)
    {
        var clinician = await apps.ClinicianAsync();
        patient ??= await apps.InsertPatientAsync("Synthetic", "AI review");
        if (session is null) { await apps.AssignAsync(clinician, patient.Value); session = await SignInAsync(apps.OnPrem, clinician); }
        var token = Token(await session.Client.GetStringAsync($"/patients/{patient}/dictate", Cancel));
        var review = Guid.NewGuid();
        using var response = await JsonPost(session.Client, token, Api + "/for-note", new { reviewId = review, patientId = patient, draftId = draft, version });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var json = await Json(response);
        return new(session, patient.Value, review, json.GetProperty("draftId").GetGuid(), json.GetProperty("sessionId").GetString()!,
            token, json.GetProperty("editorUrl").GetString()!, json.GetProperty("url").GetString()!);
    }

    async Task<Flow> StageAsync(bool plan = true, bool objective = false)
    {
        sidecar.IncludePlan = plan;
        sidecar.IncludeObjective = objective;
        var flow = await BeginAsync();
        await DictateAsync(flow, 5);
        using var staged = await Stage(flow, 1);
        Assert.Equal(HttpStatusCode.OK, staged.StatusCode);
        return flow;
    }

    static async Task DictateAsync(Flow f, int chunks)
    {
        for (var i = 0; i < chunks; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{Api}/{f.SessionId}/audio?seq={i}");
            request.Headers.Add("RequestVerificationToken", f.Token);
            request.Content = new ByteArrayContent([1, 2, 3]);
            request.Content.Headers.ContentType = new("audio/webm");
            using var response = await f.Session.Client.SendAsync(request, Cancel);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        using var stop = await JsonPost(f.Session.Client, f.Token, $"{Api}/{f.SessionId}/stop", new { });
        Assert.Equal(HttpStatusCode.OK, stop.StatusCode);
    }

    static Task<HttpResponseMessage> Stage(Flow f, long version, object? fake = null) =>
        JsonPost(f.Session.Client, f.Token, $"{Api}/review/{f.Review}/stage", fake ?? new { version });

    static async Task<HttpResponseMessage> JsonPost(HttpClient client, string token, string path, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Add("RequestVerificationToken", token);
        return await client.SendAsync(request, Cancel);
    }

    static async Task<JsonElement> Json(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(Cancel)).RootElement.Clone();

    static async Task<HttpResponseMessage> ReviewPost(Flow f, string handler, params (string Name, object Value)[] fields)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, f.ReviewUrl + "?handler=" + handler)
        {
            Content = Form([("__RequestVerificationToken", f.Token), .. fields]),
        };
        request.Headers.Add("HX-Request", "true");
        return await f.Session.Client.SendAsync(request, Cancel);
    }

    async Task<(Guid Id, string Field)[]> Proposals(Flow f)
    {
        await using var connection = await apps.OpenDatabaseAsync();
        await using var command = new NpgsqlCommand("SELECT id, field FROM ehr.ai_field_proposals WHERE review_id = @id ORDER BY field", connection);
        command.Parameters.AddWithValue("id", f.Review);
        await using var reader = await command.ExecuteReaderAsync(Cancel);
        var rows = new List<(Guid, string)>();
        while (await reader.ReadAsync(Cancel)) rows.Add((reader.GetGuid(0), reader.GetString(1)));
        return rows.ToArray();
    }

    static async Task<HttpResponseMessage> Decide(Flow f, Guid proposal, long version, string decision, string? value = null, Guid? key = null) =>
        await ReviewPost(f, "Decide", ("ProposalId", proposal), ("Version", version), ("DecisionKey", key ?? Guid.NewGuid()),
            ("Decision", decision), ("EditedValue", value ?? ""));

    async Task<long> Scalar(string sql, Guid id)
    {
        await using var db = await apps.OpenDatabaseAsync();
        await using var cmd = new NpgsqlCommand(sql, db);
        cmd.Parameters.AddWithValue("id", id);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(Cancel));
    }

    [Fact]
    public async Task TC_UC13_01_02_03_trusted_result_accept_edit_reject_then_manual_complete_and_sign()
    {
        var f = await StageAsync();
        Assert.Equal(1, await Scalar("SELECT count(*) FROM ehr.clinical_drafts WHERE id = @id AND history IS NULL AND subjective = '' AND objective = '' AND assessment = '' AND plan = '' AND has_unreviewed_proposals", f.Draft));
        var proposals = await Proposals(f);
        foreach (var proposal in proposals)
        {
            var decision = proposal.Field == "subjective" ? "accepted" : proposal.Field == "assessment" ? "edited" : "rejected";
            using var response = await Decide(f, proposal.Id, 2 + Array.IndexOf(proposals, proposal), decision, "Synthetic corrected assessment");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        // Decisions alone never populate clinical content or permit signing before explicit transfer.
        Assert.Equal(1, await Scalar("SELECT count(*) FROM ehr.clinical_drafts WHERE id = @id AND subjective = '' AND has_unreviewed_proposals", f.Draft));
        using var completed = await ReviewPost(f, "Complete", ("Version", 5));
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        var html = await f.Session.Client.GetStringAsync(f.Editor, Cancel);
        Assert.Contains("Cough for three days.", html);
        Assert.Contains("Synthetic corrected assessment", html);
        Assert.DoesNotContain("Fluids and rest.", html);
        Assert.Equal(3, await Scalar("SELECT count(*) FROM ehr.ai_field_proposals WHERE review_id = @id AND reviewer_id IS NOT NULL AND reviewed_at IS NOT NULL AND state <> 'pending'", f.Review));
        using var save = await EditorPost(f, "Review", 6, "Cough for three days.", "Synthetic corrected assessment", "Synthetic manual plan");
        Assert.Equal(HttpStatusCode.OK, save.StatusCode);
        using var sign = await EditorPost(f, "Sign", 7, extra: [("Confirmed", true)]);
        Assert.Equal(HttpStatusCode.OK, sign.StatusCode);
        var signed = await f.Session.Client.GetStringAsync((await Json(sign)).GetProperty("url").GetString()!, Cancel);
        Assert.Contains("Saved AI reviews", signed);
        Assert.Contains(f.Review.ToString(), signed);
        Assert.Equal(1, await Scalar("SELECT count(*) FROM ehr.clinical_notes WHERE draft_id = @id", f.Draft));
        Assert.Equal(1, await apps.AuditEventsAsync(AuditActions.AiReviewCompleted, patientId: f.Patient));
        Assert.Equal(3, await apps.AuditEventsAsync(AuditActions.AiFieldReviewed, patientId: f.Patient));
        Assert.Equal(1, await apps.AuditEventsAsync(AuditActions.SignNote, patientId: f.Patient));
        var review = await f.Session.Client.GetStringAsync(f.ReviewUrl, Cancel);
        Assert.Contains("originalValue", review);
        Assert.Contains("read-only", review);
        Assert.DoesNotContain("htmx.min.js", review);
    }

    static async Task<HttpResponseMessage> EditorPost(Flow f, string handler, long version,
        string subjective = "Synthetic subjective", string assessment = "Synthetic assessment", string plan = "Synthetic plan",
        params (string Name, object Value)[] extra)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, f.Editor + (handler == "Sign" ? "/confirm" : "?handler=" + handler))
        { Content = Form([("__RequestVerificationToken", f.Token), ("Version", version), ("Subjective", subjective), ("Objective", "Synthetic objective"), ("Assessment", assessment), ("Plan", plan), .. extra]) };
        request.Headers.Add("HX-Request", "true");
        return await f.Session.Client.SendAsync(request, Cancel);
    }

    [Fact]
    public async Task TC_UC13_04_pending_records_block_finish_and_forged_flag_signing()
    {
        var f = await StageAsync();
        using var finish = await ReviewPost(f, "Complete", ("Version", 2));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, finish.StatusCode);
        Assert.Contains("subjective", await finish.Content.ReadAsStringAsync(Cancel));
        // Supply complete manual content, then maliciously clear only the old cached flag at the database boundary.
        using var save = await EditorPost(f, "Save", 2);
        await using var db = await apps.OpenDatabaseAsync();
        await using var cmd = new NpgsqlCommand("UPDATE ehr.clinical_drafts SET has_unreviewed_proposals = false, version = version + 1, structured_version = version + 1 WHERE id = @id", db);
        cmd.Parameters.AddWithValue("id", f.Draft);
        await cmd.ExecuteNonQueryAsync(Cancel);
        using var sign = await EditorPost(f, "Sign", 4, extra: [("Confirmed", true), ("HasUnreviewedProposals", false), ("Reviewed", true)]);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, sign.StatusCode);
        Assert.Contains("pending fields", await sign.Content.ReadAsStringAsync(Cancel));
        Assert.Equal(0, await Scalar("SELECT count(*) FROM ehr.clinical_notes WHERE draft_id = @id", f.Draft));
        Assert.Equal(3, await Scalar("SELECT count(*) FROM ehr.ai_field_proposals WHERE review_id = @id AND state = 'pending'", f.Review));
    }

    [Fact]
    public async Task Unavailable_model_keeps_transcript_and_manual_editor_without_proposals()
    {
        sidecar.Sorting = StubSorting.Unavailable;
        var f = await BeginAsync();
        await DictateAsync(f, 2);
        using var staged = await Stage(f, 1);
        Assert.Equal(HttpStatusCode.OK, staged.StatusCode);
        Assert.Empty(await Proposals(f));
        var html = await f.Session.Client.GetStringAsync(f.ReviewUrl, Cancel);
        Assert.Contains("Cough for three days.", html);
        Assert.Contains("Continue to note", html);
        using var save = await EditorPost(f, "Save", 2);
        using var sign = await EditorPost(f, "Sign", 3, extra: [("Confirmed", true)]);
        Assert.Equal(HttpStatusCode.OK, sign.StatusCode);
    }

    [Fact]
    public async Task Other_clinician_and_wrong_patient_cannot_read_or_decide()
    {
        var f = await StageAsync();
        var other = await apps.CreateStaffAsync("Synthetic reviewer", StaffRoles.Clinician);
        await apps.AssignAsync(other, f.Patient);
        var session = await SignInAsync(apps.OnPrem, other);
        var token = Token(await session.Client.GetStringAsync($"/patients/{f.Patient}", Cancel));
        var stolen = f with { Session = session, Token = token };
        using var read = await session.Client.GetAsync(f.ReviewUrl, Cancel);
        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
        using var decide = await Decide(stolen, (await Proposals(f))[0].Id, 2, "accepted");
        Assert.Equal(HttpStatusCode.NotFound, decide.StatusCode);
        using var stage = await Stage(stolen, 2);
        Assert.Equal(HttpStatusCode.NotFound, stage.StatusCode);
        var otherPatient = await apps.InsertPatientAsync("Synthetic", "Other");
        await apps.AssignAsync(await apps.ClinicianAsync(), otherPatient);
        using var mismatch = await f.Session.Client.GetAsync(f.ReviewUrl.Replace(f.Patient.ToString(), otherPatient.ToString(), StringComparison.Ordinal), Cancel);
        Assert.Equal(HttpStatusCode.NotFound, mismatch.StatusCode);
    }

    // Supporting test (not numbered in the SE document): a stage, decision or completion sent again, one after another,
    // is applied once. The concurrent case, TC-UC13-06, is the test named for it below.
    [Fact]
    public async Task Duplicate_stage_decision_and_completion_are_idempotent()
    {
        var f = await StageAsync(plan: false);
        using var stageAgain = await Stage(f, 1);
        Assert.Equal(HttpStatusCode.OK, stageAgain.StatusCode);
        Assert.Equal(2, (await Proposals(f)).Length);
        var proposals = await Proposals(f);
        var key = Guid.NewGuid();
        using var first = await Decide(f, proposals[0].Id, 2, "accepted", key: key);
        using var duplicate = await Decide(f, proposals[0].Id, 2, "accepted", key: key);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);
        using var changed = await Decide(f, proposals[0].Id, 3, "rejected");
        Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
        using var second = await Decide(f, proposals[1].Id, 3, "rejected");
        using var complete = await ReviewPost(f, "Complete", ("Version", 4));
        using var retry = await ReviewPost(f, "Complete", ("Version", 4));
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(1, await apps.AuditEventsAsync(AuditActions.AiReviewCompleted, patientId: f.Patient));
    }

    // TC-UC13-08 (successful flow; the expansion plan's step 14): a dictation is reviewed in the note's four fields, in
    // their order: each proposed one staged, shown with the transcript passage it came from, and once accepted put into
    // its own section of the draft, Objective too. A field the model proposed nothing for says so.
    [Fact]
    public async Task TC_UC13_08_a_dictation_is_reviewed_in_the_four_fields_and_each_goes_into_its_section()
    {
        var f = await StageAsync(objective: true);
        var review = await f.Session.Client.GetStringAsync(f.ReviewUrl, Cancel);
        Assert.Equal(["subjective", "objective", "assessment", "plan"],
            System.Text.RegularExpressions.Regex.Matches(review, "data-dictation-field=\"([a-z]+)\"").Select(m => m.Groups[1].Value));
        var snapshot = JsonDocument.Parse(System.Net.WebUtility.HtmlDecode(
            System.Text.RegularExpressions.Regex.Match(review, "<script type=\"application/json\" data-review-snapshot>(.*?)</script>").Groups[1].Value)).RootElement;
        var transcript = snapshot.GetProperty("result").GetProperty("transcript").GetProperty("text").GetString()!;
        var fields = snapshot.GetProperty("result").GetProperty("fields");
        foreach (var (field, value) in new[] { ("subjective", "Cough for three days."), ("objective", "Chest is clear."), ("assessment", "No fever."), ("plan", "Fluids and rest.") })
        {
            var proposed = fields.GetProperty(field);
            Assert.Equal(value, proposed.GetProperty("value").GetString());
            var source = Assert.Single(proposed.GetProperty("sources").EnumerateArray().ToArray());
            Assert.Equal(value, transcript[source.GetProperty("start").GetInt32()..source.GetProperty("end").GetInt32()]);
            Assert.Matches($"(?s)data-dictation-field=\"{field}\"[^>]*data-review-state=\"pending\".*?<dd data-field-value>{System.Text.RegularExpressions.Regex.Escape(value)}</dd>", review);
        }

        var proposals = await Proposals(f);
        Assert.Equal(["assessment", "objective", "plan", "subjective"], proposals.Select(p => p.Field));
        var version = 2L;
        foreach (var proposal in proposals)
        {
            using var accepted = await Decide(f, proposal.Id, version++, "accepted");
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        }

        using var complete = await ReviewPost(f, "Complete", ("Version", version));
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
        Assert.Equal(1, await Scalar(
            "SELECT count(*) FROM ehr.clinical_drafts WHERE id = @id AND history IS NULL AND subjective = 'Cough for three days.' AND objective = 'Chest is clear.' AND assessment = 'No fever.' AND plan = 'Fluids and rest.'",
            f.Draft));

        // Nothing proposed for a field: the review says so, and nothing is staged for it.
        var without = await StageAsync(plan: false);
        Assert.Matches("(?s)data-dictation-field=\"plan\".*?No usable suggestion", await without.Session.Client.GetStringAsync(without.ReviewUrl, Cancel));
        Assert.DoesNotContain("plan", (await Proposals(without)).Select(p => p.Field));
    }

    // A live result has the note's four fields only (the expansion plan's step 14): one that carries a History beside
    // them, which only a review stored before then has, is refused at staging and nothing is stored, so the review can't
    // be shown in the earlier layout with its Subjective proposal hidden.
    [Fact]
    public async Task A_live_result_with_a_history_beside_the_four_fields_is_refused_and_nothing_is_staged()
    {
        sidecar.LiveHistory = true;
        var f = await BeginAsync();
        await DictateAsync(f, 5);
        using var staged = await Stage(f, 1);
        Assert.Equal(HttpStatusCode.BadGateway, staged.StatusCode);
        Assert.Empty(await Proposals(f));
        Assert.Equal(1, await Scalar("SELECT count(*) FROM ehr.ai_note_reviews WHERE id = @id AND state = 'recording' AND result_json IS NULL", f.Review));
    }

    // TC-UC13-07 (alternative flow; the expansion plan's step 14): a review saved before step 14, of a dictation sorted
    // into History, Assessment and Plan, is shown in those fields, its History labeled as going into Subjective, and once
    // approved the History goes there; into a Subjective with text only when appending is allowed, after that text. The
    // draft has no History afterwards. The review is made as one saved then would be: a review staged now, its result
    // and proposals given the earlier names with every trigger held, since the database stages no History any more.
    [Fact]
    public async Task TC_UC13_07_an_approved_history_goes_into_subjective_after_any_text_there()
    {
        var f = await StageAsync(plan: false);
        await using (var connection = new NpgsqlConnection(apps.Database.SuperuserConnectionString))
        {
            await connection.OpenAsync(Cancel);
            await using var earlier = new NpgsqlCommand(
                """
                BEGIN;
                SET LOCAL session_replication_role = replica;
                UPDATE ehr.ai_note_reviews
                SET result_json = jsonb_set(result_json #- '{fields,subjective}' #- '{fields,objective}', '{fields,history}', result_json #> '{fields,subjective}')
                WHERE id = @id;
                UPDATE ehr.ai_field_proposals SET field = 'history' WHERE review_id = @id AND field = 'subjective';
                COMMIT;
                """, connection);
            earlier.Parameters.AddWithValue("id", f.Review);
            await earlier.ExecuteNonQueryAsync(Cancel);
        }

        var review = await f.Session.Client.GetStringAsync(f.ReviewUrl, Cancel);
        Assert.Contains("History (goes into Subjective)", review);
        Assert.Equal(["history", "assessment", "plan"], System.Text.RegularExpressions.Regex.Matches(review, "data-dictation-field=\"([a-z]+)\"").Select(m => m.Groups[1].Value));
        using var manual = await EditorPost(f, "Save", 2, "Synthetic typed subjective", "", "");
        Assert.Equal(HttpStatusCode.OK, manual.StatusCode);
        var proposals = await Proposals(f);
        using (var accept = await Decide(f, proposals.Single(p => p.Field == "history").Id, 3, "accepted"))
        {
            Assert.Equal(HttpStatusCode.OK, accept.StatusCode);
        }

        using (var reject = await Decide(f, proposals.Single(p => p.Field != "history").Id, 4, "rejected"))
        {
            Assert.Equal(HttpStatusCode.OK, reject.StatusCode);
        }

        using var withoutConsent = await ReviewPost(f, "Complete", ("Version", 5));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, withoutConsent.StatusCode);
        using var complete = await ReviewPost(f, "Complete", ("Version", 5), ("Append", true));
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
        Assert.Equal(1, await Scalar(
            "SELECT count(*) FROM ehr.clinical_drafts WHERE id = @id AND history IS NULL AND objective = 'Synthetic objective' AND subjective = 'Synthetic typed subjective' || E'\n\n' || 'Cough for three days.'",
            f.Draft));
    }

    [Fact]
    public async Task Changed_drafts_preserve_manual_text_and_require_current_version_and_append_consent()
    {
        var f = await StageAsync(plan: false);
        using var manual = await EditorPost(f, "Save", 2, "Synthetic manual history", "", "");
        var proposals = await Proposals(f);
        using var stale = await Decide(f, proposals[0].Id, 2, "accepted");
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using var accept = await Decide(f, proposals[0].Id, 3, "accepted");
        using var accept2 = await Decide(f, proposals[1].Id, 4, "accepted");
        using var withoutConsent = await ReviewPost(f, "Complete", ("Version", 5));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, withoutConsent.StatusCode);
        using var complete = await ReviewPost(f, "Complete", ("Version", 5), ("Append", true));
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
        Assert.Contains("Synthetic manual history", await f.Session.Client.GetStringAsync(f.Editor, Cancel));
        Assert.Contains("Cough for three days.", await f.Session.Client.GetStringAsync(f.Editor, Cancel));
    }

    [Fact]
    public async Task Stage_ignores_browser_AI_payload_and_rejects_unbound_or_wrong_sessions()
    {
        var f = await BeginAsync();
        await DictateAsync(f, 3);
        using var staged = await Stage(f, 1, new { version = 1, sessionId = "fabricated-session-12345", fields = new { subjective = "FORGED" }, reviewed = true });
        Assert.Equal(HttpStatusCode.OK, staged.StatusCode);
        var html = await f.Session.Client.GetStringAsync(f.ReviewUrl, Cancel);
        Assert.Contains("Cough for three days.", html);
        Assert.DoesNotContain("FORGED", html);
        using var unbound = await JsonPost(f.Session.Client, f.Token, $"{Api}/review/{Guid.NewGuid()}/stage", new { version = 1, sessionId = f.SessionId });
        Assert.Equal(HttpStatusCode.NotFound, unbound.StatusCode);
        using var cancelPending = await JsonPost(f.Session.Client, f.Token, $"{Api}/review/{f.Review}/discard", new { });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, cancelPending.StatusCode);
    }

    [Fact]
    public async Task Lost_primary_link_does_not_claim_a_saved_review_or_modify_the_draft()
    {
        var f = await BeginAsync();
        await DictateAsync(f, 3);
        var offline = f with { Session = f.Session with { Client = Client(apps.CloudCutOff, f.Session.Jar) } };
        using var unavailable = await Stage(offline, 1);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
        Assert.Empty(await Proposals(f));
        using var retry = await Stage(f, 1);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
    }

    // TC-UC13-06 (NFR-09) in the SE document (Satwik Yash Padhy): concurrent review decisions stay consistent. Two
    // conflicting decisions sent at once, two identical ones, and two completions at once never double-apply or silently
    // replace a decision.
    [Fact]
    public async Task TC_UC13_06_Simultaneous_decisions_and_completions_never_double_apply_or_silently_replace_a_decision()
    {
        var f = await StageAsync(plan: false);
        var proposals = await Proposals(f);
        var attempts = await Task.WhenAll(Decide(f, proposals[0].Id, 2, "accepted"), Decide(f, proposals[0].Id, 2, "rejected"));
        Assert.Single(attempts, r => r.StatusCode == HttpStatusCode.OK);
        Assert.Single(attempts, r => r.StatusCode == HttpStatusCode.Conflict);
        foreach (var attempt in attempts) attempt.Dispose();
        var key = Guid.NewGuid();
        var retries = await Task.WhenAll(Decide(f, proposals[1].Id, 3, "accepted", key: key), Decide(f, proposals[1].Id, 3, "accepted", key: key));
        Assert.All(retries, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        foreach (var retry in retries) retry.Dispose();
        var completions = await Task.WhenAll(ReviewPost(f, "Complete", ("Version", 4)), ReviewPost(f, "Complete", ("Version", 4)));
        Assert.All(completions, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        foreach (var completion in completions) completion.Dispose();
        Assert.Equal(1, await apps.AuditEventsAsync(AuditActions.AiReviewCompleted, patientId: f.Patient));
        Assert.Equal(2, await apps.AuditEventsAsync(AuditActions.AiFieldReviewed, patientId: f.Patient));
        Assert.Equal(5, await Scalar("SELECT version FROM ehr.clinical_drafts WHERE id = @id", f.Draft));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("oversized")]
    public async Task Invalid_edits_leave_the_original_pending_proposal_and_version_intact(string value)
    {
        var f = await StageAsync(plan: false);
        if (value == "oversized") value = new string('x', NoteContent.MaximumFieldLength + 1);
        using var response = await Decide(f, (await Proposals(f))[0].Id, 2, "edited", value);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(2, await Scalar("SELECT version FROM ehr.clinical_drafts WHERE id = @id", f.Draft));
        Assert.Equal(2, await Scalar("SELECT count(*) FROM ehr.ai_field_proposals WHERE review_id = @id AND state = 'pending' AND approved_value IS NULL", f.Review));
    }

    [Fact]
    public async Task Staging_a_stale_result_preserves_new_manual_content_and_requires_explicit_retry_at_the_current_version()
    {
        var f = await BeginAsync();
        await DictateAsync(f, 3);
        using var manual = await EditorPost(f, "Save", 1, "Synthetic newer manual history", "", "");
        Assert.Equal(HttpStatusCode.OK, manual.StatusCode);
        using var stale = await Stage(f, 1);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Empty(await Proposals(f));
        Assert.Contains("Synthetic newer manual history", await f.Session.Client.GetStringAsync(f.Editor, Cancel));
        using var retry = await Stage(f, 2);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(2, (await Proposals(f)).Length);
        Assert.Contains("Synthetic newer manual history", await f.Session.Client.GetStringAsync(f.Editor, Cancel));
    }

    [Fact]
    public async Task Discarded_fields_never_become_proposals_and_trusted_provenance_and_warnings_survive_reopening_without_PHI_logs()
    {
        using var logs = new LoggingTests.CapturedLogs();
        apps.OnPrem.Services.GetRequiredService<ILoggerFactory>().AddProvider(logs);
        sidecar.Sorting = StubSorting.Discarded;
        var f = await BeginAsync();
        await DictateAsync(f, 3);
        using var stage = await Stage(f, 1);
        Assert.Equal(HttpStatusCode.OK, stage.StatusCode);
        var proposal = Assert.Single(await Proposals(f));
        Assert.Equal("subjective", proposal.Field);
        var html = await f.Session.Client.GetStringAsync(f.ReviewUrl, Cancel);
        Assert.Contains("possible_correction", html);
        Assert.Contains("notCarriedOver", html);
        Assert.Contains("note-prompt-1", html);
        Assert.Contains("not_in_transcript", html);
        Assert.DoesNotContain(DictationSidecarStub.DiscardedText, html);
        using var accept = await Decide(f, proposal.Id, 2, "accepted", "A client cannot replace the accepted original");
        Assert.Equal(HttpStatusCode.OK, accept.StatusCode);
        using var complete = await ReviewPost(f, "Complete", ("Version", 3));
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
        Assert.Equal(0, await Scalar("SELECT count(*) FROM ehr.allergy_intolerances WHERE patient_id = @id", f.Patient));
        Assert.Equal(0, await Scalar("SELECT count(*) FROM ehr.medication_statements WHERE patient_id = @id", f.Patient));
        Assert.NotEmpty(logs.Messages);
        Assert.DoesNotContain(logs.Messages, line => line.Contains("Cough for three days.", StringComparison.Ordinal)
            || line.Contains(DictationSidecarStub.DiscardedText, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_bound_start_retry_reuses_the_receipt_and_cloud_reads_are_auditable_but_cut_off_writes_fail()
    {
        var f = await StageAsync(plan: false);
        var starts = sidecar.Received.Count(r => r.PathAndQuery == "/v1/sessions");
        using var retry = await JsonPost(f.Session.Client, f.Token, Api + "/for-note", new { reviewId = f.Review, patientId = f.Patient });
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.Equal(f.Draft, (await Json(retry)).GetProperty("draftId").GetGuid());
        Assert.Equal(starts, sidecar.Received.Count(r => r.PathAndQuery == "/v1/sessions"));
        var cloud = Client(apps.Cloud, f.Session.Jar);
        using var view = await cloud.GetAsync(f.ReviewUrl, Cancel);
        Assert.Equal(HttpStatusCode.OK, view.StatusCode);
        var token = Token(await view.Content.ReadAsStringAsync(Cancel));
        var offline = f with { Session = f.Session with { Client = Client(apps.CloudCutOff, f.Session.Jar) }, Token = token };
        using var refused = await Decide(offline, (await Proposals(f))[0].Id, 2, "accepted");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
        Assert.Equal(2, await Scalar("SELECT count(*) FROM ehr.ai_field_proposals WHERE review_id = @id AND state = 'pending'", f.Review));
    }

    [Theory]
    [InlineData("source")]
    [InlineData("not-final")]
    [InlineData("unavailable-pending")]
    public async Task Malformed_trusted_results_are_refused_without_staging_any_proposal(string kind)
    {
        var f = await BeginAsync();
        await DictateAsync(f, 3);
        using var result = await JsonPost(f.Session.Client, f.Token, $"{Api}/{f.SessionId}/result", new { });
        var json = System.Text.Json.Nodes.JsonNode.Parse(await result.Content.ReadAsStringAsync(Cancel))!;
        if (kind == "source") json["fields"]!["subjective"]!["sources"]![0]!["end"] = 1000000;
        if (kind == "not-final") json["transcript"]!["final"] = false;
        if (kind == "unavailable-pending") json["sorting"] = "unavailable";
        sidecar.Answer = (HttpStatusCode.OK, json.ToJsonString());
        using var stage = await Stage(f, 1);
        Assert.Equal(HttpStatusCode.BadGateway, stage.StatusCode);
        Assert.Empty(await Proposals(f));
        Assert.Equal(1, await Scalar("SELECT version FROM ehr.clinical_drafts WHERE id = @id", f.Draft));
        Assert.Contains("Subjective", await f.Session.Client.GetStringAsync(f.Editor, Cancel));
    }

    [Fact]
    public async Task Other_draft_routes_and_proposal_IDs_are_refused_even_for_the_same_owner_and_missing_antiforgery_cannot_stage()
    {
        var f = await StageAsync(plan: false);
        var other = await StageAsync(plan: false);
        using var wrongDraft = await f.Session.Client.GetAsync(f.ReviewUrl.Replace(f.Draft.ToString(), other.Draft.ToString(), StringComparison.Ordinal), Cancel);
        Assert.Equal(HttpStatusCode.NotFound, wrongDraft.StatusCode);
        using var wrongProposal = await Decide(f, (await Proposals(other))[0].Id, 2, "accepted");
        Assert.Equal(HttpStatusCode.NotFound, wrongProposal.StatusCode);
        using var csrf = await f.Session.Client.PostAsJsonAsync($"{Api}/review/{f.Review}/stage", new { version = 2 }, Cancel);
        Assert.Equal(HttpStatusCode.BadRequest, csrf.StatusCode);
        Assert.Equal(2, await Scalar("SELECT count(*) FROM ehr.ai_field_proposals WHERE review_id = @id AND state = 'pending'", f.Review));
        Assert.Equal(2, await Scalar("SELECT count(*) FROM ehr.ai_field_proposals WHERE review_id = @id AND state = 'pending'", other.Review));
    }

    [Fact]
    public async Task Skipping_result_staging_cannot_bypass_review_and_explicitly_discarding_unfinished_dictation_keeps_manual_signing_available()
    {
        var f = await BeginAsync();
        await DictateAsync(f, 3);
        using var save = await EditorPost(f, "Save", 1);
        Assert.Equal(HttpStatusCode.OK, save.StatusCode);
        using var blocked = await EditorPost(f, "Sign", 2, extra: [("Confirmed", true), ("Reviewed", true)]);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, blocked.StatusCode);
        Assert.Contains("unfinished dictation", await blocked.Content.ReadAsStringAsync(Cancel));
        Assert.Equal(0, await Scalar("SELECT count(*) FROM ehr.clinical_notes WHERE draft_id = @id", f.Draft));
        using var discard = await JsonPost(f.Session.Client, f.Token, $"{Api}/review/{f.Review}/discard", new { });
        Assert.Equal(HttpStatusCode.OK, discard.StatusCode);
        using var manualSign = await EditorPost(f, "Sign", 2, extra: [("Confirmed", true)]);
        Assert.Equal(HttpStatusCode.OK, manualSign.StatusCode);
    }

    // A dictation in Spanish (the expansion plan's step 17), staged from the stub's Spanish mode: five chunks, every one of
    // the four fields proposed in English with its statements.
    async Task<Flow> StageSpanishAsync()
    {
        sidecar.Spanish = true;
        var flow = await BeginAsync();
        await DictateAsync(flow, 5);
        using var staged = await Stage(flow, 1);
        Assert.Equal(HttpStatusCode.OK, staged.StatusCode);
        return flow;
    }

    async Task<JsonElement> StoredResultAsync(Flow f)
    {
        await using var connection = await apps.OpenDatabaseAsync();
        await using var command = new NpgsqlCommand("SELECT result_json::text FROM ehr.ai_note_reviews WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", f.Review);
        return JsonDocument.Parse((string)(await command.ExecuteScalarAsync(Cancel))!).RootElement.Clone();
    }

    static string Translated(string english, string spanish) =>
        $"<li class=\"sc-translation__pair\"><p class=\"sc-translation__english\">{english}</p><p class=\"sc-translation__spanish\">You said: <span lang=\"es\">“{spanish}”</span></p></li>";

    // TC-UC13-15 (successful flow; the expansion plan's step 17, proposed FR-61): a dictation in Spanish is reviewed in the
    // English screens: its transcript in Spanish, a notice that the proposals are translations to check sentence by
    // sentence, and each field's English sentences, each beside the Spanish it was translated from, each field accepted
    // with "Accept translated". The Spanish transcript and each sentence's Spanish words are stored with the review, and
    // what is approved goes into the note in English.
    [Fact]
    public async Task TC_UC13_15_a_dictation_in_spanish_is_reviewed_a_sentence_at_a_time_beside_its_spanish()
    {
        var f = await StageSpanishAsync();
        var html = WebUtility.HtmlDecode(await f.Session.Client.GetStringAsync(f.ReviewUrl, Cancel));
        Assert.Contains("Original transcript, in Spanish", html);
        Assert.Contains("<div class=\"sc-transcript__body\" data-dictation-transcript lang=\"es\">", html);
        Assert.Contains("Translated from Spanish: these proposals are English translations of what you said.", html);
        Assert.Contains("Nothing is proposed for orders or the allergy list from a dictation in Spanish.", html);
        Assert.DoesNotContain("data-chart-proposals ", html);
        foreach (var (field, statements) in DictationSidecarStub.SpanishFields)
        {
            var card = html.Split($"data-dictation-field=\"{field}\"")[1];
            Assert.Matches("^\\s*<ol class=\"sc-translation\">", card[(card.IndexOf("<dd data-field-value>", StringComparison.Ordinal) + "<dd data-field-value>".Length)..]);
            foreach (var (english, spanish) in statements)
            {
                Assert.Contains(Translated(english, spanish), html);
            }

            Assert.Contains($">Accept translated {field}</button>", html);
            Assert.DoesNotContain($">Accept {field}</button>", html);
        }

        var stored = await StoredResultAsync(f);
        Assert.Equal("es", stored.GetProperty("spokenLanguage").GetString());
        var transcript = stored.GetProperty("transcript").GetProperty("text").GetString()!;
        Assert.Equal(string.Join(' ', DictationSidecarStub.SpanishSentences), transcript);
        foreach (var (field, statements) in DictationSidecarStub.SpanishFields)
        {
            var proposed = stored.GetProperty("fields").GetProperty(field);
            var value = proposed.GetProperty("value").GetString()!;
            var saved = proposed.GetProperty("statements").EnumerateArray().ToArray();
            Assert.Equal(statements.Select(s => s.English), saved.Select(s => value[s.GetProperty("start").GetInt32()..s.GetProperty("end").GetInt32()]));
            Assert.Equal(statements.Select(s => s.Spanish), saved.Select(s =>
            {
                var source = Assert.Single(s.GetProperty("sources").EnumerateArray().ToArray());
                return transcript[source.GetProperty("start").GetInt32()..source.GetProperty("end").GetInt32()];
            }));
        }

        var version = 2L;
        foreach (var proposal in await Proposals(f))
        {
            using var accepted = await Decide(f, proposal.Id, version++, "accepted");
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        }

        using var complete = await ReviewPost(f, "Complete", ("Version", version));
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
        Assert.Equal(1, await Scalar(
            "SELECT count(*) FROM ehr.clinical_drafts WHERE id = @id AND subjective = 'Cough for three days. Denies fever.' AND objective = 'Lungs clear.' AND assessment = 'Likely viral illness.' AND plan LIKE 'Plenty of fluids and relative rest%'",
            f.Draft));
    }

    // TC-UC13-16 (alternative flow; step 17): the review of a dictation in Spanish is reopened later, from the draft's and
    // then the signed note's saved reviews, which say it was in Spanish, and still shows each sentence beside its Spanish,
    // read-only once signed. A review of a dictation in English says nothing of a language, as before.
    [Fact]
    public async Task TC_UC13_16_a_review_in_spanish_reopened_later_and_from_the_signed_note_still_shows_its_spanish()
    {
        var f = await StageSpanishAsync();
        var version = 2L;
        foreach (var proposal in await Proposals(f))
        {
            using var accepted = await Decide(f, proposal.Id, version++, "accepted");
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        }

        using (var complete = await ReviewPost(f, "Complete", ("Version", version)))
        {
            Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
        }

        var editor = WebUtility.HtmlDecode(await f.Session.Client.GetStringAsync(f.Editor, Cancel));
        Assert.Matches($"(?s)Saved AI reviews.*{f.Review}\">Dictation in Spanish and review saved ", editor);
        using var sign = await EditorPost(f, "Sign", version + 1, extra: [("Confirmed", true)]);
        Assert.Equal(HttpStatusCode.OK, sign.StatusCode);
        var signed = WebUtility.HtmlDecode(await f.Session.Client.GetStringAsync((await Json(sign)).GetProperty("url").GetString()!, Cancel));
        Assert.Matches($"(?s)Saved AI reviews.*{f.Review}\">Dictation in Spanish and review saved ", signed);

        var reopened = WebUtility.HtmlDecode(await f.Session.Client.GetStringAsync(f.ReviewUrl, Cancel));
        Assert.Contains("The review of its fields is read-only.", reopened);
        Assert.Contains("Translated from Spanish:", reopened);
        foreach (var (english, spanish) in DictationSidecarStub.SpanishFields.SelectMany(field => field.Statements))
        {
            Assert.Contains(Translated(english, spanish), reopened);
        }

        sidecar.Spanish = false;
        var inEnglish = await StageAsync(plan: false);
        version = 2L;
        foreach (var proposal in await Proposals(inEnglish))
        {
            using var accepted = await Decide(inEnglish, proposal.Id, version++, "accepted");
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        }

        using (var complete = await ReviewPost(inEnglish, "Complete", ("Version", version)))
        {
            Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
        }

        Assert.Matches($"(?s)Saved AI reviews.*{inEnglish.Review}\">Dictation and review saved ",
            WebUtility.HtmlDecode(await inEnglish.Session.Client.GetStringAsync(inEnglish.Editor, Cancel)));
    }

    // TC-UC13-17 (invalid input; step 17): a result from Spanish whose sentences don't hold is refused at staging and
    // nothing is stored, so the review never shows a sentence beside Spanish it didn't come from, and no load of it can
    // fail. Each case but outside is caught by one rule of TrustedResult alone (removing that rule turns that case red
    // and no other; empty and past-value then fail with an error, not a refusal): a sentence that doesn't start where
    // the last one ended (gap), sentences that don't reach the value's end (uncovered), a sentence ending inside a word
    // (mid-word), a sentence of no length (empty) or past the value's end (past-value), a space at the value's start
    // (leading-space) or end (trailing-space) or two between sentences (double-space), a sentence citing nothing
    // (uncited), its sources out of order (reversed) or one cited twice (repeated-span), a sentence citing a span the
    // field doesn't have (extra), a field source no sentence cites (uncited-source), both at once (foreign, and outside,
    // where the one a sentence cites is past the transcript's end), a source cut between a letter and its combining
    // accent (combining) or in a passage not heard as Spanish (other-language), and a sentence citing, besides its own,
    // a span past the transcript's end (extra-outside: the sources' own range check and the rule that the sentences cite
    // the field's sources each refuse it, so that weakening the second to "at least the field's sources" alone still
    // stores nothing, while with both gone it would be stored and the review fail on every load), a sentence citing 401
    // characters of the transcript, more than the 400 the Spanish note schema's two quotes of 200 allow (over-400), a
    // field from Spanish with no sentences (no-statements), sentences on a field from English (english-statements), a
    // language the contract doesn't have,
    // with no sentences anywhere (unknown-language: the read fails), a language that isn't exactly "en" or "es", each in
    // the shape that a lenient read would stage (5 with no sentences, as an English result; 1, "1", " es", "es " and
    // "en, es", which a lenient read takes for Spanish, with them), and an order proposed from Spanish, otherwise one the
    // expansion plan's step 15 would stage (proposal: nothing is proposed from a dictation in Spanish).
    [Theory]
    [InlineData("gap")]
    [InlineData("uncovered")]
    [InlineData("mid-word")]
    [InlineData("uncited")]
    [InlineData("foreign")]
    [InlineData("outside")]
    [InlineData("no-statements")]
    [InlineData("english-statements")]
    [InlineData("unknown-language")]
    [InlineData("language-5")]
    [InlineData("language-1")]
    [InlineData("language-1-string")]
    [InlineData("language-padded")]
    [InlineData("language-trailing")]
    [InlineData("language-list")]
    [InlineData("proposal")]
    [InlineData("empty")]
    [InlineData("past-value")]
    [InlineData("leading-space")]
    [InlineData("trailing-space")]
    [InlineData("double-space")]
    [InlineData("reversed")]
    [InlineData("repeated-span")]
    [InlineData("extra")]
    [InlineData("extra-outside")]
    [InlineData("uncited-source")]
    [InlineData("combining")]
    [InlineData("other-language")]
    [InlineData("over-400")]
    public async Task TC_UC13_17_a_result_from_spanish_whose_sentences_dont_hold_is_refused_and_nothing_is_staged(string kind)
    {
        sidecar.Spanish = kind != "english-statements";
        var f = await BeginAsync();
        await DictateAsync(f, 5);
        using var result = await JsonPost(f.Session.Client, f.Token, $"{Api}/{f.SessionId}/result", new { });
        var json = System.Text.Json.Nodes.JsonNode.Parse(await result.Content.ReadAsStringAsync(Cancel))!;
        var subjective = json["fields"]!["subjective"]!;
        var first = subjective["statements"]?[0];
        var second = subjective["statements"]?[1];
        switch (kind)
        {
            case "gap":
                second!["start"] = second["start"]!.GetValue<int>() + 1;
                break;
            case "uncovered":
                subjective["statements"]!.AsArray().RemoveAt(1);
                subjective["sources"]!.AsArray().RemoveAt(1);
                break;
            case "mid-word":
                first!["end"] = first["end"]!.GetValue<int>() - 2;
                second!["start"] = first["end"]!.GetValue<int>() + 1;
                break;
            case "uncited":
                second!["sources"]!.AsArray().Insert(0, first!["sources"]![0]!.DeepClone());
                first["sources"] = new System.Text.Json.Nodes.JsonArray();
                break;
            case "foreign":
                first!["sources"]![0]!["end"] = first["sources"]![0]!["end"]!.GetValue<int>() - 1;
                break;
            case "outside":
                first!["sources"]![0]!["end"] = 1000000;
                break;
            case "no-statements":
                json["fields"]!["plan"]!.AsObject().Remove("statements");
                break;
            case "english-statements":
                subjective["statements"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject
                {
                    ["start"] = 0,
                    ["end"] = subjective["value"]!.GetValue<string>().Length,
                    ["sources"] = subjective["sources"]!.DeepClone(),
                });
                break;
            case "empty":
                subjective["statements"]!.AsArray().Insert(0, new System.Text.Json.Nodes.JsonObject
                {
                    ["start"] = 0,
                    ["end"] = 0,
                    ["sources"] = first!["sources"]!.DeepClone(),
                });
                break;
            case "past-value":
                second!["end"] = subjective["value"]!.GetValue<string>().Length + 1;
                break;
            case "leading-space":
                subjective["value"] = " " + subjective["value"]!.GetValue<string>();
                first!["end"] = first["end"]!.GetValue<int>() + 1;
                second!["start"] = second["start"]!.GetValue<int>() + 1;
                second["end"] = second["end"]!.GetValue<int>() + 1;
                break;
            case "trailing-space":
                subjective["value"] = subjective["value"]!.GetValue<string>() + " ";
                second!["end"] = second["end"]!.GetValue<int>() + 1;
                break;
            case "double-space":
                var words = subjective["value"]!.GetValue<string>();
                subjective["value"] = words.Insert(first!["end"]!.GetValue<int>(), " ");
                second!["end"] = second["end"]!.GetValue<int>() + 1;
                break;
            case "reversed":
                second!["sources"]!.AsArray().Add(first!["sources"]![0]!.DeepClone());
                break;
            case "repeated-span":
                second!["sources"]!.AsArray().Add(second["sources"]![0]!.DeepClone());
                break;
            case "extra":
                second!["sources"]!.AsArray().Add(json["fields"]!["objective"]!["sources"]![0]!.DeepClone());
                break;
            case "extra-outside":
                var last = second!["sources"]![0]!["end"]!.GetValue<int>();
                second["sources"]!.AsArray().Add(new System.Text.Json.Nodes.JsonObject { ["start"] = last + 1, ["end"] = 1000000 });
                break;
            case "uncited-source":
                subjective["sources"]!.AsArray().Add(json["fields"]!["objective"]!["sources"]![0]!.DeepClone());
                break;
            case "combining":
                var assessment = json["fields"]!["assessment"]!;
                var text = json["transcript"]!["text"]!.GetValue<string>();
                var cut = new System.Text.Json.Nodes.JsonObject
                {
                    ["start"] = assessment["sources"]![0]!["start"]!.GetValue<int>(),
                    ["end"] = text.IndexOf('\u0301', StringComparison.Ordinal),
                };
                assessment["sources"] = new System.Text.Json.Nodes.JsonArray(cut);
                assessment["statements"]![0]!["sources"] = new System.Text.Json.Nodes.JsonArray(cut.DeepClone());
                break;
            case "other-language":
                var objective = json["fields"]!["objective"]!;
                var passage = json["transcript"]!["segments"]![2]!;
                var notSpanish = new System.Text.Json.Nodes.JsonObject { ["start"] = passage["start"]!.DeepClone(), ["end"] = passage["end"]!.DeepClone() };
                objective["sources"] = new System.Text.Json.Nodes.JsonArray(notSpanish);
                objective["statements"]![0]!["sources"] = new System.Text.Json.Nodes.JsonArray(notSpanish.DeepClone());
                break;
            case "over-400":
                var said = json["transcript"]!["text"]!.GetValue<string>();
                var own = second!["sources"]![0]!;
                var more = 401 - (own["end"]!.GetValue<int>() - own["start"]!.GetValue<int>());
                json["transcript"]!["text"] = said + " " + string.Concat(Enumerable.Repeat("Refiere cansancio por las tardes. ", more / 30 + 1))[..more];
                var further = new System.Text.Json.Nodes.JsonObject { ["start"] = said.Length + 1, ["end"] = said.Length + 1 + more };
                second["sources"]!.AsArray().Add(further);
                subjective["sources"]!.AsArray().Add(further.DeepClone());
                break;
            case "proposal":
                var order = json["notCarriedOver"]![0]!;
                json["proposals"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject
                {
                    ["kind"] = "order",
                    ["start"] = order["start"]!.DeepClone(),
                    ["end"] = order["end"]!.DeepClone(),
                    ["warnings"] = new System.Text.Json.Nodes.JsonArray(),
                });
                break;
            case "language-5":
                json["spokenLanguage"] = 5;
                foreach (var field in json["fields"]!.AsObject())
                {
                    field.Value!.AsObject().Remove("statements");
                }

                break;
            case "language-1":
                json["spokenLanguage"] = 1;
                break;
            case "language-1-string":
                json["spokenLanguage"] = "1";
                break;
            case "language-padded":
                json["spokenLanguage"] = " es";
                break;
            case "language-trailing":
                json["spokenLanguage"] = "es ";
                break;
            case "language-list":
                json["spokenLanguage"] = "en, es";
                break;
            case "unknown-language":
                json["spokenLanguage"] = "fr";
                foreach (var field in json["fields"]!.AsObject())
                {
                    field.Value!.AsObject().Remove("statements");
                }

                break;
        }

        sidecar.Answer = (HttpStatusCode.OK, json.ToJsonString());
        using var stage = await Stage(f, 1);
        Assert.Equal(HttpStatusCode.BadGateway, stage.StatusCode);
        Assert.Empty(await Proposals(f));
        Assert.Equal(1, await Scalar("SELECT count(*) FROM ehr.ai_note_reviews WHERE id = @id AND state = 'recording' AND result_json IS NULL", f.Review));
        Assert.Equal(1, await Scalar("SELECT version FROM ehr.clinical_drafts WHERE id = @id", f.Draft));
    }

    // Step 17's second series: the result in Spanish that the dictation service itself writes (its pinned contract file,
    // result-200-done-es.json) is staged as it is, so TrustedResult takes the shape the service writes: sentences joined
    // by one space, each citing whole Spanish of its own, a Spanish instruction listed, nothing proposed.
    [Fact]
    public async Task The_dictation_services_own_result_in_spanish_is_staged()
    {
        var f = await BeginAsync();
        var contract = System.Text.Json.Nodes.JsonNode.Parse(
            await File.ReadAllTextAsync(Ehr.Testing.RepositoryRoot.Combine("src/Ehr.Dictation/contract/result-200-done-es.json"), Cancel))!;
        sidecar.Answer = (HttpStatusCode.OK, contract["body"]!.ToJsonString());

        using var stage = await Stage(f, 1);

        Assert.Equal(HttpStatusCode.OK, stage.StatusCode);
        var stored = await StoredResultAsync(f);
        Assert.Equal("es", stored.GetProperty("spokenLanguage").GetString());
        Assert.Equal(2, stored.GetProperty("fields").GetProperty("subjective").GetProperty("statements").GetArrayLength());
    }

    // Step 17: a stored review's line among the saved reviews says "in Spanish" only when its result's spokenLanguage is
    // exactly "es", and reading it never throws, whatever the stored evidence holds.
    [Theory]
    [InlineData(null, false)]
    [InlineData("{}", false)]
    [InlineData("[]", false)]
    [InlineData("5", false)]
    [InlineData("not JSON", false)]
    [InlineData("{\"spokenLanguage\":5}", false)]
    [InlineData("{\"spokenLanguage\":1}", false)]
    [InlineData("{\"spokenLanguage\":\"1\"}", false)]
    [InlineData("{\"spokenLanguage\":\" es\"}", false)]
    [InlineData("{\"spokenLanguage\":\"en, es\"}", false)]
    [InlineData("{\"spokenLanguage\":[\"es\"]}", false)]
    [InlineData("{\"spokenLanguage\":\"en\"}", false)]
    [InlineData("{\"spokenLanguage\":\"es\"}", true)]
    public void A_stored_reviews_language_is_spanish_only_when_it_says_exactly_es_and_reading_it_never_throws(string? json, bool spanish) =>
        Assert.Equal(spanish ? SpokenLanguage.Spanish : SpokenLanguage.English, DictationResult.SpokenLanguageOf(json));

    // Step 17: a review of a dictation in English shows nothing of translation, and its result is stored with no language
    // and no statements, as every review was before Spanish, so one saved before then reads the same.
    [Fact]
    public async Task A_review_in_english_shows_nothing_of_translation_and_is_stored_as_before()
    {
        var f = await StageAsync(objective: true);
        var html = WebUtility.HtmlDecode(await f.Session.Client.GetStringAsync(f.ReviewUrl, Cancel));
        foreach (var translation in new[] { "Accept translated", "sc-translation", "lang=\"es\"", "Translated from Spanish", "in Spanish", "spokenLanguage", "statements" })
        {
            Assert.DoesNotContain(translation, html);
        }

        Assert.Contains(">Accept subjective</button>", html);
        Assert.Contains("<div class=\"sc-transcript__body\" data-dictation-transcript><p>", html);
        Assert.DoesNotContain("data-chart-proposals-none", html);
        var stored = await StoredResultAsync(f);
        Assert.False(stored.TryGetProperty("spokenLanguage", out _));
        Assert.All(stored.GetProperty("fields").EnumerateObject(), field => Assert.False(field.Value.TryGetProperty("statements", out _)));
    }
}
