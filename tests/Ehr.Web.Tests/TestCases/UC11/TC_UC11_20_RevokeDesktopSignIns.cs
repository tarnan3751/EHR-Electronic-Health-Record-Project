using System.Net;
using Ehr.Data.Accounts;
using Ehr.Data.Audit;
using Ehr.Domain.Accounts;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.TestCases.UC8.TC_UC8_07_TwelveHourDesktopSignIn;

namespace Ehr.Web.Tests.TestCases.UC11;

// Test Case ID: TC-UC11-20
// Purpose / scenario: The remote wipe's first half (the expansion plan's step 20): an administrator opens a clinician's
//   desktop sign-ins from the staff page, reads what revoking them loses, and revokes them all at once; each token is
//   then refused with the wipe's marker, the revocation is recorded, and the clinician can sign in again. An
//   administrator who is also a clinician does the same for their own account, whose laptop it may be.
// Type: Functional (successful flow)
// Requirements: proposed FR-69
// Use case: UC 11, Sign in with multi-factor authentication
// Preconditions: Dr. Lost Laptop, a clinician, signed in to the desktop app twice (two computers, or two 12-hour
//   sign-ins); a third session of theirs signed out. Wipe Admin, an administrator; Admin Clinician, an administrator
//   and clinician, signed in to the desktop app once. All on the on-site app.
// Inputs and system conditions: GET /admin/staff and /admin/staff/{id}/desktop; POST the latter; GET
//   /api/sync/quick-texts with each token.
// Test data: none beyond the accounts.
// Steps:
//   1. As Wipe Admin, open the staff page, then Dr. Lost Laptop's desktop sign-ins.
//   2. Select "Revoke and delete".
//   3. Sync with each of Dr. Lost Laptop's tokens; sign in to the desktop app as them again, and sync.
//   4. As Admin Clinician, open their own desktop sign-ins and revoke them; sync with their token.
// Expected result: Step 1: a "Desktop sign-ins" link for each clinician, the administrator's own included; the page
//   counts 2 sign-ins and says what is lost. Step 2: back on the staff page, saying they are revoked; one
//   desktop_revoked event by Wipe Admin for the account. Step 3: both tokens 401 with "Ehr-Sign-In: revoked", the
//   signed-out one 401 without it; the new sign-in syncs. Step 4: the page is theirs to use; their token is refused with
//   the marker, and their web session carries on.
// Creator: Tahseen Arnan
public class TC_UC11_20_RevokeDesktopSignIns(WebApps apps) : IClassFixture<WebApps>
{
    const string Sync = "/api/sync/quick-texts?since=0";

    [Fact]
    public async Task An_administrator_revokes_a_clinicians_desktop_sign_ins_and_their_own()
    {
        var clinician = await apps.CreateStaffAsync("Dr. Lost Laptop", StaffRoles.Clinician);
        var first = await apps.DesktopSignInAsync(apps.OnPrem, clinician);
        var second = await apps.DesktopSignInAsync(apps.OnPrem, clinician);
        var signedOut = await apps.DesktopSignInAsync(apps.OnPrem, clinician);
        using (var ended = await SendAsync(apps.OnPrem, HttpMethod.Post, "/api/desktop/sign-out", signedOut))
        {
            Assert.Equal(HttpStatusCode.NoContent, ended.StatusCode);
        }

        var adminAccount = await apps.CreateStaffAsync("Wipe Admin", StaffRoles.Admin);
        var admin = await SignInAsync(apps.OnPrem, adminAccount);

        // 1.
        var staff = await admin.Client.GetStringAsync("/admin/staff", Cancel);
        Assert.Contains($"href=\"/admin/staff/{clinician.Id}/desktop\">Desktop sign-ins</a>", staff);
        Assert.DoesNotContain($"/admin/staff/{adminAccount.Id}/desktop", staff);
        var page = await admin.Client.GetStringAsync($"/admin/staff/{clinician.Id}/desktop", Cancel);
        Assert.Contains("Dr. Lost Laptop has 2 active desktop sign-ins.", Words(page));
        Assert.Contains("Unsent drafts and recordings are lost.", Words(page));
        Assert.Contains("Revoke and delete", page);

        // 2.
        using (var revoked = await PostFormAsync(admin.Client, $"/admin/staff/{clinician.Id}/desktop", $"/admin/staff/{clinician.Id}/desktop"))
        {
            Assert.Equal("/admin/staff", PathOf(revoked));
            Assert.Contains("Dr. Lost Laptop's desktop sign-ins are revoked.", Words(await ReadAsync(revoked)));
        }

        Assert.Equal(1, await apps.AuditEventsAsync(AuditActions.DesktopRevoked, actorId: adminAccount.Id, table: "staff_users"));

        // 3.
        foreach (var token in new[] { first, second })
        {
            using var refused = await SendAsync(apps.OnPrem, HttpMethod.Get, Sync, token);
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
            Assert.Equal([DesktopTokenHandler.Revoked], refused.Headers.GetValues(DesktopTokenHandler.SignInHeader));
        }

        using (var over = await SendAsync(apps.OnPrem, HttpMethod.Get, Sync, signedOut))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, over.StatusCode);
            Assert.False(over.Headers.Contains(DesktopTokenHandler.SignInHeader));
        }

        var again = await apps.DesktopSignInAsync(apps.OnPrem, clinician);
        using (var synced = await SendAsync(apps.OnPrem, HttpMethod.Get, Sync, again))
        {
            Assert.Equal(HttpStatusCode.OK, synced.StatusCode);
        }

        // 4.
        var both = await apps.CreateStaffAsync("Admin Clinician", StaffRoles.Admin, StaffRoles.Clinician);
        var own = await apps.DesktopSignInAsync(apps.OnPrem, both);
        var self = await SignInAsync(apps.OnPrem, both);
        Assert.Contains($"href=\"/admin/staff/{both.Id}/desktop\">Desktop sign-ins</a>", await self.Client.GetStringAsync("/admin/staff", Cancel));
        using (var revokedOwn = await PostFormAsync(self.Client, $"/admin/staff/{both.Id}/desktop", $"/admin/staff/{both.Id}/desktop"))
        {
            Assert.Equal("/admin/staff", PathOf(revokedOwn));
        }

        using (var refusedOwn = await SendAsync(apps.OnPrem, HttpMethod.Get, Sync, own))
        {
            Assert.Equal([DesktopTokenHandler.Revoked], refusedOwn.Headers.GetValues(DesktopTokenHandler.SignInHeader));
        }

        using var stillSignedIn = await self.Client.GetAsync("/admin/staff", Cancel);
        Assert.Equal("/admin/staff", PathOf(stillSignedIn));
    }
}
