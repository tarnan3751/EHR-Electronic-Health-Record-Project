using System.Net;
using Ehr.Web.Forwarding;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Ehr.Web.Tests.Browser;

namespace Ehr.Web.Tests;

// The cloud app's forwarding to the on-prem app: saves always, and a person's reads until the replica has their
// latest save. The tests in this class run one at a time, so each can count what crossed the link.
public class CloudForwardingTests(WebApps apps) : IClassFixture<WebApps>
{
    const string LastSaveCookieName = "__Host-Ehr.LastSave";

    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_on_prem_app_reports_its_WAL_position_after_a_save()
    {
        var page = await OpenAsync(apps.OnPrem);

        using var response = await page.PostAsync("Add", ("Shortcut", WebApps.NewShortcut()), ("Body", "Text"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Matches("^[0-9A-F]+/[0-9A-F]+$", Assert.Single(response.Headers.GetValues("Ehr-Wal-Position")));
    }

    [Fact]
    public async Task Saves_on_the_cloud_app_are_made_by_the_on_prem_app()
    {
        var shortcut = WebApps.NewShortcut();
        var page = await OpenAsync(apps.Cloud);
        var forwarded = apps.Forwarded.Count;

        using var response = await page.PostAsync("Add", ("Shortcut", shortcut), ("Body", "Saved through the cloud"));

        Assert.Contains("Saved through the cloud", await ReadAsync(response));
        Assert.Equal([(shortcut, "Saved through the cloud", 1)], await apps.RowsAsync(shortcut));
        Assert.Equal(["POST /?handler=Add"], apps.Forwarded.Skip(forwarded));

        // The WAL position became the browser's last-save cookie and isn't passed on.
        Assert.False(response.Headers.Contains("Ehr-Wal-Position"));
        Assert.Contains(response.Headers.GetValues("Set-Cookie"), cookie => cookie.StartsWith(LastSaveCookieName + "="));
    }

    [Fact]
    public async Task Reads_go_to_the_on_prem_app_until_the_replica_has_the_last_save()
    {
        var forwarded = apps.Forwarded.Count;

        using var response = await GetFromCloudAsync("/", LastSaveCookie(position: "FFFFFFFF/FFFFFFFF"));

        Assert.Contains("Quick text", await ReadAsync(response));
        Assert.Equal(["GET /"], apps.Forwarded.Skip(forwarded));
    }

    [Fact]
    public async Task Reads_come_from_the_replica_once_it_has_the_last_save()
    {
        var forwarded = apps.Forwarded.Count;

        using var response = await GetFromCloudAsync("/", LastSaveCookie(position: "0/0"));

        Assert.Contains("Quick text", await ReadAsync(response));
        Assert.Empty(apps.Forwarded.Skip(forwarded));
        Assert.True(RemovesLastSaveCookie(response));
    }

    [Fact]
    public async Task An_altered_last_save_cookie_is_ignored_and_removed()
    {
        var forwarded = apps.Forwarded.Count;

        using var response = await GetFromCloudAsync("/", LastSaveCookie(position: "FFFFFFFF/FFFFFFFF") + "x");

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
        var page = await OpenAsync(apps.CloudCutOff);

        using var response = await page.PostAsync("Add", ("Shortcut", shortcut), ("Body", "Text"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("can't be reached", await response.Content.ReadAsStringAsync(Cancel));
        Assert.Empty(await apps.RowsAsync(shortcut));
    }

    [Fact]
    public async Task With_the_on_prem_app_unreachable_reads_come_from_the_replica()
    {
        await apps.InsertAsync(WebApps.NewShortcut(), "Still readable");

        using var client = CreateClient(apps.CloudCutOff, handleCookies: false);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Add("Cookie", LastSaveCookie(position: "FFFFFFFF/FFFFFFFF"));
        using var response = await client.SendAsync(request, Cancel);

        Assert.Contains("Still readable", await ReadAsync(response));
    }

    async Task<HttpResponseMessage> GetFromCloudAsync(string path, string cookie)
    {
        using var client = CreateClient(apps.Cloud, handleCookies: false);
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Cookie", cookie);
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

    static bool RemovesLastSaveCookie(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var cookies)
        && cookies.Any(cookie => cookie.StartsWith(LastSaveCookieName + "=;") && cookie.Contains("expires=Thu, 01 Jan 1970"));
}
