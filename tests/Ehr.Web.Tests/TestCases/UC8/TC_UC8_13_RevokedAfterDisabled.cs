using System.Net;
using Ehr.Data.Accounts;
using Ehr.Domain.Accounts;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.TestCases.UC8.TC_UC8_07_TwelveHourDesktopSignIn;

namespace Ehr.Web.Tests.TestCases.UC8;

// Test Case ID: TC-UC8-13
// Purpose / scenario: Revoking after disabling (step 20's final review). An administrator disables a clinician first,
//   as someone leaving is often handled; the clinician's desktop token is then refused plainly, with no marker, which
//   the desktop app takes as its session ended, keeping the token (src-tauri/src/sync.rs). The staff page says that
//   what SwiftChart keeps on their computers stays until their desktop sign-ins are revoked. The administrator then
//   revokes them, and the same token is refused with the remote wipe's marker, so the computer still holding it is
//   wiped the next time it presents it. Revoking works in either order.
// Type: Functional (alternative flow)
// Requirements: proposed FR-69, FR-24
// Use case: UC 8, Sync the desktop app
// Preconditions: Dr. Leaving Desktop signed in to the desktop app on the on-site app; the administrator Leaving Admin
//   signed in on the on-site app.
// Inputs and system conditions: POST /admin/staff?handler=Disable; GET /api/sync/quick-texts with the token on both
//   sites; POST /admin/staff/{id}/desktop; the same GET again.
// Test data: none beyond the accounts.
// Steps:
//   1. Disable Dr. Leaving Desktop from the staff page.
//   2. Sync with the token on both sites.
//   3. Open their desktop sign-ins, and revoke them.
//   4. Sync with the same token on both sites.
// Expected result: Step 1: "is disabled and can no longer sign in", and that what SwiftChart keeps on their computers
//   stays until their desktop sign-ins are revoked. Step 2: 401 without the Ehr-Sign-In header. Step 3: the page counts
//   the sign-in and says revoking works before or after disabling. Step 4: 401 with "Ehr-Sign-In: revoked".
// Creator: Tahseen Arnan
public class TC_UC8_13_RevokedAfterDisabled(WebApps apps) : IClassFixture<WebApps>
{
    const string Sync = "/api/sync/quick-texts?since=0";

    [Fact]
    public async Task A_token_refused_for_a_disabled_account_still_carries_the_marker_once_revoked()
    {
        var clinician = await apps.CreateStaffAsync("Dr. Leaving Desktop", StaffRoles.Clinician);
        var token = await apps.DesktopSignInAsync(apps.OnPrem, clinician);
        var admin = await SignInAsync(apps.OnPrem, await apps.CreateStaffAsync("Leaving Admin", StaffRoles.Admin));

        // 1.
        using (var disabled = await PostFormAsync(admin.Client, "/admin/staff?handler=Disable", "/admin/staff", ("id", clinician.Id)))
        {
            var staffPage = Words(await ReadAsync(disabled));
            Assert.Contains("Dr. Leaving Desktop is disabled and can no longer sign in. What SwiftChart keeps on their computers stays there until their desktop sign-ins are revoked", staffPage);
        }

        // 2.
        foreach (var site in new[] { apps.OnPrem, apps.Cloud })
        {
            using var plain = await SendAsync(site, HttpMethod.Get, Sync, token);
            Assert.Equal(HttpStatusCode.Unauthorized, plain.StatusCode);
            Assert.False(plain.Headers.Contains(DesktopTokenHandler.SignInHeader));
        }

        // 3.
        var path = $"/admin/staff/{clinician.Id}/desktop";
        var page = Words(await admin.Client.GetStringAsync(path, TestContext.Current.CancellationToken));
        Assert.Contains("Dr. Leaving Desktop has 1 desktop sign-in that hasn't ended.", page);
        Assert.Contains("Revoking is what deletes it.", page);
        using (var revoked = await PostFormAsync(admin.Client, path, path))
        {
            Assert.Equal("/admin/staff", PathOf(revoked));
        }

        // 4.
        foreach (var site in new[] { apps.OnPrem, apps.Cloud })
        {
            using var marked = await SendAsync(site, HttpMethod.Get, Sync, token);
            Assert.Equal(HttpStatusCode.Unauthorized, marked.StatusCode);
            Assert.Equal([DesktopTokenHandler.Revoked], marked.Headers.GetValues(DesktopTokenHandler.SignInHeader));
        }
    }
}
