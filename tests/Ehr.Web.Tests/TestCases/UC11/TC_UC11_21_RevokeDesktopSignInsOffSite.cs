using System.Net;
using Ehr.Data.Accounts;
using Ehr.Domain.Accounts;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.TestCases.UC8.TC_UC8_07_TwelveHourDesktopSignIn;

namespace Ehr.Web.Tests.TestCases.UC11;

// Test Case ID: TC-UC11-21
// Purpose / scenario: Revoking desktop sign-ins off-site (the expansion plan's step 20). The cloud app shows the page
//   from its copy and forwards the revocation to the clinic's server, which makes it; with the clinic's server cut off,
//   the page says revoking needs it and offers nothing to press. An account with nothing to revoke says so.
// Type: Functional (exceptional condition)
// Requirements: proposed FR-69, FR-33, FR-34
// Use case: UC 11, Sign in with multi-factor authentication
// Preconditions: Dr. Away, a clinician, signed in to the desktop app; Dr. Never, a clinician who never did; the
//   administrator Remote Admin, signed in on the cloud app, connected, then on the cloud app cut off.
// Inputs and system conditions: GET and POST /admin/staff/{id}/desktop on the cloud app; GET on the cut-off app.
// Test data: none beyond the accounts.
// Steps:
//   1. On the cut-off app, open Dr. Away's desktop sign-ins.
//   2. On the cloud app, open them and revoke them; sync with Dr. Away's token on the on-site app.
//   3. Open Dr. Never's desktop sign-ins.
// Expected result: Step 1: "Revoking needs the clinic's server. Nothing has been revoked.", no button. Step 2: the post
//   crosses the WAN link; the token is refused with "Ehr-Sign-In: revoked". Step 3: "None:", nothing to press.
// Creator: Tahseen Arnan
public class TC_UC11_21_RevokeDesktopSignInsOffSite(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Off_site_the_revocation_is_forwarded_and_cut_off_it_waits()
    {
        var away = await apps.CreateStaffAsync("Dr. Away", StaffRoles.Clinician);
        var token = await apps.DesktopSignInAsync(apps.OnPrem, away);
        var admin = await SignInAsync(apps.Cloud, await apps.CreateStaffAsync("Remote Admin", StaffRoles.Admin));
        var path = $"/admin/staff/{away.Id}/desktop";

        // 1.
        var cutOff = await Client(apps.CloudCutOff, admin.Jar).GetStringAsync(path, Cancel);
        Assert.Contains("Revoking needs the clinic's server. Nothing has been revoked.", cutOff);
        Assert.DoesNotContain("Revoke and delete", cutOff);

        // 2.
        using (var revoked = await PostFormAsync(admin.Client, path, path))
        {
            Assert.Equal("/admin/staff", PathOf(revoked));
        }

        Assert.Contains($"POST {path}", apps.Forwarded);
        using (var refused = await SendAsync(apps.OnPrem, HttpMethod.Get, "/api/sync/quick-texts?since=0", token))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
            Assert.Equal([DesktopTokenHandler.Revoked], refused.Headers.GetValues(DesktopTokenHandler.SignInHeader));
        }

        // 3.
        var never = await apps.CreateStaffAsync("Dr. Never", StaffRoles.Clinician);
        var none = await admin.Client.GetStringAsync($"/admin/staff/{never.Id}/desktop", Cancel);
        Assert.Contains("No desktop sign-ins. Dr. Never has none to revoke.", Words(none));
        Assert.DoesNotContain("Revoke and delete", none);
    }
}
