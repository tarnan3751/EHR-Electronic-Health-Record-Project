using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Ehr.Data.Accounts;
using Ehr.Data.Audit;
using Ehr.Domain.Accounts;
using Ehr.Web.Api.Desktop;
using Ehr.Web.Tests.TestCases.UC8;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;
using static Ehr.Web.Tests.Browser;

namespace Ehr.Web.Tests.TestCases.UC7;

// Test Case ID: TC-UC7-07
// Purpose / scenario: The desktop app's lock opens with the account's password alone, checked by the server when it can
//   be reached (the expansion plan's step 19a, Tahseen's ruling 11b of 25 November 2026): for the token's own account,
//   named by nothing in the request; each unlock and each refusal recorded; a wrong password counting towards the
//   account's lockout, as at sign-in; forwarded by the cloud app, and refused there with 503 while the clinic's server
//   can't be reached, so the app checks the password on the computer instead; and refused, saying so, once the sign-in
//   is 12 hours old.
// Type: Functional (successful flow, invalid input, exceptional condition)
// Requirements: proposed FR-68, FR-28
// Use case: UC 7, Work offline in the desktop app
// Preconditions: Dr. Unlock, Dr. Mistype and Dr. Overnight are signed in to the desktop app on the on-site app.
// Inputs and system conditions: POST /api/desktop/unlock with each token, on the on-site app, the cloud app, and the
//   cloud app cut off from the clinic's server; the Desktop scheme's clock moved on 12 hours at the end.
// Test data: the test accounts' password, and a wrong one.
// Steps:
//   1. Unlock Dr. Unlock with the right password, then a wrong one, then the right one with another address in the
//      request; unlock through the cloud app, and through the cloud app cut off.
//   2. Unlock with no token.
//   3. Unlock Dr. Mistype with a wrong password five times, then sync with the token.
//   4. Let 12 hours pass; unlock Dr. Overnight with the right password.
// Expected result: Step 1: 204; 403 "Wrong password.", recorded as a failed sign-in with the desktop session; 204, the
//   address ignored; 204 through the cloud app; 503 cut off. Two unlocks recorded. Step 2: 401. Step 3: four 403s
//   "Wrong password.", then "This account is locked after too many failed attempts. Try again in 15 minutes."; the
//   sync is refused with 401 and no Ehr-Sign-In header: that session is over. Step 4: 401 with "Ehr-Sign-In: ended".
// Creator: Tahseen Arnan
public class TC_UC7_07_DesktopUnlock(WebApps apps) : IClassFixture<WebApps>
{
    const string Unlock = "/api/desktop/unlock";

    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_lock_opens_with_the_tokens_own_password_counted_and_recorded_as_sign_in_is()
    {
        var clinician = await apps.CreateStaffAsync("Dr. Unlock", StaffRoles.Clinician);
        var mistyping = await apps.CreateStaffAsync("Dr. Mistype", StaffRoles.Clinician);
        var overnight = await apps.CreateStaffAsync("Dr. Overnight", StaffRoles.Clinician);
        var token = await apps.DesktopSignInAsync(apps.OnPrem, clinician);
        var mistypingToken = await apps.DesktopSignInAsync(apps.OnPrem, mistyping);
        var overnightToken = await apps.DesktopSignInAsync(apps.OnPrem, overnight);

        using (var right = await UnlockAsync(apps.OnPrem, token, new { password = WebApps.StaffPassword }))
        {
            Assert.Equal(HttpStatusCode.NoContent, right.StatusCode);
        }

        using (var wrong = await UnlockAsync(apps.OnPrem, token, new { password = "not-the-password" }))
        {
            Assert.Equal(HttpStatusCode.Forbidden, wrong.StatusCode);
            Assert.Equal("Wrong password.", await ProblemAsync(wrong));
        }

        using (var named = await UnlockAsync(apps.OnPrem, token, new { email = "someone.else@example.com", password = WebApps.StaffPassword }))
        {
            Assert.Equal(HttpStatusCode.NoContent, named.StatusCode);
        }

        using (var offSite = await UnlockAsync(apps.Cloud, token, new { password = WebApps.StaffPassword }))
        {
            Assert.Equal(HttpStatusCode.NoContent, offSite.StatusCode);
        }

        using (var cutOff = await UnlockAsync(apps.CloudCutOff, token, new { password = WebApps.StaffPassword }))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, cutOff.StatusCode);
        }

        Assert.Equal(3, await apps.AuditEventsAsync(AuditActions.Unlock, actorId: clinician.Id));
        Assert.Equal(1, await apps.AuditEventsAsync(AuditActions.SignInFailed, actorId: clinician.Id));
        Assert.Equal(3, await DesktopEventsAsync(AuditActions.Unlock, clinician.Id));
        Assert.Equal(1, await DesktopEventsAsync(AuditActions.SignInFailed, clinician.Id));

        using (var anonymous = await CreateClient(apps.OnPrem).PostAsJsonAsync(Unlock, new { password = WebApps.StaffPassword }, Cancel))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        }

        for (var attempt = 1; attempt <= 5; attempt++)
        {
            using var wrong = await UnlockAsync(apps.OnPrem, mistypingToken, new { password = "not-the-password" });
            Assert.Equal(HttpStatusCode.Forbidden, wrong.StatusCode);
            Assert.Equal(
                attempt < 5 ? "Wrong password." : "This account is locked after too many failed attempts. Try again in 15 minutes.",
                await ProblemAsync(wrong));
        }

        using (var lockedOut = await TC_UC8_07_TwelveHourDesktopSignIn.SendAsync(apps.OnPrem, HttpMethod.Get, "/api/sync/quick-texts?since=0", mistypingToken))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, lockedOut.StatusCode);
            Assert.False(lockedOut.Headers.Contains(DesktopTokenHandler.SignInHeader));
        }

        apps.Clock.Pass(TimeSpan.FromHours(12));
        using var ended = await UnlockAsync(apps.OnPrem, overnightToken, new { password = WebApps.StaffPassword });
        Assert.Equal(HttpStatusCode.Unauthorized, ended.StatusCode);
        Assert.Equal([DesktopTokenHandler.SignInEnded], ended.Headers.GetValues(DesktopTokenHandler.SignInHeader));
        Assert.Equal(0, await apps.AuditEventsAsync(AuditActions.Unlock, actorId: overnight.Id));
    }

    static async Task<HttpResponseMessage> UnlockAsync(WebApplicationFactory<Program> app, string token, object body)
    {
        var client = CreateClient(app);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.PostAsJsonAsync(Unlock, body, Cancel);
    }

    static async Task<string> ProblemAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<Refusal>(JsonSerializerOptions.Web, Cancel))!.Problem;

    // How many of the actor's events of this action say they came from the desktop app.
    async Task<long> DesktopEventsAsync(string action, Guid actorId)
    {
        await using var connection = await apps.OpenDatabaseAsync();
        await using var command = new Npgsql.NpgsqlCommand(
            "SELECT count(*) FROM ehr.audit_events WHERE action = @action AND actor_id = @actor AND details->>'client' = 'desktop'", connection);
        command.Parameters.AddWithValue("action", action);
        command.Parameters.AddWithValue("actor", actorId);
        return (long)(await command.ExecuteScalarAsync(Cancel))!;
    }
}
