using System.Net;
using Ehr.Data.Accounts;
using Ehr.Domain.Accounts;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Ehr.Web.Tests.TestCases.UC8.TC_UC8_07_TwelveHourDesktopSignIn;

namespace Ehr.Web.Tests.TestCases.UC8;

// Test Case ID: TC-UC8-09
// Purpose / scenario: The remote wipe's marker (the expansion plan's step 20). A desktop token an administrator revoked
//   is refused on every desktop and sync request, on both sites, with "Ehr-Sign-In: revoked", whatever else has become
//   true of it since (its password changed, its 12 hours or its 30 days past), so a computer that comes back after any
//   time away deletes what the app keeps there. A token signed out, before or after the revocation, is never answered
//   with the marker: a sign-out keeps what the computer holds.
// Type: Functional (alternative flow)
// Requirements: proposed FR-69, FR-24
// Use case: UC 8, Sync the desktop app
// Preconditions: Dr. Revoked Desktop signed in to the desktop app three times on the on-site app; one of those sessions
//   signed out. The Desktop scheme's clock (ShiftedClock) starts with nothing passed.
// Inputs and system conditions: the revocation, as an administrator makes it (DesktopSessions.RevokeAllAsync); GET
//   /api/sync/quick-texts and POST /api/desktop/offline-panel and /api/desktop/unlock with each token, on both sites.
// Test data: none beyond the accounts.
// Steps:
//   1. Revoke the account's desktop sign-ins; use each token on both sites.
//   2. Change the account's password; let 31 days pass; use the revoked tokens again.
// Expected result: Step 1: the two revoked tokens get 401 with "Ehr-Sign-In: revoked" on every request, the cloud app's
//   forwarded ones included; the signed-out token gets 401 without the header. Step 2: the same.
// Creator: Tahseen Arnan
public class TC_UC8_09_RevokedDesktopToken(WebApps apps) : IClassFixture<WebApps>
{
    [Fact]
    public async Task A_revoked_token_carries_the_wipes_marker_whatever_else_is_true_and_a_signed_out_one_never_does()
    {
        var account = await apps.CreateStaffAsync("Dr. Revoked Desktop", StaffRoles.Clinician);
        var revoked = new[] { await apps.DesktopSignInAsync(apps.OnPrem, account), await apps.DesktopSignInAsync(apps.OnPrem, account) };
        var signedOut = await apps.DesktopSignInAsync(apps.OnPrem, account);
        using (var ended = await SendAsync(apps.OnPrem, HttpMethod.Post, "/api/desktop/sign-out", signedOut))
        {
            Assert.Equal(HttpStatusCode.NoContent, ended.StatusCode);
        }

        var administrator = await apps.CreateStaffAsync("Revoking Admin", StaffRoles.Admin);
        await using (var scope = apps.OnPrem.Services.CreateAsyncScope())
        {
            Assert.Equal(2, await scope.ServiceProvider.GetRequiredService<DesktopSessions>()
                .RevokeAllAsync(account.Id, administrator.Id, TestContext.Current.CancellationToken));
        }

        // 1.
        await AssertMarkedAsync(revoked, signedOut);

        // 2.
        await apps.UpdateSecurityStampAsync(account);
        apps.Clock.Pass(TimeSpan.FromDays(31));
        await AssertMarkedAsync(revoked, signedOut);
    }

    async Task AssertMarkedAsync(string[] revoked, string signedOut)
    {
        var requests = new[]
        {
            (apps.OnPrem, HttpMethod.Get, "/api/sync/quick-texts?since=0"), (apps.Cloud, HttpMethod.Get, "/api/sync/quick-texts?since=0"),
            (apps.Cloud, HttpMethod.Post, "/api/desktop/offline-panel"), (apps.OnPrem, HttpMethod.Post, "/api/desktop/unlock"),
        };
        foreach (var (site, method, path) in requests)
        {
            foreach (var token in revoked)
            {
                using var refused = await SendAsync(site, method, path, token);
                Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
                Assert.Equal([DesktopTokenHandler.Revoked], refused.Headers.GetValues(DesktopTokenHandler.SignInHeader));
            }

            using var over = await SendAsync(site, method, path, signedOut);
            Assert.Equal(HttpStatusCode.Unauthorized, over.StatusCode);
            Assert.False(over.Headers.Contains(DesktopTokenHandler.SignInHeader), $"{method} {path}");
        }
    }
}
