using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ehr.Data.Accounts;
using Ehr.Data.Audit;
using Ehr.Domain.Audit;
using Ehr.Web.Api.Dictation;
using Ehr.Web.Areas.Portal.Pages;
using Ehr.Web.CloudAudit;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;
using static Ehr.Web.Tests.Browser;

namespace Ehr.Web.Tests;

// The expansion plan's step 13c2: each page and API that reads a patient's record records which part of the record it
// was (AuditParts) on every read it makes, each kind of read with its own table. Each case opens one page, or calls one
// API, as the account that would, for a patient of its own, and reads back the reads that request recorded: by this
// account, of this patient, from the moment before it was sent.
public class AuditPartTests(WebApps apps) : IClassFixture<WebApps>
{
    // A patient signed in to the portal, on the panel of a clinician of their own, who is signed in: so the clinician's
    // panel, day and inbox hold this patient alone.
    async Task<(StaffAccount Clinician, Session Session, ProxyTests.Person Patient)> PatientAsync()
    {
        var clinician = await apps.CreateStaffAsync("Dr. Parts", StaffRoles.Clinician);
        var person = await ProxyTests.PersonAsync(apps, "Synthetic", "Parted");
        var session = await SignInAsync(apps.OnPrem, clinician);
        await RecordViewTests.AssignNowAsync(apps, clinician, person.Patient);
        return (clinician, session, person);
    }

    // "table part" for each read recorded by the account, of the patient, since the moment given: "none" for a read with
    // no part.
    async Task<List<string>> ReadsAsync(Guid patient, Guid actor, DateTimeOffset since)
    {
        await using var connection = await apps.OpenDatabaseAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT DISTINCT coalesce(table_name, '-') || ' ' || coalesce(part, 'none') FROM ehr.audit_events
            WHERE patient_id = @patient AND actor_id = @actor AND action = ANY(@reads) AND occurred_at >= @since
            ORDER BY 1
            """, connection);
        command.Parameters.AddWithValue("patient", patient);
        command.Parameters.AddWithValue("actor", actor);
        command.Parameters.AddWithValue("reads", AuditActions.Reads);
        command.Parameters.AddWithValue("since", since);
        var reads = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Cancel);
        while (await reader.ReadAsync(Cancel))
        {
            reads.Add(reader.GetString(0));
        }

        return reads;
    }

    // The clinic's pages for one patient, and the portal's, each with the reads it records. {0} is the patient.
    [Theory]
    [InlineData("clinic", "/patients/{0}", "patients chart")]
    [InlineData("clinic", "/patients/{0}/allergies", "patients lists")]
    [InlineData("clinic", "/patients/{0}/chart-history", "patients lists")]
    [InlineData("clinic", "/patients/{0}/orders/new", "patients orders")]
    [InlineData("clinic", "/patients/{0}/orders/discontinued", "patients orders")]
    [InlineData("clinic", "/patients/{0}/vitals", "patients vitals")]
    [InlineData("clinic", "/patients/{0}/appointments/new", "patients appointments")]
    [InlineData("clinic", "/patients/{0}/messages/new", "patients conversations")]
    [InlineData("clinic", "/patients/{0}/proxies", "patients access")]
    [InlineData("clinic", "/patients/{0}/dictate", "patients note-draft")]
    [InlineData("clinic", "/patients/{0}/listen", "patients note-draft")]
    [InlineData("clinic", "/patients", "patients panel")]
    [InlineData("clinic", "/patients?name=Parted", "patients search")]
    [InlineData("clinic", "/fhir/Patient/{0}", "patients fhir")]
    [InlineData("clinic", "/patients/{0}/forms", "patients forms")]
    [InlineData("portal", "/portal", "patients home")]
    [InlineData("portal", "/portal/appointments", "patients appointments")]
    [InlineData("portal", "/portal/results", "patients results")]
    [InlineData("portal", "/portal/messages", "patients conversations")]
    [InlineData("portal", "/portal/summaries", "patients summaries")]
    [InlineData("portal", "/portal/access", "patients access")]
    [InlineData("portal", "/portal/views", "audit_events views")]
    [InlineData("portal", "/portal/refills", "patients refills")]
    [InlineData("portal", "/portal/forms", "patients forms")]
    [InlineData("portal", RecordDownload.FilePath, "patients download")]
    [InlineData("portal", RecordDownload.PrintPath, "patients printable")]
    public async Task Each_page_records_its_part_on_every_read(string site, string path, params string[] reads)
    {
        var (clinician, session, patient) = await PatientAsync();
        var (client, actor) = site == "clinic" ? (session.Client, clinician.Id) : (patient.Client, patient.Account);
        var since = DateTimeOffset.UtcNow;

        using var response = await client.GetAsync(string.Format(System.Globalization.CultureInfo.InvariantCulture, path, patient.Patient), Cancel);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(reads, await ReadsAsync(patient.Patient, actor, since));
    }

    // The reads of the pages and APIs that need something of the patient's first: an order's results, an appointment's
    // page, the FHIR API's searches of entries, a signed note's sensitivity and addendum pages, the dictation API starting a
    // note, the desktop app's draft sync, and a draft's editor and the vital signs it offers (step 14). One row each, so a wrong part on any one of them turns its own row red.
    [Theory]
    [InlineData("order results", "patients results")]
    [InlineData("appointment", "patients appointments")]
    [InlineData("fhir conditions", "patients fhir")]
    [InlineData("fhir observations", "patients fhir")]
    [InlineData("fhir allergies", "patients fhir")]
    [InlineData("note sensitivity", "clinical_notes visit-note", "patients visit-note")]
    [InlineData("note addendum", "clinical_notes visit-note", "patients visit-note")]
    [InlineData("dictation api", "patients note-draft")]
    [InlineData("desktop draft sync", "clinical_drafts note-draft")]
    [InlineData("refill review", "patients refills")]
    [InlineData("refill order form", "patients refills")]
    [InlineData("note editor", "clinical_drafts note-draft", "patients note-draft")]
    [InlineData("note vital signs", "patients vitals")]
    [InlineData("dictation review", "ai_chart_proposals note-draft", "ai_field_proposals note-draft", "ai_note_reviews note-draft", "clinical_drafts note-draft", "patients note-draft")]
    [InlineData("proposed order form", "ai_chart_proposals note-draft", "ai_note_reviews note-draft", "patients orders")]
    [InlineData("proposed allergy form", "ai_chart_proposals note-draft", "ai_note_reviews note-draft", "patients lists")]
    [InlineData("note editor with proposals", "ai_chart_proposals note-draft", "ai_note_reviews note-draft", "clinical_drafts note-draft", "patients note-draft")]
    [InlineData("signed note with proposals", "ai_chart_proposals visit-note", "ai_note_reviews visit-note", "clinical_notes visit-note", "patients visit-note")]
    public async Task Each_api_and_page_that_needs_an_entry_records_its_part(string what, params string[] reads)
    {
        var (clinician, session, patient) = await PatientAsync();
        var chart = $"/patients/{patient.Patient}";
        Func<Task<HttpResponseMessage>> request;
        switch (what)
        {
            case "order results":
                var order = await OrderTests.SignedAsync(session.Client, patient.Patient, "lab", "Complete blood count (CBC)", OrderTests.Cbc);
                request = () => session.Client.GetAsync($"{chart}/orders/{order}/results", Cancel);
                break;
            case "appointment":
                await RecordViewTests.DayAndInboxAsync(apps, clinician.Id, patient.Patient);
                var appointment = await AppointmentTests.IdAsync(apps, patient.Patient);
                request = () => session.Client.GetAsync($"/appointments/{appointment}", Cancel);
                break;
            case "fhir conditions" or "fhir observations" or "fhir allergies":
                var resource = what switch { "fhir conditions" => "Condition", "fhir observations" => "Observation", _ => "AllergyIntolerance" };
                request = () => session.Client.GetAsync($"/fhir/{resource}?patient={patient.Patient}", Cancel);
                break;
            case "note sensitivity" or "note addendum":
                var note = await NoteAddendumTests.SignedNoteAsync(session.Client, patient.Patient);
                var path = what == "note sensitivity" ? NoteAddendumTests.SensitivityPath(patient.Patient, note) : NoteAddendumTests.AddendumPath(patient.Patient, note);
                request = () => session.Client.GetAsync(path, Cancel);
                break;
            case "dictation api":
                apps.Dictation.Reset();
                var token = Token(await session.Client.GetStringAsync($"{chart}/dictate", Cancel));
                request = () =>
                {
                    var start = new HttpRequestMessage(HttpMethod.Post, DictationApi.Path + "/for-note")
                    {
                        Content = JsonContent.Create(new { reviewId = Guid.NewGuid(), patientId = patient.Patient }),
                    };
                    start.Headers.Add("RequestVerificationToken", token);
                    return session.Client.SendAsync(start, Cancel);
                };
                break;
            case "refill review" or "refill order form":
                await RefillRequestTests.MedicationAsync(apps, patient.Patient);
                using (var asked = await RefillRequestTests.AskAsync(patient.Client, RefillRequestTests.Lisinopril))
                {
                    Assert.True(asked.IsSuccessStatusCode, asked.StatusCode.ToString());
                }

                var refill = await RefillRequestTests.RequestIdAsync(apps, patient.Patient);
                var refillPath = what == "refill review" ? RefillRequestTests.ReviewPath(patient.Patient, refill) : RefillRequestTests.ApprovePath(patient.Patient, refill);
                request = () => session.Client.GetAsync(refillPath, Cancel);
                break;
            case "dictation review" or "proposed order form" or "proposed allergy form" or "note editor with proposals":
                // Step 15: what a dictation proposes, read on its review, on the forms opened from it, and on its note.
                var dictated = await DictationProposalTests.DictateAsync(apps, session, patient.Patient);
                var proposed = what switch
                {
                    "dictation review" => dictated.ReviewPath,
                    "proposed order form" => DictationProposalTests.OrderFormPath(patient.Patient, dictated.Order),
                    "proposed allergy form" => DictationProposalTests.AllergyFormPath(patient.Patient, dictated.Allergy),
                    _ => $"{chart}/notes/drafts/{dictated.Draft}",
                };
                request = () => session.Client.GetAsync(proposed, Cancel);
                break;
            case "signed note with proposals":
                var signedNote = await DictationProposalTests.SignNoteAsync(apps, await DictationProposalTests.DictateAsync(apps, session, patient.Patient));
                request = () => session.Client.GetAsync(signedNote, Cancel);
                break;
            case "note editor" or "note vital signs":
                // A draft's editor, and the vital signs it offers for Objective when asked (step 14).
                using (var created = await PostFormAsync(session.Client, $"{chart}?handler=NewNote", chart))
                {
                    var editor = PathOf(created) + (what == "note vital signs" ? "?handler=Vitals" : "");
                    request = () => session.Client.GetAsync(editor, Cancel);
                }

                break;
            default:
                var desktop = CreateClient(apps.OnPrem);
                desktop.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await apps.DesktopSignInAsync(apps.OnPrem, clinician));
                using (var pushed = await desktop.PostAsJsonAsync("/api/desktop/drafts/push",
                    new { Key = Guid.NewGuid(), Id = Guid.NewGuid(), PatientId = patient.Patient, BaseVersion = (long?)null, Body = "Synthetic draft" }, JsonSerializerOptions.Web, Cancel))
                {
                    Assert.Equal(HttpStatusCode.OK, pushed.StatusCode);
                }

                request = () => desktop.PostAsJsonAsync("/api/desktop/drafts/pull", new { Since = "0", Patients = new[] { patient.Patient } }, JsonSerializerOptions.Web, Cancel);
                break;
        }

        var since = DateTimeOffset.UtcNow;
        using var response = await request();

        Assert.True(response.IsSuccessStatusCode, $"{what}: {response.StatusCode}");
        Assert.Equal(reads, await ReadsAsync(patient.Patient, clinician.Id, since));
    }

    // Pre-visit forms (step 13e): the clinician's page of them and one form's answers, the portal's page of them and one
    // form; each a read of the patient with the forms' part.
    [Fact]
    public async Task The_pre_visit_forms_pages_record_their_part()
    {
        var visit = await PreVisitFormTests.VisitAsync(apps, "Dr. Forms Parts");
        using (var asked = await PreVisitFormTests.AskAsync(visit.Session.Client, visit.Patient, visit.Appointment, PreVisitFormTests.Phq9))
        {
            Assert.True(asked.IsSuccessStatusCode, asked.StatusCode.ToString());
        }

        var form = await PreVisitFormTests.RequestAsync(apps, visit.Appointment, PreVisitFormTests.Phq9);
        var answers = await PreVisitFormTests.AnswerAsync(apps, visit.Account, form, PreVisitFormTests.Answers(PreVisitFormTests.PhqItems, 0, 0, 0, 0, 0, 0, 0, 0, 0));
        var (portal, _) = await PreVisitFormTests.PortalAsync(apps, visit);
        foreach (var (client, actor, path) in new[]
        {
            (visit.Session.Client, visit.Clinician.Id, PreVisitFormTests.FormsPath(visit.Patient)),
            (visit.Session.Client, visit.Clinician.Id, $"{PreVisitFormTests.FormsPath(visit.Patient)}/{answers}"),
            (portal, visit.Account, "/portal/forms"),
            (portal, visit.Account, PreVisitFormTests.FormPath(form)),
        })
        {
            var since = DateTimeOffset.UtcNow;
            using var response = await client.GetAsync(path, Cancel);
            Assert.True(response.IsSuccessStatusCode, $"{path}: {response.StatusCode}");
            Assert.Equal(["patients forms"], await ReadsAsync(visit.Patient, actor, since));
        }
    }

    // A forms page that showed no form is worded as that page, never as a form: someone the record is shared with, without
    // sensitive items, opens the portal's Pre-visit forms, which shows them none though the patient has one. The patient
    // reads their view as the page with the forms hidden; they themselves are shown nothing of it, as before.
    [Fact]
    public async Task A_proxy_without_sensitive_items_opening_the_forms_page_is_shown_to_the_patient_as_the_page_with_the_forms_hidden()
    {
        var visit = await PreVisitFormTests.VisitAsync(apps, "Dr. Forms Shown None");
        using (var asked = await PreVisitFormTests.AskAsync(visit.Session.Client, visit.Patient, visit.Appointment, PreVisitFormTests.Phq9))
        {
            Assert.True(asked.IsSuccessStatusCode, asked.StatusCode.ToString());
        }

        var (portal, jar) = await PreVisitFormTests.PortalAsync(apps, visit);
        var (without, _) = await VisitSummaryTests.ProxyForAsync(apps, new ProxyTests.Person(visit.Patient, visit.Account, visit.Email, portal, jar), "Formsnone", sensitive: false);
        var since = DateTimeOffset.UtcNow;
        using (var page = await without.Client.GetAsync("/portal/forms", Cancel))
        {
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            Assert.DoesNotContain(PreVisitFormTests.FormPath(await PreVisitFormTests.RequestAsync(apps, visit.Appointment, PreVisitFormTests.Phq9)),
                await page.Content.ReadAsStringAsync(Cancel));
        }

        Assert.Equal(["patients forms"], await ReadsAsync(visit.Patient, without.Account, since));
        var mine = RecordViewTests.Lines(await RecordViewTests.PageAsync(portal))
            .Where(line => line.StartsWith($"proxy | {without.Email} | ", StringComparison.Ordinal)).Select(line => string.Join(" | ", line.Split(" | ").Take(3)));
        Assert.Equal([$"proxy | {without.Email} | Your pre-visit forms list, without the forms", $"proxy | {without.Email} | Your portal home page, with your appointments"], mine);
        Assert.DoesNotContain(RecordViewTests.Lines(await RecordViewTests.PageAsync(without.Client)), line => line.Contains("forms", StringComparison.Ordinal));
    }

    // A clinician opens the forms page twice with the forms hidden, as it opens: the patient reads it as that page, with
    // how many times in a minute, as any part counted by views; never as forms.
    [Fact]
    public async Task A_clinicians_forms_page_opened_twice_with_the_forms_hidden_is_counted_as_views_of_that_page()
    {
        var visit = await PreVisitFormTests.VisitAsync(apps, "Dr. Forms Asker");
        using (var asked = await PreVisitFormTests.AskAsync(visit.Session.Client, visit.Patient, visit.Appointment, PreVisitFormTests.Phq9))
        {
            Assert.True(asked.IsSuccessStatusCode, asked.StatusCode.ToString());
        }

        // Signed in before joining the panel, so their home's list of patients is no view of this one.
        var twice = await apps.CreateStaffAsync("Dr. Forms Twice", StaffRoles.Clinician);
        var session = await SignInAsync(apps.OnPrem, twice);
        await apps.AssignAsync(twice, visit.Patient);
        for (var time = 0; time < 2; time++)
        {
            using var page = await session.Client.GetAsync(PreVisitFormTests.FormsPath(visit.Patient), Cancel);
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        }

        // One line when both fell in one minute, as they nearly always do; two, each once, when a minute began between.
        var minutes = (await PreVisitFormTests.QueryAsync(apps,
            "SELECT to_char(date_trunc('minute', occurred_at), 'HH24:MI') FROM ehr.audit_events WHERE actor_id = @twice AND patient_id = @patient ORDER BY occurred_at",
            ("twice", twice.Id), ("patient", visit.Patient))).Distinct().Count();
        const string Page = "staff | Dr. Forms Twice, clinician | Your pre-visit forms list, without the forms";
        var lines = RecordViewTests.Lines(await RecordViewTests.PageAsync((await PreVisitFormTests.PortalAsync(apps, visit)).Client))
            .Where(line => line.Contains("Dr. Forms Twice", StringComparison.Ordinal)).Select(line => string.Join(" | ", line.Split(" | ").Take(3)));
        Assert.Equal(minutes == 1 ? [Page + ", 2 times"] : [Page, Page], lines);
    }

    // A clinician who holds a pre-visit form's alert (step 13e) without the patient on their panel reads its answers from
    // the inbox, not through emergency access: the patient is shown the view as one of the pre-visit forms, unmarked; so
    // is a proxy with sensitive items; a proxy without them is shown nothing of it.
    [Fact]
    public async Task An_alert_holder_off_the_panel_reading_a_forms_answers_is_shown_as_a_forms_view_and_not_as_emergency_access()
    {
        var visit = await PreVisitFormTests.VisitAsync(apps, "Dr. Forms Owner");
        using (var asked = await PreVisitFormTests.AskAsync(visit.Session.Client, visit.Patient, visit.Appointment, PreVisitFormTests.Phq9))
        {
            Assert.True(asked.IsSuccessStatusCode, asked.StatusCode.ToString());
        }

        var answers = await PreVisitFormTests.AnswerAsync(apps, visit.Account, await PreVisitFormTests.RequestAsync(apps, visit.Appointment, PreVisitFormTests.Phq9),
            PreVisitFormTests.Answers(PreVisitFormTests.PhqItems, 0, 0, 0, 0, 0, 0, 0, 0, 1));
        var item = (await PreVisitFormTests.QueryAsync(apps, "SELECT id::text FROM ehr.inbox_items WHERE recipient_id = @clinician AND kind = 'form-alert'",
            ("clinician", visit.Clinician.Id))).Single();
        using (var done = await PostFormAsync(visit.Session.Client, $"/inbox?handler=Done&id={item}", "/inbox"))
        {
            Assert.Equal(HttpStatusCode.OK, done.StatusCode);
        }

        // Given the item on joining the panel, which nobody on it held open; kept on leaving.
        var covering = await apps.CreateStaffAsync("Dr. Forms Holder", StaffRoles.Clinician);
        await apps.AssignAsync(covering, visit.Patient);
        await PreVisitFormTests.QueryAsync(apps, "UPDATE ehr.patient_assignments SET ended_at = now() WHERE clinician_id = @covering AND ended_at IS NULL", ("covering", covering.Id));
        var (portal, jar) = await PreVisitFormTests.PortalAsync(apps, visit);
        var patient = new ProxyTests.Person(visit.Patient, visit.Account, visit.Email, portal, jar);
        var (allowed, _) = await VisitSummaryTests.ProxyForAsync(apps, patient, "Formsallowed", sensitive: true);
        var (without, _) = await VisitSummaryTests.ProxyForAsync(apps, patient, "Formswithout", sensitive: false);
        var holder = await SignInAsync(apps.OnPrem, covering);
        var since = DateTimeOffset.UtcNow;
        using (var read = await holder.Client.GetAsync($"{PreVisitFormTests.FormsPath(visit.Patient)}/{answers}", Cancel))
        {
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        }

        Assert.Equal(["patients forms"], await ReadsAsync(visit.Patient, covering.Id, since));
        Assert.Equal(0, await apps.AuditEventsAsync(AuditActions.BreakGlass, covering.Id));
        string[] Holder(string html) => [.. RecordViewTests.Lines(html).Where(line => line.Contains("Dr. Forms Holder", StringComparison.Ordinal))
            .Select(line => string.Join(" | ", line.Split(" | ").Take(3)))];
        Assert.Equal(["staff | Dr. Forms Holder, clinician | Your pre-visit forms"], Holder(await RecordViewTests.PageAsync(portal)));
        Assert.Equal(["staff | Dr. Forms Holder, clinician | The pre-visit forms"], Holder(await RecordViewTests.PageAsync(allowed.Client)));
        Assert.Empty(Holder(await RecordViewTests.PageAsync(without.Client)));
    }

    // A read or a download recorded without the part of the record it was for is refused, whichever way it is recorded,
    // so a page whose read forgot it fails; any other event is recorded with none.
    [Fact]
    public async Task A_read_recorded_without_its_part_is_refused()
    {
        using var scope = apps.OnPrem.Services.CreateScope();
        var audit = scope.ServiceProvider.GetRequiredService<AuditRecorder>();
        var patient = Guid.CreateVersion7();

        Assert.All(AuditActions.Reads, action => Assert.Throws<InvalidOperationException>(() => audit.Record(action, patient, "patients", patient)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => audit.RecordReadsAsync("patients", null!, [(patient, patient)], Cancel));
        await Assert.ThrowsAsync<InvalidOperationException>(() => audit.RecordAllAsync(null!, [(AuditActions.Read, patient, "patients", patient)], Cancel));
        Assert.Null(audit.Record(AuditActions.SignOut, patient).Part);
    }

    // A list's reads, a hundred patients on the clinic's home, are written in one statement, in the page's transaction,
    // not one statement each when the unit of work is saved: each statement prepares the table's checks again, which cost
    // the home 1.2 to 1.4 ms a page once the part had its check (step 13c2). Nothing is left for the save to insert, and
    // the events commit with the transaction.
    [Fact]
    public async Task A_lists_reads_are_written_in_one_statement_in_the_pages_transaction()
    {
        using var scope = apps.OnPrem.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Ehr.Data.EhrDbContext>();
        var audit = scope.ServiceProvider.GetRequiredService<AuditRecorder>();
        (Guid, Guid)[] patients = [.. Enumerable.Range(0, 100).Select(_ => Guid.CreateVersion7()).Select(patient => (patient, patient))];

        await using var transaction = await db.Database.BeginTransactionAsync(Cancel);
        await audit.RecordReadsAsync("patients", AuditParts.Panel, patients, Cancel);

        Assert.Empty(db.ChangeTracker.Entries<AuditEvent>());
        await transaction.CommitAsync(Cancel);
        await using var connection = await apps.OpenDatabaseAsync();
        await using var count = new NpgsqlCommand("SELECT count(*) FROM ehr.audit_events WHERE patient_id = ANY(@patients) AND part = 'panel'", connection);
        count.Parameters.AddWithValue("patients", patients.Select(p => p.Item1).ToArray());
        Assert.Equal(100L, await count.ExecuteScalarAsync(Cancel));
    }

    // A page's reads through several of the PHI reader's calls, recorded together (the expansion plan's step 16): every
    // event held until the reads return, nothing in the unit of work, then all written in the page's transaction; and
    // nothing at all when the reads throw.
    [Fact]
    public async Task A_pages_reads_recorded_together_are_written_when_they_return()
    {
        using var scope = apps.OnPrem.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Ehr.Data.EhrDbContext>();
        var audit = scope.ServiceProvider.GetRequiredService<AuditRecorder>();
        Guid[] patients = [Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7()];

        await using var transaction = await db.Database.BeginTransactionAsync(Cancel);
        await Assert.ThrowsAsync<InvalidOperationException>(() => audit.TogetherAsync<bool>(() =>
        {
            audit.Record(AuditActions.Read, patients[0], "patients", patients[0], part: AuditParts.NoteDraft);
            throw new InvalidOperationException("Synthetic: the page failed after a read.");
        }, Cancel));
        var unsaved = await audit.TogetherAsync(async () =>
        {
            audit.Record(AuditActions.Read, patients[1], "patients", patients[1], part: AuditParts.NoteDraft);
            await audit.RecordReadsAsync("ai_field_proposals", AuditParts.NoteDraft, [(patients[2], Guid.CreateVersion7()), (patients[2], Guid.CreateVersion7())], Cancel);
            return db.ChangeTracker.Entries<AuditEvent>().Count();
        }, Cancel);

        Assert.Equal(0, unsaved);
        Assert.Empty(db.ChangeTracker.Entries<AuditEvent>());
        await transaction.CommitAsync(Cancel);
        await using var connection = await apps.OpenDatabaseAsync();
        await using var count = new NpgsqlCommand(
            "SELECT patient_id, count(*) FROM ehr.audit_events WHERE patient_id = ANY(@patients) GROUP BY patient_id", connection);
        count.Parameters.AddWithValue("patients", patients);
        var recorded = new Dictionary<Guid, long>();
        await using (var reader = await count.ExecuteReaderAsync(Cancel))
        {
            while (await reader.ReadAsync(Cancel)) recorded[reader.GetGuid(0)] = reader.GetInt64(1);
        }

        Assert.Equal(new Dictionary<Guid, long> { [patients[1]] = 1, [patients[2]] = 2 }, recorded);
    }

    // The review of a recorded visit records every read it makes (the patient, the review, the draft, the transcript
    // and each proposal) together, in one statement: none of them is left for the unit of work, which would write each
    // in a statement of its own; and it reads the transcript in the query that reads its review, one round trip fewer
    // (step 16, for its render time).
    [Fact]
    public async Task The_review_of_a_visit_records_its_reads_together()
    {
        apps.Dictation.Reset();
        var d = await AmbientFlows.DraftAsync(apps);
        var saves = new AuditSaves();
        await using var counted = apps.OnPrem.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.ConfigureDbContext<Ehr.Data.EhrDbContext>(options => options.AddInterceptors(saves))));
        var session = await SignInAsync(counted, await apps.ClinicianAsync());
        var since = DateTimeOffset.UtcNow;
        var signedIn = saves.Count;

        using var page = await session.Client.GetAsync(d.ReviewUrl, Cancel);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal(signedIn, saves.Count);
        var read = Assert.Single(saves.Transcripts);
        Assert.Contains("ai_note_reviews", read, StringComparison.Ordinal);
        await using var connection = await apps.OpenDatabaseAsync();
        await using var reads = new NpgsqlCommand("""
            SELECT table_name || ' ' || count(*) FROM ehr.audit_events
            WHERE patient_id = @patient AND action = 'read' AND occurred_at >= @since GROUP BY table_name ORDER BY table_name
            """, connection);
        reads.Parameters.AddWithValue("patient", d.Listening.Patient);
        reads.Parameters.AddWithValue("since", since);
        var recorded = new List<string>();
        await using (var reader = await reads.ExecuteReaderAsync(Cancel))
        {
            while (await reader.ReadAsync(Cancel)) recorded.Add(reader.GetString(0));
        }

        Assert.Equal(["ai_field_proposals 4", "ai_note_reviews 1", "ambient_transcripts 1", "clinical_drafts 1", "patients 1"], recorded);
    }

    sealed class AuditSaves : DbCommandInterceptor
    {
        int count;

        public int Count => count;

        // The queries that read a visit's transcript.
        public System.Collections.Concurrent.ConcurrentQueue<string> Transcripts { get; } = new();

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("INSERT INTO ehr.audit_events", StringComparison.Ordinal)) Interlocked.Increment(ref count);
            else if (command.CommandText.Contains("ambient_transcripts", StringComparison.Ordinal)) Transcripts.Enqueue(command.CommandText);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("INSERT INTO ehr.audit_events", StringComparison.Ordinal)) Interlocked.Increment(ref count);
            return ValueTask.FromResult(result);
        }
    }

    // The parts are one list wherever they are kept: the code's (AuditParts), the database's check, the cloud app's
    // allow-list and the portal's words, none with a part the others don't have, in either direction; the words also
    // name every part a view recorded without one is named by, and say "including sensitive items" (or "with the
    // sensitive ones") for every part the allow-list takes on a read of them.
    [Fact]
    public async Task The_parts_are_one_list_in_the_code_the_database_the_cloud_app_and_the_words()
    {
        await using var connection = await apps.OpenDatabaseAsync();
        await using var command = new NpgsqlCommand("SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conname = 'ck_audit_events_part'", connection);
        var check = (string)(await command.ExecuteScalarAsync(Cancel))!;
        var database = Regex.Matches(check[check.IndexOf("(part)::text", StringComparison.Ordinal)..], @"'([^']*)'::character varying")
            .Select(match => match.Groups[1].Value);
        var cloud = CloudAuditEvents.Parts.Values.SelectMany(parts => parts).Distinct();
        string[] all = [.. AuditParts.All.Order(StringComparer.Ordinal)];

        Assert.Equal(all.Length, AuditParts.All.Distinct().Count());
        Assert.Equal(all, database.Order(StringComparer.Ordinal));
        Assert.Equal(all, cloud.Order(StringComparer.Ordinal));
        Assert.Equal(all.Union(RecordViewParts.All).Order(StringComparer.Ordinal), ViewsModel.Parts.Keys.Order(StringComparer.Ordinal));
        Assert.All(CloudAuditEvents.Parts.Where(shape => shape.Key.Action is AuditActions.ReadSensitive or AuditActions.DownloadSensitive).SelectMany(shape => shape.Value),
            part => Assert.NotNull(ViewsModel.Parts[part].Sensitive));

        // Pre-visit forms, always sensitive items: a plain read is a forms page that showed none, a view of sensitive
        // items is the forms; counted as views, never as forms.
        Assert.Equal(
            [
                "Your pre-visit forms list, without the forms", "The pre-visit forms list, without the forms",
                "Your pre-visit forms list, without the forms, 2 times", "Your pre-visit forms", "The pre-visit forms", "The pre-visit forms, 3 times",
            ],
            [
                ViewsModel.PartWords(AuditParts.Forms, own: true, count: 1, sensitive: false), ViewsModel.PartWords(AuditParts.Forms, own: false, count: 1, sensitive: false),
                ViewsModel.PartWords(AuditParts.Forms, own: true, count: 2, sensitive: false), ViewsModel.PartWords(AuditParts.Forms, own: true, count: 1, sensitive: true),
                ViewsModel.PartWords(AuditParts.Forms, own: false, count: 1, sensitive: true), ViewsModel.PartWords(AuditParts.Forms, own: false, count: 3, sensitive: true),
            ]);
    }

    // A part the page has no words for, as from a database newer than the app during a deploy, is never shown as it is:
    // the line is the record.
    [Fact]
    public void A_part_the_page_has_no_words_for_is_the_record()
    {
        Assert.Equal("Your record", ViewsModel.PartWords("from-a-newer-database", own: true, count: 1, sensitive: false));
        Assert.Equal("The record", ViewsModel.PartWords("from-a-newer-database", own: false, count: 1, sensitive: true));
        Assert.Equal("Your record, 3 times", ViewsModel.PartWords("from-a-newer-database", own: true, count: 3, sensitive: false));
    }

    // A clinician's day and inbox name the patient: each a read of the patient, as part of that list.
    [Fact]
    public async Task The_day_and_the_inbox_record_their_parts()
    {
        var (clinician, session, patient) = await PatientAsync();
        await RecordViewTests.DayAndInboxAsync(apps, clinician.Id, patient.Patient);

        var since = DateTimeOffset.UtcNow;
        Assert.Equal(HttpStatusCode.OK, (await session.Client.GetAsync("/schedule", Cancel)).StatusCode);
        Assert.Equal(["patients schedule"], await ReadsAsync(patient.Patient, clinician.Id, since));

        since = DateTimeOffset.UtcNow;
        Assert.Equal(HttpStatusCode.OK, (await session.Client.GetAsync("/inbox", Cancel)).StatusCode);
        Assert.Equal(["patients inbox"], await ReadsAsync(patient.Patient, clinician.Id, since));
    }

    // Writing a note records each of its reads as a note being written; the signed note's page as a visit note; and the
    // chart, which then lists it, records the read of the note it lists as part of the chart.
    [Fact]
    public async Task A_note_records_note_draft_while_written_visit_note_once_signed_and_chart_where_listed()
    {
        var (clinician, session, patient) = await PatientAsync();
        var since = DateTimeOffset.UtcNow;
        var note = await NoteAddendumTests.SignedNoteAsync(session.Client, patient.Patient);
        Assert.DoesNotContain(await ReadsAsync(patient.Patient, clinician.Id, since), read => !read.EndsWith(" note-draft", StringComparison.Ordinal)
            && read != "patients chart");
        Assert.Contains("clinical_drafts note-draft", await ReadsAsync(patient.Patient, clinician.Id, since));

        since = DateTimeOffset.UtcNow;
        Assert.Equal(HttpStatusCode.OK, (await session.Client.GetAsync($"/patients/{patient.Patient}/notes/{note}", Cancel)).StatusCode);
        Assert.Equal(["clinical_notes visit-note", "patients visit-note"], await ReadsAsync(patient.Patient, clinician.Id, since));

        since = DateTimeOffset.UtcNow;
        Assert.Equal(HttpStatusCode.OK, (await session.Client.GetAsync($"/patients/{patient.Patient}", Cancel)).StatusCode);
        Assert.Equal(["clinical_notes chart", "patients chart"], await ReadsAsync(patient.Patient, clinician.Id, since));
    }

    // The desktop app's offline copy of the clinician's panel: the chart and its notes, as the offline copy.
    [Fact]
    public async Task The_desktop_offline_copy_records_its_part()
    {
        var (clinician, session, patient) = await PatientAsync();
        await NoteAddendumTests.SignedNoteAsync(session.Client, patient.Patient);
        var token = await apps.DesktopSignInAsync(apps.OnPrem, clinician);
        var since = DateTimeOffset.UtcNow;

        using var offline = await DesktopSignInTests.PostAsync(apps.OnPrem, "/api/desktop/offline-panel", token);

        Assert.Equal(HttpStatusCode.OK, offline.StatusCode);
        Assert.Equal(["clinical_notes offline", "patients offline"], await ReadsAsync(patient.Patient, clinician.Id, since));
    }
}
