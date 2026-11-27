using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Ehr.Data.Accounts;
using Ehr.Data.Audit;
using Ehr.Web.Api.Audit;
using Npgsql;
using Xunit;
using static Ehr.Web.Tests.Browser;

namespace Ehr.Web.Tests;

// When a page fails (ErrorPages): the search and the chart say so in fixed words with a way back, and nothing about the
// failure or the patient, as does a form refused for its token; the APIs and htmx requests keep their bare statuses; in
// Development the developer exception page stays. A database error is made by revoking a privilege from the app's role. Only this class's own database sees
// that: a class fixture is the class's alone, xunit runs a class's tests one at a time, and each test grants the
// privilege back before it ends.
public class ErrorPageTests(WebApps apps) : IClassFixture<WebApps>
{
    [Fact]
    public async Task A_database_error_says_the_search_could_not_be_completed()
    {
        var clinician = await apps.CreateStaffAsync("Dr. Unlucky", StaffRoles.Clinician);
        var session = await SignInAsync(apps.OnPremProduction, clinician);

        await RevokedAsync("patients", async () =>
        {
            using var response = await session.Client.GetAsync("/patients?name=Ada&birthdate=1980-01-01", Cancel);

            var html = await response.Content.ReadAsStringAsync(Cancel);
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            Assert.Contains("<h1>Search not completed</h1>", html);
            Assert.Contains("Try again in a moment.", html);
            Assert.Contains("<a href=\"/patients\">Back to patients</a>", html);
            AssertNothingAbout(html, "Ada", "1980-01-01");
        });
    }

    [Fact]
    public async Task A_database_error_says_the_chart_could_not_be_loaded_and_records_no_read()
    {
        var clinician = await apps.CreateStaffAsync("Dr. Unlucky", StaffRoles.Clinician);
        var patient = await apps.InsertPatientAsync("Ada", "Failing");
        await apps.AssignAsync(clinician, patient);
        var session = await SignInAsync(apps.OnPremProduction, clinician);
        // Signing in lands on the panel, which lists the patient: one read already.
        var before = await apps.AuditEventsAsync(AuditActions.Read, clinician.Id, patient);

        await RevokedAsync("encounters", async () =>
        {
            using var response = await session.Client.GetAsync($"/patients/{patient}?name=Failing", Cancel);

            var html = await response.Content.ReadAsStringAsync(Cancel);
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            Assert.Contains("<h1>Chart not loaded</h1>", html);
            Assert.Contains("Try again in a moment.", html);
            Assert.Contains("<a href=\"/patients\">Back to patients</a>", html);
            AssertNothingAbout(html, "Failing", patient.ToString());
        });

        // The read failed with the rest of the page's transaction.
        Assert.Equal(before, await apps.AuditEventsAsync(AuditActions.Read, clinician.Id, patient));
    }

    // A change that fails may still have been saved, such as when the transaction commits and what follows fails, so
    // the page says it may not have been.
    [Fact]
    public async Task A_change_that_fails_says_it_may_not_have_been_saved()
    {
        var clinician = await apps.CreateStaffAsync("Dr. Unsaved", StaffRoles.Clinician);
        var patient = await apps.InsertPatientAsync("Ada", "Unsaved");
        await apps.AssignAsync(clinician, patient);
        var session = await SignInAsync(apps.OnPremProduction, clinician);

        await RevokedAsync("UPDATE", "patient_assignments", async () =>
        {
            using var response = await PostFormAsync(session.Client, $"/patients/{patient}?handler=Unassign", $"/patients/{patient}");

            var html = await response.Content.ReadAsStringAsync(Cancel);
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            Assert.Contains("Your change may not have been saved. Check before trying again.", html);
            Assert.DoesNotContain("loaded", html);
            Assert.Contains("<a href=\"/patients\">Back to patients</a>", html);
            AssertNothingAbout(html, "Unsaved", patient.ToString());
        });
    }

    // A chart that doesn't exist asks for a reason first, like any chart off the panel, and then says nothing matches
    // the address. In Development too: only failures keep the developer exception page.
    [Fact]
    public async Task A_chart_that_does_not_exist_says_it_could_not_be_loaded()
    {
        var session = await SignInAsync(apps.OnPrem, await apps.ClinicianAsync());
        var nobody = Guid.CreateVersion7();

        using var response = await PostFormAsync(session.Client, $"/patients/{nobody}/reason", $"/patients/{nobody}", ("Reason", "Covering the night shift"));

        var html = await response.Content.ReadAsStringAsync(Cancel);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("No patient or chart item matches this address.", html);
        Assert.Contains("<a href=\"/patients\">Back to patients</a>", html);
    }

    // A note that doesn't exist, in a chart that does, gets the same page, which says nothing to suggest the patient is
    // missing.
    [Fact]
    public async Task A_note_that_does_not_exist_in_a_real_chart_is_not_called_a_missing_patient()
    {
        var clinician = await apps.ClinicianAsync();
        var patient = await apps.InsertPatientAsync("Synthetic", "Nonote");
        await apps.AssignAsync(clinician, patient);
        var session = await SignInAsync(apps.OnPrem, clinician);

        using var response = await session.Client.GetAsync($"/patients/{patient}/notes/{Guid.CreateVersion7()}", Cancel);

        var html = await response.Content.ReadAsStringAsync(Cancel);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("No patient or chart item matches this address.", html);
        Assert.Contains("<a href=\"/patients\">Back to patients</a>", html);
    }

    // A form opened while one account was signed in and sent after another signed in, in this browser: its token is
    // refused before the handler runs, and the page says so with a way back, rather than nothing. htmx's refusal stays
    // bare, for request-errors.js.
    [Fact]
    public async Task A_form_sent_after_someone_else_signed_in_says_it_was_not_sent()
    {
        var first = await SignInAsync(apps.OnPrem, await apps.CreateStaffAsync("Dr. First Tab", StaffRoles.Clinician));
        var token = Token(await first.Client.GetStringAsync(QuickTexts, Cancel));
        var second = await SignInAsync(apps.OnPrem, await apps.CreateStaffAsync("Dr. Second Tab", StaffRoles.Clinician));
        var shortcut = WebApps.NewShortcut();

        using var refused = await second.Client.PostAsync($"{QuickTexts}?handler=Add", Form(("Shortcut", shortcut), ("Body", "Text"), ("__RequestVerificationToken", token)), Cancel);

        var html = await refused.Content.ReadAsStringAsync(Cancel);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("<title>Not sent</title>", html);
        Assert.Contains("Nothing was changed. Open the page again and try again.", html);
        Assert.Contains("<a href=\"/\">Back to home</a>", html);
        Assert.Empty(await apps.RowsAsync(shortcut));

        using var htmx = new HttpRequestMessage(HttpMethod.Post, $"{QuickTexts}?handler=Add")
        {
            Content = Form(("Shortcut", shortcut), ("Body", "Text"), ("__RequestVerificationToken", token)),
        };
        htmx.Headers.Add("HX-Request", "true");
        using var bare = await second.Client.SendAsync(htmx, Cancel);
        Assert.Equal(HttpStatusCode.BadRequest, bare.StatusCode);
        Assert.Equal("", await bare.Content.ReadAsStringAsync(Cancel));
    }

    [Fact]
    public async Task In_development_a_failing_page_still_shows_the_developer_exception_page()
    {
        var session = await SignInAsync(apps.OnPrem, await apps.CreateStaffAsync("Dr. Developer", StaffRoles.Clinician));

        await RevokedAsync("patients", async () =>
        {
            using var response = await session.Client.GetAsync("/patients?name=Ada", Cancel);

            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            Assert.DoesNotContain("Search not completed", await response.Content.ReadAsStringAsync(Cancel));
        });
    }

    // request-errors.js reads only the status, and says the results couldn't be loaded. A failure nothing handles is the
    // server's to answer with a bare 500, as before; the in-memory server hands the exception to the caller instead.
    [Fact]
    public async Task A_failing_htmx_request_is_left_bare()
    {
        var session = await SignInAsync(apps.OnPremProduction, await apps.CreateStaffAsync("Dr. Typist", StaffRoles.Clinician));

        await RevokedAsync("patients", async () =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/patients?handler=Results&name=Ada");
            request.Headers.Add("HX-Request", "true");

            await Assert.ThrowsAsync<PostgresException>(() => session.Client.SendAsync(request, Cancel));
        });
    }

    // What the desktop app and the cloud app's audit shipper read stays as it was: refusals keep their bare statuses and
    // empty bodies, and a failure inside an API is left to the server, as before.
    [Fact]
    public async Task Api_refusals_and_failures_are_left_bare()
    {
        using var client = CreateClient(apps.OnPremProduction, handleCookies: false);

        using var anonymous = await client.GetAsync("/api/sync/quick-texts?since=0", Cancel);
        using var nowhere = await client.GetAsync("/api/nothing-here", Cancel);
        using var noKey = await client.PostAsync(CloudAuditApi.Path, new StringContent("[]", Encoding.UTF8, "application/json"), Cancel);

        // Nothing anonymous gets past the fallback policy, a path that leads nowhere included: under /api, that is the
        // desktop token's bare 401.
        Assert.All([anonymous, nowhere, noKey], response => Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode));
        foreach (var response in new[] { anonymous, nowhere, noKey })
        {
            Assert.Equal("", await response.Content.ReadAsStringAsync(Cancel));
        }

        var token = await apps.DesktopSignInAsync(apps.OnPremProduction, await apps.CreateStaffAsync("Dr. Desktop", StaffRoles.Clinician));
        await RevokedAsync("quick_texts", async () =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/sync/quick-texts?since=0");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            await Assert.ThrowsAsync<PostgresException>(() => client.SendAsync(request, Cancel));
        });
    }

    // The page says nothing of the database, the exception, or what was asked for.
    static void AssertNothingAbout(string html, params string[] asked)
    {
        foreach (var detail in new[] { "Exception", "Npgsql", "Postgres", "permission denied", "SELECT " })
        {
            Assert.DoesNotContain(detail, html, StringComparison.OrdinalIgnoreCase);
        }

        foreach (var detail in asked)
        {
            Assert.DoesNotContain(detail, html);
        }
    }

    // Runs the action with SELECT on the table revoked from the on-prem app's role, and grants it back after.
    Task RevokedAsync(string table, Func<Task> action) => RevokedAsync("SELECT", table, action);

    async Task RevokedAsync(string privilege, string table, Func<Task> action)
    {
        await ExecuteAsync($"REVOKE {privilege} ON ehr.{table} FROM ehr_app");
        try
        {
            await action();
        }
        finally
        {
            await ExecuteAsync($"GRANT {privilege} ON ehr.{table} TO ehr_app");
        }
    }

    async Task ExecuteAsync(string sql)
    {
        await using var connection = await apps.OpenDatabaseAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(Cancel);
    }
}
