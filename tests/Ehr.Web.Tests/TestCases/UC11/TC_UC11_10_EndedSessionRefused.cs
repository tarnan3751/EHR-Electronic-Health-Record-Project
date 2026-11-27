using System.Net;
using Ehr.Data.Accounts;
using Ehr.Domain.Accounts;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Ehr.Web.Tests.Browser;

namespace Ehr.Web.Tests.TestCases.UC11;

// Test Case ID: TC-UC11-10
// Purpose / scenario: A session that has ended stays ended, whatever the browser sends (the expansion plan's step 18):
//   15 minutes after its last request a page sends it to sign in with nothing of the patient on it, the dictation API
//   answers 401, and neither the keep-alive nor a check brings it back; a check, at any age, never keeps a session;
//   and a check from an account that has since been disabled is refused once the account is checked.
// Type: Functional (invalid input)
// Requirements: proposed FR-63, FR-28
// Use case: UC 11, Sign in with multi-factor authentication
// Preconditions: Dr. Ended has a patient on their panel and is signed in on the on-site app. The cookies' clock can be
//   moved on (ShiftedClock).
// Inputs and system conditions: GET /patients/{id}, POST /api/dictation/sessions, GET /session with and without the
//   check header (Ehr-Session-Check), after 15 minutes and 5 seconds without a request; then, for Dr. Disabled, a check
//   6 minutes after signing in with the account locked out meanwhile.
// Test data: Patient "Synthetic Endedchart". Dr. Ended, Dr. Disabled.
// Steps:
//   1. Let 15 minutes and 5 seconds pass; open the patient's chart.
//   2. Start a dictation through the API with the page's form token.
//   3. Ask /session as a keep-alive, then as a check.
//   4. Sign in as Dr. Disabled; disable the account; let 6 minutes pass; check.
// Expected result: Step 1 is a redirect to /sign-in, whose page doesn't name the patient. Step 2 is 401. Step 3 is a
//   redirect to /sign-in both times, and the chart still is afterwards. Step 4 is a redirect to /sign-in.
// Creator: Tahseen Arnan
public class TC_UC11_10_EndedSessionRefused(WebApps apps) : IClassFixture<WebApps>
{
    [Fact]
    public async Task A_session_that_has_ended_stays_ended()
    {
        var clinician = await apps.CreateStaffAsync("Dr. Ended", StaffRoles.Clinician);
        var patient = await apps.InsertPatientAsync("Synthetic", "Endedchart");
        await apps.AssignAsync(clinician, patient);
        var session = await SignInAsync(apps.OnPrem, clinician);
        var token = Token(await session.Client.GetStringAsync("/quick-texts", Cancel));
        var plain = Client(apps.OnPrem, session.Jar, followRedirects: false);

        // 1.
        apps.Clock.Pass(SessionTimes.Idle + TimeSpan.FromSeconds(5));
        using (var chart = await plain.GetAsync($"/patients/{patient}", Cancel))
        {
            Assert.Equal(HttpStatusCode.Redirect, chart.StatusCode);
            Assert.Equal("/sign-in", new Uri(Base, chart.Headers.Location!).AbsolutePath);
        }

        Assert.DoesNotContain("Endedchart", await Client(apps.OnPrem, session.Jar).GetStringAsync($"/patients/{patient}", Cancel));

        // 2.
        using (var start = new HttpRequestMessage(HttpMethod.Post, "/api/dictation/sessions"))
        {
            start.Headers.Add("RequestVerificationToken", token);
            using var refused = await plain.SendAsync(start, Cancel);
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        }

        // 3.
        foreach (var check in new[] { false, true })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/session");
            if (check)
            {
                request.Headers.Add(SessionTimes.CheckHeader, "1");
            }

            using var answer = await plain.SendAsync(request, Cancel);
            Assert.Equal(HttpStatusCode.Redirect, answer.StatusCode);
            Assert.Equal("/sign-in", new Uri(Base, answer.Headers.Location!).AbsolutePath);
        }

        Assert.False(await SessionTimeTests.SignedInAsync(apps.OnPrem, session));

        // 4.
        var disabled = await apps.CreateStaffAsync("Dr. Disabled", StaffRoles.Clinician);
        var disabledSession = await SignInAsync(apps.OnPrem, disabled);
        await using (var scope = apps.OnPrem.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<StaffUser>>();
            var user = (await users.FindByIdAsync(disabled.Id.ToString()))!;
            Assert.True((await users.SetLockoutEndDateAsync(user, DateTimeOffset.UtcNow.AddYears(100))).Succeeded);
        }

        apps.Clock.Pass(TimeSpan.FromMinutes(6));
        using var disabledCheck = new HttpRequestMessage(HttpMethod.Get, "/session");
        disabledCheck.Headers.Add(SessionTimes.CheckHeader, "1");
        using var disabledAnswer = await Client(apps.OnPrem, disabledSession.Jar, followRedirects: false).SendAsync(disabledCheck, Cancel);
        Assert.Equal(HttpStatusCode.Redirect, disabledAnswer.StatusCode);
    }
}
