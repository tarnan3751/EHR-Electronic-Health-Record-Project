using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Ehr.Data.Audit;
using Ehr.Domain.Notes;
using Ehr.Testing;
using Ehr.Web.CloudAudit;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;
using static Ehr.Web.Tests.Browser;

namespace Ehr.Web.Tests;

public class ClinicalNoteTests(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;
    static readonly NoteContent Synthetic = new("Synthetic subjective: cough for two days.", "Synthetic objective: chest clear, temperature 37.9.", "Synthetic assessment: viral illness.", "Synthetic plan: rest and follow-up.");

    sealed record DraftPage(Session Session, Guid Patient, Guid Draft, string Path, string Token);

    async Task<DraftPage> CreateAsync()
    {
        var clinician = await apps.ClinicianAsync();
        var patient = await apps.InsertPatientAsync("Synthetic", "Note");
        await apps.AssignAsync(clinician, patient);
        var session = await SignInAsync(apps.OnPrem, clinician);
        using var response = await PostFormAsync(session.Client, $"/patients/{patient}?handler=NewNote", $"/patients/{patient}");
        var html = await ReadAsync(response);
        var path = PathOf(response);
        Assert.Contains("Draft · unsigned", html);
        Assert.Contains("autofocus", html);
        Assert.DoesNotContain("htmx.min.js", html);
        return new(session, patient, Guid.Parse(path.Split('/')[^1]), path, Token(html));
    }

    static async Task<HttpResponseMessage> PostAsync(DraftPage page, string handler, long version, NoteContent? content = null,
        HttpClient? client = null, params (string Name, object Value)[] extra)
    {
        content ??= Synthetic;
        using var request = new HttpRequestMessage(HttpMethod.Post, page.Path + (handler == "Sign" ? "/confirm" : "?handler=" + handler));
        request.Headers.Add("HX-Request", "true");
        request.Content = Form([("__RequestVerificationToken", page.Token), ("Version", version),
            ("Subjective", content.Subjective), ("Objective", content.Objective), ("Assessment", content.Assessment), ("Plan", content.Plan), .. extra]);
        return await (client ?? page.Session.Client).SendAsync(request, Cancel);
    }

    static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(Cancel)).RootElement.Clone();

    // TC-UC14-01: the chart creates a private server draft; a later request reopens the saved structured content.
    [Fact]
    public async Task Create_save_reopen_and_chart_link_preserve_all_four_sections()
    {
        var page = await CreateAsync();
        using var saved = await PostAsync(page, "Save", 1);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal(2, (await JsonAsync(saved)).GetProperty("version").GetInt64());
        var html = await page.Session.Client.GetStringAsync(page.Path, Cancel);
        Assert.Contains(Synthetic.Subjective, html);
        Assert.Contains(Synthetic.Objective, html);
        Assert.Contains(Synthetic.Assessment, html);
        Assert.Contains(Synthetic.Plan, html);
        Assert.Contains("Saved", html);
        var chart = await page.Session.Client.GetStringAsync($"/patients/{page.Patient}", Cancel);
        Assert.Contains(page.Path, chart);
        Assert.DoesNotContain("htmx.min.js", chart);
        Assert.True(await apps.AuditEventsAsync(AuditActions.Read, patientId: page.Patient) > 0);
    }

    // TC-UC14-02: signing uses persisted values, an explicit confirmation and server identity/time, with audit.
    [Fact]
    public async Task Review_confirm_sign_and_retry_create_one_immutable_chart_record()
    {
        var page = await CreateAsync();
        using var review = await PostAsync(page, "Review", 1);
        Assert.Equal(HttpStatusCode.OK, review.StatusCode);
        var confirmUrl = (await JsonAsync(review)).GetProperty("url").GetString()!;
        var confirmation = await page.Session.Client.GetStringAsync(confirmUrl, Cancel);
        Assert.Contains("Confirm signature", confirmation);
        Assert.Contains("I have reviewed this note and sign it as Dr. Test.", confirmation);
        Assert.DoesNotContain("htmx.min.js", confirmation);
        using var unconfirmed = await PostAsync(page, "Sign", 2);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unconfirmed.StatusCode);
        // By the database's clock, which stamps the times.
        DateTimeOffset before = (await apps.Database.NowAsync()).AddSeconds(-1);
        using var signed = await PostAsync(page, "Sign", 2, extra: [("Confirmed", true), ("SignerId", Guid.NewGuid()), ("SignedAt", "2000-01-01")]);
        Assert.Equal(HttpStatusCode.OK, signed.StatusCode);
        var url = (await JsonAsync(signed)).GetProperty("url").GetString()!;
        var html = await page.Session.Client.GetStringAsync(url, Cancel);
        Assert.Contains("Signed · read-only", html);
        Assert.Contains(Synthetic.Plan, html);
        Assert.DoesNotContain("textarea", html);
        Assert.DoesNotContain("htmx.min.js", html);
        Assert.Contains("<dt>Signed by</dt><dd>Dr. Test</dd>", html);
        Assert.DoesNotContain((await apps.ClinicianAsync()).Id.ToString(), html);
        var (count, signer, time) = await SignedAsync(page.Draft);
        Assert.Equal(1, count);
        Assert.Equal((await apps.ClinicianAsync()).Id, signer);
        Assert.InRange(time, before, (DateTimeOffset)(await apps.Database.NowAsync()).AddSeconds(1));
        Assert.Equal(1, await apps.AuditEventsAsync(AuditActions.SignNote, signer, page.Patient));
        Assert.Contains("Signed note", await page.Session.Client.GetStringAsync($"/patients/{page.Patient}", Cancel));
        using var retry = await PostAsync(page, "Sign", 2, extra: [("Confirmed", true)]);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(url, (await JsonAsync(retry)).GetProperty("url").GetString());
        using var edit = await PostAsync(page, "Save", 3, new("Changed", "Changed", "Changed", "Changed"));
        Assert.Equal(HttpStatusCode.Conflict, edit.StatusCode);
        Assert.Contains(Synthetic.Subjective, await page.Session.Client.GetStringAsync(url, Cancel));
        Assert.Equal(1, await apps.AuditEventsAsync(AuditActions.SignNote, signer, page.Patient));
    }

    // TC-UC14-03: drafts may be incomplete; signing must reject them without changing the draft.
    [Fact]
    public async Task Incomplete_draft_is_saved_but_signing_is_rejected_and_preserves_it()
    {
        var page = await CreateAsync();
        using var save = await PostAsync(page, "Save", 1, new("Synthetic subjective", "", "", ""));
        Assert.Equal(HttpStatusCode.OK, save.StatusCode);
        using var sign = await PostAsync(page, "Sign", 2, extra: [("Confirmed", true)]);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, sign.StatusCode);
        var errors = await sign.Content.ReadAsStringAsync(Cancel);
        Assert.Contains("Enter Objective before signing", errors);
        Assert.Contains("Enter Assessment before signing", errors);
        Assert.Contains("Enter Plan before signing", errors);
        Assert.Equal(0, (await SignedAsync(page.Draft)).Count);
        var html = await page.Session.Client.GetStringAsync(page.Path, Cancel);
        Assert.Contains("Synthetic subjective", html);
        Assert.Contains("value=\"2\"", html);
    }

    [Fact]
    public async Task Unreviewed_server_proposals_cannot_be_cleared_by_posted_review_flags()
    {
        var page = await CreateAsync();
        using var save = await PostAsync(page, "Save", 1);
        await SqlAsync("UPDATE ehr.clinical_drafts SET has_unreviewed_proposals = true, version = version + 1, structured_version = version + 1 WHERE id = @id", page.Draft);
        using var sign = await PostAsync(page, "Sign", 3, extra: [("Confirmed", true), ("HasUnreviewedProposals", false), ("Reviewed", true)]);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, sign.StatusCode);
        Assert.Contains("Review every associated AI proposal", await sign.Content.ReadAsStringAsync(Cancel));
        Assert.Equal(0, (await SignedAsync(page.Draft)).Count);
    }

    // TC-UC14-04: knowledge of patient and draft identifiers grants no access to another author's draft.
    [Fact]
    public async Task Another_clinician_cannot_read_edit_or_sign_a_private_draft()
    {
        var page = await CreateAsync();
        var other = await apps.CreateStaffAsync("Synthetic colleague", Ehr.Data.Accounts.StaffRoles.Clinician);
        await apps.AssignAsync(other, page.Patient);
        var session = await SignInAsync(apps.OnPrem, other);
        using var read = await session.Client.GetAsync(page.Path, Cancel);
        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
        var token = Token(await session.Client.GetStringAsync($"/patients/{page.Patient}", Cancel));
        var otherPage = page with { Token = token, Session = session };
        using var edit = await PostAsync(otherPage, "Save", 1);
        using var sign = await PostAsync(otherPage, "Sign", 1, extra: [("Confirmed", true)]);
        Assert.Equal(HttpStatusCode.NotFound, edit.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, sign.StatusCode);
        Assert.Equal(0, (await SignedAsync(page.Draft)).Count);
        using var mismatch = await PostAsync(page with { Path = page.Path.Replace(page.Patient.ToString(), Guid.NewGuid().ToString(), StringComparison.Ordinal) }, "Save", 1);
        Assert.Equal(HttpStatusCode.NotFound, mismatch.StatusCode);
    }

    [Fact]
    public async Task Without_a_panel_assignment_creation_and_draft_operations_are_refused()
    {
        var page = await CreateAsync();
        await SqlAsync("UPDATE ehr.patient_assignments SET ended_at = now() WHERE patient_id = @id", page.Patient);
        using var create = await page.Session.Client.PostAsync($"/patients/{page.Patient}?handler=NewNote", Form(("__RequestVerificationToken", page.Token)), Cancel);
        Assert.Equal("/denied", PathOf(create));
        using var edit = await PostAsync(page, "Save", 1);
        using var sign = await PostAsync(page, "Sign", 1, extra: [("Confirmed", true)]);
        Assert.Equal(HttpStatusCode.NotFound, edit.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, sign.StatusCode);
    }

    // TC-UC14-06: simultaneous signing can return success or an explicit conflict, but never two records.
    [Fact]
    public async Task Concurrent_signatures_and_stale_updates_never_duplicate_or_overwrite()
    {
        var page = await CreateAsync();
        using var save = await PostAsync(page, "Save", 1);
        using var stale = await PostAsync(page, "Save", 1, new("Stale", "Stale", "Stale", "Stale"));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var responses = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => PostAsync(page, "Sign", 2, extra: [("Confirmed", true)])));
        try
        {
            Assert.Contains(responses, r => r.StatusCode == HttpStatusCode.OK);
            Assert.All(responses, r => Assert.Contains(r.StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.Conflict }));
            Assert.Equal(1, (await SignedAsync(page.Draft)).Count);
            Assert.Equal(1, await apps.AuditEventsAsync(AuditActions.SignNote, patientId: page.Patient));
        }
        finally
        {
            foreach (var response in responses) response.Dispose();
        }
    }

    [Fact]
    public async Task Saving_after_confirmation_invalidates_the_old_signature_version()
    {
        var page = await CreateAsync();
        using var review = await PostAsync(page, "Review", 1);
        using var change = await PostAsync(page, "Save", 2, Synthetic with { Subjective = "New subjective" });
        using var sign = await PostAsync(page, "Sign", 2, extra: [("Confirmed", true)]);
        Assert.Equal(HttpStatusCode.Conflict, sign.StatusCode);
        Assert.Equal(0, (await SignedAsync(page.Draft)).Count);
    }

    // Supporting test (not numbered in the SE document): both a broken link and a primary that answers but cannot save refuse signatures.
    [Fact]
    public async Task Primary_outage_refuses_signature_and_keeps_the_server_draft_available()
    {
        var page = await CreateAsync();
        using var saved = await PostAsync(page, "Save", 1);
        var cutOff = Client(apps.CloudCutOff, page.Session.Jar);
        using var down = await PostAsync(page, "Sign", 2, client: cutOff, extra: [("Confirmed", true)]);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, down.StatusCode);
        apps.PrimaryDown = true;
        try
        {
            using var unavailable = await PostAsync(page, "Sign", 2, client: Client(apps.CloudPrimaryDrill, page.Session.Jar), extra: [("Confirmed", true)]);
            Assert.Equal(HttpStatusCode.InternalServerError, unavailable.StatusCode);
            Assert.Equal(0, (await SignedAsync(page.Draft)).Count);
            // Cloud can reopen the draft even while primary writes are unavailable; its reads are queued.
            var html = await cutOff.GetStringAsync(page.Path, Cancel);
            Assert.Contains(Synthetic.Subjective, html);
            var queued = apps.CloudCutOff.Services.GetRequiredService<CloudAuditQueue>().ReadPending(int.MaxValue).Events;
            Assert.Contains(queued, e => e.TableName == "clinical_drafts" && e.RowId == page.Draft);
        }
        finally { apps.PrimaryDown = false; }
        Assert.Contains(Synthetic.Plan, await page.Session.Client.GetStringAsync(page.Path, Cancel));
    }

    [Fact]
    public async Task Desktop_plain_text_changes_require_structured_review_and_signed_drafts_reject_sync_updates()
    {
        var page = await CreateAsync();
        using var saved = await PostAsync(page, "Save", 1);
        var token = await apps.DesktopSignInAsync(apps.OnPrem, await apps.ClinicianAsync());
        using var desktop = CreateClient(apps.OnPrem);
        desktop.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var push = new { Key = Guid.NewGuid(), Id = page.Draft, PatientId = page.Patient, BaseVersion = 2, Body = "Synthetic offline text" };
        using var synced = await desktop.PostAsJsonAsync("/api/desktop/drafts/push", push, Cancel);
        Assert.Equal("applied", (await JsonAsync(synced)).GetProperty("outcome").GetString());
        using var sign = await PostAsync(page, "Sign", 3, extra: [("Confirmed", true)]);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, sign.StatusCode);
        Assert.Contains("Synthetic offline text", await page.Session.Client.GetStringAsync(page.Path, Cancel));
        using var resaved = await PostAsync(page, "Save", 3);
        using var signed = await PostAsync(page, "Sign", 4, extra: [("Confirmed", true)]);
        Assert.Equal(HttpStatusCode.OK, signed.StatusCode);
        using var receiptRetry = await desktop.PostAsJsonAsync("/api/desktop/drafts/push", push, Cancel);
        Assert.Equal("duplicate", (await JsonAsync(receiptRetry)).GetProperty("outcome").GetString());
        using var refused = await desktop.PostAsJsonAsync("/api/desktop/drafts/push", push with { Key = Guid.NewGuid(), BaseVersion = 5, Body = "Must not overwrite" }, Cancel);
        Assert.Equal("invalid", (await JsonAsync(refused)).GetProperty("outcome").GetString());
        Assert.Equal(1, (await SignedAsync(page.Draft)).Count);
    }

    [Fact]
    public async Task Cloud_serves_signed_notes_and_ships_their_read_audit()
    {
        var page = await CreateAsync();
        using var saved = await PostAsync(page, "Save", 1);
        using var signed = await PostAsync(page, "Sign", 2, extra: [("Confirmed", true)]);
        var url = (await JsonAsync(signed)).GetProperty("url").GetString()!;
        var client = Client(apps.Cloud, page.Session.Jar);
        Assert.Contains(Synthetic.Subjective, await client.GetStringAsync(url, Cancel));
        Assert.Contains("Signed note", await client.GetStringAsync($"/patients/{page.Patient}", Cancel));
        await apps.ShipCloudAuditAsync();
        Assert.True(await apps.AuditEventsAsync(AuditActions.Read, patientId: page.Patient, source: AuditSources.Cloud) > 0);
    }

    [Fact]
    public async Task Forged_or_missing_antiforgery_tokens_cannot_save_or_sign()
    {
        var page = await CreateAsync();
        using var save = await PostAsync(page with { Token = "forged" }, "Save", 1);
        using var sign = await PostAsync(page with { Token = "forged" }, "Sign", 1, extra: [("Confirmed", true)]);
        Assert.Equal(HttpStatusCode.BadRequest, save.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, sign.StatusCode);
    }

    [Fact]
    public async Task Cloud_domain_service_refuses_writes_even_without_forwarding()
    {
        await using var scope = apps.Cloud.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<ClinicalNotes>();
        Assert.Equal(503, (await service.SaveAsync(Guid.NewGuid(), Guid.NewGuid(), 1, Synthetic, Cancel)).Status);
        Assert.Equal(503, (await service.SignAsync(Guid.NewGuid(), Guid.NewGuid(), 1, true, Cancel)).Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateAsync(Guid.NewGuid(), Cancel));
    }

    [Fact]
    public async Task Admin_cannot_create_read_edit_or_sign_clinical_notes()
    {
        var page = await CreateAsync();
        var admin = await apps.CreateStaffAsync("Synthetic admin", Ehr.Data.Accounts.StaffRoles.Admin);
        var session = await SignInAsync(apps.OnPrem, admin);
        var token = Token(await session.Client.GetStringAsync("/admin/staff", Cancel));
        using var client = Client(apps.OnPrem, session.Jar, followRedirects: false);
        using var read = await client.GetAsync(page.Path, Cancel);
        Assert.Equal("/denied", new Uri(Base, read.Headers.Location!).AbsolutePath);
        foreach (var handler in new[] { "Save", "Sign" })
        {
            using var response = await PostAsync(page with { Token = token }, handler, 1, client: client, extra: [("Confirmed", true)]);
            Assert.Equal("/denied", new Uri(Base, response.Headers.Location!).AbsolutePath);
        }
        using var create = await client.PostAsync($"/patients/{page.Patient}?handler=NewNote", Form(("__RequestVerificationToken", token)), Cancel);
        Assert.Equal("/denied", new Uri(Base, create.Headers.Location!).AbsolutePath);
    }

    async Task SqlAsync(string sql, Guid id)
    {
        await using var db = await apps.OpenDatabaseAsync();
        await using var command = new NpgsqlCommand(sql, db);
        command.Parameters.AddWithValue("id", id);
        await command.ExecuteNonQueryAsync(Cancel);
    }

    async Task<(int Count, Guid Signer, DateTimeOffset Time)> SignedAsync(Guid draft)
    {
        await using var db = await apps.OpenDatabaseAsync();
        await using var command = new NpgsqlCommand("SELECT signer_id, signed_at FROM ehr.clinical_notes WHERE draft_id = @id", db);
        command.Parameters.AddWithValue("id", draft);
        await using var reader = await command.ExecuteReaderAsync(Cancel);
        var count = 0;
        var signer = Guid.Empty;
        var time = default(DateTimeOffset);
        while (await reader.ReadAsync(Cancel))
        {
            count++;
            signer = reader.GetGuid(0);
            time = reader.GetFieldValue<DateTimeOffset>(1);
        }
        return (count, signer, time);
    }
}
