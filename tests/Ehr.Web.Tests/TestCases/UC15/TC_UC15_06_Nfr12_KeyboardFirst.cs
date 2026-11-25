using Microsoft.Playwright;
using Xunit;
using static Ehr.Web.Tests.InboxTests;

namespace Ehr.Web.Tests.TestCases.UC15;

// Test Case ID: TC-UC15-06
// Purpose / scenario: The inbox is keyboard-first: from any clinical page to an item marked done, and on to a
//   patient's chart, with no mouse, in a real browser.
// Type: Non-functional (usability)
// Requirements: NFR-12
// Use case: UC 15, Work through the inbox
// Preconditions: The clinician is signed in on the on-site app, with two items in their inbox about a patient on
//   their panel. Chromium, driven by Playwright, with the keyboard only.
// Inputs and system conditions: Key presses only: Tab and Enter.
// Test data: Clinician "Dr. Inbox Keys". Items "Synthetic: first by keyboard" and "Synthetic: second by keyboard".
// Steps:
//   1. Open the patients page. Press Tab until the app bar's Inbox link has the focus, and press Enter.
//   2. Press Tab until the first item's "Mark done" button has the focus, and press Enter.
//   3. Press Tab until the remaining item's patient link has the focus, and press Enter.
// Expected result: Step 1 opens the inbox. Step 2 leaves one item, and the app bar's count goes from 2 to 1. Step 3
//   opens the patient's chart. Every focused control shows the focus ring, and the page reports no script errors.
// Creator: Tahseen Arnan (expansion step 3); reviewed and run by Samir Mukshe, approving PR 2
//
// In the browser tests' collection, which doesn't run beside the others (AiReviewBrowserTests says why).
[Collection(BrowserTests.Collection)]
public class TC_UC15_06_Nfr12_KeyboardFirst(WebApps apps) : IClassFixture<WebApps>
{
    [Fact]
    public async Task The_inbox_works_from_the_keyboard()
    {
        var (clinician, patient, session) = await ClinicianAsync(apps, "Dr. Inbox Keys");
        var first = await AddAsync(apps, clinician.Id, "Synthetic: first by keyboard", patient, minutesAgo: 10);
        await AddAsync(apps, clinician.Id, "Synthetic: second by keyboard", patient);
        await using var server = await ClinicalBrowserHost.StartAsync(apps);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await AiReviewBrowserTests.LaunchAsync(playwright);
        await using var context = await AiReviewBrowserTests.ContextAsync(browser, session);
        var page = await context.NewPageAsync();
        var errors = new List<string>();
        page.PageError += (_, error) => errors.Add(error);
        const string focusRing = "getComputedStyle(document.activeElement).outlineStyle !== 'none'";

        await page.GotoAsync($"{server.Url}/patients");
        Assert.Equal("2", System.Text.RegularExpressions.Regex.Match(await page.Locator("[data-inbox-count]").InnerTextAsync(), "\\d+").Value);
        await AiReviewBrowserTests.TabToAsync(page, "a.sc-appbar__link[href='/inbox']");
        Assert.True(await page.EvaluateAsync<bool>(focusRing));
        await page.Keyboard.PressAsync("Enter");
        await page.WaitForURLAsync("**/inbox");

        await AiReviewBrowserTests.TabToAsync(page, "tbody tr:first-child button");
        Assert.True(await page.EvaluateAsync<bool>(focusRing));
        await page.Keyboard.PressAsync("Enter");
        await page.WaitForFunctionAsync("document.querySelectorAll('tbody tr').length === 1");
        Assert.Contains("second by keyboard", await page.Locator("tbody").InnerTextAsync());
        Assert.True(await IsDoneAsync(apps, first));

        await AiReviewBrowserTests.TabToAsync(page, "tbody a");
        await page.Keyboard.PressAsync("Enter");
        await page.WaitForURLAsync($"**/patients/{patient}");
        Assert.Empty(errors);
    }
}
