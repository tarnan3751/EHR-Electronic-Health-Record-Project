using System.Net;
using Ehr.Data;
using Ehr.Data.Accounts;
using Ehr.Data.Audit;
using Ehr.Data.Patients;
using Ehr.Domain.Audit;
using Ehr.Domain.Phi;
using Ehr.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;
using static Ehr.Web.Tests.Browser;

namespace Ehr.Web.Tests;

// The expansion plan's step 2: adding to and changing a patient's allergies, medications and diagnoses, the helpers
// for it, and coverage beside the test cases in TestCases/UC4 (TC-UC4-07 to TC-UC4-12): the code search, sensitive
// items, the history page, the banner on other patient pages, and the off-site path. What the database refuses
// whatever the app does is in Ehr.Security.Tests (ChartListSecurityTests).
public class ChartListTests(WebApps apps) : IClassFixture<WebApps>
{
    internal static string EntryPath(Guid patient, string list, Guid? entry = null) => $"/patients/{patient}/{list}{(entry is null ? "" : $"/{entry}")}";

    internal static async Task<(StaffAccount Clinician, Guid Patient, Session Session)> OnPanelAsync(WebApps apps, string family = "Listed")
    {
        var clinician = await apps.ClinicianAsync();
        var patient = await apps.InsertPatientAsync("Synthetic", family);
        await apps.AssignAsync(clinician, patient);
        return (clinician, patient, await SignInAsync(apps.OnPrem, clinician));
    }

    // "Save", as the entry form posts it.
    internal static Task<HttpResponseMessage> SaveAsync(HttpClient client, Guid patient, string list, Guid? entry, params (string Name, object Value)[] fields) =>
        PostFormAsync(client, EntryPath(patient, list, entry), EntryPath(patient, list, entry), fields);

    // One column of a patient's rows in a chart table, in the order given.
    internal static Task<List<string>> RowsAsync(WebApps apps, string table, Guid patient, string expression, string order = "display") =>
        apps.PatientRowsAsync($"SELECT {expression} FROM ehr.{table} WHERE patient_id = @patient ORDER BY {order}", patient);

    internal static async Task<Guid> IdAsync(WebApps apps, string table, Guid patient) =>
        Guid.Parse((await RowsAsync(apps, table, patient, "id::text", "id")).Single());

    // The search offers names that start with what was typed first, then names with a word that does, and a diagnosis
    // by its code; it reads nothing about a patient, so it records nothing; and only clinicians reach it.
    [Fact]
    public async Task The_code_search_offers_matching_names_and_is_for_clinicians_only()
    {
        var (clinician, patient, session) = await OnPanelAsync(apps);
        // On-site events only: an earlier test's off-site reads may still be arriving from the cloud app's queue.
        var reads = await apps.AuditEventsAsync(AuditActions.Read, clinician.Id, source: AuditSources.OnPrem);

        var hypertension = await session.Client.GetStringAsync("/codes?kind=condition&q=essential", Cancel);
        Assert.Contains("<option value=\"Essential (primary) hypertension\" label=\"I10\"></option>", hypertension);
        var byWord = await session.Client.GetStringAsync("/codes?kind=condition&q=anxiety", Cancel);
        Assert.Contains("value=\"Generalized anxiety disorder\"", byWord);
        Assert.Contains("value=\"Type 2 diabetes mellitus without complications\"", await session.Client.GetStringAsync("/codes?kind=condition&q=e11.9", Cancel));
        Assert.Contains("value=\"lisinopril 10 MG Oral Tablet\"", await session.Client.GetStringAsync("/codes?kind=medication&q=lisin", Cancel));
        var allergens = await session.Client.GetStringAsync("/codes?kind=allergen&q=peanu", Cancel);
        Assert.Contains("value=\"Peanut\"", allergens);
        Assert.Contains("value=\"Penicillin G\"", await session.Client.GetStringAsync("/codes?kind=allergen&q=penicillin", Cancel));
        Assert.DoesNotContain("label=", allergens);
        // Words in any order, each by its start, among about a hundred thousand names.
        Assert.Contains("value=\"Type 2 diabetes mellitus without complications\"",
            await session.Client.GetStringAsync("/codes?kind=condition&q=diab+mell+without+compl+type+2", Cancel));
        Assert.Contains("[Lipitor]", await session.Client.GetStringAsync("/codes?kind=medication&q=lipitor+20", Cancel));
        // What is typed is never read as the search's own syntax.
        Assert.DoesNotContain("<option", await session.Client.GetStringAsync("/codes?kind=condition&q=" + Uri.EscapeDataString("zz:* | !a & (b"), Cancel));
        // Too little typed, an unknown kind, and LIKE's own characters: nothing.
        foreach (var address in new[] { "/codes?kind=condition&q=e", "/codes?kind=order&q=essential", "/codes?kind=condition&q=%25%25", "/codes" })
        {
            Assert.DoesNotContain("<option", await session.Client.GetStringAsync(address, Cancel));
        }

        Assert.True(System.Text.RegularExpressions.Regex.Matches(await session.Client.GetStringAsync("/codes?kind=condition&q=type", Cancel), "<option").Count
            <= Ehr.Domain.Patients.ChartLists.Suggestions);
        Assert.Equal(reads, await apps.AuditEventsAsync(AuditActions.Read, clinician.Id, source: AuditSources.OnPrem));
        _ = patient;

        var admin = await SignInAsync(apps.OnPrem, await apps.CreateStaffAsync("Code Search Admin", StaffRoles.Admin));
        using var asAdmin = await Client(apps.OnPrem, admin.Jar, followRedirects: false).GetAsync("/codes?kind=condition&q=essential", Cancel);
        Assert.Equal("/denied", new Uri(Base, asAdmin.Headers.Location!).AbsolutePath);
        using var anonymous = await CreateClient(apps.OnPrem).GetAsync("/codes?kind=condition&q=essential", Cancel);
        Assert.DoesNotContain("<option", await anonymous.Content.ReadAsStringAsync(Cancel));
    }

    // A sensitive diagnosis or medication isn't sent with the chart until the clinician asks; asking is recorded as its
    // own audit action; and it never appears in the banner. The history page follows the same rule.
    [Fact]
    public async Task Sensitive_items_are_left_out_until_asked_for_and_asking_is_recorded()
    {
        var (clinician, patient, session) = await OnPanelAsync(apps, "Sensitive");
        using var anxiety = await SaveAsync(session.Client, patient, "diagnoses", null, ("Name", "Generalized anxiety disorder"), ("Status", "active"));
        using var hypertension = await SaveAsync(session.Client, patient, "diagnoses", null, ("Name", "Essential (primary) hypertension"), ("Status", "active"));
        using var sertraline = await SaveAsync(session.Client, patient, "medications", null, ("Name", "sertraline 50 MG Oral Tablet"), ("Status", "active"), ("Sensitive", "true"));
        // The database marked the F-code diagnosis sensitive, though the form didn't; the medication is as the clinician marked it.
        Assert.Equal(["Essential (primary) hypertension false", "Generalized anxiety disorder true"],
            await RowsAsync(apps, "conditions", patient, "display || ' ' || sensitive"));
        Assert.Equal(["sertraline 50 MG Oral Tablet true"], await RowsAsync(apps, "medication_statements", patient, "display || ' ' || sensitive"));
        var shownBefore = await apps.AuditEventsAsync(AuditActions.ReadSensitive, clinician.Id, patient);

        var chart = await session.Client.GetStringAsync($"/patients/{patient}", Cancel);
        Assert.Contains("Essential (primary) hypertension", chart);
        Assert.DoesNotContain("anxiety", chart, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sertraline", chart, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Sensitive items hidden. 2 diagnoses or medications are not shown.", Words(chart));
        Assert.Contains($"href=\"/patients/{patient}?sensitive=true\">Show sensitive items</a>", chart);
        var history = await session.Client.GetStringAsync($"/patients/{patient}/chart-history", Cancel);
        Assert.DoesNotContain("anxiety", history, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Sensitive items hidden. 2 changes are not shown.", Words(history));
        Assert.Equal(shownBefore, await apps.AuditEventsAsync(AuditActions.ReadSensitive, clinician.Id, patient));

        var shown = await session.Client.GetStringAsync($"/patients/{patient}?sensitive=true", Cancel);
        Assert.Contains("<strong>Generalized anxiety disorder</strong>", shown);
        Assert.Contains("<strong>sertraline 50 MG Oral Tablet</strong>", shown);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(shown, "<span class=\"sc-tag sc-tag--warning\">Sensitive</span>").Count);
        Assert.Contains("Sensitive items shown. Your view is logged.", Words(shown));
        Assert.Equal(shownBefore + 1, await apps.AuditEventsAsync(AuditActions.ReadSensitive, clinician.Id, patient));
        Assert.Contains("Generalized anxiety disorder", await session.Client.GetStringAsync($"/patients/{patient}/chart-history?sensitive=true", Cancel));
        Assert.Equal(shownBefore + 2, await apps.AuditEventsAsync(AuditActions.ReadSensitive, clinician.Id, patient));

        // Opening a sensitive entry's form shows it, so that is recorded too.
        var entry = Guid.Parse((await RowsAsync(apps, "conditions", patient, "id::text", "display DESC")).First());
        Assert.Contains("value=\"Generalized anxiety disorder\"", await session.Client.GetStringAsync(EntryPath(patient, "diagnoses", entry), Cancel));
        Assert.Equal(shownBefore + 3, await apps.AuditEventsAsync(AuditActions.ReadSensitive, clinician.Id, patient));

        // Off-site, from the cloud copy, asking is recorded in the cloud app's audit queue and arrives on-site.
        var cloud = Client(apps.Cloud, session.Jar);
        Assert.Contains("<strong>Generalized anxiety disorder</strong>", await cloud.GetStringAsync($"/patients/{patient}?sensitive=true", Cancel));
        Assert.True(await apps.Cloud.Services.GetRequiredService<Ehr.Web.CloudAudit.CloudAuditShipper>().ShipAsync(Cancel));
        Assert.Equal(1, await apps.AuditEventsAsync(AuditActions.ReadSensitive, clinician.Id, patient, AuditSources.Cloud));
    }

    // An entry marked sensitive after it was added is sensitive throughout its history: the history page leaves out
    // every change to it, the ones made before it was marked included. And the desktop app's offline copy never holds
    // sensitive items: it is told how many it wasn't given.
    [Fact]
    public async Task An_entry_marked_sensitive_later_leaves_the_history_and_the_offline_copy()
    {
        var clinician = await apps.CreateStaffAsync("Dr. Later", StaffRoles.Clinician);
        var patient = await apps.InsertPatientAsync("Synthetic", "Marked later");
        await apps.AssignAsync(clinician, patient);
        var session = await SignInAsync(apps.OnPrem, clinician);
        using var added = await SaveAsync(session.Client, patient, "medications", null, ("Name", "sertraline 50 MG Oral Tablet"), ("Status", "active"));
        using var other = await SaveAsync(session.Client, patient, "medications", null, ("Name", "lisinopril 10 MG Oral Tablet"), ("Status", "active"));
        using var anxiety = await SaveAsync(session.Client, patient, "diagnoses", null, ("Name", "Generalized anxiety disorder"), ("Status", "active"));
        Assert.Contains("sertraline", await session.Client.GetStringAsync($"/patients/{patient}/chart-history", Cancel));
        var entry = Guid.Parse((await RowsAsync(apps, "medication_statements", patient, "id::text", "display DESC")).First());

        using var marked = await SaveAsync(session.Client, patient, "medications", entry, ("Name", "sertraline 50 MG Oral Tablet"), ("Status", "active"), ("Sensitive", "true"));

        var before = await apps.AuditEventsAsync(AuditActions.ReadSensitive, clinician.Id, patient);
        var history = await session.Client.GetStringAsync($"/patients/{patient}/chart-history", Cancel);
        Assert.DoesNotContain("sertraline", history, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("anxiety", history, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("lisinopril", history);
        // The sertraline's two changes and the diagnosis's one.
        Assert.Contains("Sensitive items hidden. 3 changes are not shown.", Words(history));
        Assert.Equal(before, await apps.AuditEventsAsync(AuditActions.ReadSensitive, clinician.Id, patient));
        var shown = Words(await session.Client.GetStringAsync($"/patients/{patient}/chart-history?sensitive=true", Cancel));
        Assert.Contains("Sensitive: no → yes", shown);
        Assert.Equal(before + 1, await apps.AuditEventsAsync(AuditActions.ReadSensitive, clinician.Id, patient));

        var token = await apps.DesktopSignInAsync(apps.OnPrem, clinician);
        using var offline = await DesktopSignInTests.PostAsync(apps.OnPrem, "/api/desktop/offline-panel", token);
        var copy = await offline.Content.ReadAsStringAsync(Cancel);
        Assert.Contains("lisinopril 10 MG Oral Tablet", copy);
        Assert.DoesNotContain("sertraline", copy, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("anxiety", copy, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"sensitiveNotKept\":2", copy);
        Assert.Equal(before + 1, await apps.AuditEventsAsync(AuditActions.ReadSensitive, clinician.Id, patient));
    }

    // The banner shows the patient's allergies on the note and dictation pages too, within the read each already records.
    [Fact]
    public async Task The_banner_shows_allergies_on_the_note_and_dictation_pages()
    {
        var (clinician, patient, session) = await OnPanelAsync(apps, "Bannered");
        using var saved = await SaveAsync(session.Client, patient, "allergies", null, ("Name", "Peanut"), ("Reaction", "Anaphylaxis"), ("Criticality", "high"),
            ("Severity", "severe"), ("Status", "active"));
        Assert.Equal(["Peanut QE1QX6B99R unii food high severe"],
            await RowsAsync(apps, "allergy_intolerances", patient, "concat_ws(' ', display, code, code_system, category, criticality, severity)"));
        var reads = await apps.AuditEventsAsync(AuditActions.Read, clinician.Id, patient);

        var dictate = await session.Client.GetStringAsync($"/patients/{patient}/dictate", Cancel);

        Assert.Contains("<strong>Peanut</strong>: Anaphylaxis", dictate);
        Assert.Equal(reads + 1, await apps.AuditEventsAsync(AuditActions.Read, clinician.Id, patient));
        using var created = await PostFormAsync(session.Client, $"/patients/{patient}?handler=NewNote", $"/patients/{patient}");
        Assert.Contains("<strong>Peanut</strong>: Anaphylaxis", await ReadAsync(created));
    }

    // After a save the chart says what happened: the entry named and where it is listed; an entry given a status that
    // takes it off the chart, and why it isn't listed; and a sensitive entry the chart leaves out, without its name.
    [Fact]
    public async Task After_a_save_the_chart_says_what_was_saved_without_naming_a_hidden_sensitive_entry()
    {
        var (_, patient, session) = await OnPanelAsync(apps, "Confirmed");

        using var added = await SaveAsync(session.Client, patient, "diagnoses", null, ("Name", "Essential (primary) hypertension"), ("Status", "active"));
        var chart = await ReadAsync(added);
        Assert.Equal($"/patients/{patient}", PathOf(added));
        Assert.Contains("Diagnosis saved.", Words(chart));
        Assert.Contains("Essential (primary) hypertension", chart);

        using var medication = await SaveAsync(session.Client, patient, "medications", null, ("Name", "lisinopril 10 MG Oral Tablet"), ("Status", "active"));
        var entry = await IdAsync(apps, "medication_statements", patient);
        using var stopped = await SaveAsync(session.Client, patient, "medications", entry, ("Name", "lisinopril 10 MG Oral Tablet"), ("Status", "stopped"));
        Assert.Contains("Medication saved. lisinopril 10 MG Oral Tablet is now stopped.", Words(await ReadAsync(stopped)));

        using var anxiety = await SaveAsync(session.Client, patient, "diagnoses", null, ("Name", "Generalized anxiety disorder"), ("Status", "active"));
        var hidden = await ReadAsync(anxiety);
        Assert.Contains("Diagnosis saved. Hidden as sensitive.", Words(hidden));
        Assert.DoesNotContain("anxiety", hidden, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Sensitive items hidden. 1 diagnosis or medication is not shown.", Words(hidden));

        // An address naming another patient's entry, or none that exists, confirms nothing.
        var other = await apps.InsertPatientAsync("Synthetic", "Not confirmed");
        await apps.AssignAsync(await apps.ClinicianAsync(), other);
        var elsewhere = await session.Client.GetStringAsync($"/patients/{other}?saved={entry}&savedList=medications", Cancel);
        Assert.DoesNotContain("data-saved", elsewhere);
        Assert.DoesNotContain("lisinopril", elsewhere);
    }

    // When the save can't happen, the clinician is told, and what they typed isn't lost. A patient taken off the panel
    // between opening the form and saving it: a page that says so, with the typed name to copy, and nothing about the
    // patient on it, since nothing about them may be read now.
    [Fact]
    public async Task A_save_for_a_patient_no_longer_on_the_panel_says_it_was_not_saved_and_keeps_what_was_typed()
    {
        var (clinician, patient, session) = await OnPanelAsync(apps, "Left the panel");
        using var form = await session.Client.GetAsync(EntryPath(patient, "diagnoses"), Cancel);
        await using (var db = await apps.OpenDatabaseAsync())
        {
            await using var end = new NpgsqlCommand("UPDATE ehr.patient_assignments SET ended_at = now() WHERE patient_id = @patient AND clinician_id = @clinician", db);
            end.Parameters.AddWithValue("patient", patient);
            end.Parameters.AddWithValue("clinician", clinician.Id);
            Assert.Equal(1, await end.ExecuteNonQueryAsync(Cancel));
        }

        var reads = await apps.AuditEventsAsync(AuditActions.Read, clinician.Id, patient);
        using var posted = await PostFormAsync(session.Client, EntryPath(patient, "diagnoses"), form, ("Name", "Essential (primary) hypertension"), ("Status", "active"));

        Assert.Equal(HttpStatusCode.Conflict, posted.StatusCode);
        var page = await posted.Content.ReadAsStringAsync(Cancel);
        Assert.Contains("Not saved. This isn't one of your patients.", Words(page));
        Assert.Contains("Essential (primary) hypertension</textarea>", page);
        Assert.DoesNotContain("Left the panel", page);
        Assert.DoesNotContain("sc-banner", page);
        Assert.Equal(reads, await apps.AuditEventsAsync(AuditActions.Read, clinician.Id, patient));
        Assert.Empty(await RowsAsync(apps, "conditions", patient, "display"));
    }

    // "No known allergies" posted from a chart opened before a colleague recorded an allergy: the database refuses it,
    // and the chart says so, with the allergy listed.
    [Fact]
    public async Task No_known_allergies_after_a_colleague_recorded_an_allergy_is_not_saved_and_the_chart_says_why()
    {
        var (_, patient, session) = await OnPanelAsync(apps, "Colleague recorded");
        using var chart = await session.Client.GetAsync($"/patients/{patient}", Cancel);
        var colleague = await apps.CreateStaffAsync("Dr. Colleague Allergy", StaffRoles.Clinician);
        await apps.AssignAsync(colleague, patient);
        var theirs = await SignInAsync(apps.OnPrem, colleague);
        using var peanut = await SaveAsync(theirs.Client, patient, "allergies", null, ("Name", "Peanut"), ("Reaction", "Hives"), ("Status", "active"));

        using var refused = await PostFormAsync(session.Client, $"/patients/{patient}?handler=NoKnownAllergies", chart);
        var page = Words(await ReadAsync(refused));

        Assert.Contains("Not saved. An allergy is already recorded.", page);
        Assert.Equal(["Peanut active"], await RowsAsync(apps, "allergy_intolerances", patient, "display || ' ' || clinical_status"));

        // The notice can't be made to claim what isn't so: with an allergy listed, "saved" says nothing.
        Assert.DoesNotContain("Saved. No known allergies.", Words(await session.Client.GetStringAsync($"/patients/{patient}?noKnownAllergies=saved", Cancel)));
    }

    // "Record no known allergies" pressed twice, or from two tabs, records one: the second finds it recorded already,
    // and the chart confirms it the same way.
    [Fact]
    public async Task No_known_allergies_posted_twice_is_recorded_once()
    {
        var (_, patient, session) = await OnPanelAsync(apps, "Double click");
        using var chart = await session.Client.GetAsync($"/patients/{patient}", Cancel);
        var token = Token(await chart.Content.ReadAsStringAsync(Cancel));

        foreach (var _ in Enumerable.Range(0, 2))
        {
            using var posted = await session.Client.PostAsync($"/patients/{patient}?handler=NoKnownAllergies", Form(("__RequestVerificationToken", token)), Cancel);
            Assert.Contains("Saved. No known allergies.", Words(await ReadAsync(posted)));
        }

        Assert.Equal(["no-known-allergy active"], await RowsAsync(apps, "allergy_intolerances", patient, "code || ' ' || clinical_status"));
    }

    // Who recorded "No known allergies" is read in the chart's patient statement: about a third of the seeded charts
    // say "No known allergies", and the chart is the page nearest the render gate, so it costs no statement of its own
    // (step 7a; VisitSummaryTests holds the same for the summary tag).
    [Fact]
    public async Task Who_recorded_no_known_allergies_costs_the_chart_no_statement_of_its_own()
    {
        var (clinician, patient, session) = await OnPanelAsync(apps, "Recorded by");
        async Task<(int Commands, PatientChart Chart)> ChartAsync()
        {
            var commands = new PatientSearchQueryTests.CapturedCommands();
            var user = new UserContext { UserId = clinician.Id };
            var options = new DbContextOptionsBuilder<EhrDbContext>();
            EhrDbContext.Configure(options, apps.Database.ConnectionStringFor("ehr_app", MigratedDatabase.AppPassword));
            options.AddInterceptors(new UserContextInterceptor(user), commands);
            await using var db = new EhrDbContext(options.Options);
            var reader = new PhiReader(db, new AuditRecorder(db, user, new AuditSource(AuditSources.OnPrem)), user);
            await using var transaction = await db.Database.BeginTransactionAsync(Cancel);
            var chart = (await reader.ChartAsync(patient, 5, AuditParts.Chart, Cancel))!;
            await transaction.RollbackAsync(Cancel);
            return (commands.Executed.Count, chart);
        }

        var (unasked, nothingRecorded) = await ChartAsync();
        Assert.Null(nothingRecorded.NoKnownAllergiesRecordedBy);
        using var recorded = await PostFormAsync(session.Client, $"/patients/{patient}?handler=NoKnownAllergies", $"/patients/{patient}");
        Assert.Contains("Saved. No known allergies.", Words(await ReadAsync(recorded)));

        var (noneKnown, chart) = await ChartAsync();

        Assert.Equal("Dr. Test", chart.NoKnownAllergiesRecordedBy);
        Assert.Equal(unasked, noneKnown);
    }

    // A diagnosis sensitive by its code has its "Sensitive" box checked and fixed, with a hint that says why, and
    // stays sensitive whatever is posted; any other diagnosis's box can be checked and cleared.
    [Fact]
    public async Task A_diagnosis_sensitive_by_its_code_has_its_sensitive_box_fixed()
    {
        var (_, patient, session) = await OnPanelAsync(apps, "Fixed box");
        using var anxiety = await SaveAsync(session.Client, patient, "diagnoses", null, ("Name", "Generalized anxiety disorder"), ("Status", "active"));
        using var hypertension = await SaveAsync(session.Client, patient, "diagnoses", null, ("Name", "Essential (primary) hypertension"), ("Status", "active"));
        var ids = (await RowsAsync(apps, "conditions", patient, "id::text", "display")).Select(Guid.Parse).ToList();
        var (anxietyId, hypertensionId) = (ids[1], ids[0]);
        const string fixedBox = "<input type=\"checkbox\" id=\"entry-sensitive\" checked disabled aria-describedby=\"entry-sensitive-hint\">";

        var byCode = await session.Client.GetStringAsync(EntryPath(patient, "diagnoses", anxietyId), Cancel);
        Assert.Contains(fixedBox, byCode);
        Assert.Contains("Always sensitive for this code.", Words(byCode));
        var other = await session.Client.GetStringAsync(EntryPath(patient, "diagnoses", hypertensionId), Cancel);
        Assert.DoesNotContain("disabled", other);
        Assert.Contains("name=\"Sensitive\" value=\"true\"", other);

        // Posted without the box, as a disabled box is: still sensitive. And a form shown again after a mistake keeps it fixed.
        using var changed = await SaveAsync(session.Client, patient, "diagnoses", anxietyId, ("Name", "Generalized anxiety disorder"), ("Status", "remission"));
        Assert.Equal(["Essential (primary) hypertension false", "Generalized anxiety disorder true"],
            await RowsAsync(apps, "conditions", patient, "display || ' ' || sensitive"));
        using var mistake = await SaveAsync(session.Client, patient, "diagnoses", null, ("Name", "Generalized anxiety disorder"), ("Status", "active"), ("Onset", "2999-01-01"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, mistake.StatusCode);
        Assert.Contains(fixedBox, await mistake.Content.ReadAsStringAsync(Cancel));
    }

    // The form comes back with the cursor in the first field to fix, in the form's order, whichever it is; and the
    // chart's notice after a save is the one thing marked to take the focus.
    [Fact]
    public async Task The_first_field_with_an_error_takes_the_focus()
    {
        var (_, patient, session) = await OnPanelAsync(apps, "Focused");
        static string Focused(string html) =>
            string.Join(" ", System.Text.RegularExpressions.Regex.Matches(html, "<(?:input|select)[^>]*\\sautofocus=\"autofocus\"[^>]*\\sid=\"([^\"]+)\"|<(?:input|select)[^>]*\\sid=\"([^\"]+)\"[^>]*\\sautofocus=\"autofocus\"")
                .Select(match => match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value));

        using var onset = await SaveAsync(session.Client, patient, "diagnoses", null, ("Name", "Essential (primary) hypertension"), ("Status", "active"), ("Onset", "2999-01-01"));
        Assert.Equal("entry-onset", Focused(await onset.Content.ReadAsStringAsync(Cancel)));
        using var both = await SaveAsync(session.Client, patient, "medications", null, ("Name", "lisinopril 10 MG Oral Tablet"), ("Status", "deleted"), ("Started", "2024-02-30"));
        Assert.Equal("entry-started", Focused(await both.Content.ReadAsStringAsync(Cancel)));
        using var allergy = await SaveAsync(session.Client, patient, "allergies", null, ("Name", "Peanut"), ("Status", "active"), ("Criticality", "extreme"));
        Assert.Equal("entry-criticality", Focused(await allergy.Content.ReadAsStringAsync(Cancel)));
        Assert.Equal("entry-name", Focused(await session.Client.GetStringAsync(EntryPath(patient, "diagnoses"), Cancel)));

        using var saved = await SaveAsync(session.Client, patient, "diagnoses", null, ("Name", "Essential (primary) hypertension"), ("Status", "active"));
        var chart = await ReadAsync(saved);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(chart, "data-focus-on-load"));
        Assert.Matches("<p class=\"sc-notice sc-notice--success\" role=\"status\" tabindex=\"-1\" data-focus-on-load data-saved>", chart);
    }

    // An "Add" form posted twice (a double click, or Back and post again) saves one entry: its ID came with the form,
    // so the second post updates the first's row. Two posts at the same moment do too. And an ID that is another
    // patient's entry's can't be used to change that entry from this patient's form.
    [Fact]
    public async Task An_add_form_posted_twice_saves_one_entry()
    {
        var (clinician, patient, session) = await OnPanelAsync(apps, "Posted twice");
        using var page = await session.Client.GetAsync(EntryPath(patient, "allergies"), Cancel);
        var html = await page.Content.ReadAsStringAsync(Cancel);
        var newId = Guid.Parse(System.Text.RegularExpressions.Regex.Match(html, "name=\"NewEntryId\" value=\"([^\"]+)\"").Groups[1].Value);
        var token = Token(html);
        Task<HttpResponseMessage> PostAsync(string reaction) => session.Client.PostAsync(EntryPath(patient, "allergies"),
            Form(("__RequestVerificationToken", token), ("NewEntryId", newId), ("Name", "Peanut"), ("Reaction", reaction), ("Status", "active")), Cancel);

        using (var first = await PostAsync("Hives"))
        using (var second = await PostAsync("Rash"))
        {
            var twice = await ReadAsync(second);
            Assert.Contains("Allergy saved.", Words(twice));
            Assert.Contains("<strong>Peanut</strong>", twice);
        }

        Assert.Equal([$"{newId} Peanut Rash"], await RowsAsync(apps, "allergy_intolerances", patient, "concat_ws(' ', id, display, reaction)"));
        // At once: still one.
        var other = await apps.InsertPatientAsync("Synthetic", "Posted at once");
        await apps.AssignAsync(clinician, other);
        var form = await session.Client.GetStringAsync(EntryPath(other, "diagnoses"), Cancel);
        var otherId = System.Text.RegularExpressions.Regex.Match(form, "name=\"NewEntryId\" value=\"([^\"]+)\"").Groups[1].Value;
        var posts = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => session.Client.PostAsync(EntryPath(other, "diagnoses"),
            Form(("__RequestVerificationToken", Token(form)), ("NewEntryId", otherId), ("Name", "Essential (primary) hypertension"), ("Status", "active")), Cancel)));
        Assert.All(posts, posted => Assert.Equal(HttpStatusCode.OK, posted.StatusCode));
        Assert.Equal(["Essential (primary) hypertension"], await RowsAsync(apps, "conditions", other, "display"));

        // Another patient's entry's ID, posted from this patient's form: refused, and that entry is unchanged.
        using var crossed = await session.Client.PostAsync(EntryPath(other, "allergies"),
            Form(("__RequestVerificationToken", token), ("NewEntryId", newId), ("Name", "Penicillin G"), ("Status", "active")), Cancel);
        Assert.Equal(HttpStatusCode.Conflict, crossed.StatusCode);
        Assert.Contains("Not saved. The entry changed, or this isn't one of your patients.", Words(await crossed.Content.ReadAsStringAsync(Cancel)));
        Assert.Equal([$"{newId} Peanut Rash"], await RowsAsync(apps, "allergy_intolerances", patient, "concat_ws(' ', id, display, reaction)"));
        Assert.Empty(await RowsAsync(apps, "allergy_intolerances", other, "display"));
    }

    // With sensitive items shown, the way from the chart to the history or a form, and back, keeps them shown: each
    // such view is recorded as one, as asked for. Without, nothing adds them.
    [Fact]
    public async Task Sensitive_items_shown_stay_shown_through_the_history_and_the_entry_form()
    {
        var (_, patient, session) = await OnPanelAsync(apps, "Kept shown");
        using var anxiety = await SaveAsync(session.Client, patient, "diagnoses", null, ("Name", "Generalized anxiety disorder"), ("Status", "active"));
        var entry = await IdAsync(apps, "conditions", patient);

        var shown = await session.Client.GetStringAsync($"/patients/{patient}?sensitive=true", Cancel);
        Assert.Contains($"href=\"/patients/{patient}/chart-history?sensitive=true\"", shown);
        Assert.Contains($"href=\"{EntryPath(patient, "diagnoses", entry)}?showSensitive=true\"", shown);
        Assert.Contains($"href=\"{EntryPath(patient, "medications")}?showSensitive=true\"", shown);
        var history = await session.Client.GetStringAsync($"/patients/{patient}/chart-history?sensitive=true", Cancel);
        Assert.Contains($"href=\"/patients/{patient}?sensitive=true\">Back to chart", history);
        var form = await session.Client.GetStringAsync($"{EntryPath(patient, "diagnoses", entry)}?showSensitive=true", Cancel);
        Assert.Contains($"href=\"/patients/{patient}?sensitive=true\">Back to chart", form);

        var path = $"{EntryPath(patient, "diagnoses", entry)}?showSensitive=true";
        using var saved = await PostFormAsync(session.Client, path, path, ("Name", "Generalized anxiety disorder"), ("Status", "active"), ("Onset", "2024-01-02"));
        Assert.Contains("sensitive=true", saved.RequestMessage!.RequestUri!.Query);
        var back = await ReadAsync(saved);
        Assert.Contains("Diagnosis saved.", Words(back));
        Assert.Contains("Generalized anxiety disorder", back);
        Assert.Contains("Sensitive items shown.", Words(back));

        // Hidden, nothing carries them.
        var hidden = await session.Client.GetStringAsync($"/patients/{patient}", Cancel);
        Assert.DoesNotContain("showSensitive", hidden);
        Assert.Contains($"href=\"/patients/{patient}/chart-history\"", hidden);
    }

    // The history and the discontinued orders show the banner with the patient's allergies, read in the page's one
    // recorded read, and, under emergency access, its tag with the time it ends.
    [Fact]
    public async Task The_history_and_discontinued_orders_show_the_allergy_banner_and_emergency_access()
    {
        var (clinician, patient, session) = await OnPanelAsync(apps, "Bannered pages");
        using var peanut = await SaveAsync(session.Client, patient, "allergies", null, ("Name", "Peanut"), ("Reaction", "Hives"), ("Criticality", "high"), ("Status", "active"));

        foreach (var path in new[] { $"/patients/{patient}/chart-history", $"/patients/{patient}/orders/discontinued" })
        {
            var reads = await apps.AuditEventsAsync(AuditActions.Read, clinician.Id, patient);
            var page = await session.Client.GetStringAsync(path, Cancel);
            Assert.Matches("<section class=\"sc-banner\"[\\s\\S]*High risk: <strong>Peanut</strong>: Hives[\\s\\S]*</section>", page);
            Assert.Contains("<span class=\"sc-tag\">Yours</span>", page);
            Assert.Equal(reads + 1, await apps.AuditEventsAsync(AuditActions.Read, clinician.Id, patient));
        }

        var covering = await SignInAsync(apps.OnPrem, await apps.CreateStaffAsync("Dr. Covering Banner", StaffRoles.Clinician));
        using var opened = await PostFormAsync(covering.Client, $"/patients/{patient}/reason", $"/patients/{patient}/reason", ("Reason", "Covering the night shift"));
        foreach (var path in new[] { $"/patients/{patient}/chart-history", $"/patients/{patient}/orders/discontinued" })
        {
            var page = Words(await covering.Client.GetStringAsync(path, Cancel));
            Assert.Contains("Break-the-glass access until", page);
            Assert.Contains("High risk: Peanut: Hives", page);
        }
    }

    // A diagnosis can be saved by its ICD-10-CM code, typed in any case and with or without the dot, as the form's
    // hint offers: it saves that code's diagnosis. Something code-like that isn't one is still refused.
    [Fact]
    public async Task A_diagnosis_can_be_saved_by_its_typed_code()
    {
        var (_, patient, session) = await OnPanelAsync(apps, "Typed code");

        using var byCode = await SaveAsync(session.Client, patient, "diagnoses", null, ("Name", " i10 "), ("Status", "active"));
        using var noDot = await SaveAsync(session.Client, patient, "diagnoses", null, ("Name", "e119"), ("Status", "active"));
        var noDotChart = await ReadAsync(noDot);
        Assert.Contains("Diagnosis saved.", Words(noDotChart));
        Assert.Contains("Type 2 diabetes mellitus without complications", noDotChart);
        using var notACode = await SaveAsync(session.Client, patient, "diagnoses", null, ("Name", "Z99.999"), ("Status", "active"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, notACode.StatusCode);
        // Codes are for diagnoses only: a medication's isn't looked up that way.
        using var medication = await SaveAsync(session.Client, patient, "medications", null, ("Name", "314076"), ("Status", "active"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, medication.StatusCode);

        Assert.Equal(["Essential (primary) hypertension I10", "Type 2 diabetes mellitus without complications E11.9"],
            await RowsAsync(apps, "conditions", patient, "concat_ws(' ', display, code)"));
    }

    // The entry form is never kept by the browser, opened to add or to change, and a sensitive entry's form included:
    // the page says so itself, whatever else on it would.
    [Fact]
    public async Task The_entry_form_is_never_cached()
    {
        var (_, patient, session) = await OnPanelAsync(apps, "Uncached");
        using var added = await SaveAsync(session.Client, patient, "diagnoses", null, ("Name", "Generalized anxiety disorder"), ("Status", "active"));
        var entry = await IdAsync(apps, "conditions", patient);

        foreach (var path in new[] { EntryPath(patient, "diagnoses"), EntryPath(patient, "medications"), EntryPath(patient, "allergies"), EntryPath(patient, "diagnoses", entry) })
        {
            using var form = await session.Client.GetAsync(path, Cancel);
            Assert.Equal(HttpStatusCode.OK, form.StatusCode);
            Assert.True(form.Headers.CacheControl?.NoStore, $"{path} may be cached: {form.Headers.CacheControl}");
        }
    }

    // The search the clinician came from travels through the form and back to the chart, and the entry's own name
    // never becomes a search criterion: it would put the name, a sensitive diagnosis's too, in the chart's address.
    [Fact]
    public async Task Saving_an_entry_keeps_the_search_and_never_puts_the_entrys_name_in_the_address()
    {
        var (_, patient, session) = await OnPanelAsync(apps, "Addressed");
        var form = $"{EntryPath(patient, "diagnoses")}?name=Addressed";

        using var saved = await PostFormAsync(session.Client, form, form, ("Name", "Generalized anxiety disorder"), ("Status", "active"));

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var address = saved.RequestMessage!.RequestUri!;
        Assert.Equal($"/patients/{patient}", address.AbsolutePath);
        Assert.Contains("name=Addressed", address.Query);
        Assert.DoesNotContain("anxiety", Uri.UnescapeDataString(address.Query), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("anxiety", await saved.Content.ReadAsStringAsync(Cancel), StringComparison.OrdinalIgnoreCase);

        // Without a search, nothing at all is added for one.
        using var plain = await SaveAsync(session.Client, patient, "diagnoses", null, ("Name", "Essential (primary) hypertension"), ("Status", "active"));
        Assert.DoesNotContain("name=", plain.RequestMessage!.RequestUri!.Query);
    }

    // Off-site, a saved entry is forwarded to the on-site server; the history is read from the cloud copy.
    [Fact]
    public async Task Off_site_a_saved_entry_is_forwarded_and_saved_on_site()
    {
        var (_, patient, session) = await OnPanelAsync(apps, "Forwarded");
        var cloud = Client(apps.Cloud, session.Jar);

        using var saved = await SaveAsync(cloud, patient, "medications", null, ("Name", "Lisinopril 10 mg oral tablet"), ("Dosage", "1 tablet by mouth daily"), ("Status", "active"));

        Assert.Contains($"POST {EntryPath(patient, "medications")}", apps.Forwarded);
        // The name is the reference code's own, whatever case it was typed in.
        Assert.Equal(["lisinopril 10 MG Oral Tablet 314076 1 tablet by mouth daily"],
            await RowsAsync(apps, "medication_statements", patient, "concat_ws(' ', display, code, dosage)"));
    }
}
