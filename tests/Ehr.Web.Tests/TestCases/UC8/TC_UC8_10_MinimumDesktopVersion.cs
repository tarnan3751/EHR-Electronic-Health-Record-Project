using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Ehr.Data.Accounts;
using Ehr.Testing;
using Ehr.Web.Api.Desktop;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;
using static Ehr.Web.Tests.Browser;

namespace Ehr.Web.Tests.TestCases.UC8;

// Test Case ID: TC-UC8-10
// Purpose / scenario: The minimum desktop version (the expansion plan's step 20, Tahseen's 15a). With
//   Ehr:Desktop:MinimumVersion set, a desktop build that says it is older in Ehr-Desktop-Version, or says nothing, as
//   builds from before step 20 don't, is refused its data with 426, the minimum in Ehr-Desktop-Minimum and the reason
//   in words; one at or past it syncs. Sign-in, the lock's check of the password and sign-out are never refused for the
//   version, so an old build's lock opens and its offline charts stay readable. Without a minimum nothing is refused.
// Type: Functional (alternative flow)
// Requirements: proposed FR-70, FR-24
// Use case: UC 8, Sync the desktop app
// Preconditions: Dr. Old Build signed in to the desktop app; the on-site app and the cloud app each also run with
//   Ehr:Desktop:MinimumVersion 0.3.0.
// Inputs and system conditions: GET /api/sync/quick-texts on both, POST /api/desktop/offline-panel on the on-site
//   app, with no version, 0.2.0 and 0.2.9, then 0.3.0, 0.3 and 1.0.0; POST /api/desktop/sign-in, /unlock and
//   /sign-out with no version.
// Test data: none beyond the account.
// Steps:
//   1. Sync and download charts as each older build.
//   2. Sync as each build at or past the minimum.
//   3. Sign in, check a wrong password at the lock, and sign out, saying no version.
//   4. Sync with no version on the app with no minimum.
// Expected result: Step 1: 426 each time, "Ehr-Desktop-Minimum: 0.3.0", and "Update needed. The clinic's server …".
//   Step 2: 200. Step 3: 200, 403 "Wrong password." and 204: none is 426. Step 4: 200.
// Creator: Tahseen Arnan
public class TC_UC8_10_MinimumDesktopVersion(WebApps apps) : IClassFixture<WebApps>
{
    const string Sync = "/api/sync/quick-texts?since=0";

    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_build_older_than_the_minimum_is_refused_its_data_and_never_its_lock()
    {
        var account = await apps.CreateStaffAsync("Dr. Old Build", StaffRoles.Clinician);
        var token = await apps.DesktopSignInAsync(apps.OnPrem, account);
        await using var onPrem = apps.OnPrem.WithWebHostBuilder(builder => builder.UseSetting("Ehr:Desktop:MinimumVersion", "0.3.0"));
        await using var cloud = apps.Cloud.WithWebHostBuilder(builder => builder.UseSetting("Ehr:Desktop:MinimumVersion", "0.3.0"));

        // 1.
        foreach (var version in new[] { null, "0.2.0", "0.2.9" })
        {
            foreach (var (site, method, path) in new[] { (onPrem, HttpMethod.Get, Sync), (cloud, HttpMethod.Get, Sync), (onPrem, HttpMethod.Post, "/api/desktop/offline-panel") })
            {
                using var refused = await SendAsync(site, method, path, token, version);
                Assert.Equal(HttpStatusCode.UpgradeRequired, refused.StatusCode);
                Assert.Equal(["0.3.0"], refused.Headers.GetValues(DesktopVersionFilter.MinimumHeader));
                var refusal = await refused.Content.ReadFromJsonAsync<Refusal>(JsonSerializerOptions.Web, Cancel);
                Assert.StartsWith("Update needed. The clinic's server syncs only with SwiftChart 0.3.0 or later.", refusal!.Problem);
            }
        }

        // 2.
        foreach (var version in new[] { "0.3.0", "0.3", "1.0.0" })
        {
            using var synced = await SendAsync(onPrem, HttpMethod.Get, Sync, token, version);
            Assert.Equal(HttpStatusCode.OK, synced.StatusCode);
        }

        // 3.
        using (var signedIn = await apps.DesktopSignInResponseAsync(onPrem, account, Totp.Code(account.AuthenticatorKey)))
        {
            Assert.Equal(HttpStatusCode.OK, signedIn.StatusCode);
        }

        using (var unlock = await SendAsync(onPrem, HttpMethod.Post, "/api/desktop/unlock", token, null, JsonContent.Create(new { password = "not the password" })))
        {
            Assert.Equal(HttpStatusCode.Forbidden, unlock.StatusCode);
            Assert.Equal("Wrong password.", (await unlock.Content.ReadFromJsonAsync<Refusal>(JsonSerializerOptions.Web, Cancel))!.Problem);
        }

        // 4.
        using (var unlimited = await SendAsync(apps.OnPrem, HttpMethod.Get, Sync, token, null))
        {
            Assert.Equal(HttpStatusCode.OK, unlimited.StatusCode);
        }

        using var signedOut = await SendAsync(onPrem, HttpMethod.Post, "/api/desktop/sign-out", token, null);
        Assert.Equal(HttpStatusCode.NoContent, signedOut.StatusCode);
    }

    static async Task<HttpResponseMessage> SendAsync(
        WebApplicationFactory<Program> app, HttpMethod method, string path, string token, string? version, HttpContent? content = null)
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (version is not null)
        {
            request.Headers.Add(DesktopVersionFilter.VersionHeader, version);
        }

        return await CreateClient(app).SendAsync(request, Cancel);
    }
}
