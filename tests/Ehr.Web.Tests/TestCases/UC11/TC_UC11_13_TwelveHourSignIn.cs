using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Ehr.Data.Accounts;
using Ehr.Domain.Accounts;
using Xunit;
using static Ehr.Web.Tests.Browser;

namespace Ehr.Web.Tests.TestCases.UC11;

// Test Case ID: TC-UC11-13
// Purpose / scenario: A sign-in lasts at most 12 hours, staff and portal, however active (the expansion plan's step 18,
//   Tahseen's ruling of 24 November 2026), and that end never cuts a recording off: in the sign-in's last stretch the
//   session page says the end is final, and starting a dictation or a visit is refused, in words that say to sign in
//   again first, once less is left than its longest recording (3 and 20 minutes), as Tahseen agreed on 24 November 2026.
// Type: Functional (alternative flow)
// Requirements: proposed FR-63, FR-28
// Use case: UC 11, Sign in with multi-factor authentication
// Preconditions: Dr. Long Day has a patient on their panel and is signed in on the on-site app; the patient has a
//   confirmed portal account, signed in too. Both sessions make a request every 10 minutes of the cookies' clock
//   (ShiftedClock), so neither is ever idle for 15.
// Inputs and system conditions: GET /session every 10 minutes; at 11 h 39 min, 11 h 41 min, 11 h 51 min and 11 h 58
//   min, POST /api/dictation/sessions/ambient/start (a visit) and /api/dictation/sessions (a dictation); at 12 hours, a
//   page.
// Test data: Patient "Synthetic Longday".
// Steps:
//   1. Keep both sessions busy to 11 h 39 min, 21 minutes before the end; start a visit.
//   2. At 11 h 41 min, start a visit; read the session page.
//   3. At 11 h 51 min, read the session page and start a dictation; at 11 h 58 min, start another.
//   4. At 12 hours, open a page as each.
// Expected result: Step 1: the visit starts (201). Step 2: 409 sign_in_ending, "Sign in again first. Your sign-in ends
//   in 19 minutes, too soon for a visit of up to 20 minutes. …"; the
//   session page doesn't call the end final yet, since the idle end comes first. Step 3: at 11 h 51 min the session page
//   says the end is final, 9 minutes away; the first dictation starts (201), the second, 2 minutes before the end, is
//   refused the same way, for a dictation. Step 4: each is sent to its sign-in.
// Creator: Tahseen Arnan
public class TC_UC11_13_TwelveHourSignIn(WebApps apps) : IClassFixture<WebApps>
{
    static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_sign_in_ends_at_12_hours_and_no_recording_starts_that_it_would_cut_off()
    {
        var clinician = await apps.CreateStaffAsync("Dr. Long Day", StaffRoles.Clinician);
        var patient = await apps.InsertPatientAsync("Synthetic", "Longday");
        await apps.AssignAsync(clinician, patient);
        var session = await SignInAsync(apps.OnPrem, clinician);
        var token = Token(await session.Client.GetStringAsync($"/patients/{patient}/listen", Cancel));
        var (portal, portalJar) = await PortalResultsTests.SignInPatientAsync(apps, apps.OnPrem, patient);
        var passed = TimeSpan.Zero;

        async Task KeepBusyUntilAsync(TimeSpan until)
        {
            while (passed + TimeSpan.FromMinutes(10) <= until)
            {
                apps.Clock.Pass(TimeSpan.FromMinutes(10));
                passed += TimeSpan.FromMinutes(10);
                await SessionTimeTests.AskAsync(session.Client, check: false);
                await SessionTimeTests.AskAsync(portal, check: false, "/portal/session");
            }

            apps.Clock.Pass(until - passed);
            passed = until;
        }

        async Task<HttpResponseMessage> StartVisitAsync() =>
            await PostJsonAsync($"{AmbientFlows.ListenApi}/start", AmbientFlows.Consent(Guid.NewGuid(), patient));

        async Task<HttpResponseMessage> PostJsonAsync(string path, object? body)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = body is null ? null : JsonContent.Create(body) };
            request.Headers.Add("RequestVerificationToken", token);
            return await session.Client.SendAsync(request, Cancel);
        }

        // 1. A visit's length left, and a minute.
        await KeepBusyUntilAsync(TimeSpan.FromHours(11) + TimeSpan.FromMinutes(39));
        using (var visit = await StartVisitAsync())
        {
            Assert.Equal(HttpStatusCode.Created, visit.StatusCode);
        }

        // 2.
        await KeepBusyUntilAsync(TimeSpan.FromHours(11) + TimeSpan.FromMinutes(41));
        using (var late = await StartVisitAsync())
        {
            Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
            var refusal = JsonNode.Parse(await late.Content.ReadAsStringAsync(Cancel))!;
            Assert.Equal("sign_in_ending", (string)refusal["error"]!);
            Assert.Equal("Sign in again first. Your sign-in ends in 19 minutes, too soon for a visit of up to 20 minutes. "
                + "Sign out, sign in again, then start listening.", (string)refusal["message"]!);
        }

        var (before, _) = await SessionTimeTests.AskAsync(session.Client, check: true);
        Assert.False((bool)before["final"]!);
        // The portal kept busy too, its last request 11 minutes ago.
        await SessionTimeTests.AskAsync(portal, check: false, "/portal/session");

        // 3.
        await KeepBusyUntilAsync(TimeSpan.FromHours(11) + TimeSpan.FromMinutes(51));
        var (answer, _) = await SessionTimeTests.AskAsync(session.Client, check: true);
        Assert.True((bool)answer["final"]!);
        // Nine minutes on the cookies' clock, less the real seconds the test has taken since signing in.
        Assert.InRange((int)answer["left"]!, 480, 540);
        using (var dictation = await PostJsonAsync("/api/dictation/sessions", null))
        {
            Assert.Equal(HttpStatusCode.Created, dictation.StatusCode);
        }

        await KeepBusyUntilAsync(TimeSpan.FromHours(11) + TimeSpan.FromMinutes(58));
        using (var tooLate = await PostJsonAsync("/api/dictation/sessions", null))
        {
            Assert.Equal(HttpStatusCode.Conflict, tooLate.StatusCode);
            Assert.Equal("Sign in again first. Your sign-in ends in 2 minutes, too soon for a dictation of up to 3 minutes. "
                + "Sign out, sign in again, then dictate.", (string)JsonNode.Parse(await tooLate.Content.ReadAsStringAsync(Cancel))!["message"]!);
        }

        // 4.
        await KeepBusyUntilAsync(TimeSpan.FromHours(12));
        Assert.False(await SessionTimeTests.SignedInAsync(apps.OnPrem, session));
        using var portalPage = await Client(apps.OnPrem, portalJar, followRedirects: false).GetAsync("/portal", Cancel);
        Assert.Equal(HttpStatusCode.Redirect, portalPage.StatusCode);
        Assert.Equal("/portal/sign-in", new Uri(Base, portalPage.Headers.Location!).AbsolutePath);
    }
}
