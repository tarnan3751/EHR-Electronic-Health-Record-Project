using System.Net;
using System.Net.Http.Headers;
using Ehr.Data.Accounts;
using Ehr.Domain.Accounts;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;
using static Ehr.Web.Tests.Browser;

namespace Ehr.Web.Tests.TestCases.UC8;

// Test Case ID: TC-UC8-07
// Purpose / scenario: A desktop sign-in lasts at most 12 hours, as a web sign-in does (the expansion plan's step 19a,
//   Tahseen's ruling 11b of 25 November 2026): 12 hours after the sign-in with both factors, the server refuses the
//   token on every desktop request, on both sites, and says that the sign-in has ended, so the app keeps its offline
//   copy and asks for a full sign-in; every other refusal still means the session is over. A new full sign-in works.
// Type: Functional (alternative flow)
// Requirements: proposed FR-68, FR-24
// Use case: UC 8, Sync the desktop app
// Preconditions: Dr. Shift Desktop is signed in to the desktop app on the on-site app; the Desktop scheme's clock
//   (ShiftedClock) starts with nothing passed.
// Inputs and system conditions: GET /api/sync/quick-texts and POST /api/desktop/offline-panel with the token, on the
//   on-site app and the cloud app, at 11 h 59 min and at 12 hours; two more sessions, one signed out, one whose
//   password changed.
// Test data: none beyond the accounts.
// Steps:
//   1. Sync on both sites; let 11 h 59 min pass; sync on both again.
//   2. Let a minute more pass; sync on both, and ask the cloud app for the offline charts (forwarded to the on-site app).
//   3. Sign in again with both factors; sync with the new token.
//   4. Sign in two more sessions; sign one out, and change the other's password; sync with each.
// Expected result: Step 1: 200 each time. Step 2: 401 on each, with "Ehr-Sign-In: ended". Step 3: 200. Step 4: 401
//   for each, without the header: those sessions are over.
// Creator: Tahseen Arnan
public class TC_UC8_07_TwelveHourDesktopSignIn(WebApps apps) : IClassFixture<WebApps>
{
    const string Sync = "/api/sync/quick-texts?since=0";

    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_desktop_token_is_refused_12_hours_after_its_sign_in_saying_so()
    {
        var clinician = await apps.CreateStaffAsync("Dr. Shift Desktop", StaffRoles.Clinician);
        var token = await apps.DesktopSignInAsync(apps.OnPrem, clinician);
        foreach (var site in new[] { apps.OnPrem, apps.Cloud })
        {
            using var fresh = await SendAsync(site, HttpMethod.Get, Sync, token);
            Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
        }

        apps.Clock.Pass(TimeSpan.FromHours(12) - TimeSpan.FromMinutes(1));
        foreach (var site in new[] { apps.OnPrem, apps.Cloud })
        {
            using var late = await SendAsync(site, HttpMethod.Get, Sync, token);
            Assert.Equal(HttpStatusCode.OK, late.StatusCode);
        }

        apps.Clock.Pass(TimeSpan.FromMinutes(1));
        foreach (var (site, method, path) in new[]
        {
            (apps.OnPrem, HttpMethod.Get, Sync), (apps.Cloud, HttpMethod.Get, Sync), (apps.Cloud, HttpMethod.Post, "/api/desktop/offline-panel"),
        })
        {
            using var ended = await SendAsync(site, method, path, token);
            Assert.Equal(HttpStatusCode.Unauthorized, ended.StatusCode);
            Assert.Equal([DesktopTokenHandler.SignInEnded], ended.Headers.GetValues(DesktopTokenHandler.SignInHeader));
        }

        var again = await apps.DesktopSignInAsync(apps.OnPrem, clinician);
        using var signedInAgain = await SendAsync(apps.OnPrem, HttpMethod.Get, Sync, again);
        Assert.Equal(HttpStatusCode.OK, signedInAgain.StatusCode);

        var leaving = await apps.DesktopSignInAsync(apps.OnPrem, await apps.CreateStaffAsync("Dr. Leaving Shift", StaffRoles.Clinician));
        var changingAccount = await apps.CreateStaffAsync("Dr. Changed Shift", StaffRoles.Clinician);
        var changing = await apps.DesktopSignInAsync(apps.OnPrem, changingAccount);
        using (var signedOut = await SendAsync(apps.OnPrem, HttpMethod.Post, "/api/desktop/sign-out", leaving))
        {
            Assert.Equal(HttpStatusCode.NoContent, signedOut.StatusCode);
        }

        await apps.UpdateSecurityStampAsync(changingAccount);
        foreach (var over in new[] { leaving, changing })
        {
            using var refused = await SendAsync(apps.OnPrem, HttpMethod.Get, Sync, over);
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
            Assert.False(refused.Headers.Contains(DesktopTokenHandler.SignInHeader));
        }
    }

    internal static async Task<HttpResponseMessage> SendAsync(WebApplicationFactory<Program> app, HttpMethod method, string path, string token)
    {
        var client = CreateClient(app);
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request, Cancel);
    }
}
