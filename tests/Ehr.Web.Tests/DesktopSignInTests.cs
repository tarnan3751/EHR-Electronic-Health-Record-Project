using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Ehr.Data.Accounts;
using Ehr.Data.Audit;
using Ehr.Testing;
using Ehr.Web.Api.Desktop;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;
using static Ehr.Web.Tests.Browser;

namespace Ehr.Web.Tests;

// The desktop app's sign-in (Api/Desktop, DesktopSessions, DesktopTokenHandler): both factors for a token, the token on
// the sync API on both servers, and the ways a session ends.
public class DesktopSignInTests(WebApps apps) : IClassFixture<WebApps>
{
    const string Sync = "/api/sync/quick-texts?since=0";

    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_clinician_signs_in_with_both_factors_and_syncs_on_both_servers()
    {
        var account = await apps.CreateStaffAsync("Dr. Desktop", StaffRoles.Clinician);

        var token = await apps.DesktopSignInAsync(apps.OnPrem, account);

        using var onPrem = await GetAsync(apps.OnPrem, Sync, token);
        using var cloud = await GetAsync(apps.Cloud, Sync, token);
        Assert.Equal(HttpStatusCode.OK, onPrem.StatusCode);
        Assert.Equal(HttpStatusCode.OK, cloud.StatusCode);
        Assert.Equal(1, await apps.AuditEventsAsync(AuditActions.SignIn, actorId: account.Id));
    }

    [Fact]
    public async Task Offline_download_only_includes_the_clinicians_assigned_charts()
    {
        const string offlinePanel = "/api/desktop/offline-panel";
        var clinician = await apps.CreateStaffAsync("Dr. Offline", StaffRoles.Clinician);
        var colleague = await apps.CreateStaffAsync("Dr. Colleague", StaffRoles.Clinician);
        var own = await apps.InsertPatientAsync("Demo", "Assigned");
        var other = await apps.InsertPatientAsync("Demo", "OtherPanel");
        await apps.AssignAsync(clinician, own);
        await apps.AssignAsync(colleague, other);

        var token = await apps.DesktopSignInAsync(apps.OnPrem, clinician);
        using var rejected = await PostAsync(apps.OnPrem, offlinePanel, "not-a-token");
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);

        using var response = await PostAsync(apps.OnPrem, offlinePanel, token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore == true);
        var charts = await response.Content.ReadFromJsonAsync<CachedChart[]>(JsonSerializerOptions.Web, Cancel);
        Assert.Equal(own, Assert.Single(charts!).Id);
        Assert.Equal(1, await apps.AuditEventsAsync(AuditActions.Read, actorId: clinician.Id, patientId: own));
        Assert.Equal(0, await apps.AuditEventsAsync(AuditActions.Read, actorId: clinician.Id, patientId: other));
    }

    [Fact]
    public async Task Draft_sync_only_returns_the_authors_drafts_and_deduplicates_an_outdated_retry()
    {
        const string push = "/api/desktop/drafts/push";
        const string pull = "/api/desktop/drafts/pull";
        var author = await apps.CreateStaffAsync("Draft author", StaffRoles.Clinician);
        var colleague = await apps.CreateStaffAsync("Draft colleague", StaffRoles.Clinician);
        var patient = await apps.InsertPatientAsync("Demo", "Draft");
        await apps.AssignAsync(author, patient);
        await apps.AssignAsync(colleague, patient);
        var token = await apps.DesktopSignInAsync(apps.OnPrem, author);
        var otherToken = await apps.DesktopSignInAsync(apps.OnPrem, colleague);
        var id = Guid.NewGuid();
        var initial = new { Key = Guid.NewGuid(), Id = id, PatientId = patient, BaseVersion = (long?)null, Body = "First draft" };

        using var unauthorized = await PostAsync(apps.OnPrem, push, "not-a-token");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        var added = await PostJsonAsync(apps.OnPrem, push, token, initial);
        Assert.Equal("applied", added.GetProperty("outcome").GetString());

        var original = await PostJsonAsync(apps.Cloud, pull, token, new { Since = "0", Patients = new[] { patient } });
        Assert.Equal(id, Assert.Single(original.GetProperty("drafts").EnumerateArray()).GetProperty("id").GetGuid());
        var since = original.GetProperty("watermark").GetString();
        var changed = await PostJsonAsync(apps.Cloud, push, token, initial with { Key = Guid.NewGuid(), BaseVersion = 1, Body = "Newer copy" });
        Assert.Equal(2, changed.GetProperty("current").GetProperty("version").GetInt64());

        var retry = await PostJsonAsync(apps.OnPrem, push, token, initial);
        Assert.Equal("duplicate", retry.GetProperty("outcome").GetString());
        Assert.Equal(2, retry.GetProperty("current").GetProperty("version").GetInt64());
        var stale = await PostJsonAsync(apps.OnPrem, push, token, initial with { Key = Guid.NewGuid(), BaseVersion = 1, Body = "Stale edit" });
        Assert.Equal("conflict", stale.GetProperty("outcome").GetString());
        Assert.Equal("Newer copy", stale.GetProperty("current").GetProperty("body").GetString());

        var delta = await PostJsonAsync(apps.OnPrem, pull, token, new { Since = since, Patients = new[] { patient } });
        Assert.Equal("Newer copy", Assert.Single(delta.GetProperty("drafts").EnumerateArray()).GetProperty("body").GetString());
        var unrelated = await PostJsonAsync(apps.OnPrem, pull, otherToken, new { Since = "0", Patients = new[] { patient } });
        Assert.Empty(unrelated.GetProperty("drafts").EnumerateArray());
    }

    static async Task<JsonElement> PostJsonAsync(WebApplicationFactory<Program> app, string path, string token, object body)
    {
        using var client = CreateClient(app);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.PostAsJsonAsync(path, body, JsonSerializerOptions.Web, Cancel);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>(JsonSerializerOptions.Web, Cancel);
    }

    [Fact]
    public async Task A_bad_token_is_refused_with_401_and_no_redirect()
    {
        using var response = await GetAsync(apps.OnPrem, Sync, "not-a-token");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }

    [Fact]
    public async Task A_wrong_code_is_refused_with_a_reason_and_recorded()
    {
        var account = await apps.CreateStaffAsync("Dr. Mistyped Desktop", StaffRoles.Clinician);

        using var response = await apps.DesktopSignInResponseAsync(apps.OnPrem, account, Totp.WrongCode(account.AuthenticatorKey));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Wrong email, password or code.", await ProblemAsync(response));
        Assert.Equal(1, await apps.AuditEventsAsync(AuditActions.SignInFailed, actorId: account.Id));
        Assert.Equal(0, await apps.AuditEventsAsync(AuditActions.SignIn, actorId: account.Id));
    }

    [Fact]
    public async Task An_account_that_has_not_finished_its_web_setup_is_refused()
    {
        var account = await apps.CreateStaffWithoutAuthenticatorAsync("Dr. Not Yet", StaffRoles.Clinician);

        using var response = await apps.DesktopSignInResponseAsync(apps.OnPrem, account, "000000");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("Finish setting up your account in the web app first.", await ProblemAsync(response));
    }

    [Fact]
    public async Task An_admin_cannot_use_the_desktop_app()
    {
        var admin = await apps.CreateStaffAsync("Desktop Admin", StaffRoles.Admin);

        using var response = await apps.DesktopSignInResponseAsync(apps.OnPrem, admin, Totp.Code(admin.AuthenticatorKey));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("This account can't use the desktop app.", await ProblemAsync(response));
    }

    [Fact]
    public async Task Missing_fields_are_a_validation_problem()
    {
        using var response = await CreateClient(apps.OnPrem).PostAsJsonAsync("/api/desktop/sign-in",
            new SignInRequest("", null, " ", null), JsonSerializerOptions.Web, Cancel);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Signing_out_ends_the_session()
    {
        var account = await apps.CreateStaffAsync("Dr. Leaving Desktop", StaffRoles.Clinician);
        var token = await apps.DesktopSignInAsync(apps.OnPrem, account);

        using var signedOut = await PostAsync(apps.OnPrem, "/api/desktop/sign-out", token);
        Assert.Equal(HttpStatusCode.NoContent, signedOut.StatusCode);

        using var afterwards = await GetAsync(apps.OnPrem, Sync, token);
        using var again = await PostAsync(apps.OnPrem, "/api/desktop/sign-out", token);
        Assert.Equal(HttpStatusCode.Unauthorized, afterwards.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, again.StatusCode);
        Assert.Equal(1, await apps.AuditEventsAsync(AuditActions.SignOut, actorId: account.Id));
    }

    [Fact]
    public async Task A_password_change_ends_the_session()
    {
        var account = await apps.CreateStaffAsync("Dr. Changed Desktop", StaffRoles.Clinician);
        var token = await apps.DesktopSignInAsync(apps.OnPrem, account);
        using var before = await GetAsync(apps.OnPrem, Sync, token);
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);

        await apps.UpdateSecurityStampAsync(account);

        using var after = await GetAsync(apps.OnPrem, Sync, token);
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
    }

    [Fact]
    public async Task The_cloud_app_forwards_the_sign_in_to_the_on_prem_app()
    {
        var account = await apps.CreateStaffAsync("Dr. Remote Desktop", StaffRoles.Clinician);

        var token = await apps.DesktopSignInAsync(apps.Cloud, account);

        using var pull = await GetAsync(apps.Cloud, Sync, token);
        Assert.Equal(HttpStatusCode.OK, pull.StatusCode);
    }

    static Task<HttpResponseMessage> GetAsync(WebApplicationFactory<Program> app, string path, string token)
    {
        var client = CreateClient(app);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client.GetAsync(path, Cancel);
    }

    static Task<HttpResponseMessage> PostAsync(WebApplicationFactory<Program> app, string path, string token)
    {
        var client = CreateClient(app);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client.PostAsync(path, null, Cancel);
    }

    static async Task<string> ProblemAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<Refusal>(JsonSerializerOptions.Web, Cancel))!.Problem;
}
