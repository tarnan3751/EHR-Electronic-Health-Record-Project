using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Ehr.Data.Accounts;
using Ehr.Data.Audit;
using Ehr.Web.CloudAudit;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Ehr.Web.Tests.Browser;

namespace Ehr.Web.Tests;

// UC 2, Read records during an on-site outage (Tahseen Arnan): what the other classes don't already cover. A form loaded
// before the outage and sent during it gets the "Not saved" page, which shows again what was typed, and only that;
// pages offer no change while nothing can be saved; the notes, draft sync and dictation added since the cloud milestone
// are read from the replica and audited, or refused; nobody can sign in until the clinic's server is back; and once
// it's back, the queued reads arrive and saving works again. CloudCutOff is the link down, CloudPrimaryDrill the
// primary down.
public class OffSiteOutageTests(WebApps apps) : IClassFixture<WebApps>
{
    static readonly (string Name, object Value)[] Note =
        [("Subjective", "Synthetic subjective: cough."), ("Objective", "Synthetic objective: chest clear."), ("Assessment", "Synthetic assessment: viral."), ("Plan", "Synthetic plan: <rest>.")];

    [Fact]
    public async Task A_reason_that_cannot_be_saved_is_shown_again_to_copy()
    {
        var patient = await apps.InsertPatientAsync("Noor", "Unreached");
        var session = await SignInAsync(apps.OnPrem, await apps.ClinicianAsync());
        // Loaded while the clinic's server could be reached; sent once it can't.
        var token = Token(await session.Client.GetStringAsync($"/patients/{patient}/reason", Cancel));

        using var response = await PlainPostAsync(Client(apps.CloudCutOff, session.Jar), $"/patients/{patient}/reason", token, ("Reason", "Covering <the> night shift"));

        var html = await NotSavedAsync(response);
        Assert.Contains("Copy what you entered below", html);
        Assert.Contains("<label>Reason <textarea readonly rows=\"4\">\nCovering &lt;the&gt; night shift</textarea></label>", html);
        Assert.Equal(0, await apps.AuditEventsAsync(AuditActions.BreakGlass, patientId: patient));
    }

    // Any page can post a form here; only the browser's own form gets its text shown back.
    [Fact]
    public async Task Text_posted_from_another_site_is_not_shown_back()
    {
        var patient = await apps.InsertPatientAsync("Ola", "Elsewhere");
        var session = await SignInAsync(apps.OnPrem, await apps.ClinicianAsync());
        var token = Token(await session.Client.GetStringAsync($"/patients/{patient}/reason", Cancel));

        using var response = await PlainPostAsync(Client(apps.CloudCutOff, session.Jar), $"/patients/{patient}/reason", token, "cross-site",
            ("Reason", "Text another site chose"));

        var html = await NotSavedAsync(response);
        Assert.DoesNotContain("Text another site chose", html);
        Assert.Contains("Go back and try again later.", html);
    }

    // What is shown again was kept in memory, never on the cloud server's disk, so a form too large for that isn't kept.
    [Fact]
    public async Task A_form_too_large_to_keep_in_memory_is_refused_without_being_shown_back()
    {
        var patient = await apps.InsertPatientAsync("Pia", "Oversized");
        var session = await SignInAsync(apps.OnPrem, await apps.ClinicianAsync());
        var token = Token(await session.Client.GetStringAsync($"/patients/{patient}/reason", Cancel));

        using var response = await PlainPostAsync(Client(apps.CloudCutOff, session.Jar), $"/patients/{patient}/reason", token,
            ("Reason", "Too long to keep. " + new string('x', 300_000)));

        var html = await NotSavedAsync(response);
        Assert.DoesNotContain("Too long to keep.", html);
        Assert.Contains("Go back and try again later.", html);
    }

    // UC 1's order form during an outage: the order is refused, and what was entered is kept. With JavaScript,
    // js/orders.js posts with fetch, gets the 503 and leaves the form filled; without it, the "Not saved" page shows the
    // order's fields again.
    [Fact]
    public async Task An_order_placed_during_an_outage_is_refused_and_its_entries_are_kept()
    {
        var clinician = await apps.ClinicianAsync();
        var patient = await apps.InsertPatientAsync("Quill", "Unordered");
        await apps.AssignAsync(clinician, patient);
        var session = await SignInAsync(apps.OnPrem, clinician);
        var path = $"/patients/{patient}/orders/new?type=medication";
        // Loaded while the clinic's server could be reached; sent once it can't.
        var token = Token(await session.Client.GetStringAsync(path, Cancel));
        var cutOff = Client(apps.CloudCutOff, session.Jar);
        (string Name, object Value)[] order =
        [
            ("Item", "Metformin 500 mg tablet"), ("Details[dose]", "500 mg"), ("Details[route]", "By mouth"),
            ("Details[frequency]", "Twice a day <with meals>"),
        ];

        // As the script sends it: refused, with nothing for the script to replace the form with.
        using var fetched = await HtmxPostAsync(cutOff, path, token, order);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, fetched.StatusCode);
        Assert.DoesNotContain("<form", await fetched.Content.ReadAsStringAsync(Cancel));

        // As a plain form: the "Not saved" page, with each entry to copy.
        using var posted = await PlainPostAsync(cutOff, path, token, order);
        var html = await NotSavedAsync(posted);
        Assert.Contains("Copy what you entered below", html);
        Assert.Contains(">\nMetformin 500 mg tablet</textarea>", html);
        Assert.Contains(">\n500 mg</textarea>", html);
        Assert.Contains(">\nBy mouth</textarea>", html);
        Assert.Contains(">\nTwice a day &lt;with meals&gt;</textarea>", html);

        await using var db = await apps.OpenDatabaseAsync();
        await using var count = new Npgsql.NpgsqlCommand("SELECT count(*) FROM ehr.orders WHERE patient_id = @patient", db);
        count.Parameters.AddWithValue("patient", patient);
        Assert.Equal(0L, await count.ExecuteScalarAsync(Cancel));
    }

    // Without JavaScript the editor's save is a plain form post; with it, note-editor.js keeps the text on screen.
    [Fact]
    public async Task A_note_saved_without_JavaScript_is_shown_again_and_the_draft_offers_no_editor()
    {
        var (session, patient, path) = await NewDraftAsync();
        var token = Token(await session.Client.GetStringAsync(path, Cancel));
        await ShipAsync(apps.CloudCutOff);
        var offSite = Client(apps.CloudCutOff, session.Jar);

        using var response = await PlainPostAsync(offSite, $"{path}?handler=Save", token, [("Version", 1), .. Note]);

        var html = await NotSavedAsync(response);
        Assert.Contains("<label>Subjective <textarea readonly rows=\"4\">\nSynthetic subjective: cough.</textarea></label>", html);
        Assert.Contains("<label>Objective <textarea readonly rows=\"4\">\nSynthetic objective: chest clear.</textarea></label>", html);
        Assert.Contains("<label>Assessment <textarea readonly rows=\"4\">\nSynthetic assessment: viral.</textarea></label>", html);
        Assert.Contains("<label>Plan <textarea readonly rows=\"4\">\nSynthetic plan: &lt;rest&gt;.</textarea></label>", html);
        Assert.DoesNotContain("Version", html);

        // The draft, reopened off-site, is shown as last saved, without the editor.
        var draft = await offSite.GetStringAsync(path, Cancel);
        Assert.Contains("This draft can't be changed or signed until the clinic's server is back.", draft);
        Assert.DoesNotContain("<textarea", draft);
        Assert.DoesNotContain("note-editor.js", draft);
        Assert.Contains(patient.ToString(), draft);
    }

    [Fact]
    public async Task A_quick_text_added_without_JavaScript_is_shown_again_and_the_page_offers_no_changes()
    {
        var existing = WebApps.NewShortcut();
        await apps.InsertAsync(existing, "Still readable off-site");
        var session = await SignInAsync(apps.OnPrem, await apps.ClinicianAsync());
        var token = Token(await session.Client.GetStringAsync(QuickTexts, Cancel));
        await ShipAsync(apps.CloudCutOff);
        var offSite = Client(apps.CloudCutOff, session.Jar);
        var shortcut = WebApps.NewShortcut();

        using var response = await PlainPostAsync(offSite, $"{QuickTexts}?handler=Add", token, ("Shortcut", shortcut), ("Body", "Typed during the outage"));

        var html = await NotSavedAsync(response);
        Assert.Contains($"<label>Shortcut <textarea readonly rows=\"4\">\n{shortcut}</textarea></label>", html);
        Assert.Contains("<label>Body <textarea readonly rows=\"4\">\nTyped during the outage</textarea></label>", html);
        Assert.Empty(await apps.RowsAsync(shortcut));

        var page = await offSite.GetStringAsync(QuickTexts, Cancel);
        Assert.Contains("Still readable off-site", page);
        Assert.Contains("Phrases can't be added or changed until the clinic's server is back.", page);
        Assert.DoesNotContain("add-quick-text", page);
        Assert.DoesNotContain("handler=Edit", page);
    }

    // The body kept for the "Not saved" page is still forwarded whole.
    [Fact]
    public async Task With_the_link_up_a_plain_form_post_is_still_saved_on_prem()
    {
        var session = await SignInAsync(apps.Cloud, await apps.ClinicianAsync());
        var token = Token(await session.Client.GetStringAsync(QuickTexts, Cancel));
        var shortcut = WebApps.NewShortcut();

        using var response = await PlainPostAsync(session.Client, $"{QuickTexts}?handler=Add", token, ("Shortcut", shortcut), ("Body", "Forwarded whole"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal([(shortcut, "Forwarded whole", 1)], await apps.RowsAsync(shortcut));
    }

    // Search and the chart have their own outage tests (PatientSearchTests, PatientChartTests); this is the panel's.
    [Fact]
    public async Task The_panel_is_read_from_the_replica_and_each_patient_listed_is_audited()
    {
        var clinician = await apps.CreateStaffAsync("Dr. Panel Away", StaffRoles.Clinician);
        var patient = await apps.InsertPatientAsync("Lior", "Panelread");
        var session = await SignInAsync(apps.OnPrem, clinician);
        await apps.AssignAsync(clinician, patient);
        await ShipAsync(apps.CloudCutOff);

        var panel = await Client(apps.CloudCutOff, session.Jar).GetStringAsync("/patients", Cancel);

        Assert.Contains("Panelread, Lior", panel);
        Assert.Contains("Read-only. The clinic's server can't be reached.", Words(panel));
        Assert.Single(QueuedReads(apps.CloudCutOff), e => e.PatientId == patient && e.ActorId == clinician.Id);
    }

    // UC 14 during an outage: the chart and a signed note come from the replica and their reads are queued; nothing
    // that writes is offered, and a new note sent anyway is refused.
    [Fact]
    public async Task Notes_are_read_from_the_replica_and_audited_and_note_writes_are_refused()
    {
        var (session, patient, path) = await NewDraftAsync();
        using (var saved = await HtmxPostAsync(session.Client, $"{path}?handler=Save", Token(await session.Client.GetStringAsync(path, Cancel)), [("Version", 1), .. Note]))
        {
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        }

        // Ready to sign, then the outage: the confirmation offers no signature.
        await ShipAsync(apps.CloudCutOff);
        var offSite = Client(apps.CloudCutOff, session.Jar);
        var confirm = await offSite.GetStringAsync($"{path}/confirm?version=2", Cancel);
        Assert.Contains("This note can't be signed while the clinic's server can't be reached.", confirm);
        Assert.DoesNotContain("Sign note", confirm);

        // Signed on-site, read off-site.
        string noteUrl;
        using (var signed = await HtmxPostAsync(session.Client, $"{path}/confirm", Token(await session.Client.GetStringAsync($"{path}/confirm?version=2", Cancel)),
                   ("Version", 2), ("Confirmed", true)))
        {
            noteUrl = JsonDocument.Parse(await signed.Content.ReadAsStringAsync(Cancel)).RootElement.GetProperty("url").GetString()!;
        }

        var chart = await offSite.GetStringAsync($"/patients/{patient}", Cancel);
        var note = await offSite.GetStringAsync(noteUrl, Cancel);
        Assert.Contains("Signed note", chart);
        Assert.DoesNotContain("handler=NewNote", chart);
        Assert.Contains("Synthetic plan: &lt;rest&gt;.", note);
        var noteId = Guid.Parse(new Uri(Base, noteUrl).AbsolutePath.Split('/')[^1]);
        var queued = QueuedReads(apps.CloudCutOff);
        Assert.Contains(queued, e => e.TableName == "patients" && e.PatientId == patient);
        Assert.Contains(queued, e => e.TableName == "clinical_notes" && e.RowId == noteId);

        using var newNote = await PlainPostAsync(offSite, $"/patients/{patient}?handler=NewNote", Token(chart));
        await NotSavedAsync(newNote);
    }

    // UC 8 and UC 7 during an outage: the desktop app's draft sync and offline panel are POSTs, which the cloud app
    // forwards, so both are refused, reads included; its quick text sync reads the replica.
    [Fact]
    public async Task The_desktop_apps_draft_sync_and_offline_panel_are_refused_and_quick_text_sync_still_reads()
    {
        var clinician = await apps.CreateStaffAsync("Dr. Desktop Away", StaffRoles.Clinician);
        var patient = await apps.InsertPatientAsync("Demo", "Desktopdraft");
        await apps.AssignAsync(clinician, patient);
        var token = await apps.DesktopSignInAsync(apps.OnPrem, clinician);
        using var client = CreateClient(apps.CloudCutOff);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var push = await client.PostAsJsonAsync("/api/desktop/drafts/push",
            new { Key = Guid.NewGuid(), Id = Guid.NewGuid(), PatientId = patient, BaseVersion = (long?)null, Body = "Synthetic offline text" }, JsonSerializerOptions.Web, Cancel);
        using var pull = await client.PostAsJsonAsync("/api/desktop/drafts/pull", new { Since = "0", Patients = new[] { patient } }, JsonSerializerOptions.Web, Cancel);
        using var offlinePanel = await client.PostAsync("/api/desktop/offline-panel", null, Cancel);
        using var quickTexts = await client.GetAsync("/api/sync/quick-texts?since=0", Cancel);

        foreach (var refused in new[] { push, pull, offlinePanel })
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
            Assert.Equal("Not saved. The clinic's server can't be reached.", await refused.Content.ReadAsStringAsync(Cancel));
        }

        Assert.Equal(HttpStatusCode.OK, quickTexts.StatusCode);
    }

    // UC 9 during an outage: the dictation page still names the patient, one queued read, and the API is refused.
    [Fact]
    public async Task The_dictation_page_read_is_audited_and_dictation_is_refused()
    {
        var clinician = await apps.ClinicianAsync();
        var patient = await apps.InsertPatientAsync("Pia", "Dictationdown");
        await apps.AssignAsync(clinician, patient);
        var session = await SignInAsync(apps.OnPrem, clinician);
        await ShipAsync(apps.CloudCutOff);
        var offSite = Client(apps.CloudCutOff, session.Jar);

        var page = await offSite.GetStringAsync($"/patients/{patient}/dictate", Cancel);
        using var start = await HtmxPostAsync(offSite, Ehr.Web.Api.Dictation.DictationApi.Path, Token(page));

        Assert.Contains("Dictation needs the clinic's server", page);
        Assert.Contains(QueuedReads(apps.CloudCutOff), e => e.TableName == "patients" && e.PatientId == patient && e.ActorId == clinician.Id);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, start.StatusCode);
    }

    // UC 2 assumes a clinician signed in before the outage. Someone who isn't gets the sign-in page from the replica,
    // but the sign-in itself goes to the clinic's server, so it isn't saved, and nothing typed is shown back.
    [Fact]
    public async Task During_an_outage_nobody_can_sign_in_off_site()
    {
        var clinician = await apps.CreateStaffAsync("Dr. Late Arrival", StaffRoles.Clinician);
        await ShipAsync(apps.CloudCutOff);
        var anonymous = Client(apps.CloudCutOff, new CookieContainer());

        using var home = await anonymous.GetAsync("/patients", Cancel);
        var signInPage = await ReadAsync(home);
        using var signIn = await PlainPostAsync(anonymous, "/sign-in", Token(signInPage), ("Input.Email", clinician.Email), ("Input.Password", clinician.Password));

        Assert.Equal("/sign-in", PathOf(home));
        Assert.Contains("Read-only. The clinic's server can't be reached.", Words(signInPage));
        var html = await NotSavedAsync(signIn);
        Assert.DoesNotContain(clinician.Password, html);
        Assert.DoesNotContain(clinician.Email, html);
        using var after = await anonymous.GetAsync("/patients", Cancel);
        Assert.Equal("/sign-in", PathOf(after));
        Assert.Equal(0, await apps.AuditEventsAsync(AuditActions.SignIn, actorId: clinician.Id));
    }

    // Once the clinic's server is back: TestCases/UC2/TC_UC2_02_AlternativeFlow.cs.

    // A patient on the fixture's clinician's panel with a new draft, made on-site: the session, the patient and the
    // editor's address.
    async Task<(Session Session, Guid Patient, string Path)> NewDraftAsync()
    {
        var clinician = await apps.ClinicianAsync();
        var patient = await apps.InsertPatientAsync("Synthetic", "Outagenote");
        await apps.AssignAsync(clinician, patient);
        var session = await SignInAsync(apps.OnPrem, clinician);
        using var created = await PostFormAsync(session.Client, $"/patients/{patient}?handler=NewNote", $"/patients/{patient}");
        await ReadAsync(created);
        return (session, patient, PathOf(created));
    }

    // The check the cloud app makes every couple of seconds, now: the clinic's server can't be reached.
    static async Task ShipAsync(WebApplicationFactory<Program> cloud) =>
        Assert.False(await cloud.Services.GetRequiredService<CloudAuditShipper>().ShipAsync(Cancel));

    static List<AuditEvent> QueuedReads(WebApplicationFactory<Program> cloud) =>
        [.. cloud.Services.GetRequiredService<CloudAuditQueue>().ReadPending(int.MaxValue).Events.Where(e => e.Action == AuditActions.Read)];

    // A form posted by the browser without JavaScript, from one of the site's own pages unless `site` says otherwise.
    static Task<HttpResponseMessage> PlainPostAsync(HttpClient client, string path, string token, params (string Name, object Value)[] fields) =>
        PlainPostAsync(client, path, token, "same-origin", fields);

    static async Task<HttpResponseMessage> PlainPostAsync(HttpClient client, string path, string token, string site, params (string Name, object Value)[] fields)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = Form([.. fields, ("__RequestVerificationToken", token)]) };
        request.Headers.Add("Sec-Fetch-Site", site);
        return await client.SendAsync(request, Cancel);
    }

    // The same through htmx or a page's script, which sends the form token in a header too, as the dictation API wants.
    internal static async Task<HttpResponseMessage> HtmxPostAsync(HttpClient client, string path, string token, params (string Name, object Value)[] fields)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = Form([.. fields, ("__RequestVerificationToken", token)]) };
        request.Headers.Add("HX-Request", "true");
        request.Headers.Add("RequestVerificationToken", token);
        return await client.SendAsync(request, Cancel);
    }

    // The cloud app's "Not saved" page, which no browser keeps.
    static async Task<string> NotSavedAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var html = await response.Content.ReadAsStringAsync(Cancel);
        Assert.Contains("Not saved.</span> The clinic's server can't be reached.", html);
        return html;
    }
}
