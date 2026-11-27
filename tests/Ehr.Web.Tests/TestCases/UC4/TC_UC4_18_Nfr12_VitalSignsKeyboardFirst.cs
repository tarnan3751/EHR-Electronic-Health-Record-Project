using Microsoft.Playwright;
using Xunit;
using static Ehr.Web.Tests.OrderTests;
using static Ehr.Web.Tests.VitalTests;

namespace Ehr.Web.Tests.TestCases.UC4;

// Test Case ID: TC-UC4-18
// Purpose / scenario: Recording vital signs is keyboard-first: from the chart to the saved set with no mouse, in a
//   real browser; and a trend's table of values opens from the keyboard.
// Type: Non-functional (usability)
// Requirements: NFR-12
// Use case: UC 4, View patient chart
// Preconditions: The clinician is signed in on the on-site app with the patient on their panel, who has one earlier
//   set of vital signs (blood pressure 148/92). Chromium, driven by Playwright, with the keyboard only.
// Inputs and system conditions: Key presses only: Tab, Enter and typing.
// Test data: Systolic 126, diastolic 80, typed in the order the form lists them.
// Steps:
//   1. Open the chart. Press Tab until "Flowsheet, trends and recording" has the focus, and press Enter.
//   2. Type 126, press Tab, type 80 (the cursor starts in the first field); press Tab until "Record vital signs" has
//      the focus, and press Enter.
//   3. Press Tab until the first trend's "The values as a table" has the focus, and press Enter.
// Expected result: Step 1 opens the vital signs page with the cursor in the systolic field. Step 2 saves: the page
//   says "Saved." and shows two trend charts. Step 3 opens the table, which lists both values. Every focused control
//   shows the focus ring, the charts are drawn (each has a size), and the page reports no script errors.
// Creator: Tahseen Arnan
//
// In the browser tests' collection, which doesn't run beside the others (AiReviewBrowserTests says why).
[Collection(BrowserTests.Collection)]
public class TC_UC4_18_Nfr12_VitalSignsKeyboardFirst(WebApps apps) : IClassFixture<WebApps>
{
    [Fact]
    public async Task Recording_vital_signs_and_reading_a_trends_table_work_from_the_keyboard()
    {
        var (_, patient, session) = await OnPanelAsync(apps);
        using var earlier = await RecordAsync(apps, session.Client, patient, 24 * 60, (Systolic, "148"), (Diastolic, "92"));
        await using var server = await ClinicalBrowserHost.StartAsync(apps);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await AiReviewBrowserTests.LaunchAsync(playwright);
        await using var context = await AiReviewBrowserTests.ContextAsync(browser, session);
        var page = await context.NewPageAsync();
        var errors = new List<string>();
        page.PageError += (_, error) => errors.Add(error);
        const string focusRing = "getComputedStyle(document.activeElement).outlineStyle !== 'none'";

        await page.GotoAsync($"{server.Url}/patients/{patient}");
        await AiReviewBrowserTests.TabToAsync(page, "a[data-vitals]");
        Assert.True(await page.EvaluateAsync<bool>(focusRing));
        await page.Keyboard.PressAsync("Enter");
        await page.WaitForURLAsync("**/vitals");
        // Focus lands once the page has loaded: waited for, not looked at once (it failed now and then under load).
        await page.WaitForFunctionAsync($"document.activeElement.matches('input[name=\"Values[{Systolic}]\"]')");
        Assert.True(await page.EvaluateAsync<bool>(focusRing));

        await page.Keyboard.TypeAsync("126");
        await page.Keyboard.PressAsync("Tab");
        Assert.True(await page.EvaluateAsync<bool>($"document.activeElement.matches('input[name=\"Values[{Diastolic}]\"]')"));
        await page.Keyboard.TypeAsync("80");
        await AiReviewBrowserTests.TabToAsync(page, "button[data-record-vitals]");
        Assert.True(await page.EvaluateAsync<bool>(focusRing));
        await page.Keyboard.PressAsync("Enter");
        await page.WaitForURLAsync(url => url.Contains("saved=", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("Saved.", await page.Locator(".sc-notice--success").TextContentAsync());
        Assert.Equal(2, await page.Locator("figure.sc-trend").CountAsync());
        Assert.True(await page.EvaluateAsync<bool>("[...document.querySelectorAll('svg.sc-trend__chart')].every(svg => svg.getBoundingClientRect().height > 40)"));

        await AiReviewBrowserTests.TabToAsync(page, ".sc-trend__values summary");
        Assert.True(await page.EvaluateAsync<bool>(focusRing));
        await page.Keyboard.PressAsync("Enter");
        Assert.Equal(2, await page.Locator(".sc-trend__values[open] tbody tr").CountAsync());

        Assert.Equal(4, (await RecordedAsync(apps, patient)).Count);
        Assert.Empty(errors);
    }
}
