using System.Net;
using System.Text.RegularExpressions;
using Ehr.Data.Accounts;
using Ehr.Data.Audit;
using Ehr.Web.CloudAudit;
using Ehr.Web.Forwarding;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Ehr.Web.Tests.Browser;

namespace Ehr.Web.Tests;

// The cloud app's forwarding to the on-prem app: saves always, and a person's reads until the replica has their
// latest save, unless the on-prem app can't save just then; a save that can't be forwarded is told why. The tests in
// this class run one at a time, so each can count what crossed the link.
public partial class CloudForwardingTests(WebApps apps) : IClassFixture<WebApps>
{
    const string LastSaveCookieName = "__Host-Ehr.LastSave";

    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_on_prem_app_reports_its_WAL_position_after_a_save()
    {
        var page = await OpenAsync(apps, apps.OnPrem);

        using var response = await page.PostAsync("Add", ("Shortcut", WebApps.NewShortcut()), ("Body", "Text"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Matches("^[0-9A-F]+/[0-9A-F]+$", Assert.Single(response.Headers.GetValues("Ehr-Wal-Position")));
    }

    [Fact]
    public async Task Saves_on_the_cloud_app_are_made_by_the_on_prem_app()
    {
        var shortcut = WebApps.NewShortcut();
        var page = await OpenAsync(apps, apps.Cloud);
        var forwarded = apps.Forwarded.Count;

        using var response = await page.PostAsync("Add", ("Shortcut", shortcut), ("Body", "Saved through the cloud"));

        Assert.Contains("Saved through the cloud", await ReadAsync(response));
        Assert.Equal([(shortcut, "Saved through the cloud", 1)], await apps.RowsAsync(shortcut));
        Assert.Equal([$"POST {QuickTexts}?handler=Add"], apps.Forwarded.Skip(forwarded));

        // The WAL position became the browser's last-save cookie and isn't passed on.
        Assert.False(response.Headers.Contains("Ehr-Wal-Position"));
        Assert.Contains(response.Headers.GetValues("Set-Cookie"), cookie => cookie.StartsWith(LastSaveCookieName + "="));
    }

    [Fact]
    public async Task Reads_go_to_the_on_prem_app_until_the_replica_has_the_last_save()
    {
        var forwarded = apps.Forwarded.Count;

        using var response = await GetFromCloudAsync(QuickTexts, LastSaveCookie(position: "FFFFFFFF/FFFFFFFF"));

        Assert.Contains("Quick text", await ReadAsync(response));
        Assert.Equal([$"GET {QuickTexts}"], apps.Forwarded.Skip(forwarded));
    }

    [Fact]
    public async Task Reads_come_from_the_replica_once_it_has_the_last_save()
    {
        var forwarded = apps.Forwarded.Count;

        using var response = await GetFromCloudAsync(QuickTexts, LastSaveCookie(position: "0/0"));

        Assert.Contains("Quick text", await ReadAsync(response));
        Assert.Empty(apps.Forwarded.Skip(forwarded));
        Assert.True(RemovesLastSaveCookie(response));
    }

    [Fact]
    public async Task An_altered_last_save_cookie_is_ignored_and_removed()
    {
        var forwarded = apps.Forwarded.Count;

        using var response = await GetFromCloudAsync(QuickTexts, LastSaveCookie(position: "FFFFFFFF/FFFFFFFF") + "x");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(apps.Forwarded.Skip(forwarded));
        Assert.True(RemovesLastSaveCookie(response));
    }

    [Fact]
    public async Task Static_files_are_never_forwarded()
    {
        var forwarded = apps.Forwarded.Count;

        using var response = await GetFromCloudAsync("/_content/Ehr.Design/css/ehr.css", LastSaveCookie(position: "FFFFFFFF/FFFFFFFF"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(apps.Forwarded.Skip(forwarded));
    }

    [Fact]
    public async Task With_the_on_prem_app_unreachable_a_save_fails_clearly()
    {
        var shortcut = WebApps.NewShortcut();
        var page = await OpenAsync(apps, apps.CloudCutOff, via: apps.Cloud);

        using var response = await page.PostAsync("Add", ("Shortcut", shortcut), ("Body", "Text"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("can't be reached", await response.Content.ReadAsStringAsync(Cancel));
        Assert.Empty(await apps.RowsAsync(shortcut));
    }

    [Fact]
    public async Task A_form_post_that_cannot_be_forwarded_gets_a_page_that_explains()
    {
        var patient = await apps.InsertPatientAsync("Lane", "Unsaved");
        var signedIn = await CookieHeaderAsync(apps.OnPrem, await apps.ClinicianAsync());
        using var client = CreateClient(apps.CloudCutOff, handleCookies: false);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/patients/{patient}/reason")
        {
            Content = Form(("Reason", "Covering the night shift")),
        };
        request.Headers.Add("Cookie", signedIn);

        using var response = await client.SendAsync(request, Cancel);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        var html = await response.Content.ReadAsStringAsync(Cancel);
        Assert.Contains("Nothing was saved, because the clinic's server can't be reached.", html);
        // Back to the form, by the browser's history where JavaScript runs, by its address where it doesn't.
        Assert.Contains($"<a href=\"/patients/{patient}/reason\" data-back>Back</a>", html);
        var assets = PageAssets().Matches(html).Select(match => match.Groups[1].Value).ToList();
        Assert.Equal(["/_content/Ehr.Design/css/ehr.css", "/_content/Ehr.Design/js/back-link.js"], assets);
        foreach (var asset in assets)
        {
            using var file = await client.GetAsync(asset, Cancel);
            Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        }
    }

    [Fact]
    public async Task An_htmx_post_that_cannot_be_forwarded_still_gets_the_short_message()
    {
        var page = await OpenAsync(apps, apps.CloudCutOff, via: apps.Cloud);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{QuickTexts}?handler=Add")
        {
            Content = Form(("Shortcut", WebApps.NewShortcut()), ("Body", "Text"), ("__RequestVerificationToken", page.Token)),
        };
        request.Headers.Add("HX-Request", "true");

        using var response = await page.Client.SendAsync(request, Cancel);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("Not saved: the on-prem server can't be reached.", await response.Content.ReadAsStringAsync(Cancel));
    }

    [Fact]
    public async Task Signing_out_off_site_is_done_by_the_cloud_app_and_recorded_from_there()
    {
        var account = await apps.CreateStaffAsync("Dr. Done", StaffRoles.Clinician);
        var session = await SignInAsync(apps.Cloud, account);
        var forwarded = apps.Forwarded.Count;

        using var signedOut = await PostFormAsync(session.Client, "/sign-out", QuickTexts);

        Assert.Contains("Signed out", await ReadAsync(signedOut));
        Assert.Empty(apps.Forwarded.Skip(forwarded));
        await apps.ShipCloudAuditAsync();
        Assert.Equal(1, await apps.AuditEventsAsync(AuditActions.SignOut, actorId: account.Id, source: AuditSources.Cloud));
    }

    [Fact]
    public async Task A_sign_out_off_site_without_its_form_token_is_refused()
    {
        var account = await apps.CreateStaffAsync("Dr. Staying", StaffRoles.Clinician);
        var session = await SignInAsync(apps.Cloud, account);

        using var refused = await session.Client.PostAsync("/sign-out", Form(), Cancel);

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        // Still signed in: the clinic's home sends them to their patients, not to sign in.
        using var home = await Client(apps.Cloud, session.Jar, followRedirects: false).GetAsync("/", Cancel);
        Assert.Equal("/patients", new Uri(Base, home.Headers.Location!).AbsolutePath);
        await apps.ShipCloudAuditAsync();
        Assert.Equal(0, await apps.AuditEventsAsync(AuditActions.SignOut, actorId: account.Id));
    }

    [Fact]
    public async Task During_an_outage_reads_after_a_save_come_from_the_replica()
    {
        var clinician = await apps.ClinicianAsync();
        var patient = await apps.InsertPatientAsync("Remy", "Justsaved");
        await apps.AssignAsync(clinician, patient);
        var signedIn = await CookieHeaderAsync(apps.OnPrem, clinician);
        apps.PrimaryDown = true;
        try
        {
            Assert.False(await apps.CloudPrimaryDrill.Services.GetRequiredService<CloudAuditShipper>().ShipAsync(Cancel));
            using var client = CreateClient(apps.CloudPrimaryDrill, handleCookies: false);
            using var request = new HttpRequestMessage(HttpMethod.Get, $"/patients/{patient}");
            // A save the replica doesn't have yet, which would otherwise send this read to the on-prem app.
            request.Headers.Add("Cookie", $"{signedIn}; {LastSaveCookie(position: "FFFFFFFF/FFFFFFFF")}");

            using var response = await client.SendAsync(request, Cancel);

            var html = await ReadAsync(response);
            Assert.Contains("Justsaved, Remy", html);
            Assert.Contains("Read-only: the clinic's server can't be reached", html);
            Assert.False(RemovesLastSaveCookie(response));
            // The read is recorded in the queue, for the on-prem app once it's back.
            var (queued, _) = apps.CloudPrimaryDrill.Services.GetRequiredService<CloudAuditQueue>().ReadPending(int.MaxValue);
            Assert.Contains(queued, e => e.Action == AuditActions.Read && e.PatientId == patient && e.ActorId == clinician.Id);
        }
        finally
        {
            apps.PrimaryDown = false;
        }
    }

    [Fact]
    public async Task With_the_on_prem_app_unreachable_reads_come_from_the_replica()
    {
        await apps.InsertAsync(WebApps.NewShortcut(), "Still readable");

        // Signed in on-site; the cookie is as good off-site, with the link down.
        var signedIn = await CookieHeaderAsync(apps.OnPrem, await apps.ClinicianAsync());
        using var client = CreateClient(apps.CloudCutOff, handleCookies: false);
        using var request = new HttpRequestMessage(HttpMethod.Get, QuickTexts);
        request.Headers.Add("Cookie", $"{signedIn}; {LastSaveCookie(position: "FFFFFFFF/FFFFFFFF")}");
        using var response = await client.SendAsync(request, Cancel);

        Assert.Contains("Still readable", await ReadAsync(response));
    }

    // A request from a signed-in browser, with the given last-save cookie added. The sign-in goes through the on-prem
    // app, so nothing of it crosses the link being counted.
    async Task<HttpResponseMessage> GetFromCloudAsync(string path, string cookie)
    {
        var signedIn = await CookieHeaderAsync(apps.OnPrem, await apps.ClinicianAsync());
        using var client = CreateClient(apps.Cloud, handleCookies: false);
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Cookie", $"{signedIn}; {cookie}");
        return await client.SendAsync(request, Cancel);
    }

    // A last-save cookie as the cloud app would set it after a save at this WAL position.
    string LastSaveCookie(string position)
    {
        var context = new DefaultHttpContext();
        apps.Cloud.Services.GetRequiredService<LastSaveCookie>().Set(context, position);
        var setCookie = context.Response.Headers.SetCookie.ToString();
        return setCookie[..setCookie.IndexOf(';')];
    }

    [GeneratedRegex("(?:href|src)=\"(/_content/[^\"]+)\"")]
    private static partial Regex PageAssets();

    static bool RemovesLastSaveCookie(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var cookies)
        && cookies.Any(cookie => cookie.StartsWith(LastSaveCookieName + "=;") && cookie.Contains("expires=Thu, 01 Jan 1970"));
}
