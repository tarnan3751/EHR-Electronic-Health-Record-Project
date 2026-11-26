using Microsoft.Playwright;
using Xunit;
using static Ehr.Web.Tests.NoteAddendumTests;
using static Ehr.Web.Tests.OrderTests;
using static Ehr.Web.Tests.VisitSummaryTests;

namespace Ehr.Web.Tests.TestCases.UC14;

// Test Case ID: TC-UC14-25
// Purpose / scenario: Writing and releasing a visit summary is keyboard-first: from the chart to the released summary
//   with no mouse, in a real browser.
// Type: Non-functional (usability)
// Requirements: NFR-12
// Use case: UC 14, Review and sign a clinical note
// Preconditions: The clinician is signed in on the on-site app with the patient on their panel and has signed a note.
//   Chromium, driven by Playwright, with the keyboard only.
// Inputs and system conditions: Key presses only: Tab, Enter and typing.
// Test data: The three parts of the test's summary.
// Steps:
//   1. Open the chart. Press Tab until the signed note's link has the focus, and press Enter.
//   2. Press Tab until "Write visit summary" has the focus, and press Enter.
//   3. Type What we talked about (the cursor starts in it), press Tab and type What we found, press Tab and type What
//      happens next. Press Tab until "Release to the patient" has the focus, and press Enter.
// Expected result: Step 1 opens the signed note. Step 2 opens the summary page with the cursor in What we talked about.
//   Step 3 releases it: the note's page says "Released." and shows the summary. Every focused control shows the focus
//   ring, and the pages report no script errors.
// Creator: Tahseen Arnan
//
// In the browser tests' collection, which doesn't run beside the others (AiReviewBrowserTests says why).
[Collection(BrowserTests.Collection)]
public class TC_UC14_25_Nfr12_VisitSummaryKeyboardFirst(WebApps apps) : IClassFixture<WebApps>
{
    [Fact]
    public async Task A_summary_is_written_and_released_from_the_keyboard()
    {
        var (_, patient, session) = await OnPanelAsync(apps);
        var note = await SignedNoteAsync(session.Client, patient);
        await using var server = await ClinicalBrowserHost.StartAsync(apps);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await AiReviewBrowserTests.LaunchAsync(playwright);
        await using var context = await AiReviewBrowserTests.ContextAsync(browser, session);
        var page = await context.NewPageAsync();
        var errors = new List<string>();
        page.PageError += (_, error) => errors.Add(error);
        const string focusRing = "getComputedStyle(document.activeElement).outlineStyle !== 'none'";

        await page.GotoAsync($"{server.Url}/patients/{patient}");
        await AiReviewBrowserTests.TabToAsync(page, $"a[href='{NotePath(patient, note)}']");
        Assert.True(await page.EvaluateAsync<bool>(focusRing));
        await page.Keyboard.PressAsync("Enter");
        await page.WaitForURLAsync($"**{NotePath(patient, note)}");
        await AiReviewBrowserTests.TabToAsync(page, "a[data-write-summary]");
        Assert.True(await page.EvaluateAsync<bool>(focusRing));
        await page.Keyboard.PressAsync("Enter");
        await page.WaitForURLAsync("**/summary");
        // Focus lands once the page has loaded: waited for, not looked at once.
        await page.WaitForFunctionAsync("document.activeElement.matches('textarea[name=\"TalkedAbout\"]')");
        Assert.True(await page.EvaluateAsync<bool>(focusRing));

        await page.Keyboard.TypeAsync(TalkedAbout);
        await page.Keyboard.PressAsync("Tab");
        Assert.True(await page.EvaluateAsync<bool>("document.activeElement.matches('textarea[name=\"Found\"]')"));
        await page.Keyboard.TypeAsync("Your chest sounded clear.");
        await page.Keyboard.PressAsync("Tab");
        Assert.True(await page.EvaluateAsync<bool>("document.activeElement.matches('textarea[name=\"NextSteps\"]')"));
        await page.Keyboard.TypeAsync(NextSteps);
        await AiReviewBrowserTests.TabToAsync(page, "button[data-release-summary]");
        Assert.True(await page.EvaluateAsync<bool>(focusRing));
        await page.Keyboard.PressAsync("Enter");
        await page.WaitForURLAsync(url => url.Contains("done=summary", StringComparison.Ordinal));
        Assert.Contains("Released.", await page.Locator(".sc-notice--success").TextContentAsync());
        Assert.Contains(TalkedAbout, await page.Locator("[data-summary=standing]").TextContentAsync());

        Assert.Single(await SummariesAsync(apps, patient));
        Assert.Empty(errors);
    }
}
