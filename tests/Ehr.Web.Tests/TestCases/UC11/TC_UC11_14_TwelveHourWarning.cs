using Ehr.Data.Accounts;
using Microsoft.Playwright;
using Xunit;
using static Ehr.Web.Tests.Browser;
using static Ehr.Web.Tests.SessionBrowser;

namespace Ehr.Web.Tests.TestCases.UC11;

// Test Case ID: TC-UC11-14
// Purpose / scenario: The warning before a sign-in's 12-hour end (the expansion plan's step 18). It opens once, in each
//   page; "Keep working" and Escape each close it for good, so the clinician can finish with every key landing in the
//   page; and the page still ends at the 12-hour end, keeping what was typed.
// Type: Functional (alternative flow)
// Requirements: proposed FR-63, NFR-12
// Use case: UC 11, Sign in with multi-factor authentication
// Preconditions: Dr. Long Shift has a patient on their panel and is signed in on the on-site app, kept busy to 11 h 50 min
//   by a request every 10 minutes. Chromium, its clock installed, the server's cookie clock moved on with it.
// Inputs and system conditions: two pages, a new message and quick texts; 8 minutes with nothing done; Escape in the
//   first, Enter on "Keep working" in the second; 90 seconds more, 10 at a time, typing in the first; then the end.
// Test data: Patient "Synthetic Longshift". Text "Synthetic words typed in the last minutes."
// Steps:
//   1. Open both pages at 11 h 50 min; leave them 8 minutes.
//   2. Press Escape in the first page and Enter in the second.
//   3. Let 90 seconds pass, 10 at a time, typing in the first page.
//   4. Let the 12-hour end pass.
// Expected result: Step 1: each page warns that a sign-in lasts at most 12 hours, with the focus on "Keep working".
//   Step 2: both close. Step 3: neither opens again, and the text is in the message. Step 4: both pages end, saying a
//   sign-in lasts at most 12 hours; the first says what was typed is kept.
// Creator: Tahseen Arnan
[Collection(BrowserTests.Collection)]
public class TC_UC11_14_TwelveHourWarning(WebApps apps) : IClassFixture<WebApps>
{
    const string Typed = "Synthetic words typed in the last minutes.";

    [Fact]
    public async Task The_12_hour_warning_dismissed_stays_closed_and_the_page_still_ends_at_the_limit()
    {
        var clinician = await apps.CreateStaffAsync("Dr. Long Shift", StaffRoles.Clinician);
        var patient = await apps.InsertPatientAsync("Synthetic", "Longshift");
        await apps.AssignAsync(clinician, patient);
        var session = await SignInAsync(apps.OnPrem, clinician);
        for (var passed = TimeSpan.Zero; passed < TimeSpan.FromHours(11) + TimeSpan.FromMinutes(50); passed += TimeSpan.FromMinutes(10))
        {
            apps.Clock.Pass(TimeSpan.FromMinutes(10));
            await SessionTimeTests.AskAsync(session.Client, check: false);
        }

        await using var server = await ClinicalBrowserHost.StartAsync(apps);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await AiReviewBrowserTests.LaunchAsync(playwright);
        await using var context = await ContextAsync(browser, session);
        var message = await context.NewPageAsync();
        var errors = new List<string>();
        message.PageError += (_, error) => errors.Add(error);
        await message.GotoAsync($"{server.Url}/patients/{patient}/messages/new");
        var other = await context.NewPageAsync();
        await other.GotoAsync($"{server.Url}/quick-texts");
        await message.FocusAsync("#message-body");
        await message.Keyboard.TypeAsync("Synthetic ");

        // 1.
        await IdleAsync(apps, message, TimeSpan.FromMinutes(8));
        foreach (var page in new[] { message, other })
        {
            await page.WaitForSelectorAsync(OpenWarning);
            Assert.True(await page.IsVisibleAsync("[data-session-final-text]"));
            Assert.True(await page.EvaluateAsync<bool>("document.activeElement.matches('[data-session-carry-on]')"));
        }

        // 2.
        await message.Keyboard.PressAsync("Escape");
        await other.Keyboard.PressAsync("Enter");
        foreach (var page in new[] { message, other })
        {
            await page.WaitForSelectorAsync(OpenWarning, new() { State = WaitForSelectorState.Detached });
            await WatchWarningAsync(page);
        }

        // 3.
        await message.FocusAsync("#message-body");
        for (var i = 0; i < 9; i++)
        {
            await IdleAsync(apps, message, TimeSpan.FromSeconds(10));
            await message.WaitForTimeoutAsync(300);
            await message.Keyboard.TypeAsync("x");
        }

        await message.Keyboard.TypeAsync(" " + Typed);
        Assert.False(await WarnedAsync(message));
        Assert.False(await WarnedAsync(other));
        Assert.EndsWith(Typed, await message.InputValueAsync("#message-body"), StringComparison.Ordinal);

        // 4.
        await IdleAsync(apps, message, TimeSpan.FromMinutes(1));
        foreach (var page in new[] { message, other })
        {
            await page.WaitForSelectorAsync("[data-session-why='longest']");
            Assert.Contains("a sign-in lasts at most 12 hours", await SeenAsync(page));
        }

        Assert.True(await message.IsVisibleAsync("[data-session-kept]"));
        Assert.Empty(errors);
    }
}
