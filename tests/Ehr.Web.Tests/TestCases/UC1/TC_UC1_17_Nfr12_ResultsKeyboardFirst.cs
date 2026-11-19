using Microsoft.Playwright;
using Xunit;
using static Ehr.Web.Tests.OrderTests;
using static Ehr.Web.Tests.ResultTests;

namespace Ehr.Web.Tests.TestCases.UC1;

// Test Case ID: TC-UC1-17
// Purpose / scenario: Entering an order's results and acknowledging them is keyboard-first: from the chart to the
//   filed results, and from the inbox to the acknowledgment, with no mouse, in a real browser.
// Type: Non-functional (usability)
// Requirements: NFR-12
// Use case: UC 1, Place an order
// Preconditions: The clinician is signed in on the on-site app with the patient on their panel and has signed a
//   complete blood count. Chromium, driven by Playwright, with the keyboard only.
// Inputs and system conditions: Key presses only: Tab, Enter and typing.
// Test data: Hematocrit 44, hemoglobin 14, leukocytes 7, platelets 250, typed in the order the form lists the tests.
// Steps:
//   1. Open the chart. Press Tab until "Enter results" has the focus, and press Enter.
//   2. Type the four values, pressing Tab between them (the cursor starts in the first field); press Tab until "File
//      results" has the focus, and press Enter.
//   3. Open the inbox. Press Tab until "Open the results" has the focus, and press Enter. Press Enter again:
//      "Acknowledge results" already has the focus.
// Expected result: Step 1 opens the results page with the cursor in the first test's field. Step 2 files all four:
//   the page says "Saved:" and lists four results. Step 3 acknowledges: the page says "Acknowledged:" and the app
//   bar's inbox count is gone. Every focused control shows the focus ring, and the page reports no script errors.
// Creator: Tahseen Arnan
//
// In the browser tests' collection, which doesn't run beside the others (AiReviewBrowserTests says why).
[Collection(BrowserTests.Collection)]
public class TC_UC1_17_Nfr12_ResultsKeyboardFirst(WebApps apps) : IClassFixture<WebApps>
{
    [Fact]
    public async Task Entering_and_acknowledging_results_works_from_the_keyboard()
    {
        var (_, patient, session) = await OnPanelAsync(apps);
        var order = await SignedAsync(session.Client, patient, "lab", "Complete blood count (CBC)", Cbc);
        await using var server = await ClinicalBrowserHost.StartAsync(apps);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await AiReviewBrowserTests.LaunchAsync(playwright);
        await using var context = await AiReviewBrowserTests.ContextAsync(browser, session);
        var page = await context.NewPageAsync();
        var errors = new List<string>();
        page.PageError += (_, error) => errors.Add(error);
        const string focusRing = "getComputedStyle(document.activeElement).outlineStyle !== 'none'";

        await page.GotoAsync($"{server.Url}/patients/{patient}");
        await AiReviewBrowserTests.TabToAsync(page, "a[data-enter-results]");
        Assert.True(await page.EvaluateAsync<bool>(focusRing));
        await page.Keyboard.PressAsync("Enter");
        await page.WaitForURLAsync("**/results");
        // Focus lands once the page has loaded: waited for, not looked at once (it failed now and then under load).
        await page.WaitForFunctionAsync("document.activeElement.matches('input[name^=\"Values[\"]')");
        Assert.True(await page.EvaluateAsync<bool>(focusRing));

        foreach (var value in new[] { "44", "14", "7", "250" })
        {
            Assert.True(await page.EvaluateAsync<bool>("document.activeElement.matches('input[name^=\"Values[\"]')"));
            await page.Keyboard.TypeAsync(value);
            await page.Keyboard.PressAsync("Tab");
        }

        await AiReviewBrowserTests.TabToAsync(page, "button[data-file-results]");
        Assert.True(await page.EvaluateAsync<bool>(focusRing));
        await page.Keyboard.PressAsync("Enter");
        await page.WaitForURLAsync(url => url.Contains("done=filed", StringComparison.Ordinal));
        Assert.Contains("Saved:", await page.Locator(".sc-notice--success").TextContentAsync());
        Assert.Equal(4, await page.Locator("tr[data-result]").CountAsync());

        await page.GotoAsync($"{server.Url}/inbox");
        await AiReviewBrowserTests.TabToAsync(page, "a[data-inbox-source]");
        await page.Keyboard.PressAsync("Enter");
        await page.WaitForURLAsync("**/results");
        // Focus lands once the page has loaded: waited for, not looked at once (it failed now and then under load).
        await page.WaitForFunctionAsync("document.activeElement.matches('button[data-acknowledge]')");
        Assert.True(await page.EvaluateAsync<bool>(focusRing));
        await page.Keyboard.PressAsync("Enter");
        await page.WaitForURLAsync(url => url.Contains("done=acknowledged", StringComparison.Ordinal));
        Assert.Contains("Acknowledged:", await page.Locator(".sc-notice--success").TextContentAsync());
        Assert.Equal(0, await page.Locator("[data-inbox-count]").CountAsync());

        Assert.Equal(4, (await FiledAsync(apps, patient)).Count);
        Assert.Single(await MessageTests.RowsAsync(apps, patient, "id::text", "result_acknowledgments", "id"));
        Assert.Empty(errors);
    }
}
