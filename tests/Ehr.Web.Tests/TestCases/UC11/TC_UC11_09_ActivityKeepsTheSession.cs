using Ehr.Data.Accounts;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Playwright;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.SessionBrowser;

namespace Ehr.Web.Tests.TestCases.UC11;

// Test Case ID: TC-UC11-09
// Purpose / scenario: What counts as activity (the expansion plan's step 18, and Tahseen's ruling that recording counts
//   as activity). Typing keeps a session, though nothing is saved; a keep-alive in one tab closes another tab's warning;
//   a dictation being recorded is never warned over, on-site or off-site, even with its audio not getting through, the
//   page itself keeping the session; a visit recorded for its whole 20 minutes, with no key pressed, is never warned
//   over or signed out; and a visit that is paused may be warned, saying what the dictation service does with it.
// Type: Functional (alternative flow)
// Requirements: proposed FR-63, FR-62, NFR-12
// Use case: UC 11, Sign in with multi-factor authentication; with UC 9 (dictate) and UC 23 (listen to a visit)
// Preconditions: A clinician with the patient on their panel, signed in on the on-site app (or off-site, through the
//   cloud app). Chromium, its clock installed, the server's cookie clock moved on with it. A fake microphone and
//   recorder, and the dictation service's stub.
// Inputs and system conditions: a key every 5 minutes on the new-message page for 20 minutes; two tabs left 13
//   minutes, then Enter in one; Alt+Shift+D on the Dictate page with its audio refused by the network, then 13 minutes
//   30 seconds; the consent and Start listening on the Listen page, then 20 minutes a minute at a time; Pause listening,
//   then 13 minutes.
// Test data: Patients "Synthetic Typingkeeps", "Synthetic Dictationkeeps", "Synthetic Visitkeeps".
// Steps:
//   1. Type a key every 5 minutes for 20 minutes.
//   2. Leave two tabs for 13 minutes; press Enter in the second.
//   3. Dictate, with the audio refused, and leave it 13 minutes and 30 seconds; on-site, then off-site.
//   4. Listen to a visit for 20 minutes.
//   5. Pause a visit and leave it 13 minutes.
// Expected result: Step 1: no warning; the server gives the session 15 minutes. Step 2: both tabs warn; the first's
//   warning closes when the second stays signed in. Step 3: no warning; the session still has 15 minutes, by the
//   page's own keep-alive. Step 4: no warning and no end; at 20 minutes the visit stops at its limit and asks who each
//   voice is, and the session has 15 minutes left. Step 5: the warning opens, with the sentence that the dictation
//   service discards a visit left paused for 10 minutes.
// Creator: Tahseen Arnan
[Collection(BrowserTests.Collection)]
public class TC_UC11_09_ActivityKeepsTheSession(WebApps apps) : IClassFixture<WebApps>
{
    async Task<(StaffAccount Clinician, Guid Patient)> ClinicianAsync(string name, string family)
    {
        apps.Dictation.Reset();
        var clinician = await apps.CreateStaffAsync(name, StaffRoles.Clinician);
        var patient = await apps.InsertPatientAsync("Synthetic", family);
        await apps.AssignAsync(clinician, patient);
        return (clinician, patient);
    }

    [Fact]
    public async Task Typing_keeps_a_session_and_one_tabs_keep_alive_closes_anothers_warning()
    {
        var (clinician, patient) = await ClinicianAsync("Dr. Typing", "Typingkeeps");
        var session = await SignInAsync(apps.OnPrem, clinician);
        await using var server = await ClinicalBrowserHost.StartAsync(apps);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await AiReviewBrowserTests.LaunchAsync(playwright);
        await using var context = await ContextAsync(browser, session);
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{server.Url}/patients/{patient}/messages/new");
        await WatchWarningAsync(page);

        // 1.
        await page.FocusAsync("#message-body");
        for (var i = 0; i < 4; i++)
        {
            await IdleAsync(apps, page, TimeSpan.FromMinutes(5));
            await page.Keyboard.TypeAsync("a");
            await page.WaitForTimeoutAsync(300);
        }

        Assert.False(await WarnedAsync(page));
        Assert.InRange(await LeftAsync(page), 880, 900);

        // 2.
        var second = await context.NewPageAsync();
        await second.GotoAsync($"{server.Url}/quick-texts");
        await IdleAsync(apps, page, TimeSpan.FromMinutes(13));
        await page.WaitForSelectorAsync(OpenWarning);
        await second.WaitForSelectorAsync(OpenWarning);
        await second.Keyboard.PressAsync("Enter");
        await page.WaitForSelectorAsync(OpenWarning, new() { State = WaitForSelectorState.Detached });
        Assert.InRange(await LeftAsync(page), 880, 900);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_dictation_being_recorded_is_never_warned_over(bool offSite)
    {
        var (clinician, patient) = await ClinicianAsync("Dr. Dictating", "Dictationkeeps");
        WebApplicationFactory<Program> app = offSite ? apps.Cloud : apps.OnPrem;
        var session = await SignInAsync(app, clinician);
        await using var server = await ClinicalBrowserHost.StartAsync(apps, app);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await AiReviewBrowserTests.LaunchAsync(playwright);
        await using var context = await ContextAsync(browser, session, microphone: true);
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{server.Url}/patients/{patient}/dictate");
        await WatchWarningAsync(page);

        // 3. Recording, its audio not getting through, so only the page itself keeps the session.
        await page.RouteAsync("**/audio?**", route => route.AbortAsync());
        await page.Keyboard.PressAsync("Alt+Shift+D");
        await page.WaitForFunctionAsync("document.body.dataset.sessionRecording === 'recording'");
        await IdleAsync(apps, page, TimeSpan.FromMinutes(13) + TimeSpan.FromSeconds(30));
        await page.WaitForTimeoutAsync(1000);
        Assert.False(await WarnedAsync(page));
        Assert.InRange(await LeftAsync(page), 880, 900);
        Assert.Equal("recording", await page.EvaluateAsync<string>("document.body.dataset.sessionRecording"));
    }

    [Fact]
    public async Task A_20_minute_visit_is_never_warned_over_or_signed_out()
    {
        var (clinician, patient) = await ClinicianAsync("Dr. Long Visit", "Visitkeeps");
        var session = await SignInAsync(apps.OnPrem, clinician);
        await using var server = await ClinicalBrowserHost.StartAsync(apps);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await AiReviewBrowserTests.LaunchAsync(playwright);
        await using var context = await ContextAsync(browser, session, microphone: true);
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{server.Url}/patients/{patient}/listen");
        await WatchWarningAsync(page);
        await AmbientBrowserTests.ConsentAsync(page);
        await page.WaitForFunctionAsync("document.body.dataset.sessionRecording === 'recording'");
        await page.EvaluateAsync("""
            window.__said = [];
            const status = document.querySelector('[data-ambient-status]');
            new MutationObserver(() => window.__said.push(status.textContent)).observe(status, { childList: true, characterData: true, subtree: true });
            """);

        // 4. Twenty minutes, a minute at a time, with no key pressed.
        await StepAsync(apps, page, TimeSpan.FromMinutes(20) + TimeSpan.FromSeconds(5));
        await page.WaitForSelectorAsync("[data-ambient-voice='1']");
        Assert.False(await WarnedAsync(page));
        Assert.Equal(0, await page.Locator("[data-session-why]").CountAsync());
        Assert.Contains(await page.EvaluateAsync<string[]>("window.__said"), said => said.StartsWith("Listening stopped at its 20-minute limit.", StringComparison.Ordinal));
        Assert.InRange(await LeftAsync(page), 840, 900);
    }

    [Fact]
    public async Task A_paused_visit_is_warned_saying_what_the_dictation_service_does_with_it()
    {
        var (clinician, patient) = await ClinicianAsync("Dr. Paused Visit", "Pausedvisit");
        var session = await SignInAsync(apps.OnPrem, clinician);
        await using var server = await ClinicalBrowserHost.StartAsync(apps);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await AiReviewBrowserTests.LaunchAsync(playwright);
        await using var context = await ContextAsync(browser, session, microphone: true);
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{server.Url}/patients/{patient}/listen");
        await AmbientBrowserTests.ConsentAsync(page);
        await page.WaitForFunctionAsync("document.body.dataset.sessionRecording === 'recording'");

        // 5.
        await page.ClickAsync("[data-ambient-pause]");
        Assert.Equal("waiting", await page.EvaluateAsync<string>("document.body.dataset.sessionRecording"));
        await IdleAsync(apps, page, TimeSpan.FromMinutes(13));
        await page.WaitForSelectorAsync(OpenWarning);
        Assert.True(await page.IsVisibleAsync("[data-session-waiting]"));
        Assert.Contains("The dictation service discards a visit left like that for 10 minutes", await page.TextContentAsync("[data-session-waiting]"));
    }
}
