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

    async Task<Flow> StageAsync(bool plan = true)
    {
        sidecar.IncludePlan = plan;
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
            var decision = proposal.Field == "history" ? "accepted" : proposal.Field == "assessment" ? "edited" : "rejected";
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
        Assert.Contains("history", await finish.Content.ReadAsStringAsync(Cancel));
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

    // TC-UC13-07 (alternative flow; the expansion plan's step 14): a dictation sorted into History, as the dictation
    // service still sorts one and as reviews saved before step 14 were, is labeled as going into Subjective, and once
    // approved goes there; into a Subjective with text only when appending is allowed, after that text. The draft has
    // no History afterwards.
    [Fact]
    public async Task TC_UC13_07_an_approved_history_goes_into_subjective_after_any_text_there()
    {
        var f = await StageAsync(plan: false);
        Assert.Contains("History (goes into Subjective)", await f.Session.Client.GetStringAsync(f.ReviewUrl, Cancel));
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
        using var staged = await Stage(f, 1, new { version = 1, sessionId = "fabricated-session-12345", fields = new { history = "FORGED" }, reviewed = true });
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
        Assert.Equal("history", proposal.Field);
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
        if (kind == "source") json["fields"]!["history"]!["sources"]![0]!["end"] = 1000000;
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
}
